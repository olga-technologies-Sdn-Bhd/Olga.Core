using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Olga.Core.Worker;

public sealed class ProfileImageConsumerWorker(
    IServiceScopeFactory scopes,
    ServiceBusClient serviceBus,
    IConfiguration configuration,
    ILogger<ProfileImageConsumerWorker> logger) : BackgroundService
{
    private readonly ServiceBusProcessor processor = serviceBus.CreateProcessor(
        configuration["ServiceBus:ContentScanQueueName"]
            ?? throw new InvalidOperationException("ServiceBus:ContentScanQueueName is required."),
        new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 2, PrefetchCount = 4 });

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
        if (envelope is null || !ProfileImageUploaded.TryParse(envelope, out var uploaded))
        {
            await args.DeadLetterMessageAsync(args.Message, "UNSUPPORTED_EVENT", envelope?.EventType ?? "Missing event type");
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ProfileImageProcessor>().ProcessAsync(uploaded!, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Profile image event {EventId} failed on delivery {DeliveryCount}", envelope.EventId, args.Message.DeliveryCount);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Profile image queue receive failure in {ErrorSource} for {EntityPath}", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await processor.DisposeAsync();
    }
}
