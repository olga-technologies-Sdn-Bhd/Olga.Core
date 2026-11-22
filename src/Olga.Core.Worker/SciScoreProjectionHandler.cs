using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Infrastructure;

namespace Olga.Core.Worker;

// Exact contract emitted by nlp.apply_sci_link_score_v2.
public sealed record SciLinkScoreChanged(
    [property: JsonPropertyName("link_id")] string LinkId,
    [property: JsonPropertyName("owner_member_id")] string OwnerMemberId,
    [property: JsonPropertyName("venture_pk")] long VenturePk,
    [property: JsonPropertyName("score")] short Score,
    [property: JsonPropertyName("band")] string Band,
    [property: JsonPropertyName("is_estimated")] bool IsEstimated,
    [property: JsonPropertyName("semantic_component")] decimal SemanticComponent,
    [property: JsonPropertyName("complementarity_component")] decimal ComplementarityComponent,
    [property: JsonPropertyName("recency_component")] decimal RecencyComponent,
    [property: JsonPropertyName("scorer_version")] string ScorerVersion,
    [property: JsonPropertyName("scoring_config_id")] string ScoringConfigId,
    [property: JsonPropertyName("cause_event_id")] string CauseEventId)
{
    public const string EventType = "SciLinkScoreChanged.v2";

    public static bool TryParse(IntegrationEventEnvelope envelope, out SciLinkScoreChanged? value)
    {
        value = null;
        if (!string.Equals(envelope.EventType, EventType, StringComparison.Ordinal)
            || !string.Equals(envelope.AggregateType, "SCI_LINK", StringComparison.Ordinal)) return false;
        try
        {
            value = envelope.Data.Deserialize<SciLinkScoreChanged>();
            return value is not null
                && value.LinkId.Length is > 0 and <= 64 && value.OwnerMemberId.Length is > 0 and <= 64
                && value.VenturePk > 0 && value.Score is >= 0 and <= 100
                && value.Band is "REPOSITORY" or "WAITLIST" or "NETWORK"
                && value.SemanticComponent is >= 0 and <= 1
                && value.ComplementarityComponent is >= 0 and <= 1
                && value.RecencyComponent is >= 0 and <= 1
                && value.ScoringConfigId.Length is > 0 and <= 64
                && value.CauseEventId.Length is > 0 and <= 64
                && string.Equals(envelope.AggregateId, value.LinkId, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}

public sealed record SciScoringCompleted(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("owner_member_id")] string OwnerMemberId,
    [property: JsonPropertyName("venture_pk")] long VenturePk,
    [property: JsonPropertyName("processed_count")] int ProcessedCount,
    [property: JsonPropertyName("scorer_version")] string ScorerVersion,
    [property: JsonPropertyName("scoring_config_id")] string ScoringConfigId)
{
    public const string EventType="SciScoringCompleted.v1";
    public static bool TryParse(IntegrationEventEnvelope envelope,out SciScoringCompleted? value)
    {
        value=null;
        if(envelope.EventType!=EventType||envelope.AggregateType!="SCI_MEMBER") return false;
        try
        {
            value=envelope.Data.Deserialize<SciScoringCompleted>();
            return value is not null&&value.JobId.Length is >0 and <=64&&value.OwnerMemberId.Length is >0 and <=64
                && value.VenturePk>0&&value.ProcessedCount>=0&&value.ScoringConfigId.Length is >0 and <=64
                && envelope.AggregateId==value.OwnerMemberId;
        }
        catch(JsonException){return false;}
    }
}

public sealed class SciScoreProjectionHandler(CoreDbContext db)
{
    public async Task HandleCompletionAsync(string eventId,SciScoringCompleted completed,CancellationToken ct)
    {
        var connection=(NpgsqlConnection)db.Database.GetDbConnection();var close=connection.State!=ConnectionState.Open;
        if(close) await connection.OpenAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        try
        {
            await using(var advisory=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@event_id,0))",connection,transaction))
            {Add(advisory,"event_id",eventId);await advisory.ExecuteNonQueryAsync(ct);}
            await using(var seen=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM ops.sync_change WHERE resource_type='SCI_EVENT' AND resource_id=@event_id)",connection,transaction))
            {Add(seen,"event_id",eventId);if((bool)(await seen.ExecuteScalarAsync(ct)??false)){await transaction.CommitAsync(ct);return;}}
            await using(var ready=new NpgsqlCommand("""
                UPDATE social.sci_member_state SET projection_status='READY',projection_completed_at=CURRENT_TIMESTAMP,
                  projection_error_code=NULL,updated_at=CURRENT_TIMESTAMP
                WHERE owner_member_id=@owner_member_id AND primary_venture_pk=@venture_pk
                """,connection,transaction))
            {Add(ready,"owner_member_id",completed.OwnerMemberId);ready.Parameters.AddWithValue("venture_pk",completed.VenturePk);await ready.ExecuteNonQueryAsync(ct);}
            await ProjectSummaryAsync(connection,transaction,completed.OwnerMemberId,completed.ScorerVersion,completed.ScoringConfigId,ct);
            await ProjectReachOutAsync(connection,transaction,completed.OwnerMemberId,ct);
            await EnsureGoalAndMilestonesAsync(connection,transaction,completed.OwnerMemberId,ct);
            await using(var sync=new NpgsqlCommand("""
                INSERT INTO ops.sync_change(community_id,member_scope_id,resource_type,resource_id,change_type,payload_json,expires_at)
                SELECT member.community_id,@owner_member_id,'SCI_EVENT',@event_id,'UPSERT',@payload,CURRENT_TIMESTAMP+interval '30 days'
                FROM iam.member member WHERE member.member_id=@owner_member_id
                """,connection,transaction))
            {Add(sync,"owner_member_id",completed.OwnerMemberId);Add(sync,"event_id",eventId);sync.Parameters.Add("payload",NpgsqlDbType.Jsonb).Value=JsonSerializer.Serialize(completed);await sync.ExecuteNonQueryAsync(ct);}
            await transaction.CommitAsync(ct);
        }
        finally{if(close) await connection.CloseAsync();}
    }

    public async Task HandleAsync(string eventId, SciLinkScoreChanged change, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@event_id,0))", connection, transaction))
            { Add(advisory, "event_id", eventId); await advisory.ExecuteNonQueryAsync(ct); }

            await using (var seen = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM ops.sync_change WHERE resource_type='SCI_EVENT' AND resource_id=@event_id)", connection, transaction))
            {
                Add(seen, "event_id", eventId);
                if ((bool)(await seen.ExecuteScalarAsync(ct) ?? false)) { await transaction.CommitAsync(ct); return; }
            }

            await ProjectContactAsync(connection, transaction, eventId, change, ct);
            await ProjectSummaryAsync(connection, transaction, change.OwnerMemberId,change.ScorerVersion,change.ScoringConfigId, ct);
            await ProjectReachOutAsync(connection, transaction, change.OwnerMemberId, ct);
            await EnsureGoalAndMilestonesAsync(connection, transaction, change.OwnerMemberId, ct);

            await using (var sync = new NpgsqlCommand("""
                INSERT INTO ops.sync_change (community_id, member_scope_id, resource_type, resource_id, change_type, payload_json, expires_at)
                SELECT m.community_id, @owner_member_id, 'SCI_EVENT', @event_id, 'UPSERT', @payload, CURRENT_TIMESTAMP + interval '30 days'
                FROM iam.member m WHERE m.member_id=@owner_member_id
                """, connection, transaction))
            {
                Add(sync, "owner_member_id", change.OwnerMemberId); Add(sync, "event_id", eventId);
                sync.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(change);
                await sync.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static async Task ProjectContactAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string eventId, SciLinkScoreChanged change, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH state AS (
                UPDATE social.sci_member_state
                SET projection_status='READY', current_projection_version=GREATEST(current_projection_version,1),
                    projection_completed_at=CURRENT_TIMESTAMP, projection_error_code=NULL, updated_at=CURRENT_TIMESTAMP
                WHERE owner_member_id=@owner_member_id AND primary_venture_pk=@venture_pk
                RETURNING current_projection_version
            ), source AS (
                SELECT l.contact_link_pk, l.lifecycle_state, l.history_frozen_at,
                       COALESCE(r.rolling_points,0) AS rolling_points, COALESCE(r.tie_strength,0) AS tie_strength,
                       r.layer_code, r.layer_ordinal, r.last_activity_at,
                       EXISTS (
                           SELECT 1 FROM social.member_block b
                           WHERE b.removed_at IS NULL AND l.contact_member_id IS NOT NULL
                             AND ((b.blocker_member_id=l.owner_member_id AND b.blocked_member_id=l.contact_member_id)
                               OR (b.blocker_member_id=l.contact_member_id AND b.blocked_member_id=l.owner_member_id))
                       ) AS is_blocked, state.current_projection_version
                FROM social.contact_link l CROSS JOIN state
                LEFT JOIN social.sci_relationship_projection r
                  ON r.owner_member_id=l.owner_member_id AND r.contact_link_pk=l.contact_link_pk
                WHERE l.owner_member_id=@owner_member_id AND l.link_id=@link_id
            ), previous AS (
                SELECT p.contact_link_pk, p.band, p.match_score FROM social.sci_contact_projection p
                JOIN source s ON s.contact_link_pk=p.contact_link_pk
                WHERE p.owner_member_id=@owner_member_id
            ), upserted AS (
                INSERT INTO social.sci_contact_projection (
                    projection_id, owner_member_id, contact_link_pk, primary_venture_pk, lifecycle_state,
                    match_score, band, is_estimated, counts_in_sci, counts_in_potential, projection_state,
                    frozen_at, freeze_reason, layer_code, layer_ordinal, outcome_points, tie_strength,
                    reason_code, scorer_version, scoring_config_id, projection_version,
                    score_calculated_at, last_activity_at, as_of)
                SELECT 'scp_' || substr(md5(@owner_member_id || ':' || @link_id),1,32), @owner_member_id,
                       s.contact_link_pk, @venture_pk, s.lifecycle_state, @score, @band, @is_estimated,
                       s.lifecycle_state='CONNECTED' AND @band='NETWORK' AND NOT @is_estimated AND NOT s.is_blocked,
                       s.lifecycle_state NOT IN ('CONNECTED','REMOVED') AND @band='NETWORK' AND NOT s.is_blocked,
                       CASE WHEN s.lifecycle_state='REMOVED' OR s.is_blocked THEN 'FROZEN' ELSE 'ACTIVE' END,
                       CASE WHEN s.lifecycle_state='REMOVED' OR s.is_blocked THEN COALESCE(s.history_frozen_at,CURRENT_TIMESTAMP) END,
                       CASE WHEN s.is_blocked THEN 'BLOCKED' WHEN s.lifecycle_state='REMOVED' THEN 'REMOVED' END,
                       CASE WHEN s.lifecycle_state='CONNECTED' AND @band='NETWORK' AND NOT @is_estimated AND NOT s.is_blocked THEN s.layer_code END,
                       CASE WHEN s.lifecycle_state='CONNECTED' AND @band='NETWORK' AND NOT @is_estimated AND NOT s.is_blocked THEN s.layer_ordinal END,
                       s.rolling_points, s.tie_strength,
                       CASE WHEN @is_estimated THEN 'GENERAL_PROFILE_MATCH' ELSE 'PRIMARY_INTENT_MATCH' END,
                       @scorer_version, @scoring_config_id, s.current_projection_version,
                       CURRENT_TIMESTAMP, s.last_activity_at, CURRENT_TIMESTAMP
                FROM source s
                ON CONFLICT (owner_member_id, contact_link_pk) DO UPDATE SET
                    primary_venture_pk=EXCLUDED.primary_venture_pk, lifecycle_state=EXCLUDED.lifecycle_state,
                    match_score=EXCLUDED.match_score, band=EXCLUDED.band, is_estimated=EXCLUDED.is_estimated,
                    counts_in_sci=EXCLUDED.counts_in_sci, counts_in_potential=EXCLUDED.counts_in_potential,
                    projection_state=EXCLUDED.projection_state, frozen_at=EXCLUDED.frozen_at,
                    freeze_reason=EXCLUDED.freeze_reason, layer_code=EXCLUDED.layer_code,
                    layer_ordinal=EXCLUDED.layer_ordinal, outcome_points=EXCLUDED.outcome_points,
                    tie_strength=EXCLUDED.tie_strength, reason_code=EXCLUDED.reason_code,
                    scorer_version=EXCLUDED.scorer_version, scoring_config_id=EXCLUDED.scoring_config_id,
                    projection_version=EXCLUDED.projection_version, score_calculated_at=EXCLUDED.score_calculated_at,
                    last_activity_at=EXCLUDED.last_activity_at, as_of=EXCLUDED.as_of, updated_at=CURRENT_TIMESTAMP
                RETURNING contact_link_pk, projection_version
            )
            INSERT INTO social.sci_band_change (
                band_change_id, owner_member_id, contact_link_pk, cause_event_id,
                from_score, to_score, from_band, to_band, scorer_version, scoring_config_id, occurred_at)
            SELECT 'band_' || substr(md5(@event_id),1,32), @owner_member_id, u.contact_link_pk, @cause_event_id,
                   p.match_score, @score, p.band, @band, @scorer_version, @scoring_config_id, CURRENT_TIMESTAMP
            FROM upserted u LEFT JOIN previous p ON p.contact_link_pk=u.contact_link_pk
            WHERE p.band IS DISTINCT FROM @band
            ON CONFLICT (band_change_id) DO NOTHING
            """, connection, transaction);
        Bind(command, eventId, change);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ProjectSummaryAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string ownerMemberId,string scorerVersion,string scoringConfigId,CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH state AS (
                SELECT current_projection_version, projection_status FROM social.sci_member_state
                WHERE owner_member_id=@owner_member_id
            ), totals AS (
                SELECT count(*) FILTER (WHERE counts_in_sci)::int AS sci_count,
                       count(*) FILTER (WHERE band='NETWORK' AND NOT counts_in_sci)::int AS potential_count,
                       count(*) FILTER (WHERE band='WAITLIST')::int AS waitlist_count,
                       count(*) FILTER (WHERE band='REPOSITORY')::int AS repository_count,
                       count(*)::int AS total_contact_count,
                       count(*) FILTER (WHERE layer_ordinal=1)::int AS core_count
                FROM social.sci_contact_projection
                WHERE owner_member_id=@owner_member_id AND projection_state='ACTIVE'
            ), intent_count AS (
                SELECT count(*)::int AS value FROM nlp.venture_intent_set
                WHERE owner_member_id=@owner_member_id AND status IN ('ACTIVE','SUPERSEDED')
            )
            INSERT INTO social.sci_summary_projection (
                summary_id, owner_member_id, sci_count, potential_count, waitlist_count,
                repository_count, total_contact_count, core_count, month_change, intent_update_count,
                projection_status, projection_version, scorer_version, scoring_config_id, as_of)
            SELECT 'scs_' || substr(md5(@owner_member_id),1,32), @owner_member_id,
                   t.sci_count, t.potential_count, t.waitlist_count, t.repository_count,
                   t.total_contact_count, t.core_count, 0, i.value, s.projection_status,
                   s.current_projection_version, @scorer_version, @scoring_config_id, CURRENT_TIMESTAMP
            FROM state s CROSS JOIN totals t CROSS JOIN intent_count i
            ON CONFLICT (owner_member_id) DO UPDATE SET
                sci_count=EXCLUDED.sci_count, potential_count=EXCLUDED.potential_count,
                waitlist_count=EXCLUDED.waitlist_count, repository_count=EXCLUDED.repository_count,
                total_contact_count=EXCLUDED.total_contact_count, core_count=EXCLUDED.core_count,
                intent_update_count=EXCLUDED.intent_update_count, projection_status=EXCLUDED.projection_status,
                projection_version=EXCLUDED.projection_version, scorer_version=EXCLUDED.scorer_version,
                scoring_config_id=EXCLUDED.scoring_config_id, as_of=EXCLUDED.as_of, updated_at=CURRENT_TIMESTAMP
            """, connection, transaction);
        Add(command, "owner_member_id", ownerMemberId);
        Add(command, "scorer_version", scorerVersion);
        Add(command, "scoring_config_id", scoringConfigId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureGoalAndMilestonesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string ownerMemberId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO social.sci_goal (goal_id, owner_member_id, goal_value, goal_sequence, goal_source, owner_type, product_config_id)
            SELECT 'goal_' || substr(md5(@owner_member_id || ':1'),1,32), @owner_member_id,
                   config.initial_goal_value, 1, 'SYSTEM', 'INDIVIDUAL', config.product_config_id
            FROM social.sci_product_config config
            WHERE config.status='ACTIVE'
              AND NOT EXISTS (SELECT 1 FROM social.sci_goal WHERE owner_member_id=@owner_member_id);

            WITH active_goal AS (
                SELECT sci_goal_pk, goal_value FROM social.sci_goal
                WHERE owner_member_id=@owner_member_id AND status='ACTIVE'
            ), current_sci AS (
                SELECT sci_count FROM social.sci_summary_projection WHERE owner_member_id=@owner_member_id
            ), reached(percent) AS (
                VALUES (25::smallint),(50::smallint),(75::smallint),(100::smallint)
            )
            , inserted AS (
            INSERT INTO social.sci_goal_milestone (milestone_id, sci_goal_pk, milestone_percent, reached_at)
            SELECT 'gsm_' || substr(md5(@owner_member_id || ':' || g.sci_goal_pk::text || ':' || r.percent::text),1,32),
                   g.sci_goal_pk, r.percent, CURRENT_TIMESTAMP
            FROM active_goal g CROSS JOIN current_sci s CROSS JOIN reached r
            WHERE s.sci_count * 100 >= g.goal_value * r.percent
            ON CONFLICT (sci_goal_pk, milestone_percent) DO NOTHING
            RETURNING milestone_id,milestone_percent)
            SELECT notification.try_enqueue(
                'not_'||substr(md5(milestone_id||':push'),1,32),@owner_member_id,'SCI_GOAL_MILESTONE','PUSH',
                'SCI_GOAL',milestone_id,'SCI_GOAL_MILESTONE_'||milestone_percent::text,
                'sci-goal-milestone:'||milestone_id,'GENERAL',NULL)
            FROM inserted
            """, connection, transaction);
        Add(command, "owner_member_id", ownerMemberId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ProjectReachOutAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string ownerMemberId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            DELETE FROM social.sci_reach_out_projection recommendation
            USING social.sci_member_state state
            WHERE recommendation.owner_member_id=@owner_member_id
              AND state.owner_member_id=recommendation.owner_member_id
              AND recommendation.projection_version=state.current_projection_version;

            WITH state AS (
                SELECT current_projection_version, reach_out_threshold
                FROM social.sci_member_state WHERE owner_member_id=@owner_member_id
            ), ranked AS (
                SELECT contact.contact_link_pk, contact.match_score,
                       LEAST(1::numeric, GREATEST(0::numeric,
                           contact.match_score::numeric / 100 * 0.70 + contact.tie_strength / 100 * 0.30)) AS relevance_score,
                       row_number() OVER (
                           ORDER BY (contact.match_score::numeric / 100 * 0.70 + contact.tie_strength / 100 * 0.30) DESC,
                                    contact.match_score DESC, contact.contact_link_pk) AS rank,
                       state.current_projection_version, state.reach_out_threshold
                FROM social.sci_contact_projection contact CROSS JOIN state
                JOIN social.contact_link link ON link.contact_link_pk=contact.contact_link_pk
                WHERE contact.owner_member_id=@owner_member_id
                  AND contact.projection_state='ACTIVE' AND contact.lifecycle_state='CONNECTED'
                  AND contact.match_score >= state.reach_out_threshold
                  AND link.contact_member_id IS NOT NULL
            )
            INSERT INTO social.sci_reach_out_projection (
                recommendation_id, owner_member_id, contact_link_pk, projection_version,
                generated_at, reason_code, match_score, relevance_score, member_threshold,
                rank, status, expires_at)
            SELECT 'reach_' || substr(md5(@owner_member_id || ':' || current_projection_version::text || ':' || contact_link_pk::text),1,32),
                   @owner_member_id, contact_link_pk, current_projection_version, CURRENT_TIMESTAMP,
                   'QUIET_TIE', match_score, relevance_score, reach_out_threshold, rank, 'ACTIVE',
                   CURRENT_TIMESTAMP + interval '7 days'
            FROM ranked
            """, connection, transaction);
        Add(command, "owner_member_id", ownerMemberId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(NpgsqlCommand command, string eventId, SciLinkScoreChanged change)
    {
        Add(command, "event_id", eventId); Add(command, "cause_event_id", change.CauseEventId);
        Add(command, "owner_member_id", change.OwnerMemberId); Add(command, "link_id", change.LinkId);
        Add(command, "band", change.Band); Add(command, "scorer_version", change.ScorerVersion);
        Add(command, "scoring_config_id", change.ScoringConfigId);
        command.Parameters.AddWithValue("venture_pk", change.VenturePk);
        command.Parameters.AddWithValue("score", change.Score);
        command.Parameters.AddWithValue("is_estimated", change.IsEstimated);
    }

    private static void Add(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value;
}
