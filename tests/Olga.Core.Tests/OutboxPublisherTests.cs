using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;
using Olga.Core.Infrastructure;
using Olga.Core.Worker;
using Xunit.Sdk;

namespace Olga.Core.Tests;

public sealed class OutboxPublisherOptionsTests
{
    private readonly OutboxPublisherOptionsValidator validator = new();

    [Fact]
    public void Recommended_enabled_configuration_is_valid() =>
        Assert.True(validator.Validate(null, Valid()).Succeeded);

    [Theory]
    [InlineData(49)]
    [InlineData(201)]
    public void Batch_size_outside_supported_range_is_rejected(int batchSize) =>
        Assert.False(validator.Validate(null, Valid() with { BatchSize = batchSize }).Succeeded);

    [Fact]
    public void Invalid_lease_and_publish_windows_are_rejected()
    {
        Assert.False(validator.Validate(null, Valid() with { LeaseDuration = TimeSpan.Zero }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { PublishTimeout = TimeSpan.FromMinutes(5) }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { LeaseSafetyMargin = TimeSpan.FromMinutes(5) }).Succeeded);
    }

    [Fact]
    public void Invalid_retry_delays_and_concurrency_are_rejected()
    {
        Assert.False(validator.Validate(null, Valid() with { InitialRetryDelay = TimeSpan.Zero }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { MaximumRetryDelay = TimeSpan.FromSeconds(1) }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { MaximumPublishConcurrency = 0 }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { MaximumPublishConcurrency = 33 }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { RetryJitterPercentage = 51 }).Succeeded);
        Assert.False(validator.Validate(null, Valid() with { MaximumErrorSummaryLength = 513 }).Succeeded);
    }

