using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Infrastructure;

public sealed class NpgsqlSciAdminRepository(NpgsqlDataSource dataSource) : ISciAdminRepository
{
    public async Task<SciAdminOverviewResponse> GetOverviewAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
              (SELECT count(*)::int FROM social.sci_member_state),
              (SELECT count(*)::int FROM social.sci_member_state WHERE projection_status='READY'),
              (SELECT count(*)::int FROM social.sci_member_state WHERE projection_status='RECALCULATING'),
              (SELECT COALESCE(sum(sci_count),0)::bigint FROM social.sci_summary_projection),
              (SELECT count(*)::int FROM ops.sci_change_approval WHERE status IN ('PENDING','FIRST_APPROVED','APPROVED')),
              (SELECT count(*)::int FROM nlp.sci_scoring_job WHERE status IN ('FAILED','DEAD')),
              CURRENT_TIMESTAMP
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<SciAdminConfigurationResponse> GetConfigurationAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT scoring.scoring_config_id, scoring.scorer_version, scoring.status,
                   scoring.semantic_weight, scoring.taxonomy_weight, scoring.recency_weight,
                   scoring.estimated_score_cap, scoring.waitlist_threshold, scoring.network_threshold,
                   scoring.recency_full_days, scoring.recency_decay_days, scoring.estimated_recency,
                   scoring.row_version,
                   product.product_config_id, product.initial_goal_value, product.next_goal_increment,
                   product.relationship_inactivity_days, product.outcome_validity_days,
                   product.reach_out_default_threshold, product.status, product.row_version
            FROM nlp.sci_scoring_config scoring
            CROSS JOIN social.sci_product_config product
            WHERE scoring.status='ACTIVE' AND product.status='ACTIVE'
            ORDER BY scoring.effective_from DESC, product.effective_from DESC
            LIMIT 1
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new DomainException("SCI_ACTIVE_CONFIGURATION_NOT_FOUND", 503);
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3),
            reader.GetDecimal(4), reader.GetDecimal(5), reader.GetInt16(6), reader.GetInt16(7), reader.GetInt16(8),
            reader.GetInt16(9), reader.GetInt16(10), reader.GetDecimal(11), reader.GetString(13), reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15), reader.GetInt16(16), reader.GetInt16(17), reader.GetInt16(18),
            reader.GetString(19), $"\"{reader.GetInt64(12):x}-{reader.GetInt64(20):x}\"");
    }

    public async Task<SciAdminChangeApprovalPage> ListApprovalsAsync(string? status, string? cursor, int limit, CancellationToken ct)
    {
        var afterPk = DecodeCursor(cursor);
        if (status is not null && status is not ("PENDING" or "FIRST_APPROVED" or "APPROVED" or "REJECTED" or "APPLIED" or "CANCELLED"))
            throw new DomainException("SCI_ADMIN_QUERY_INVALID");
        await using var command = dataSource.CreateCommand($"""
            {ApprovalSelect}
            WHERE (@status IS NULL OR status=@status) AND (@after_pk=0 OR sci_change_approval_pk<@after_pk)
            ORDER BY sci_change_approval_pk DESC
            LIMIT @take
            """);
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
        {
            Value = status is null ? DBNull.Value : status
        });
        command.Parameters.AddWithValue("after_pk", afterPk);
        command.Parameters.AddWithValue("take", limit + 1);
        var rows = new List<(long Pk, SciAdminChangeApprovalResponse Value)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add((reader.GetInt64(0), ReadApproval(reader)));
        var page = rows.Take(limit).ToArray();
        return new(page.Select(x => x.Value).ToArray(), rows.Count > limit ? EncodeCursor(page[^1].Pk) : null);
    }

    public async Task<SciAdminChangeApprovalResponse?> GetApprovalAsync(string approvalId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"{ApprovalSelect} WHERE change_approval_id=@approval_id");
        Add(command, "approval_id", approvalId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadApproval(reader) : null;
    }

    public async Task<SciAdminChangeApprovalResponse> CreateApprovalAsync(string approvalId, SciAdminActor actor,
        SciAdminChangeProposalRequest request, string payloadHash, bool autoApproved, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetActorAsync(connection, transaction, actor.ActorId, ct);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO ops.sci_change_approval (
                change_approval_id, change_type, resource_type, resource_id, payload_json,
                payload_hash, requested_by, status)
            VALUES (@approval_id,@change_type,@resource_type,@resource_id,@payload,@payload_hash,@requested_by,@status)
            ON CONFLICT (resource_type,resource_id,payload_hash) DO NOTHING
            """, connection, transaction))
        {
            Add(command, "approval_id", approvalId); Add(command, "change_type", request.ChangeType);
            Add(command, "resource_type", request.ResourceType); Add(command, "resource_id", request.ResourceId);
            command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = request.Payload.GetRawText();
            Add(command, "payload_hash", payloadHash); Add(command, "requested_by", actor.ActorId);
            Add(command, "status", autoApproved ? "APPROVED" : "PENDING");
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await FindByResourceHashAsync(request.ResourceType, request.ResourceId, payloadHash, ct)
            ?? throw new DomainException("SCI_CHANGE_PROPOSAL_CONFLICT", 409);
    }

    public async Task<SciAdminChangeApprovalResponse> DecideApprovalAsync(string approvalId, SciAdminActor actor,
        bool approve, string expectedETag, CancellationToken ct)
    {
        var expectedVersion = ParseETag(expectedETag);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetActorAsync(connection, transaction, actor.ActorId, ct);
        await using (var command = new NpgsqlCommand("""
            UPDATE ops.sci_change_approval
            SET first_approved_by=CASE WHEN @approve THEN @actor ELSE NULL END,
                first_approved_at=CASE WHEN @approve THEN CURRENT_TIMESTAMP ELSE NULL END,
                status=CASE WHEN @approve THEN 'APPROVED' ELSE 'REJECTED' END,
                updated_at=CURRENT_TIMESTAMP
            WHERE change_approval_id=@approval_id AND status='PENDING' AND row_version=@expected_version
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("approve", approve); Add(command, "actor", actor.ActorId);
            Add(command, "approval_id", approvalId); command.Parameters.AddWithValue("expected_version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DomainException("ETAG_MISMATCH", 412);
        }
        await transaction.CommitAsync(ct);
        return await GetApprovalAsync(approvalId, ct) ?? throw new DomainException("SCI_CHANGE_APPROVAL_NOT_FOUND", 404);
    }

    public async Task<SciAdminChangeApprovalResponse> ApplyApprovalAsync(string approvalId, SciAdminActor actor,
        string expectedETag, CancellationToken ct)
    {
        var expectedVersion = ParseETag(expectedETag);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetActorAsync(connection, transaction, actor.ActorId, ct);

        string changeType; string resourceType; string resourceId; string payload;
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT change_type,resource_type,resource_id,payload_json::text
            FROM ops.sci_change_approval
            WHERE change_approval_id=@approval_id AND status='APPROVED' AND row_version=@expected_version
            FOR UPDATE
            """, connection, transaction))
        {
            Add(lockCommand, "approval_id", approvalId); lockCommand.Parameters.AddWithValue("expected_version", expectedVersion);
            await using var reader = await lockCommand.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new DomainException("ETAG_MISMATCH", 412);
            changeType=reader.GetString(0); resourceType=reader.GetString(1); resourceId=reader.GetString(2); payload=reader.GetString(3);
        }

        if (changeType == "OUTCOME_CORRECTION")
            await ApplyOutcomeCorrectionAsync(connection,transaction,resourceId,payload,approvalId,ct);
        else if (changeType == "FULL_RESCORE")
            await EnqueueFullRescoreAsync(connection, transaction, approvalId, actor.ActorId, ct);
        else if (resourceType == "SCI_SCORING_CONFIG")
            await ApplyScoringConfigAsync(connection, transaction, resourceId, payload, actor.ActorId, ct);
        else if (resourceType == "SCI_PRODUCT_CONFIG")
            await ApplyProductConfigAsync(connection, transaction, resourceId, payload, actor.ActorId, ct);
        else
            throw new DomainException("SCI_CHANGE_RESOURCE_INVALID");

        await using (var command = new NpgsqlCommand("""
            UPDATE ops.sci_change_approval SET status='APPLIED',applied_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            WHERE change_approval_id=@approval_id AND row_version=@expected_version
            """, connection, transaction))
        {
            Add(command, "approval_id", approvalId); command.Parameters.AddWithValue("expected_version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DomainException("ETAG_MISMATCH", 412);
        }
        await transaction.CommitAsync(ct);
        return await GetApprovalAsync(approvalId, ct) ?? throw new DomainException("SCI_CHANGE_APPROVAL_NOT_FOUND", 404);
    }

    private static async Task ApplyScoringConfigAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string resourceId, string payload, string actorId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE nlp.sci_scoring_config SET
              semantic_weight=COALESCE((@payload->>'semantic_weight')::numeric,semantic_weight),
              taxonomy_weight=COALESCE((@payload->>'complementarity_weight')::numeric,taxonomy_weight),
              recency_weight=COALESCE((@payload->>'recency_weight')::numeric,recency_weight),
              estimated_score_cap=COALESCE((@payload->>'estimated_score_cap')::smallint,estimated_score_cap),
              waitlist_threshold=COALESCE((@payload->>'waitlist_threshold')::smallint,waitlist_threshold),
              network_threshold=COALESCE((@payload->>'network_threshold')::smallint,network_threshold),
              cosine_floor=COALESCE((@payload->>'cosine_floor')::numeric,cosine_floor),
              cosine_span=COALESCE((@payload->>'cosine_span')::numeric,cosine_span),
              recency_full_days=COALESCE((@payload->>'recency_full_days')::smallint,recency_full_days),
              recency_decay_days=COALESCE((@payload->>'recency_decay_days')::smallint,recency_decay_days),
              estimated_recency=COALESCE((@payload->>'estimated_recency')::numeric,estimated_recency),
              approved_by_cto=@actor,approved_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            WHERE scoring_config_id=@resource_id AND status='ACTIVE'
            """, connection, transaction);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value=payload; Add(command,"actor",actorId); Add(command,"resource_id",resourceId);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DomainException("SCI_ACTIVE_CONFIGURATION_NOT_FOUND",404);
    }

    private static async Task ApplyProductConfigAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string resourceId, string payload, string actorId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE social.sci_product_config SET
              initial_goal_value=COALESCE((@payload->>'initial_goal_value')::int,initial_goal_value),
              relationship_inactivity_days=COALESCE((@payload->>'relationship_inactivity_days')::smallint,relationship_inactivity_days),
              outcome_validity_days=COALESCE((@payload->>'outcome_validity_days')::smallint,outcome_validity_days),
              reach_out_default_threshold=COALESCE((@payload->>'reach_out_default_threshold')::smallint,reach_out_default_threshold),
              config_json=config_json || jsonb_strip_nulls(jsonb_build_object(
                  'layer_sizes',@payload->'layer_sizes','sci_definition',@payload->'sci_definition')),
              approved_by=@actor,approved_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            WHERE product_config_id=@resource_id AND status='ACTIVE'
            """, connection, transaction);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value=payload; Add(command,"actor",actorId); Add(command,"resource_id",resourceId);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DomainException("SCI_ACTIVE_CONFIGURATION_NOT_FOUND",404);
    }

    private static async Task EnqueueFullRescoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string approvalId, string actorId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO nlp.sci_scoring_job (
              job_id,owner_member_id,venture_pk,trigger_type,dedupe_key,is_full_rescore,approved_by_cto,approved_at)
            SELECT 'job_'||substr(md5(@approval_id||':'||state.owner_member_id),1,32),
                   state.owner_member_id,state.primary_venture_pk,'ADMIN_FULL_RESCORE',
                   'admin-full-rescore:'||@approval_id||':'||state.owner_member_id,true,@actor,CURRENT_TIMESTAMP
            FROM social.sci_member_state state WHERE state.primary_venture_pk IS NOT NULL
            ON CONFLICT (dedupe_key) DO NOTHING;
            UPDATE social.sci_member_state SET projection_status='RECALCULATING',projection_started_at=CURRENT_TIMESTAMP,
                   projection_error_code=NULL,updated_at=CURRENT_TIMESTAMP WHERE primary_venture_pk IS NOT NULL;
            """, connection, transaction);
        Add(command,"approval_id",approvalId); Add(command,"actor",actorId); await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyOutcomeCorrectionAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,
        string outcomeId,string payload,string approvalId,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("""
            WITH corrected AS (
              UPDATE social.relationship_outcome SET
                status=@payload->>'corrected_status',
                verified_at=CASE WHEN @payload->>'corrected_status'='VERIFIED' THEN CURRENT_TIMESTAMP ELSE verified_at END,
                withdrawal_due_at=CASE WHEN @payload->>'corrected_status'='VERIFIED' THEN CURRENT_TIMESTAMP+interval '30 days' ELSE withdrawal_due_at END,
                valid_until=CASE WHEN @payload->>'corrected_status'='VERIFIED' THEN CURRENT_TIMESTAMP+interval '365 days' ELSE valid_until END,
                updated_at=CURRENT_TIMESTAMP
              WHERE outcome_id=@outcome_id
              RETURNING relationship_outcome_pk,outcome_id
            ), recipients AS (
              SELECT DISTINCT party.member_id,corrected.outcome_id FROM corrected
              JOIN social.relationship_outcome_party party ON party.relationship_outcome_pk=corrected.relationship_outcome_pk
              WHERE party.party_role IN ('INITIATOR','PARTICIPANT')
            )
            SELECT count(*) FROM recipients
            CROSS JOIN LATERAL notification.try_enqueue(
              'not_'||substr(md5(@approval_id||':'||recipients.member_id),1,32),recipients.member_id,'SCI_IN_APP','IN_APP',
              'RELATIONSHIP_OUTCOME',recipients.outcome_id,'OUTCOME_CORRECTED',
              'outcome-corrected:'||@approval_id||':'||recipients.member_id,'GENERAL',NULL)
            """,connection,transaction);
        command.Parameters.Add("payload",NpgsqlDbType.Jsonb).Value=payload;Add(command,"outcome_id",outcomeId);Add(command,"approval_id",approvalId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture)==0)
            throw new DomainException("RELATIONSHIP_OUTCOME_NOT_FOUND",404);
    }

    private async Task<SciAdminChangeApprovalResponse?> FindByResourceHashAsync(string resourceType, string resourceId, string payloadHash, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"{ApprovalSelect} WHERE resource_type=@resource_type AND resource_id=@resource_id AND payload_hash=@payload_hash");
        Add(command,"resource_type",resourceType); Add(command,"resource_id",resourceId); Add(command,"payload_hash",payloadHash);
        await using var reader=await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadApproval(reader) : null;
    }

    private static SciAdminChangeApprovalResponse ReadApproval(NpgsqlDataReader reader)
    {
        using var document=JsonDocument.Parse(reader.GetString(5));
        return new(reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),document.RootElement.Clone(),
            reader.GetString(6),reader.GetString(7),reader.GetFieldValue<DateTimeOffset>(8),reader.IsDBNull(9)?null:reader.GetString(9),
            reader.IsDBNull(10)?null:reader.GetFieldValue<DateTimeOffset>(10),reader.GetString(11),
            reader.IsDBNull(12)?null:reader.GetFieldValue<DateTimeOffset>(12),[], $"\"{reader.GetInt64(13):x}\"");
    }

    private static async Task SetActorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId, CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("SELECT set_config('app.actor_id',@actor,true)",connection,transaction);
        Add(command,"actor",actorId); await command.ExecuteNonQueryAsync(ct);
    }

    private static long ParseETag(string value)
    {
        var token=value.Trim().Trim('"');
        if (!long.TryParse(token,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var version)) throw new DomainException("ETAG_INVALID",400);
        return version;
    }

    private static string EncodeCursor(long value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)));
    private static long DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        try { return long.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)),CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is FormatException or OverflowException) { throw new DomainException("SCI_ADMIN_CURSOR_INVALID"); }
    }

    private static void Add(NpgsqlCommand command,string name,string? value) =>
        command.Parameters.AddWithValue(name,value is null?DBNull.Value:value);

    private const string ApprovalSelect="""
        SELECT sci_change_approval_pk,change_approval_id,change_type,resource_type,resource_id,
               payload_json::text,payload_hash,requested_by,requested_at,first_approved_by,first_approved_at,
               status,applied_at,row_version
        FROM ops.sci_change_approval
        """;
}
