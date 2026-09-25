namespace Olga.Core.Domain;

public sealed class MemberProfile
{
    public string MemberId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Headline { get; set; }
    public string? Biography { get; set; }
    public string? Sector { get; set; }
    public string Visibility { get; set; } = "MEMBERS";
    public string Status { get; set; } = "DRAFT";
    public decimal CompletenessScore { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConsentPolicy
{
    public string PolicyId { get; set; } = "";
    public string PurposeCode { get; set; } = "";
    public string Version { get; set; } = "";
    public string Locale { get; set; } = "en";
    public string ContentHash { get; set; } = "";
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class MemberConsent
{
    public long Id { get; set; }
    public string MemberId { get; set; } = "";
    public string PolicyId { get; set; } = "";
    public string Decision { get; set; } = "GRANTED";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string CaptureChannel { get; set; } = "MOBILE";
    public string? EvidenceJson { get; set; }
}

public sealed class EventRecord
{
    public string EventId { get; set; } = "";
    public string CommunityId { get; set; } = "olga";
    public string? VenueId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = "PUBLISHED";
    public bool LiveModeEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class Venue
{
    public string VenueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? Region { get; set; }
    public string CountryCode { get; set; } = "";
    public string TimezoneId { get; set; } = "UTC";
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class EventRegistration
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Status { get; set; } = "REGISTERED";
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CheckedInAt { get; set; }
    public string Source { get; set; } = "APP";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class LiveModeSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string EventId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public long ConsentRecordId { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset ActiveUntil { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class EventPresence
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    public string CoarseCell { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Source { get; set; } = "FOREGROUND_GEO";
}

public sealed class ConnectionRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SenderMemberId { get; set; } = "";
    public string RecipientMemberId { get; set; } = "";
    public string? ContextId { get; set; }
    public long? MatchResultId { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class Connection
{
    public string ConnectionId { get; set; } = Guid.NewGuid().ToString("N");
    public string MemberLowId { get; set; } = "";
    public string MemberHighId { get; set; } = "";
    public string AcceptedRequestId { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisconnectedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class MemberBlock
{
    public long Id { get; set; }
    public string BlockerMemberId { get; set; } = "";
    public string BlockedMemberId { get; set; } = "";
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class Conversation
{
    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class ConversationParticipant
{
    public string ConversationId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastReadMessageId { get; set; }
    public DateTimeOffset? MutedUntil { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

public sealed class Message
{
    public string MessageId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SenderMemberId { get; set; } = "";
    public string MessageType { get; set; } = "TEXT";
    public string? Body { get; set; }
    public DateTimeOffset? ClientSentAt { get; set; }
    public long ServerSequence { get; set; }
    public string ModerationStatus { get; set; } = "PENDING_OR_CLEAR";
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class MessageReceipt
{
    public string MessageId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NotificationPreference
{
    public string MemberId { get; set; } = "";
    public string PurposeCode { get; set; } = "";
    public bool PushEnabled { get; set; } = true;
    public bool EmailEnabled { get; set; }
    public TimeOnly? QuietStartLocal { get; set; }
    public TimeOnly? QuietEndLocal { get; set; }
    public string? TimezoneId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class PrivacyRequest
{
    public string PrivacyRequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string MemberId { get; set; } = "";
    public string RequestType { get; set; } = "ACCESS";
    public string Status { get; set; } = "OPEN";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultFileAssetId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class SyncChange
{
    public long SyncSequence { get; set; }
    public string CommunityId { get; set; } = "olga";
    public string? MemberScopeId { get; set; }
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string ChangeType { get; set; } = "UPSERT";
    public long? ResourceVersion { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(30);
}

public sealed class OutboxEvent
{
    public string OutboxEventId { get; set; } = Guid.NewGuid().ToString("N");
    public string AggregateType { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}

public sealed class DomainException(string code, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class ModerationCase
{
    public string ModerationCaseId { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceType { get; set; } = "MEMBER_REPORT";
    public string? SourceId { get; set; }
    public string? SubjectMemberId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string Priority { get; set; } = "NORMAL";
    public string Status { get; set; } = "OPEN";
    public string? AssignedTo { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}