    [Fact]
    public void Disabled_configuration_does_not_require_publisher_values_to_validate()
    {
        var options = new OutboxPublisherOptions
        {
            Enabled = false,
            BatchSize = 0,
            LeaseDuration = TimeSpan.Zero,
            PublishTimeout = TimeSpan.Zero
        };

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    private static OutboxPublisherOptions Valid() => new();
}

public sealed class OutboxRepositorySqlTests
{
    [Fact]
    public void Claim_sql_uses_atomic_skip_locked_leasing_filters_and_ordering()
    {
        var sql = NpgsqlOutboxRepository.ClaimSql;

        Assert.Contains("FOR UPDATE SKIP LOCKED", sql, StringComparison.Ordinal);
        Assert.Contains("published_at IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("exhausted_at IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("next_attempt_at IS NULL OR next_attempt_at <= @now", sql, StringComparison.Ordinal);
        Assert.Contains("leased_until IS NULL OR leased_until <= @now", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY occurred_at, outbox_event_id", sql, StringComparison.Ordinal);
        Assert.Contains("attempt_count = event.attempt_count + 1", sql, StringComparison.Ordinal);
        Assert.Contains("lease_id = @lease_id", sql, StringComparison.Ordinal);
        Assert.Contains("leased_until = @leased_until", sql, StringComparison.Ordinal);
        Assert.Contains("last_attempt_at = @now", sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(OwnedUpdateSql))]
    public void Every_state_update_is_fenced_by_event_and_lease(string sql)
    {
        Assert.Contains("outbox_event_id = @outbox_event_id", sql, StringComparison.Ordinal);
        Assert.Contains("lease_id = @lease_id", sql, StringComparison.Ordinal);
        Assert.Contains("published_at IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("exhausted_at IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Renewal_cannot_revive_an_expired_lease() =>
        Assert.Contains("leased_until > @now", NpgsqlOutboxRepository.RenewLeaseSql, StringComparison.Ordinal);

    [Fact]
    public void Schema_readiness_query_requires_every_v2_6_column()
    {
        foreach (var column in new[]
                 {
                     "lease_id", "leased_until", "last_attempt_at", "exhausted_at",
                     "last_error_code", "last_error_summary", "row_version"
                 })
            Assert.Contains(column, NpgsqlOutboxRepository.RequiredSchemaColumns);
    }

    [Fact]
    public void Completion_and_failure_transitions_clear_lease_state()
    {
        Assert.Contains("published_at = @published_at", NpgsqlOutboxRepository.CompleteSql, StringComparison.Ordinal);
        Assert.Contains("next_attempt_at = @next_attempt_at", NpgsqlOutboxRepository.ScheduleRetrySql, StringComparison.Ordinal);
        Assert.Contains("exhausted_at = @exhausted_at", NpgsqlOutboxRepository.ExhaustSql, StringComparison.Ordinal);
        Assert.All(
            new[] { NpgsqlOutboxRepository.CompleteSql, NpgsqlOutboxRepository.ScheduleRetrySql, NpgsqlOutboxRepository.ExhaustSql },
            sql =>
            {
                Assert.Contains("lease_id = NULL", sql, StringComparison.Ordinal);
                Assert.Contains("leased_until = NULL", sql, StringComparison.Ordinal);
            });
    }

    public static TheoryData<string> OwnedUpdateSql => new()
    {
        NpgsqlOutboxRepository.RenewLeaseSql,
        NpgsqlOutboxRepository.CompleteSql,
        NpgsqlOutboxRepository.ScheduleRetrySql,
        NpgsqlOutboxRepository.ExhaustSql
    };
}

public sealed class OutboxFailureHandlingTests
{
    private readonly OutboxFailureClassifier classifier = new();

    [Fact]
    public void Known_transient_and_terminal_failures_are_classified_explicitly()
    {
        var transient = classifier.Classify(
            new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy, null, null),
            512);
        var terminal = classifier.Classify(new JsonException("payload content"), 512);

        Assert.True(transient.IsRetryable);
        Assert.Equal("BROKER_THROTTLED", transient.ErrorCode);
        Assert.False(terminal.IsRetryable);
        Assert.Equal("INVALID_EVENT_PAYLOAD", terminal.ErrorCode);
    }

    [Fact]
    public void Unexpected_exception_details_are_not_persisted()
    {
        var failure = classifier.Classify(
            new Exception("Host=secret;Password=hunter2;token=abc\nprivate payload"),
            64);

        Assert.Equal("UNEXPECTED_PUBLISH_FAILURE", failure.ErrorCode);
        Assert.True(failure.IsRetryable);
        Assert.DoesNotContain("secret", failure.SafeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", failure.SafeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', failure.SafeSummary);
        Assert.InRange(failure.SafeSummary.Length, 1, 64);
    }

    [Fact]
    public void Sanitizer_removes_control_characters_and_respects_database_bound()
    {
        var summary = OutboxFailureClassifier.Sanitize(new string('a', 600) + "\r\n", 512);

        Assert.Equal(512, summary.Length);
        Assert.DoesNotContain('\r', summary);
        Assert.DoesNotContain('\n', summary);
    }

    [Fact]
    public void Retry_backoff_grows_caps_and_has_bounded_jitter()
    {
        var settings = new OutboxPublisherOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(10),
            MaximumRetryDelay = TimeSpan.FromSeconds(40),
            RetryJitterPercentage = 20
        };
        var low = new OutboxRetryCalculator(Options.Create(settings), new FixedJitter(0));
        var high = new OutboxRetryCalculator(Options.Create(settings), new FixedJitter(1));
        var now = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(8), low.CalculateNextAttempt(1, now) - now);
        Assert.Equal(TimeSpan.FromSeconds(24), high.CalculateNextAttempt(2, now) - now);
        Assert.Equal(TimeSpan.FromSeconds(40), high.CalculateNextAttempt(20, now) - now);
    }

    private sealed class FixedJitter(double value) : IOutboxJitterSource
    {
        public double NextUnit() => value;
    }
}

public sealed class ServiceBusOutboxEventPublisherTests
{
    [Fact]
    public void Message_preserves_identity_type_and_json_payload()
    {
        var claimed = new ClaimedOutboxEvent(
            "event-123",
            "MEMBER",
            "member-456",
            "MemberProfileChanged.v1",
            "{\"display_name\":\"Ada\"}",
            new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.Zero),
            1,
            "lease",
            new DateTimeOffset(2026, 10, 23, 1, 7, 3, TimeSpan.Zero));

        var message = ServiceBusOutboxEventPublisher.CreateMessage(claimed);
        using var body = JsonDocument.Parse(message.Body.ToMemory());

        Assert.Equal(claimed.OutboxEventId, message.MessageId);
        Assert.Equal(claimed.EventType, message.Subject);
        Assert.Equal("application/json", message.ContentType);
        Assert.Equal(claimed.EventType, body.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("Ada", body.RootElement.GetProperty("data").GetProperty("display_name").GetString());
    }
}

public sealed class DisabledOutboxPublisherTests
{
    [Fact]
    public async Task Disabled_worker_does_not_access_repository_or_publisher()
    {
        var dependency = new CountingDependency();
        var worker = new OutboxPublisherWorker(
            dependency,
            dependency,
            new OutboxFailureClassifier(),
            new OutboxRetryCalculator(Options.Create(new OutboxPublisherOptions { Enabled = false }), new NoJitter()),
            Options.Create(new OutboxPublisherOptions { Enabled = false }),
            TimeProvider.System,
            NullLogger<OutboxPublisherWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, dependency.RepositoryCalls);
        Assert.Equal(0, dependency.PublishCalls);
    }

    private sealed class NoJitter : IOutboxJitterSource
    {
        public double NextUnit() => 0.5;
    }

    private sealed class CountingDependency : IOutboxRepository, IOutboxEventPublisher
    {
        public int RepositoryCalls { get; private set; }
        public int PublishCalls { get; private set; }

        public Task<OutboxSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken)
        {
            RepositoryCalls++;
            return Task.FromResult(OutboxSchemaStatus.Ready);
        }

        public Task<OutboxClaimResult> ClaimAsync(OutboxClaim claim, CancellationToken cancellationToken)
        {
            RepositoryCalls++;
            return Task.FromResult(OutboxClaimResult.NoEligibleEvents);
        }

        public Task<OutboxRepositoryResult> RenewLeaseAsync(string outboxEventId, string leaseId, DateTimeOffset now, DateTimeOffset leasedUntil, CancellationToken cancellationToken) => Result();
        public Task<OutboxRepositoryResult> CompleteAsync(string outboxEventId, string leaseId, DateTimeOffset publishedAt, CancellationToken cancellationToken) => Result();
        public Task<OutboxRepositoryResult> ScheduleRetryAsync(string outboxEventId, string leaseId, DateTimeOffset nextAttemptAt, OutboxFailure failure, CancellationToken cancellationToken) => Result();
        public Task<OutboxRepositoryResult> ExhaustAsync(string outboxEventId, string leaseId, DateTimeOffset exhaustedAt, OutboxFailure failure, CancellationToken cancellationToken) => Result();

        public Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
        {
            PublishCalls++;
            return Task.CompletedTask;
        }

        private Task<OutboxRepositoryResult> Result()
        {
            RepositoryCalls++;
            return Task.FromResult(OutboxRepositoryResult.Success);
        }
    }
}

public sealed class OutboxWorkerLifecycleTests
{
    [Fact]
    public async Task Publication_begins_only_after_claim_commit_and_completion_uses_claim_lease()
    {
        var repository = new LifecycleRepository(OutboxRepositoryResult.Success, TimeSpan.FromMinutes(5));
        var publisher = new RecordingPublisher(repository);
        var worker = CreateWorker(repository, publisher);

        await worker.StartAsync(CancellationToken.None);
        await repository.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, publisher.PublishWhileClaimActive);
        Assert.Equal("lease-123", repository.CompletedLeaseId);
        Assert.True(repository.Order.IndexOf("claim-commit") < repository.Order.IndexOf("publish"));
        Assert.True(repository.Order.IndexOf("publish") < repository.Order.IndexOf("complete"));
    }

