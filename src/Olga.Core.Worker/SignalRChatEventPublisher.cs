using System.Text.Json;
using Olga.Core.Application;

namespace Olga.Core.Worker;

// Sends a chat event to its members' devices.
public interface IChatRealtimeSender
{
    Task SendAsync(IReadOnlyList<string> memberIds, JsonElement envelope, CancellationToken cancellationToken);
}

// Publishes to Service Bus first, then forwards chat events to the conversation's members through SignalR.
// Realtime remains best effort: a SignalR failure is logged after the durable Service Bus publish,
// and clients must recover missed events through REST/sync.
public sealed class SignalRChatEventPublisher(IOutboxEventPublisher inner, IChatRealtimeSender realtime, ILogger<SignalRChatEventPublisher> logger) : IOutboxEventPublisher
{
    public async Task PublishAsync(ClaimedOutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        await inner.PublishAsync(outboxEvent, cancellationToken);
        var recipients = ChatRealtime.Recipients(outboxEvent.EventType, outboxEvent.PayloadJson);
        if (recipients.Count == 0) return;
        try
        {
            using var envelope = JsonDocument.Parse(ServiceBusOutboxEventPublisher.Envelope(outboxEvent).ToMemory());
            await realtime.SendAsync(recipients, envelope.RootElement, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Realtime delivery failed for outbox event {OutboxEventId} of type {EventType}; clients must recover through REST/sync", outboxEvent.OutboxEventId, outboxEvent.EventType);
        }
    }
}
