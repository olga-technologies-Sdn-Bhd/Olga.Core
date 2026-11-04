using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Infrastructure;

namespace Olga.Core.Worker;

// Contract and projection for SCI events dispatched by the existing integration consumer.
public sealed record SciLinkScoreChanged(
    [property: JsonPropertyName("link_id")] string LinkId,
    [property: JsonPropertyName("venture_pk")] long VenturePk,
    [property: JsonPropertyName("owner_member_id")] string OwnerMemberId,
    [property: JsonPropertyName("placement_state")] string PlacementState,
    [property: JsonPropertyName("score")] decimal Score,
    [property: JsonPropertyName("relationship_role")] string RelationshipRole,
    [property: JsonPropertyName("event_type")] string ChangeType,
    [property: JsonPropertyName("scorer_version")] string ScorerVersion)
{
    public const string EventType = "SciLinkScoreChanged.v1";
    public static bool TryParse(IntegrationEventEnvelope envelope, out SciLinkScoreChanged? value)
    {
        value = null;
        if (!string.Equals(envelope.EventType, EventType, StringComparison.Ordinal) || !string.Equals(envelope.AggregateType, "SCI_LINK", StringComparison.Ordinal)) return false;
        try
        {
            value = envelope.Data.Deserialize<SciLinkScoreChanged>();
            return value is not null && value.LinkId.Length is > 0 and <= 64 && value.OwnerMemberId.Length is > 0 and <= 64
                && value.VenturePk > 0 && value.Score is >= 0 and <= 100 && string.Equals(envelope.AggregateId, value.LinkId, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}

public sealed class SciScoreProjectionHandler(CoreDbContext db)
{
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

            await using (var clear = new NpgsqlCommand("DELETE FROM social.sci_rollup WHERE owner_member_id=@owner_member_id AND venture_pk=@venture_pk", connection, transaction))
            { Add(clear, "owner_member_id", change.OwnerMemberId); clear.Parameters.AddWithValue("venture_pk", change.VenturePk); await clear.ExecuteNonQueryAsync(ct); }

            await using (var refresh = new NpgsqlCommand("""
                INSERT INTO social.sci_rollup (rollup_id, owner_member_id, venture_pk, industry_code, contact_count, emerging_count, relevant_count, strong_count, exact_count, refreshed_at)
                SELECT 'rollup_' || substr(md5(@owner_member_id || ':' || @venture_pk::text || ':' || COALESCE(cv.industry_code,'UNKNOWN')),1,32),
                       @owner_member_id, @venture_pk, COALESCE(cv.industry_code,'UNKNOWN'), count(*)::int,
                       count(*) FILTER (WHERE s.calibrated_score >= 10 AND s.calibrated_score < 25)::int,
                       count(*) FILTER (WHERE s.calibrated_score >= 25 AND s.calibrated_score < 50)::int,
                       count(*) FILTER (WHERE s.calibrated_score >= 50 AND s.calibrated_score < 75)::int,
                       count(*) FILTER (WHERE s.calibrated_score >= 75)::int, CURRENT_TIMESTAMP
                FROM nlp.sci_link_score s
                LEFT JOIN core.venture cv ON cv.venture_pk=s.contact_venture_pk
                WHERE s.owner_member_id=@owner_member_id AND s.venture_pk=@venture_pk AND s.placement_state='IN_SCI' AND NOT s.is_stale
                GROUP BY COALESCE(cv.industry_code,'UNKNOWN')
                ON CONFLICT (owner_member_id, venture_pk, industry_code) DO UPDATE SET
                  contact_count=EXCLUDED.contact_count, emerging_count=EXCLUDED.emerging_count, relevant_count=EXCLUDED.relevant_count,
                  strong_count=EXCLUDED.strong_count, exact_count=EXCLUDED.exact_count, refreshed_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP
                """, connection, transaction))
            { Add(refresh, "owner_member_id", change.OwnerMemberId); refresh.Parameters.AddWithValue("venture_pk", change.VenturePk); await refresh.ExecuteNonQueryAsync(ct); }

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
    private static void Add(NpgsqlCommand command, string name, string value) => command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value;
}