    [Fact]
    public async Task Event_is_not_published_when_lease_renewal_loses_ownership()
    {
        var repository = new LifecycleRepository(OutboxRepositoryResult.LeaseLost, TimeSpan.FromSeconds(1));
        var publisher = new RecordingPublisher(repository);
        var worker = CreateWorker(repository, publisher);

        await worker.StartAsync(CancellationToken.None);
        await repository.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, publisher.PublishCalls);
        Assert.Equal(0, repository.CompleteCalls);
    }

    private static OutboxPublisherWorker CreateWorker(
        LifecycleRepository repository,
        RecordingPublisher publisher) =>
        new(
            repository,
            publisher,
            new OutboxFailureClassifier(),
            new OutboxRetryCalculator(Options.Create(new OutboxPublisherOptions()), new NoJitter()),
            Options.Create(new OutboxPublisherOptions()),
            TimeProvider.System,
            NullLogger<OutboxPublisherWorker>.Instance);

    private sealed class NoJitter : IOutboxJitterSource
    {
        public double NextUnit() => 0.5;
    }

    private sealed class RecordingPublisher(LifecycleRepository repository) : IOutboxEventPublisher
    {
        public int PublishCalls { get; private set; }
        public int PublishWhileClaimActive { get; private set; }

        public Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
        {
            PublishCalls++;
            if (repository.ClaimActive) PublishWhileClaimActive++;
            repository.Order.Add("publish");
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleRepository(
        OutboxRepositoryResult renewalResult,
        TimeSpan initialLeaseRemaining) : IOutboxRepository
    {
        private int claims;
        public bool ClaimActive { get; private set; }
        public List<string> Order { get; } = [];
        public int CompleteCalls { get; private set; }
        public string? CompletedLeaseId { get; private set; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OutboxSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OutboxSchemaStatus.Ready);

        public async Task<OutboxClaimResult> ClaimAsync(OutboxClaim claim, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref claims) > 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return OutboxClaimResult.NoEligibleEvents;
            }

            ClaimActive = true;
            Order.Add("claim-start");
            await Task.Yield();
            ClaimActive = false;
            Order.Add("claim-commit");
            var claimed = new ClaimedOutboxEvent(
                "event-123", "MEMBER", "member-456", "MemberProfileChanged.v1", "{}",
                DateTimeOffset.UtcNow, 1, "lease-123", DateTimeOffset.UtcNow.Add(initialLeaseRemaining));
            return new OutboxClaimResult(OutboxRepositoryResult.Success, new[] { claimed });
        }

