using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Olga.Core.Worker;

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

        if (envelope is null || !NlpMatchRequestCompleted.TryParse(envelope, out var completion))
        {
            await args.DeadLetterMessageAsync(args.Message, "UNSUPPORTED_EVENT", envelope?.EventType ?? "Missing event type");
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NlpCompletionProjectionHandler>()
                .HandleAsync(envelope.EventId, completion!, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "NLP completion event {EventId} failed on delivery {DeliveryCount}", envelope.EventId, args.Message.DeliveryCount);
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
