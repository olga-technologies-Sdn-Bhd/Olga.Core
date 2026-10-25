namespace Olga.Core.Application;

// An ACTIVE push token on one of the recipient's ACTIVE devices, still encrypted.
public sealed record PushTarget(long PushTokenId, string DeviceId, string Provider, byte[] TokenCiphertext);

// What deciding a chat push needs about one new message and its recipient.
// chat_push_enabled is null when the recipient never set a CHAT preference (push is on by default).
public sealed record ChatPushFacts(
    string MessageType,
    bool Deleted,
    string? AttachmentMediaType,
    DateTimeOffset? MutedUntil,
    bool? ChatPushEnabled,
    bool Blocked,
    bool ConversationOpen,
    string? SenderDisplayName,
    IReadOnlyList<PushTarget> Targets);

// The notification shown on the device. Never carries message text.
public sealed record PushContent(string Title, string Body, IReadOnlyDictionary<string, string> Data);

public enum PushResult { Delivered, TokenInvalid, Failed }

public interface IChatPushStore
{
    // Null when the message or the recipient's participation no longer exists.
    Task<ChatPushFacts?> GetMessageFactsAsync(string messageId, string recipientMemberId, CancellationToken ct);
    // Delivered records last_success_at; otherwise the provider rejected the token and it becomes INVALID.
    Task RecordResultAsync(long pushTokenId, bool delivered, CancellationToken ct);
}

public interface IPushSender
{
    Task<PushResult> SendAsync(string provider, string token, PushContent content, CancellationToken ct);
}

public static class ChatPush
{
    // notification.notification_preference purpose that turns chat pushes off.
    public const string Purpose = "CHAT";

    // The push for a new message, or null when the recipient shouldn't get one.
    public static PushContent? Plan(string conversationId, ChatPushFacts facts, DateTimeOffset now)
    {
        if (facts.Deleted || facts.Blocked || !facts.ConversationOpen || facts.MutedUntil > now || facts.ChatPushEnabled == false || facts.Targets.Count == 0) return null;
        var body = facts.MessageType != "FILE" ? "New message"
            : facts.AttachmentMediaType?.StartsWith("image/", StringComparison.Ordinal) == true ? "Sent a photo"
            : facts.AttachmentMediaType?.StartsWith("audio/", StringComparison.Ordinal) == true ? "Sent a voice note"
            : "Sent a file";
        var title = string.IsNullOrWhiteSpace(facts.SenderDisplayName) ? "OLGA" : facts.SenderDisplayName;
        return new(title, body, new Dictionary<string, string> { ["type"] = "chat_message", ["conversation_id"] = conversationId });
    }
}