        public Task<OutboxRepositoryResult> RenewLeaseAsync(string outboxEventId, string leaseId, DateTimeOffset now, DateTimeOffset leasedUntil, CancellationToken cancellationToken)
        {
            Renewed.TrySetResult();
            return Task.FromResult(renewalResult);
        }

        public Task<OutboxRepositoryResult> CompleteAsync(string outboxEventId, string leaseId, DateTimeOffset publishedAt, CancellationToken cancellationToken)
        {
            CompleteCalls++;
            CompletedLeaseId = leaseId;
            Order.Add("complete");
            Completed.TrySetResult();
            return Task.FromResult(OutboxRepositoryResult.Success);
        }

        public Task<OutboxRepositoryResult> ScheduleRetryAsync(string outboxEventId, string leaseId, DateTimeOffset nextAttemptAt, OutboxFailure failure, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);

        public Task<OutboxRepositoryResult> ExhaustAsync(string outboxEventId, string leaseId, DateTimeOffset exhaustedAt, OutboxFailure failure, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);
    }
}

public sealed class OutboxWorkerFailureTests
{
    [Fact]
    public async Task Unexpected_claim_exception_is_backed_off_and_retried_without_stopping_worker()
    {
        var repository = new RecoveringRepository();
        var publisher = new SignalingPublisher();
        var settings = new OutboxPublisherOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            InitialRetryDelay = TimeSpan.FromMilliseconds(1),
            MaximumRetryDelay = TimeSpan.FromMilliseconds(1),
            RetryJitterPercentage = 0
        };
        var worker = new OutboxPublisherWorker(
            repository,
            publisher,
            new OutboxFailureClassifier(),
            new OutboxRetryCalculator(Options.Create(settings), new NoJitter()),
            Options.Create(settings),
            TimeProvider.System,
            NullLogger<OutboxPublisherWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(repository.ClaimCalls >= 2);
    }

