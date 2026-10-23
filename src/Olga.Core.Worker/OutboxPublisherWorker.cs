using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Olga.Core.Application;

namespace Olga.Core.Worker;

public sealed class OutboxPublisherWorker(
    IOutboxRepository repository,
    IOutboxEventPublisher publisher,
    IOutboxFailureClassifier failureClassifier,
    IOutboxRetryCalculator retryCalculator,
    IOptions<OutboxPublisherOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("Olga.Core.Worker.Outbox", "1.0");
    private static readonly Counter<long> ClaimCycles = Meter.CreateCounter<long>("outbox.claim.cycles");
    private static readonly Counter<long> ClaimedEvents = Meter.CreateCounter<long>("outbox.claimed");
    private static readonly Histogram<double> ClaimDuration = Meter.CreateHistogram<double>("outbox.claim.duration", "ms");
    private static readonly Counter<long> PublishAttempts = Meter.CreateCounter<long>("outbox.publish.attempts");
    private static readonly Counter<long> PublishSuccesses = Meter.CreateCounter<long>("outbox.publish.successes");
    private static readonly Counter<long> PublishFailures = Meter.CreateCounter<long>("outbox.publish.failures");
    private static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>("outbox.publish.duration", "ms");
    private static readonly Counter<long> RetriesScheduled = Meter.CreateCounter<long>("outbox.retry.scheduled");
    private static readonly Counter<long> ExhaustedEvents = Meter.CreateCounter<long>("outbox.exhausted");
    private static readonly Counter<long> LeasesRenewed = Meter.CreateCounter<long>("outbox.lease.renewed");
    private static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>("outbox.lease.lost");
    private static readonly Counter<long> ClaimFailures = Meter.CreateCounter<long>("outbox.claim.database_failures");
    private static readonly Counter<long> StateTransitionFailures = Meter.CreateCounter<long>("outbox.state_transition.failures");
    private readonly OutboxPublisherOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Outbox publisher is disabled");
            return;
        }

        if (!await WaitForSchemaAsync(stoppingToken)) return;

        var consecutiveClaimFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            ClaimCycleOutcome outcome;
            try
            {
                outcome = await ProcessClaimCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ClaimFailures.Add(1);
                logger.LogError(
                    "Outbox claim cycle stopped unexpectedly with failure type {FailureType}; it will be retried",
                    exception.GetType().Name);
                outcome = ClaimCycleOutcome.DatabaseFailure;
            }

            if (outcome == ClaimCycleOutcome.WorkProcessed)
            {
                consecutiveClaimFailures = 0;
                continue;
            }

            TimeSpan delay;
            if (outcome == ClaimCycleOutcome.DatabaseFailure)
            {
                consecutiveClaimFailures = Math.Min(consecutiveClaimFailures + 1, 31);
                var now = timeProvider.GetUtcNow();
                delay = retryCalculator.CalculateNextAttempt(consecutiveClaimFailures, now) - now;
            }
            else
            {
                consecutiveClaimFailures = 0;
                delay = settings.PollInterval;
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<ClaimCycleOutcome> ProcessClaimCycleAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        ClaimCycles.Add(1);
        logger.LogDebug("Outbox claim cycle started");

        var now = timeProvider.GetUtcNow();
        var claim = new OutboxClaim(
            Guid.NewGuid().ToString("N"),
            now,
            now.Add(settings.LeaseDuration),
            settings.BatchSize);
        var result = await repository.ClaimAsync(claim, cancellationToken);
        stopwatch.Stop();
        ClaimDuration.Record(stopwatch.Elapsed.TotalMilliseconds);

        if (result.Result == OutboxRepositoryResult.DatabaseFailure)
        {
            ClaimFailures.Add(1);
            logger.LogError("Outbox claim cycle failed because the database operation was unsuccessful");
            return ClaimCycleOutcome.DatabaseFailure;
        }

        if (result.Result != OutboxRepositoryResult.Success || result.Events.Count == 0)
        {
            logger.LogDebug("Outbox claim cycle completed with no eligible events in {DurationMs} ms", stopwatch.Elapsed.TotalMilliseconds);
            return ClaimCycleOutcome.NoWork;
        }

        ClaimedEvents.Add(result.Events.Count);
        logger.LogInformation(
            "Outbox claim cycle completed with {BatchCount} events in {DurationMs} ms",
            result.Events.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        await Parallel.ForEachAsync(
            result.Events,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = settings.MaximumPublishConcurrency,
                CancellationToken = cancellationToken
            },
            ProcessEventSafelyAsync);
        return ClaimCycleOutcome.WorkProcessed;
    }

    private async ValueTask ProcessEventSafelyAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessEventAsync(outboxEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StateTransitionFailures.Add(1);
            logger.LogError(
                "Outbox event {OutboxEventId} of type {EventType} stopped unexpectedly with failure type {FailureType}; its lease will expire for recovery",
                outboxEvent.OutboxEventId,
                outboxEvent.EventType,
                exception.GetType().Name);
        }
    }

    private async ValueTask ProcessEventAsync(ClaimedOutboxEvent outboxEvent, CancellationToken stoppingToken)
    {
        var leasedUntil = outboxEvent.LeasedUntil;
        var now = timeProvider.GetUtcNow();
        if (leasedUntil - now < settings.PublishTimeout + settings.LeaseSafetyMargin)
        {
            var renewedUntil = now.Add(settings.LeaseDuration);
            var renewal = await repository.RenewLeaseAsync(
                outboxEvent.OutboxEventId,
                outboxEvent.LeaseId,
                now,
                renewedUntil,
                stoppingToken);
            if (renewal != OutboxRepositoryResult.Success)
            {
                RecordTransitionFailure(outboxEvent, renewal, "renew");
                return;
            }

            leasedUntil = renewedUntil;
            LeasesRenewed.Add(1);
            logger.LogInformation(
                "Renewed lease for outbox event {OutboxEventId} of type {EventType}",
                outboxEvent.OutboxEventId,
                outboxEvent.EventType);
        }

        now = timeProvider.GetUtcNow();
        if (leasedUntil - now < settings.PublishTimeout + settings.LeaseSafetyMargin)
        {
            RecordTransitionFailure(outboxEvent, OutboxRepositoryResult.LeaseLost, "publish safety check");
            return;
        }

        PublishAttempts.Add(1, EventTypeTag(outboxEvent));
        logger.LogInformation(
            "Publishing outbox event {OutboxEventId} of type {EventType} on attempt {AttemptCount}",
            outboxEvent.OutboxEventId,
            outboxEvent.EventType,
            outboxEvent.AttemptCount);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(settings.PublishTimeout);

        try
        {
            await publisher.PublishAsync(outboxEvent, timeout.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await HandleFailureAsync(outboxEvent, new TimeoutException(), stoppingToken);
            return;
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(outboxEvent, exception, stoppingToken);
            return;
        }
        finally
        {
            stopwatch.Stop();
            PublishDuration.Record(stopwatch.Elapsed.TotalMilliseconds, EventTypeTag(outboxEvent));
        }

        PublishSuccesses.Add(1, EventTypeTag(outboxEvent));
        logger.LogInformation(
            "Published outbox event {OutboxEventId} of type {EventType} in {DurationMs} ms",
            outboxEvent.OutboxEventId,
            outboxEvent.EventType,
            stopwatch.Elapsed.TotalMilliseconds);

        var completion = await repository.CompleteAsync(
            outboxEvent.OutboxEventId,
            outboxEvent.LeaseId,
            timeProvider.GetUtcNow(),
            stoppingToken);
        if (completion == OutboxRepositoryResult.Success) return;

        if (completion == OutboxRepositoryResult.LeaseLost)
        {
            LeasesLost.Add(1);
            logger.LogWarning(
                "Completion affected zero rows for published outbox event {OutboxEventId}; lease ownership was lost and duplicate delivery is possible",
                outboxEvent.OutboxEventId);
        }
        else
        {
            StateTransitionFailures.Add(1);
            logger.LogError(
                "Completion failed for published outbox event {OutboxEventId}; the event remains recoverable and duplicate delivery is possible",
                outboxEvent.OutboxEventId);
        }
    }

    private async Task HandleFailureAsync(
        ClaimedOutboxEvent outboxEvent,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var failure = failureClassifier.Classify(exception, settings.MaximumErrorSummaryLength);
        PublishFailures.Add(1, ErrorCodeTag(failure));
        logger.LogWarning(
            "Publication failed for outbox event {OutboxEventId} of type {EventType} on attempt {AttemptCount} with code {ErrorCode}",
            outboxEvent.OutboxEventId,
            outboxEvent.EventType,
            outboxEvent.AttemptCount,
            failure.ErrorCode);

        if (!failure.IsRetryable || outboxEvent.AttemptCount >= settings.EffectiveMaxAttempts)
        {
            var exhausted = await repository.ExhaustAsync(
                outboxEvent.OutboxEventId,
                outboxEvent.LeaseId,
                timeProvider.GetUtcNow(),
                failure,
                cancellationToken);
            if (exhausted == OutboxRepositoryResult.Success)
            {
                ExhaustedEvents.Add(1, ErrorCodeTag(failure));
                logger.LogCritical(
                    "Outbox event {OutboxEventId} was exhausted on attempt {AttemptCount} with code {ErrorCode}",
                    outboxEvent.OutboxEventId,
                    outboxEvent.AttemptCount,
                    failure.ErrorCode);
            }
            else
            {
                RecordTransitionFailure(outboxEvent, exhausted, "exhaust");
            }
            return;
        }

        var nextAttemptAt = retryCalculator.CalculateNextAttempt(outboxEvent.AttemptCount, timeProvider.GetUtcNow());
        var retry = await repository.ScheduleRetryAsync(
            outboxEvent.OutboxEventId,
            outboxEvent.LeaseId,
            nextAttemptAt,
            failure,
            cancellationToken);
        if (retry == OutboxRepositoryResult.Success)
        {
            RetriesScheduled.Add(1, ErrorCodeTag(failure));
            logger.LogWarning(
                "Retry scheduled for outbox event {OutboxEventId} at {NextAttemptAt} with code {ErrorCode}",
                outboxEvent.OutboxEventId,
                nextAttemptAt,
                failure.ErrorCode);
        }
        else
        {
            RecordTransitionFailure(outboxEvent, retry, "schedule retry");
        }
    }

    private void RecordTransitionFailure(ClaimedOutboxEvent outboxEvent, OutboxRepositoryResult result, string operation)
    {
        if (result == OutboxRepositoryResult.LeaseLost)
        {
            LeasesLost.Add(1);
            logger.LogWarning(
                "Could not {LeaseOperation} outbox event {OutboxEventId}; lease ownership was lost and the event will not be published or modified by this worker",
                operation,
                outboxEvent.OutboxEventId);
        }
        else
        {
            StateTransitionFailures.Add(1);
            logger.LogError(
                "Could not {LeaseOperation} outbox event {OutboxEventId} because the database operation failed; the lease will expire for recovery",
                operation,
                outboxEvent.OutboxEventId);
        }
    }

    private async Task<bool> WaitForSchemaAsync(CancellationToken cancellationToken)
    {
        var loggedDatabaseFailure = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            OutboxSchemaStatus status;
            try
            {
                status = await repository.CheckSchemaAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                status = OutboxSchemaStatus.DatabaseFailure;
                if (!loggedDatabaseFailure)
                {
                    logger.LogError(
                        "Outbox publication is waiting because schema readiness failed with failure type {FailureType}",
                        exception.GetType().Name);
                    loggedDatabaseFailure = true;
                }
            }
            if (status == OutboxSchemaStatus.Ready)
            {
                return true;
            }

            if (status == OutboxSchemaStatus.NotReady)
            {
                logger.LogError("Outbox publication cannot start because database schema v2.6 is not ready");
                return false;
            }

            if (!loggedDatabaseFailure)
            {
                logger.LogError("Outbox publication is waiting because the schema readiness query failed");
                loggedDatabaseFailure = true;
            }
            var retryDelay = settings.PollInterval < TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : settings.PollInterval;
            await Task.Delay(retryDelay, timeProvider, cancellationToken);
        }
        return false;
    }

    private static KeyValuePair<string, object?> EventTypeTag(ClaimedOutboxEvent outboxEvent) =>
        new("event_type", outboxEvent.EventType);

    private static KeyValuePair<string, object?> ErrorCodeTag(OutboxFailure failure) =>
        new("error_code", failure.ErrorCode);

    private enum ClaimCycleOutcome
    {
        WorkProcessed,
        NoWork,
        DatabaseFailure
    }
}
