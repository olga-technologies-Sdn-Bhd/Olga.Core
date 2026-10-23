using System.Net.Http;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using Olga.Core.Application;

namespace Olga.Core.Worker;

public sealed class OutboxFailureClassifier : IOutboxFailureClassifier
{
    public OutboxFailure Classify(Exception exception, int maximumSummaryLength)
    {
        var failure = exception switch
        {
            ServiceBusException { Reason: ServiceBusFailureReason.MessageSizeExceeded } =>
                Create("PAYLOAD_TOO_LARGE", false, "The broker rejected the event because it exceeds the allowed message size."),
            ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityNotFound } =>
                Create("BROKER_ENTITY_NOT_FOUND", false, "The configured broker entity does not exist."),
            ServiceBusException { Reason: ServiceBusFailureReason.MessagingEntityDisabled } =>
                Create("BROKER_ENTITY_DISABLED", false, "The configured broker entity is disabled."),
            ServiceBusException { Reason: ServiceBusFailureReason.ServiceBusy } =>
                Create("BROKER_THROTTLED", true, "The broker is temporarily throttling requests."),
            ServiceBusException { Reason: ServiceBusFailureReason.ServiceTimeout } =>
                Create("BROKER_TIMEOUT", true, "The broker operation timed out."),
            ServiceBusException { Reason: ServiceBusFailureReason.ServiceCommunicationProblem } =>
                Create("BROKER_UNAVAILABLE", true, "The broker is temporarily unavailable."),
            ServiceBusException { IsTransient: true } =>
                Create("BROKER_TRANSIENT", true, "The broker reported a transient failure."),
            AuthenticationFailedException or UnauthorizedAccessException =>
                Create("BROKER_AUTHENTICATION_FAILED", false, "Broker authentication or authorization failed."),
            HttpRequestException =>
                Create("NETWORK_FAILURE", true, "A temporary network failure prevented publication."),
            TimeoutException =>
                Create("PUBLISH_TIMEOUT", true, "The event publish operation timed out."),
            JsonException or InvalidDataException =>
                Create("INVALID_EVENT_PAYLOAD", false, "The stored event payload is not valid for publication."),
            NotSupportedException =>
                Create("UNSUPPORTED_EVENT_TYPE", false, "The event type is not supported by the configured publisher."),
            _ => Create("UNEXPECTED_PUBLISH_FAILURE", true, "An unexpected publication failure occurred.")
        };

        return failure with { SafeSummary = Sanitize(failure.SafeSummary, maximumSummaryLength) };
    }

    public static string Sanitize(string? summary, int maximumLength)
    {
        if (maximumLength is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (string.IsNullOrWhiteSpace(summary)) return "Publication failed."[..Math.Min(19, maximumLength)];

        var builder = new StringBuilder(Math.Min(summary.Length, maximumLength));
        foreach (var character in summary)
        {
            if (builder.Length == maximumLength) break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? "Publication failed."[..Math.Min(19, maximumLength)] : sanitized;
    }

    private static OutboxFailure Create(string code, bool retryable, string summary) =>
        new(
            code,
            retryable ? OutboxFailureClassification.Retryable : OutboxFailureClassification.NonRetryable,
            summary);
}

public interface IOutboxJitterSource
{
    double NextUnit();
}

public sealed class RandomOutboxJitterSource : IOutboxJitterSource
{
    public double NextUnit() => Random.Shared.NextDouble();
}

public sealed class OutboxRetryCalculator(
    IOptions<OutboxPublisherOptions> options,
    IOutboxJitterSource jitterSource) : IOutboxRetryCalculator
{
    private readonly OutboxPublisherOptions settings = options.Value;

    public DateTimeOffset CalculateNextAttempt(int attemptCount, DateTimeOffset now)
    {
        if (attemptCount <= 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));

        var maximumTicks = settings.MaximumRetryDelay.Ticks;
        var delayTicks = Math.Min(settings.InitialRetryDelay.Ticks, maximumTicks);
        for (var exponent = 1; exponent < attemptCount && delayTicks < maximumTicks; exponent++)
        {
            delayTicks = delayTicks > maximumTicks / 2 ? maximumTicks : Math.Min(delayTicks * 2, maximumTicks);
        }

        if (settings.RetryJitterPercentage > 0)
        {
            var fraction = settings.RetryJitterPercentage / 100d;
            var factor = 1d + ((jitterSource.NextUnit() * 2d - 1d) * fraction);
            delayTicks = Math.Clamp((long)(delayTicks * factor), 1, maximumTicks);
        }

        return now.AddTicks(Math.Max(1, delayTicks));
    }
}