    [Fact]
    public async Task Transient_failure_schedules_retry_and_clears_owned_lease_through_repository()
    {
        var repository = new FailureRepository(1);
        var worker = CreateWorker(repository, new ThrowingPublisher(new TimeoutException()));

        await worker.StartAsync(CancellationToken.None);
        await repository.RetryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("lease-123", repository.TransitionLeaseId);
        Assert.Equal("PUBLISH_TIMEOUT", repository.Failure?.ErrorCode);
        Assert.True(repository.NextAttemptAt > repository.ClaimedAt);
        Assert.Equal(0, repository.ExhaustCalls);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(1, false)]
    public async Task Max_attempts_or_terminal_failure_exhausts_event(int attemptCount, bool transient)
    {
        var repository = new FailureRepository(attemptCount);
        Exception exception = transient ? new TimeoutException() : new JsonException();
        var worker = CreateWorker(repository, new ThrowingPublisher(exception));

        await worker.StartAsync(CancellationToken.None);
        await repository.Exhausted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("lease-123", repository.TransitionLeaseId);
        Assert.Equal(1, repository.ExhaustCalls);
        Assert.Equal(0, repository.RetryCalls);
    }

    [Fact]
    public async Task Missing_v2_6_schema_stops_before_claiming()
    {
        var repository = new FailureRepository(1) { SchemaStatus = OutboxSchemaStatus.NotReady };
        var worker = CreateWorker(repository, new ThrowingPublisher(new Exception()));

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, repository.ClaimCalls);
    }

    private static OutboxPublisherWorker CreateWorker(
        FailureRepository repository,
        IOutboxEventPublisher publisher) =>
        new(
            repository,
            publisher,
            new OutboxFailureClassifier(),
            new OutboxRetryCalculator(Options.Create(new OutboxPublisherOptions()), new NoJitter()),
            Options.Create(new OutboxPublisherOptions()),
            TimeProvider.System,
            NullLogger<OutboxPublisherWorker>.Instance);

    private sealed class NoJitter : IOutboxJitterSource
    {
        public double NextUnit() => 0.5;
    }

    private sealed class ThrowingPublisher(Exception exception) : IOutboxEventPublisher
    {
        public Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class SignalingPublisher : IOutboxEventPublisher
    {
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
        {
            Published.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveringRepository : IOutboxRepository
    {
        public int ClaimCalls { get; private set; }

        public Task<OutboxSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OutboxSchemaStatus.Ready);

        public async Task<OutboxClaimResult> ClaimAsync(OutboxClaim claim, CancellationToken cancellationToken)
        {
            ClaimCalls++;
            if (ClaimCalls == 1) throw new InvalidOperationException("Synthetic claim failure.");
            if (ClaimCalls > 2)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return OutboxClaimResult.NoEligibleEvents;
            }

            var claimed = new ClaimedOutboxEvent(
                "event-123", "MEMBER", "member-456", "MemberProfileChanged.v1", "{}",
                claim.Now, 1, claim.LeaseId, claim.LeasedUntil);
            return new OutboxClaimResult(OutboxRepositoryResult.Success, new[] { claimed });
        }

        public Task<OutboxRepositoryResult> RenewLeaseAsync(string outboxEventId, string leaseId, DateTimeOffset now, DateTimeOffset leasedUntil, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);

        public Task<OutboxRepositoryResult> CompleteAsync(string outboxEventId, string leaseId, DateTimeOffset publishedAt, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);

        public Task<OutboxRepositoryResult> ScheduleRetryAsync(string outboxEventId, string leaseId, DateTimeOffset nextAttemptAt, OutboxFailure failure, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);

        public Task<OutboxRepositoryResult> ExhaustAsync(string outboxEventId, string leaseId, DateTimeOffset exhaustedAt, OutboxFailure failure, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);
    }

    private sealed class FailureRepository(int attemptCount) : IOutboxRepository
    {
        private int claims;
        public OutboxSchemaStatus SchemaStatus { get; init; } = OutboxSchemaStatus.Ready;
        public int ClaimCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int ExhaustCalls { get; private set; }
        public string? TransitionLeaseId { get; private set; }
        public OutboxFailure? Failure { get; private set; }
        public DateTimeOffset ClaimedAt { get; private set; }
        public DateTimeOffset NextAttemptAt { get; private set; }
        public TaskCompletionSource RetryScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exhausted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OutboxSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SchemaStatus);

        public async Task<OutboxClaimResult> ClaimAsync(OutboxClaim claim, CancellationToken cancellationToken)
        {
            ClaimCalls++;
            if (Interlocked.Increment(ref claims) > 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return OutboxClaimResult.NoEligibleEvents;
            }

            ClaimedAt = claim.Now;
            var claimed = new ClaimedOutboxEvent(
                "event-123", "MEMBER", "member-456", "MemberProfileChanged.v1", "{}",
                claim.Now, attemptCount, "lease-123", claim.LeasedUntil);
            return new OutboxClaimResult(OutboxRepositoryResult.Success, new[] { claimed });
        }

        public Task<OutboxRepositoryResult> RenewLeaseAsync(string outboxEventId, string leaseId, DateTimeOffset now, DateTimeOffset leasedUntil, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);

        public Task<OutboxRepositoryResult> CompleteAsync(string outboxEventId, string leaseId, DateTimeOffset publishedAt, CancellationToken cancellationToken) =>
            Task.FromResult(OutboxRepositoryResult.Success);

        public Task<OutboxRepositoryResult> ScheduleRetryAsync(string outboxEventId, string leaseId, DateTimeOffset nextAttemptAt, OutboxFailure failure, CancellationToken cancellationToken)
        {
            RetryCalls++;
            TransitionLeaseId = leaseId;
            Failure = failure;
            NextAttemptAt = nextAttemptAt;
            RetryScheduled.TrySetResult();
            return Task.FromResult(OutboxRepositoryResult.Success);
        }

        public Task<OutboxRepositoryResult> ExhaustAsync(string outboxEventId, string leaseId, DateTimeOffset exhaustedAt, OutboxFailure failure, CancellationToken cancellationToken)
        {
            ExhaustCalls++;
            TransitionLeaseId = leaseId;
            Failure = failure;
            Exhausted.TrySetResult();
            return Task.FromResult(OutboxRepositoryResult.Success);
        }
    }
}

