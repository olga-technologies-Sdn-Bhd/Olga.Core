using System.Data;
using Npgsql;

namespace Olga.Core.Worker;

public sealed class SciMaintenanceWorker(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<SciMaintenanceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MaintainAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await MaintainAsync(stoppingToken);
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using (var actor = new NpgsqlCommand("SELECT set_config('app.actor_id','olga_sci_maintenance',true)", connection, transaction))
                await actor.ExecuteNonQueryAsync(ct);
            await using (var lease = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended('olga_sci_maintenance',0))", connection, transaction))
            {
                if (await lease.ExecuteScalarAsync(ct) is not true) { await transaction.RollbackAsync(ct); return; }
            }
            await using (var command = new NpgsqlCommand(MaintenanceSql, connection, transaction))
                await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "SCI relationship maintenance failed; the next hourly pass will retry.");
        }
    }

    private const string MaintenanceSql = """
        UPDATE social.sci_product_config
        SET config_json=config_json||'{"layer_sizes":[5,15,50,150,1500],"sci_definition":"CONNECTED_PRIMARY_INTENT_MATCH_AT_LEAST_30"}'::jsonb,
            updated_at=CURRENT_TIMESTAMP
        WHERE product_config_id='SCI_PRODUCT_V1' AND status='ACTIVE'
          AND (config_json->'layer_sizes' IS NULL OR config_json->>'sci_definition' IS NULL);

        UPDATE notification.notification_policy policy
        SET status='ACTIVE',effective_from=COALESCE(effective_from,CURRENT_TIMESTAMP),effective_to=NULL,updated_at=CURRENT_TIMESTAMP
        WHERE policy.policy_version=1 AND policy.status='DRAFT'
          AND policy.purpose_code IN ('CONNECTION_REQUEST','CONNECTION_ACCEPTED','INTRODUCTION_REQUEST','SCI_HIGH_MATCH','SCI_GOAL_MILESTONE','SCI_IN_APP')
          AND NOT EXISTS (SELECT 1 FROM notification.notification_policy active
              WHERE active.status='ACTIVE' AND active.purpose_code=policy.purpose_code AND active.channel=policy.channel
                AND active.community_id IS NOT DISTINCT FROM policy.community_id);

        INSERT INTO social.sci_relationship_history(
          relationship_history_id,owner_member_id,subject_reference_id,former_contact_link_id,display_state,freeze_reason,
          last_match_score,last_band,last_layer_code,last_layer_ordinal,last_outcome_points,last_activity_at,frozen_at)
        SELECT 'history_'||substr(md5(link.owner_member_id||':'||link.contact_member_id),1,32),link.owner_member_id,
          link.contact_member_id,link.link_id,'FORMER_MEMBER','FORMER_MEMBER',contact.match_score,contact.band,
          relationship.layer_code,relationship.layer_ordinal,COALESCE(relationship.rolling_points,0),relationship.last_activity_at,CURRENT_TIMESTAMP
        FROM social.contact_link link
        JOIN iam.member member ON member.member_id=link.contact_member_id AND member.deleted_at IS NOT NULL
        LEFT JOIN social.sci_contact_projection contact ON contact.owner_member_id=link.owner_member_id AND contact.contact_link_pk=link.contact_link_pk
        LEFT JOIN social.sci_relationship_projection relationship ON relationship.owner_member_id=link.owner_member_id AND relationship.contact_link_pk=link.contact_link_pk
        WHERE link.history_freeze_reason IS DISTINCT FROM 'FORMER_MEMBER'
        ON CONFLICT(owner_member_id,subject_reference_id) DO UPDATE SET former_contact_link_id=EXCLUDED.former_contact_link_id,
          display_state='FORMER_MEMBER',freeze_reason='FORMER_MEMBER',last_match_score=EXCLUDED.last_match_score,
          last_band=EXCLUDED.last_band,last_layer_code=EXCLUDED.last_layer_code,last_layer_ordinal=EXCLUDED.last_layer_ordinal,
          last_outcome_points=EXCLUDED.last_outcome_points,last_activity_at=EXCLUDED.last_activity_at,frozen_at=EXCLUDED.frozen_at;

        UPDATE social.contact_link link SET lifecycle_state='REMOVED',history_frozen_at=CURRENT_TIMESTAMP,
          history_freeze_reason='FORMER_MEMBER',display_state='FORMER_MEMBER',last_transition_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
        FROM iam.member member WHERE member.member_id=link.contact_member_id AND member.deleted_at IS NOT NULL
          AND link.history_freeze_reason IS DISTINCT FROM 'FORMER_MEMBER';

        UPDATE social.sci_contact_projection projection SET lifecycle_state='REMOVED',counts_in_sci=false,counts_in_potential=false,
          projection_state='FROZEN',frozen_at=CURRENT_TIMESTAMP,freeze_reason='FORMER_MEMBER',as_of=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
        FROM social.contact_link link JOIN iam.member member ON member.member_id=link.contact_member_id AND member.deleted_at IS NOT NULL
        WHERE link.contact_link_pk=projection.contact_link_pk AND link.owner_member_id=projection.owner_member_id
          AND projection.freeze_reason IS DISTINCT FROM 'FORMER_MEMBER';

        UPDATE social.sci_relationship_projection projection SET projection_status='FROZEN',frozen_at=CURRENT_TIMESTAMP,
          freeze_reason='FORMER_MEMBER',updated_at=CURRENT_TIMESTAMP
        FROM social.contact_link link JOIN iam.member member ON member.member_id=link.contact_member_id AND member.deleted_at IS NOT NULL
        WHERE link.contact_link_pk=projection.contact_link_pk AND link.owner_member_id=projection.owner_member_id
          AND projection.freeze_reason IS DISTINCT FROM 'FORMER_MEMBER';

        UPDATE social.relationship_outcome
        SET status='EXPIRED',updated_at=CURRENT_TIMESTAMP
        WHERE status='PENDING' AND confirmation_due_at<=CURRENT_TIMESTAMP;

        UPDATE social.relationship_outcome
        SET status='EXPIRED',updated_at=CURRENT_TIMESTAMP
        WHERE status='VERIFIED' AND valid_until<=CURRENT_TIMESTAMP;

        WITH product AS MATERIALIZED (
          SELECT relationship_inactivity_days,
                 COALESCE((config_json#>>'{layer_sizes,0}')::int,5) AS layer_1,
                 COALESCE((config_json#>>'{layer_sizes,1}')::int,15) AS layer_2,
                 COALESCE((config_json#>>'{layer_sizes,2}')::int,50) AS layer_3,
                 COALESCE((config_json#>>'{layer_sizes,3}')::int,150) AS layer_4
          FROM social.sci_product_config WHERE status='ACTIVE' LIMIT 1
        ), links AS MATERIALIZED (
          SELECT owner_member_id,contact_link_pk,contact_member_id
          FROM social.contact_link
          WHERE lifecycle_state='CONNECTED' AND display_state='ACTIVE' AND contact_member_id IS NOT NULL
        ), statistics AS MATERIALIZED (
          SELECT link.owner_member_id,link.contact_link_pk,
                 COALESCE(sum(CASE outcome.outcome_type WHEN 'MEETING' THEN 1 WHEN 'INTRODUCTION' THEN 2 ELSE 3 END)
                   FILTER (WHERE outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP),0)::numeric(8,2) AS points,
                 count(*) FILTER (WHERE outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP)::int AS verified_count,
                 COALESCE(bool_or(outcome.outcome_type='INTRODUCTION' AND outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP),false) AS has_introduction,
                 COALESCE(bool_or(outcome.outcome_type='RESULT' AND outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP),false) AS has_result,
                 max(outcome.verified_at) FILTER (WHERE outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP) AS last_activity_at
          FROM links link
          LEFT JOIN social.relationship_outcome outcome ON
            EXISTS (SELECT 1 FROM social.relationship_outcome_party p1 WHERE p1.relationship_outcome_pk=outcome.relationship_outcome_pk AND p1.member_id=link.owner_member_id)
            AND EXISTS (SELECT 1 FROM social.relationship_outcome_party p2 WHERE p2.relationship_outcome_pk=outcome.relationship_outcome_pk AND p2.member_id=link.contact_member_id)
          GROUP BY link.owner_member_id,link.contact_link_pk
        )
        INSERT INTO social.sci_relationship_projection (
          relationship_projection_id,owner_member_id,contact_link_pk,rolling_points,verified_outcome_count,
          has_introduction,has_result,tie_strength,layer_code,layer_ordinal,last_activity_at,quiet_since,calculated_at)
        SELECT 'rel_'||substr(md5(owner_member_id||':'||contact_link_pk::text),1,32),owner_member_id,contact_link_pk,
               points,verified_count,has_introduction,has_result,LEAST(100,points*10),
               CASE WHEN verified_count>0 THEN 'QUALIFIED_NETWORK' END,CASE WHEN verified_count>0 THEN 5::smallint END,
               last_activity_at,CASE WHEN last_activity_at<CURRENT_TIMESTAMP-make_interval(days => product.relationship_inactivity_days)
                 THEN last_activity_at+make_interval(days => product.relationship_inactivity_days) END,CURRENT_TIMESTAMP
        FROM statistics CROSS JOIN product
        ON CONFLICT (owner_member_id,contact_link_pk) DO UPDATE SET
          rolling_points=EXCLUDED.rolling_points,verified_outcome_count=EXCLUDED.verified_outcome_count,
          has_introduction=EXCLUDED.has_introduction,has_result=EXCLUDED.has_result,tie_strength=EXCLUDED.tie_strength,
          layer_code=EXCLUDED.layer_code,layer_ordinal=EXCLUDED.layer_ordinal,last_activity_at=EXCLUDED.last_activity_at,
          quiet_since=EXCLUDED.quiet_since,calculated_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP;

        WITH product AS MATERIALIZED (
          SELECT relationship_inactivity_days,
                 COALESCE((config_json#>>'{layer_sizes,0}')::int,5) AS layer_1,
                 COALESCE((config_json#>>'{layer_sizes,1}')::int,15) AS layer_2,
                 COALESCE((config_json#>>'{layer_sizes,2}')::int,50) AS layer_3,
                 COALESCE((config_json#>>'{layer_sizes,3}')::int,150) AS layer_4
          FROM social.sci_product_config WHERE status='ACTIVE' LIMIT 1
        ), ranked AS (
          SELECT projection.sci_relationship_projection_pk,projection.last_activity_at,
                 row_number() OVER(PARTITION BY projection.owner_member_id ORDER BY projection.rolling_points DESC,projection.last_activity_at DESC,projection.contact_link_pk) AS position
          FROM social.sci_relationship_projection projection
          WHERE projection.projection_status='ACTIVE' AND projection.verified_outcome_count>0
        ), final_rank AS (
          SELECT ranked.sci_relationship_projection_pk,
                 LEAST(5,(CASE WHEN position<=product.layer_1 THEN 1 WHEN position<=product.layer_2 THEN 2
                   WHEN position<=product.layer_3 THEN 3 WHEN position<=product.layer_4 THEN 4 ELSE 5 END)
                   + CASE WHEN last_activity_at<CURRENT_TIMESTAMP-make_interval(days => product.relationship_inactivity_days) THEN 1 ELSE 0 END)::smallint AS ordinal
          FROM ranked CROSS JOIN product
        )
        UPDATE social.sci_relationship_projection projection SET
          layer_ordinal=rank.ordinal,
          layer_code=CASE rank.ordinal WHEN 1 THEN 'STRATEGIC_PARTNERS' WHEN 2 THEN 'DEAL_PARTNERS'
            WHEN 3 THEN 'VERIFIED_COLLABORATORS' WHEN 4 THEN 'ACTIVE_NETWORK' ELSE 'QUALIFIED_NETWORK' END,
          updated_at=CURRENT_TIMESTAMP
        FROM final_rank rank WHERE projection.sci_relationship_projection_pk=rank.sci_relationship_projection_pk;

        UPDATE social.sci_contact_projection contact SET
          layer_code=CASE WHEN contact.counts_in_sci THEN relationship.layer_code END,
          layer_ordinal=CASE WHEN contact.counts_in_sci THEN relationship.layer_ordinal END,
          outcome_points=relationship.rolling_points,tie_strength=relationship.tie_strength,
          last_activity_at=relationship.last_activity_at,as_of=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
        FROM social.sci_relationship_projection relationship
        WHERE relationship.owner_member_id=contact.owner_member_id AND relationship.contact_link_pk=contact.contact_link_pk;

        UPDATE social.sci_reach_out_projection SET status='EXPIRED',updated_at=CURRENT_TIMESTAMP
        WHERE status='ACTIVE' AND expires_at<=CURRENT_TIMESTAMP;

        UPDATE social.sci_summary_projection summary SET
          sci_count=totals.sci_count,potential_count=totals.potential_count,waitlist_count=totals.waitlist_count,
          repository_count=totals.repository_count,total_contact_count=totals.total_contact_count,core_count=totals.core_count,
          as_of=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
        FROM (SELECT summary_owner.owner_member_id,
          count(contact.contact_link_pk) FILTER(WHERE contact.counts_in_sci)::int sci_count,
          count(contact.contact_link_pk) FILTER(WHERE contact.band='NETWORK' AND NOT contact.counts_in_sci)::int potential_count,
          count(contact.contact_link_pk) FILTER(WHERE contact.band='WAITLIST')::int waitlist_count,
          count(contact.contact_link_pk) FILTER(WHERE contact.band='REPOSITORY')::int repository_count,
          count(contact.contact_link_pk)::int total_contact_count,
          count(contact.contact_link_pk) FILTER(WHERE contact.layer_ordinal=1)::int core_count
          FROM social.sci_summary_projection summary_owner
          LEFT JOIN social.sci_contact_projection contact ON contact.owner_member_id=summary_owner.owner_member_id AND contact.projection_state='ACTIVE'
          GROUP BY summary_owner.owner_member_id) totals WHERE summary.owner_member_id=totals.owner_member_id;
        """;
}
