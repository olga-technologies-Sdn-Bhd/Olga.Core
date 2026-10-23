namespace Olga.Core.Application;

public sealed record ClaimedOutboxEvent(
    string OutboxEventId,
    string AggregateType,
    string AggregateId,
    string EventType,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    int AttemptCount,
    string LeaseId,
    DateTimeOffset LeasedUntil);

public sealed record OutboxClaim(
    string LeaseId,
    DateTimeOffset Now,
    DateTimeOffset LeasedUntil,
    int BatchSize);

public enum OutboxRepositoryResult
{
    Success,
    LeaseLost,
    NoEligibleEvents,
    DatabaseFailure
}

public enum OutboxSchemaStatus
{
    Ready,
    NotReady,
    DatabaseFailure
}

public sealed record OutboxClaimResult(
    OutboxRepositoryResult Result,
    IReadOnlyList<ClaimedOutboxEvent> Events)
{
    public static OutboxClaimResult NoEligibleEvents { get; } =
        new(OutboxRepositoryResult.NoEligibleEvents, Array.Empty<ClaimedOutboxEvent>());

    public static OutboxClaimResult DatabaseFailure { get; } =
        new(OutboxRepositoryResult.DatabaseFailure, Array.Empty<ClaimedOutboxEvent>());
}

public enum OutboxFailureClassification
{
    Retryable,
    NonRetryable
}

public sealed record OutboxFailure(
    string ErrorCode,
    OutboxFailureClassification Classification,
    string SafeSummary)
{
    public bool IsRetryable => Classification == OutboxFailureClassification.Retryable;
}

public interface IOutboxRepository
{
    Task<OutboxSchemaStatus> CheckSchemaAsync(CancellationToken cancellationToken);
    Task<OutboxClaimResult> ClaimAsync(OutboxClaim claim, CancellationToken cancellationToken);
    Task<OutboxRepositoryResult> RenewLeaseAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset now,
        DateTimeOffset leasedUntil,
        CancellationToken cancellationToken);
    Task<OutboxRepositoryResult> CompleteAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken);
    Task<OutboxRepositoryResult> ScheduleRetryAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset nextAttemptAt,
        OutboxFailure failure,
        CancellationToken cancellationToken);
    Task<OutboxRepositoryResult> ExhaustAsync(
        string outboxEventId,
        string leaseId,
        DateTimeOffset exhaustedAt,
        OutboxFailure failure,
        CancellationToken cancellationToken);
}

public interface IOutboxEventPublisher
{
    Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken);
}

public interface IOutboxFailureClassifier
{
    OutboxFailure Classify(Exception exception, int maximumSummaryLength);
}

public interface IOutboxRetryCalculator
{
    DateTimeOffset CalculateNextAttempt(int attemptCount, DateTimeOffset now);
}