public sealed class OutboxPostgreSqlIntegrationTests
{
    [Fact]
    public async Task Active_claim_is_exclusive_and_stale_owner_is_fenced_after_reclaim()
    {
        var connectionString = Environment.GetEnvironmentVariable("OLGA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw SkipException.ForSkip("Set OLGA_TEST_POSTGRES to a disposable PostgreSQL 17 database with migration 004 applied.");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var repositoryA = new NpgsqlOutboxRepository(dataSource);
        var repositoryB = new NpgsqlOutboxRepository(dataSource);
        var eventId = Guid.NewGuid().ToString("N");
        await InsertEventAsync(dataSource, eventId);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var claims = await Task.WhenAll(
                repositoryA.ClaimAsync(new OutboxClaim("lease-a", now, now.AddMinutes(5), 50), CancellationToken.None),
                repositoryB.ClaimAsync(new OutboxClaim("lease-b", now, now.AddMinutes(5), 50), CancellationToken.None));
            var originalClaim = Assert.Single(
                claims.SelectMany(x => x.Events),
                x => x.OutboxEventId == eventId);

            await ExpireLeaseAsync(dataSource, eventId, originalClaim.LeaseId, now.AddSeconds(-1));
            var reclaimed = await repositoryB.ClaimAsync(
                new OutboxClaim("lease-c", now, now.AddMinutes(5), 50),
                CancellationToken.None);
            var currentClaim = Assert.Single(reclaimed.Events, x => x.OutboxEventId == eventId);
            Assert.Equal("lease-c", currentClaim.LeaseId);

            var failure = new OutboxFailure(
                "TEST_FAILURE",
                OutboxFailureClassification.Retryable,
                "Test failure.");
            Assert.Equal(
                OutboxRepositoryResult.LeaseLost,
                await repositoryA.RenewLeaseAsync(eventId, originalClaim.LeaseId, now, now.AddMinutes(5), CancellationToken.None));
            Assert.Equal(
                OutboxRepositoryResult.LeaseLost,
                await repositoryA.CompleteAsync(eventId, originalClaim.LeaseId, now, CancellationToken.None));
            Assert.Equal(
                OutboxRepositoryResult.LeaseLost,
                await repositoryA.ScheduleRetryAsync(eventId, originalClaim.LeaseId, now.AddMinutes(1), failure, CancellationToken.None));
            Assert.Equal(
                OutboxRepositoryResult.LeaseLost,
                await repositoryA.ExhaustAsync(eventId, originalClaim.LeaseId, now, failure, CancellationToken.None));
        }
        finally
        {
            await DeleteEventAsync(dataSource, eventId);
        }
    }

