using System.Text.Json;

namespace Olga.Core.Application;

// Realtime chat: the API hosts the hub and the worker forwards committed outbox events to it.
// Realtime is best effort; clients recover anything missed through REST and sync.
public static class ChatRealtime
{
    // Must match the API's hub class name: Azure SignalR identifies the hub by it.
    public const string HubName = "ChatHub";
    public const string HubPath = "/v1/realtime/chat";
    // Clients listen with connection.on("event", ...) and receive the integration event envelope.
    public const string ClientMethod = "event";

    // Sent straight from the API to the other member, never stored: { conversation_id, member_id, expires_at }.
    public const string TypingStarted = "TypingStarted.v1";
    public static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TypingDuration = TimeSpan.FromSeconds(5);

    // The members who may see an event: only the conversation's two participants.
    // Empty for events that are not forwarded to devices.
    public static IReadOnlyList<string> Recipients(string eventType, string payloadJson)
    {
        var fields = eventType switch
        {
            "MessageCreated.v1" or "MessageDeleted.v1" => new[] { "sender_member_id", "recipient_member_id" },
            "ConversationRead.v1" => new[] { "reader_member_id", "sender_member_id" },
            _ => Array.Empty<string>()
        };
        if (fields.Length == 0) return [];
        using var payload = JsonDocument.Parse(payloadJson);
        return fields
            .Select(name => payload.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

// Forwards a member's "typing…" to the other member of the conversation. Best effort: never throws.
public interface IChatTypingPublisher
{
    Task PublishAsync(string memberId, string recipientMemberId, string conversationId, CancellationToken ct);
}

// At most one typing signal per member and conversation each ChatRealtime.TypingInterval; the rest are dropped.
// Per API instance, which is enough to keep a chatty keyboard from flooding SignalR.
public sealed class ChatTypingThrottle(TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string MemberId, string ConversationId), DateTimeOffset> last = new();

    public bool TryAcquire(string memberId, string conversationId)
    {
        var now = time.GetUtcNow();
        if (last.Count > 10_000)
            foreach (var entry in last)
                if (now - entry.Value >= ChatRealtime.TypingInterval) last.TryRemove(entry);
        var key = (memberId, conversationId);
        while (true)
        {
            if (!last.TryGetValue(key, out var previous)) { if (last.TryAdd(key, now)) return true; continue; }
            if (now - previous < ChatRealtime.TypingInterval) return false;
            if (last.TryUpdate(key, now, previous)) return true;
        }
    }
}
