using System.Buffers;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Olga.Core.Application;

namespace Olga.Core.Worker;

public sealed class ServiceBusOutboxEventPublisher : IOutboxEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender integrationSender;
    private readonly ServiceBusSender contentScanSender;

    public ServiceBusOutboxEventPublisher(ServiceBusClient serviceBus, IConfiguration configuration)
    {
        integrationSender = serviceBus.CreateSender(
            configuration["ServiceBus:IntegrationTopicName"]
            ?? throw new InvalidOperationException("ServiceBus:IntegrationTopicName is required."));
        contentScanSender = serviceBus.CreateSender(
            configuration["ServiceBus:ContentScanQueueName"]
            ?? throw new InvalidOperationException("ServiceBus:ContentScanQueueName is required."));
    }

    public async Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        var message = CreateMessage(outboxEvent);
        var sender = string.Equals(outboxEvent.EventType, ProfileImageUploaded.EventType, StringComparison.Ordinal)
            ? contentScanSender
            : integrationSender;
        await sender.SendMessageAsync(message, cancellationToken);
    }

    public static ServiceBusMessage CreateMessage(ClaimedOutboxEvent outboxEvent)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event_id", outboxEvent.OutboxEventId);
            writer.WriteString("event_type", outboxEvent.EventType);
            writer.WriteString("aggregate_type", outboxEvent.AggregateType);
            writer.WriteString("aggregate_id", outboxEvent.AggregateId);
            writer.WriteString("occurred_at", outboxEvent.OccurredAt);
            writer.WritePropertyName("data");
            writer.WriteRawValue(outboxEvent.PayloadJson, skipInputValidation: false);
            writer.WriteEndObject();
        }

        var message = new ServiceBusMessage(new BinaryData(buffer.WrittenMemory))
        {
            MessageId = outboxEvent.OutboxEventId,
            Subject = outboxEvent.EventType,
            ContentType = "application/json",
            CorrelationId = outboxEvent.AggregateId
        };
        message.ApplicationProperties["event_type"] = outboxEvent.EventType;
        message.ApplicationProperties["aggregate_type"] = outboxEvent.AggregateType;
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        await integrationSender.DisposeAsync();
        await contentScanSender.DisposeAsync();
    }
}
