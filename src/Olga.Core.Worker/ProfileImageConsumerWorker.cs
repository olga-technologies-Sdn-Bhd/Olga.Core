using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Olga.Core.Worker;

public sealed class ContentScanConsumerWorker(
    IServiceScopeFactory scopes,
    ServiceBusClient serviceBus,
    IConfiguration configuration,
    ILogger<ContentScanConsumerWorker> logger) : BackgroundService
{
    private const int MaximumScanAttempts = 20;
    private readonly ServiceBusProcessor processor = serviceBus.CreateProcessor(
        configuration["ServiceBus:ContentScanQueueName"]
            ?? throw new InvalidOperationException("ServiceBus:ContentScanQueueName is required."),
        new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 2, PrefetchCount = 4 });
    private readonly ServiceBusSender retrySender = serviceBus.CreateSender(
        configuration["ServiceBus:ContentScanQueueName"]
            ?? throw new InvalidOperationException("ServiceBus:ContentScanQueueName is required."));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        IntegrationEventEnvelope? envelope;
        try { envelope = args.Message.Body.ToObjectFromJson<IntegrationEventEnvelope>(); }
        catch (JsonException ex)
        {
            await args.DeadLetterMessageAsync(args.Message, "INVALID_ENVELOPE", ex.Message);
            return;
        }
        if (envelope is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "INVALID_ENVELOPE", "Missing event envelope");
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var completed = true;
            Func<CancellationToken, Task>? markTimedOut = null;
            if (ProfileImageUploaded.TryParse(envelope, out var profileImage))
            {
                var handler = scope.ServiceProvider.GetRequiredService<ProfileImageProcessor>();
                completed = await handler.ProcessAsync(profileImage!, args.CancellationToken);
                markTimedOut = token => handler.MarkTimedOutAsync(profileImage!, token);
            }
            else if (ChatFileUploaded.TryParse(envelope, out var chatFile))
            {
                var handler = scope.ServiceProvider.GetRequiredService<ChatAttachmentProcessor>();
                completed = await handler.ProcessAsync(chatFile!, args.CancellationToken);
                markTimedOut = token => handler.MarkTimedOutAsync(chatFile!, token);
            }
            else
            {
                await args.DeadLetterMessageAsync(args.Message, "UNSUPPORTED_EVENT", envelope.EventType);
                return;
            }

            if (!completed)
            {
                var attempt = args.Message.ApplicationProperties.TryGetValue("scan_attempt", out var value)
                    ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) + 1
                    : 1;
                if (attempt >= MaximumScanAttempts)
                {
                    await markTimedOut!(args.CancellationToken);
                    await args.DeadLetterMessageAsync(args.Message, "MALWARE_SCAN_TIMEOUT", "Defender for Storage did not publish a terminal blob-index tag in time.");
                    return;
                }
                var retry = new ServiceBusMessage(args.Message.Body)
                {
                    MessageId = $"{envelope.EventId}:scan:{attempt}",
                    Subject = envelope.EventType,
                    ContentType = "application/json",
                    CorrelationId = envelope.AggregateId,
                    ScheduledEnqueueTime = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 15 * attempt))
                };
                retry.ApplicationProperties["scan_attempt"] = attempt;
                retry.ApplicationProperties["event_type"] = envelope.EventType;
                retry.ApplicationProperties["aggregate_type"] = envelope.AggregateType;
                await retrySender.SendMessageAsync(retry, args.CancellationToken);
            }
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Content scan event {EventId} failed on delivery {DeliveryCount}", envelope.EventId, args.Message.DeliveryCount);
            if (args.Message.DeliveryCount >= 5)
            {
                await using var failureScope = scopes.CreateAsyncScope();
                if (ProfileImageUploaded.TryParse(envelope, out var failedProfile))
                    await failureScope.ServiceProvider.GetRequiredService<ProfileImageProcessor>().MarkProcessingFailedAsync(failedProfile!, args.CancellationToken);
                else if (ChatFileUploaded.TryParse(envelope, out var failedChatFile))
                    await failureScope.ServiceProvider.GetRequiredService<ChatAttachmentProcessor>().MarkProcessingFailedAsync(failedChatFile!, args.CancellationToken);
                await args.DeadLetterMessageAsync(args.Message, "CONTENT_SCAN_FAILED", ex.GetType().Name);
                return;
            }
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Content scan queue receive failure in {ErrorSource} for {EntityPath}", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await processor.DisposeAsync();
        await retrySender.DisposeAsync();
    }
}