    private static async Task InsertEventAsync(NpgsqlDataSource dataSource, string eventId)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO ops.outbox_event (
                outbox_event_id, aggregate_type, aggregate_id, event_type, payload_json, occurred_at)
            VALUES (@outbox_event_id, @aggregate_type, @aggregate_id, @event_type, @payload_json, @occurred_at);
            """);
        command.Parameters.Add(new NpgsqlParameter("outbox_event_id", NpgsqlDbType.Varchar, 64) { Value = eventId });
        command.Parameters.Add(new NpgsqlParameter("aggregate_type", NpgsqlDbType.Varchar, 64) { Value = "OUTBOX_TEST" });
        command.Parameters.Add(new NpgsqlParameter("aggregate_id", NpgsqlDbType.Varchar, 64) { Value = eventId });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Varchar, 128) { Value = "OutboxLeaseTest.v1" });
        command.Parameters.Add(new NpgsqlParameter("payload_json", NpgsqlDbType.Jsonb) { Value = "{}" });
        command.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow.AddYears(-10) });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ExpireLeaseAsync(
        NpgsqlDataSource dataSource,
        string eventId,
        string leaseId,
        DateTimeOffset leasedUntil)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE ops.outbox_event
            SET leased_until = @leased_until
            WHERE outbox_event_id = @outbox_event_id
              AND lease_id = @lease_id;
            """);
        command.Parameters.Add(new NpgsqlParameter("leased_until", NpgsqlDbType.TimestampTz) { Value = leasedUntil });
        command.Parameters.Add(new NpgsqlParameter("outbox_event_id", NpgsqlDbType.Varchar, 64) { Value = eventId });
        command.Parameters.Add(new NpgsqlParameter("lease_id", NpgsqlDbType.Varchar, 64) { Value = leaseId });
        Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async Task DeleteEventAsync(NpgsqlDataSource dataSource, string eventId)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM ops.outbox_event
            WHERE outbox_event_id = @outbox_event_id;
            """);
        command.Parameters.Add(new NpgsqlParameter("outbox_event_id", NpgsqlDbType.Varchar, 64) { Value = eventId });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
