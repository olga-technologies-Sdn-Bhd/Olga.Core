using System.Data;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;

namespace Olga.Core.Infrastructure;

public sealed class NpgsqlOutboxRepository(NpgsqlDataSource dataSource) : IOutboxRepository
{
    private const int CommandTimeoutSeconds = 15;
    private static readonly string[] RequiredColumns =
    [
        "lease_id",
        "leased_until",
        "last_attempt_at",
        "exhausted_at",
        "last_error_code",
        "last_error_summary",
        "row_version"
    ];
    public static IReadOnlyList<string> RequiredSchemaColumns { get; } = Array.AsReadOnly(RequiredColumns);

    public const string ClaimSql = """
        WITH candidates AS (
            SELECT outbox_event_id
            FROM ops.outbox_event
            WHERE published_at IS NULL
              AND exhausted_at IS NULL
              AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
              AND (leased_until IS NULL OR leased_until <= @now)
            ORDER BY occurred_at, outbox_event_id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size
        )
        UPDATE ops.outbox_event AS event
        SET lease_id = @lease_id,
            leased_until = @leased_until,
            last_attempt_at = @now,
            attempt_count = event.attempt_count + 1
        FROM candidates
        WHERE event.outbox_event_id = candidates.outbox_event_id
        RETURNING
            event.outbox_event_id,
            event.aggregate_type,
            event.aggregate_id,
            event.event_type,
            event.payload_json,
            event.occurred_at,
            event.attempt_count,
            event.lease_id,
            event.leased_until;
        """;

    public const string RenewLeaseSql = """
        UPDATE ops.outbox_event
        SET leased_until = @leased_until
        WHERE outbox_event_id = @outbox_event_id
          AND lease_id = @lease_id
          AND published_at IS NULL
          AND exhausted_at IS NULL
          AND leased_until > @now;
        """;

    public const string CompleteSql = """
        UPDATE ops.outbox_event
        SET published_at = @published_at,
            lease_id = NULL,
            leased_until = NULL,
            next_attempt_at = NULL,
            last_error_code = NULL,
            last_error_summary = NULL
        WHERE outbox_event_id = @outbox_event_id
          AND lease_id = @lease_id
          AND published_at IS NULL
          AND exhausted_at IS NULL;
        """;

    public const string ScheduleRetrySql = """
        UPDATE ops.outbox_event
        SET next_attempt_at = @next_attempt_at,
            last_error_code = @error_code,
            last_error_summary = @safe_error_summary,
            lease_id = NULL,
            leased_until = NULL
        WHERE outbox_event_id = @outbox_event_id
          AND lease_id = @lease_id
          AND published_at IS NULL
          AND exhausted_at IS NULL;
        """;

    public const string ExhaustSql = """
        UPDATE ops.outbox_event
        SET exhausted_at = @exhausted_at,
            next_attempt_at = NULL,
            last_error_code = @error_code,
            last_error_summary = @safe_error_summary,
            lease_id = NULL,
            leased_until = NULL
        WHERE outbox_event_id = @outbox_event_id
          AND lease_id = @lease_id
          AND published_at IS NULL
          AND exhausted_at IS NULL;
        """;

    public const string SchemaReadinessSql = """
        SELECT count(DISTINCT column_name)
        FROM information_schema.columns
        WHERE table_schema = @schema_name
          AND table_name = @table_name
          AND column_name = ANY (@required_columns);
        """;

