using System.Data;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Infrastructure;

namespace Olga.Core.Worker;

public sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopes,
    ServiceBusClient serviceBus,
    IConfiguration configuration,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ServiceBusSender sender = serviceBus.CreateSender(
        configuration["ServiceBus:IntegrationTopicName"]
        ?? throw new InvalidOperationException("ServiceBus:IntegrationTopicName is required."));
    private readonly int maxAttempts = Math.Clamp(configuration.GetValue("Outbox:MaxPublishAttempts", 10), 1, 100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Outbox publish batch failed; the next polling cycle will retry it"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var rows = await db.OutboxEvents
            .FromSqlRaw("""
                SELECT * FROM ops.outbox_event
                WHERE published_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= CURRENT_TIMESTAMP)
                ORDER BY occurred_at, outbox_event_id
                FOR UPDATE SKIP LOCKED
                LIMIT 50
                """)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.AttemptCount++;
            try
            {
                var envelope = IntegrationEventEnvelope.FromOutbox(row);
                var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(envelope, JsonOptions))
                {
                    MessageId = row.OutboxEventId,
                    Subject = row.EventType,
                    ContentType = "application/json",
                    CorrelationId = row.AggregateId
                };
                message.ApplicationProperties["event_type"] = row.EventType;
                await sender.SendMessageAsync(message, ct);
                row.PublishedAt = DateTimeOffset.UtcNow;
                row.NextAttemptAt = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (row.AttemptCount >= maxAttempts)
                {
                    row.NextAttemptAt = DateTimeOffset.MaxValue;
                    logger.LogCritical(ex, "Outbox event {EventId} exhausted {AttemptCount} publish attempts and requires operator recovery", row.OutboxEventId, row.AttemptCount);
                }
                else
                {
                    var delaySeconds = Math.Min(1800, 15 * (1 << Math.Min(row.AttemptCount - 1, 7)));
                    row.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                    logger.LogWarning(ex, "Outbox event {EventId} publish attempt {AttemptCount} failed; retry scheduled", row.OutboxEventId, row.AttemptCount);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await sender.DisposeAsync();
    }
}
