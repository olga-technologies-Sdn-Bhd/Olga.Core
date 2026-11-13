using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Olga.Core.Worker;

// This existing subscription also dispatches SCI score events to avoid another messaging entity.
public sealed class NlpCompletionConsumerWorker(
    IServiceScopeFactory scopes,
    ServiceBusClient serviceBus,
    IConfiguration configuration,
    ILogger<NlpCompletionConsumerWorker> logger) : BackgroundService
{
    private readonly ServiceBusProcessor processor = serviceBus.CreateProcessor(
        configuration["ServiceBus:IntegrationTopicName"]
            ?? throw new InvalidOperationException("ServiceBus:IntegrationTopicName is required."),
        configuration["ServiceBus:NlpCompletionSubscriptionName"]
            ?? throw new InvalidOperationException("ServiceBus:NlpCompletionSubscriptionName is required."),
        new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 4, PrefetchCount = 16 });

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
            await args.DeadLetterMessageAsync(args.Message, "UNSUPPORTED_EVENT", envelope?.EventType ?? "Missing event type");
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (NlpMatchRequestCompleted.TryParse(envelope, out var completion))
                await scope.ServiceProvider.GetRequiredService<NlpCompletionProjectionHandler>()
                    .HandleAsync(envelope.EventId, completion!, args.CancellationToken);
            else if (SciLinkScoreChanged.TryParse(envelope, out var scoreChange))
                await scope.ServiceProvider.GetRequiredService<SciScoreProjectionHandler>()
                    .HandleAsync(envelope.EventId, scoreChange!, args.CancellationToken);
            else if (NlpIntentMatchReady.TryParse(envelope, out var intentReady))
                await scope.ServiceProvider.GetRequiredService<SciIntentRescoreHandler>()
                    .HandleAsync(intentReady!, args.CancellationToken);
            else
            {
                await args.DeadLetterMessageAsync(args.Message, "UNSUPPORTED_EVENT", envelope.EventType);
                return;
            }
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Core integration event {EventId} failed on delivery {DeliveryCount}", envelope.EventId, args.Message.DeliveryCount);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Service Bus receive failure in {ErrorSource} for {EntityPath}", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await processor.DisposeAsync();
    }
}
