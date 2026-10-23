using Microsoft.Extensions.Options;

namespace Olga.Core.Worker;

public sealed record OutboxPublisherOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; init; } = true;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; init; } = 50;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan LeaseSafetyMargin { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxAttempts { get; init; } = 10;
    // Compatibility for deployments that still supply the original setting name.
    public int? MaxPublishAttempts { get; init; }
    public int EffectiveMaxAttempts => MaxPublishAttempts ?? MaxAttempts;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(30);
    public int RetryJitterPercentage { get; init; } = 20;
    public int MaximumPublishConcurrency { get; init; } = 8;
    public int MaximumErrorSummaryLength { get; init; } = 512;
}

public sealed class OutboxPublisherOptionsValidator : IValidateOptions<OutboxPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxPublisherOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (options.BatchSize is < 50 or > 200) failures.Add("Outbox:BatchSize must be between 50 and 200.");
        if (options.PollInterval <= TimeSpan.Zero) failures.Add("Outbox:PollInterval must be positive.");
        if (options.LeaseDuration <= TimeSpan.Zero || options.LeaseDuration > TimeSpan.FromHours(1))
            failures.Add("Outbox:LeaseDuration must be positive and no greater than one hour.");
        if (options.PublishTimeout <= TimeSpan.Zero) failures.Add("Outbox:PublishTimeout must be positive.");
        if (options.PublishTimeout >= options.LeaseDuration)
            failures.Add("Outbox:PublishTimeout must be shorter than Outbox:LeaseDuration.");
        if (options.LeaseSafetyMargin <= TimeSpan.Zero || options.LeaseSafetyMargin >= options.LeaseDuration)
            failures.Add("Outbox:LeaseSafetyMargin must be positive and shorter than Outbox:LeaseDuration.");
        if (options.PublishTimeout > TimeSpan.Zero
            && options.PublishTimeout < options.LeaseDuration
            && options.LeaseSafetyMargin > TimeSpan.Zero
            && options.LeaseSafetyMargin < options.LeaseDuration
            && options.PublishTimeout >= options.LeaseDuration - options.LeaseSafetyMargin)
            failures.Add("Outbox:PublishTimeout plus Outbox:LeaseSafetyMargin must be shorter than Outbox:LeaseDuration.");
        if (options.EffectiveMaxAttempts <= 0) failures.Add("Outbox:MaxAttempts must be greater than zero.");
        if (options.InitialRetryDelay <= TimeSpan.Zero) failures.Add("Outbox:InitialRetryDelay must be positive.");
        if (options.MaximumRetryDelay <= TimeSpan.Zero) failures.Add("Outbox:MaximumRetryDelay must be positive.");
        if (options.MaximumRetryDelay < options.InitialRetryDelay)
            failures.Add("Outbox:MaximumRetryDelay must be greater than or equal to Outbox:InitialRetryDelay.");
        if (options.RetryJitterPercentage is < 0 or > 50)
            failures.Add("Outbox:RetryJitterPercentage must be between 0 and 50.");
        if (options.MaximumPublishConcurrency is < 1 or > 32)
            failures.Add("Outbox:MaximumPublishConcurrency must be between 1 and 32.");
        if (options.MaximumPublishConcurrency > options.BatchSize)
            failures.Add("Outbox:MaximumPublishConcurrency cannot exceed Outbox:BatchSize.");
        if (options.MaximumErrorSummaryLength is < 1 or > 512)
            failures.Add("Outbox:MaximumErrorSummaryLength must be between 1 and 512.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
