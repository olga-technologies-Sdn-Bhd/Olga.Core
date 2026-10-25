using System.Text.Json;

namespace Olga.Core.Application;

// Realtime chat: the API hosts the hub and the worker forwards committed outbox events to it.
// Realtime is best effort; clients must recover anything missed through REST and sync.
public static class ChatRealtime
{
    // Must match the API's hub class name: Azure SignalR identifies the hub by it.
    public const string HubName = "ChatHub";
    public const string HubPath = "/v1/realtime/chat";
    // Clients listen with connection.on("event", ...) and receive the integration event envelope.
    public const string ClientMethod = "event";

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