    public async Task<OutboxSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = CreateCommand(SchemaReadinessSql, connection);
            AddVarchar(command, "schema_name", "ops", 63);
            AddVarchar(command, "table_name", "outbox_event", 63);
            command.Parameters.Add(new NpgsqlParameter("required_columns", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                Value = RequiredColumns
            });
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            return count == RequiredColumns.Length ? OutboxSchemaStatus.Ready : OutboxSchemaStatus.NotReady;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            return OutboxSchemaStatus.DatabaseFailure;
        }
    }

    public async Task<OutboxClaimResult> ClaimAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        if (claim.BatchSize is < 50 or > 200) throw new ArgumentOutOfRangeException(nameof(claim), "Batch size must be between 50 and 200.");
        if (claim.LeaseId.Length is < 1 or > 64) throw new ArgumentException("Lease ID must contain between 1 and 64 characters.", nameof(claim));
        if (claim.LeasedUntil <= claim.Now) throw new ArgumentException("Lease expiry must be later than the claim time.", nameof(claim));

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                await using var command = CreateCommand(ClaimSql, connection, transaction);
                AddTimestamp(command, "now", claim.Now);
                AddTimestamp(command, "leased_until", claim.LeasedUntil);
                AddVarchar(command, "lease_id", claim.LeaseId, 64);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = claim.BatchSize });

                var events = new List<ClaimedOutboxEvent>(claim.BatchSize);
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        events.Add(new ClaimedOutboxEvent(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetFieldValue<DateTimeOffset>(5),
                            reader.GetInt32(6),
                            reader.GetString(7),
                            reader.GetFieldValue<DateTimeOffset>(8)));
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return events.Count == 0
                    ? OutboxClaimResult.NoEligibleEvents
                    : new OutboxClaimResult(OutboxRepositoryResult.Success, events);
            }
            catch
            {
                await RollbackSafelyAsync(transaction);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NpgsqlException)
        {
            return OutboxClaimResult.DatabaseFailure;
        }
    }

    public Task<OutboxRepositoryResult> RenewLeaseAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset now,
        DateTimeOffset leasedUntil,
        CancellationToken cancellationToken) =>
        ExecuteOwnedUpdateAsync(RenewLeaseSql, outboxEventId, leaseId, command =>
        {
            AddTimestamp(command, "now", now);
            AddTimestamp(command, "leased_until", leasedUntil);
        }, cancellationToken);

    public Task<OutboxRepositoryResult> CompleteAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken) =>
        ExecuteOwnedUpdateAsync(CompleteSql, outboxEventId, leaseId,
            command => AddTimestamp(command, "published_at", publishedAt), cancellationToken);

    public Task<OutboxRepositoryResult> ScheduleRetryAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset nextAttemptAt,
        OutboxFailure failure,
        CancellationToken cancellationToken) =>
        ExecuteOwnedUpdateAsync(ScheduleRetrySql, outboxEventId, leaseId, command =>
        {
            AddTimestamp(command, "next_attempt_at", nextAttemptAt);
            AddFailure(command, failure);
        }, cancellationToken);

    public Task<OutboxRepositoryResult> ExhaustAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset exhaustedAt,
        OutboxFailure failure,
        CancellationToken cancellationToken) =>
        ExecuteOwnedUpdateAsync(ExhaustSql, outboxEventId, leaseId, command =>
        {
            AddTimestamp(command, "exhausted_at", exhaustedAt);
            AddFailure(command, failure);
        }, cancellationToken);

    private async Task<OutboxRepositoryResult> ExecuteOwnedUpdateAsync(
        string sql,
        string outboxEventId,
        string leaseId,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = CreateCommand(sql, connection);
            AddVarchar(command, "outbox_event_id", outboxEventId, 64);
            AddVarchar(command, "lease_id", leaseId, 64);
            addParameters(command);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? OutboxRepositoryResult.Success
                : OutboxRepositoryResult.LeaseLost;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException)
        {
            return OutboxRepositoryResult.DatabaseFailure;
        }
    }

    private static NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction = null) =>
        new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value });

    private static void AddVarchar(NpgsqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar, size) { Value = value });

    private static void AddFailure(NpgsqlCommand command, OutboxFailure failure)
    {
        AddVarchar(command, "error_code", failure.ErrorCode, 64);
        AddVarchar(command, "safe_error_summary", failure.SafeSummary, 512);
    }

    private static async Task RollbackSafelyAsync(NpgsqlTransaction transaction)
    {
        try { await transaction.RollbackAsync(CancellationToken.None); }
        catch (InvalidOperationException) { }
        catch (NpgsqlException) { }
    }
}
