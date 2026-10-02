using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Infrastructure;

namespace Olga.Core.Worker;

public sealed class NlpCompletionProjectionHandler(CoreDbContext db)
{
    public async Task HandleAsync(string eventId, NlpMatchRequestCompleted completion, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using (var advisoryLock = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@request_id, 0))"))
            {
                AddVarchar(advisoryLock, "request_id", completion.RequestId, 64);
                await advisoryLock.ExecuteNonQueryAsync(ct);
            }

            var projection = await ReadProjectionAsync(connection, transaction, completion.RequestId, ct)
                ?? throw new InvalidOperationException($"Completed NLP request {completion.RequestId} does not exist.");
            if (projection.CandidateCount != completion.CandidateCount
                || !string.Equals(projection.ModelVersion, completion.ModelVersion, StringComparison.Ordinal)
                || !string.Equals(projection.PreprocessingVersion, completion.PreprocessingVersion, StringComparison.Ordinal)
                || !string.Equals(projection.RankingVersion, completion.RankingVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"NLP completion event {eventId} does not match its persisted request snapshot.");

            if (await ProjectionExistsAsync(connection, transaction, projection.RequesterId, completion.RequestId, ct))
            {
                await transaction.CommitAsync(ct);
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                request_id = completion.RequestId,
                status = "READY",
                result_count = projection.ResultCount,
                top_match_result_id = projection.TopMatchResultId,
                model_version = completion.ModelVersion,
                preprocessing_version = completion.PreprocessingVersion,
                ranking_version = completion.RankingVersion
            });
            await InsertSyncChangeAsync(connection, transaction, projection.RequesterId, completion.RequestId, payload, ct);

            if (projection.TopMatchResultId is not null && projection.TopScore is not null)
                await EnqueueNotificationAsync(connection, transaction, eventId, projection, completion.RequestId, ct);

            await transaction.CommitAsync(ct);
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private static async Task<MatchProjection?> ReadProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT r.requester_id,
                   r.context_id,
                   r.candidate_count,
                   r.model_version,
                   r.preprocessing_version,
                   r.ranking_version,
                   (SELECT count(*)::int FROM nlp.nlp_match_result m
                    WHERE m.request_id = r.request_id AND m.policy_status = 'ELIGIBLE') AS result_count,
                   best.match_result_id,
                   best.final_score
            FROM nlp.match_request r
            LEFT JOIN LATERAL (
                SELECT m.match_result_id, m.final_score
                FROM nlp.nlp_match_result m
                WHERE m.request_id = r.request_id AND m.policy_status = 'ELIGIBLE'
                ORDER BY m.rank
                LIMIT 1
            ) best ON true
            WHERE r.request_id = @request_id AND r.status = 'COMPLETED'
            """);
        AddVarchar(command, "request_id", requestId, 64);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MatchProjection(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetDecimal(8));
    }

    private static async Task<bool> ProjectionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requesterId,
        string requestId,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM ops.sync_change
                WHERE member_scope_id = @requester_id
                  AND resource_type = 'MATCH'
                  AND resource_id = @request_id
                  AND change_type = 'UPSERT')
            """);
        AddVarchar(command, "requester_id", requesterId, 64);
        AddVarchar(command, "request_id", requestId, 64);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task InsertSyncChangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requesterId,
        string requestId,
        string payload,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO ops.sync_change(
                community_id, member_scope_id, resource_type, resource_id,
                change_type, payload_json, expires_at)
            SELECT m.community_id, @requester_id, 'MATCH', @request_id,
                   'UPSERT', @payload, CURRENT_TIMESTAMP + interval '30 days'
            FROM iam.member m
            WHERE m.member_id = @requester_id AND m.status = 'ACTIVE'
            """);
        AddVarchar(command, "requester_id", requesterId, 64);
        AddVarchar(command, "request_id", requestId, 64);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException($"NLP requester {requesterId} is not an active member.");
    }

    private static async Task EnqueueNotificationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventId,
        MatchProjection projection,
        string requestId,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT * FROM notification.try_enqueue(
                @notification_id, @member_id, @purpose_code, @channel,
                @resource_type, @resource_id, @template_code, @dedupe_key,
                @context_id, @source_confidence)
            """);
        AddVarchar(command, "notification_id", StableId("nlp-match", eventId), 64);
        AddVarchar(command, "member_id", projection.RequesterId, 64);
        AddVarchar(command, "purpose_code", "MATCH", 64);
        AddVarchar(command, "channel", "PUSH", 16);
        AddVarchar(command, "resource_type", "MATCH", 32);
        AddVarchar(command, "resource_id", requestId, 64);
        AddVarchar(command, "template_code", "MATCH_RESULTS_READY", 64);
        AddVarchar(command, "dedupe_key", $"match-ready:{requestId}", 160);
        AddVarchar(command, "context_id", projection.ContextId, 64);
        command.Parameters.Add(new NpgsqlParameter("source_confidence", NpgsqlDbType.Numeric) { Precision = 6, Scale = 5, Value = projection.TopScore!.Value });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) => new(sql, connection, transaction);

    private static void AddVarchar(NpgsqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Size = size, Value = value });

    private static string StableId(string prefix, string value) =>
        $"{prefix}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32]}";

    private sealed record MatchProjection(
        string RequesterId,
        string ContextId,
        int CandidateCount,
        string ModelVersion,
        string PreprocessingVersion,
        string RankingVersion,
        int ResultCount,
        long? TopMatchResultId,
        decimal? TopScore);
}
