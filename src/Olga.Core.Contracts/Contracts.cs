namespace Olga.Core.Contracts;

public sealed record ApiError(
    string Code,
    string Message,
    string CorrelationId,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null,
    string? StackTrace = null);
public sealed record MemberCreateRequest(
    string DisplayName,
    string? Email = null,
    string? Phone = null,
    string? Headline = null,
    string? ProfessionalSummary = null,
    string? RoleCategory = null,
    string Locale = "en",
    string Visibility = "MEMBERS");
public sealed record MemberRegistrationResponse(string MemberId, string? EmailHint, string? PhoneHint, string ProfileStatus, string ETag);
public sealed record ProfileResponse(string MemberId, string DisplayName, string? Headline, string? ProfessionalSummary, string? RoleCategory, string ProfileStatus, string Visibility, decimal CompletenessScore, string ETag, DateTimeOffset UpdatedAt);
public sealed record ProfileUpdateRequest(string DisplayName, string? Headline, string? ProfessionalSummary, string? RoleCategory, string Visibility = "MEMBERS");
public sealed record ConsentRequest(string PurposeCode, string PolicyVersion, string Decision, string CaptureChannel = "MOBILE", object? Evidence = null);
public sealed record ConsentResponse(long MemberConsentId, string PolicyId, string PurposeCode, string PolicyVersion, string Decision, DateTimeOffset CapturedAt, DateTimeOffset? WithdrawnAt);
public sealed record EventResponse(string EventId, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, bool LiveModeEnabled, string? Venue, int AttendeeCount, int LiveCount);
public sealed record RegistrationResponse(string EventId, string MemberId, string Status, DateTimeOffset RegisteredAt);
public sealed record LiveModeRequest(int DurationMinutes = 60);
public sealed record LiveModeResponse(string SessionId, string EventId, string Status, DateTimeOffset ActiveUntil);
public sealed record PresenceRequest(string CoarseCell, DateTimeOffset ObservedAt, string Source = "FOREGROUND_GEO");
public sealed record ConnectionRequestCreate(string RecipientMemberId, int ExpiresInDays = 14, string? ContextId = null, long? MatchResultId = null, string? Note = null);
public sealed record ConnectionRequestResponse(string RequestId, string SenderMemberId, string RecipientMemberId, string Status, DateTimeOffset ExpiresAt);
public sealed record ConnectionDecisionRequest(string Decision);
public sealed record ConnectionResponse(string ConnectionId, string MemberId, string Status, string ConversationId);
public sealed record BlockRequest(string MemberId);
public sealed record MessageCreateRequest(string MessageId, string? Body, string MessageType = "TEXT", DateTimeOffset? ClientSentAt = null);
public sealed record MessageResponse(string MessageId, string ConversationId, string SenderMemberId, string MessageType, string? Body, long ServerSequence, string ModerationStatus, DateTimeOffset CreatedAt);
public sealed record MessageReceiptRequest(DateTimeOffset? DeliveredAt = null, DateTimeOffset? ReadAt = null);
public sealed record MessageReceiptResponse(string MessageId, string MemberId, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt, DateTimeOffset UpdatedAt);
public sealed record NotificationPreferenceRequest(string PurposeCode, bool PushEnabled, bool EmailEnabled, TimeOnly? QuietStartLocal = null, TimeOnly? QuietEndLocal = null, string? TimezoneId = null);
public sealed record NotificationPreferenceResponse(string PurposeCode, bool PushEnabled, bool EmailEnabled, TimeOnly? QuietStartLocal, TimeOnly? QuietEndLocal, string? TimezoneId, string ETag, DateTimeOffset UpdatedAt);
public sealed record PrivacyRequestCreate(string RequestType);
public sealed record PrivacyRequestResponse(string PrivacyRequestId, string RequestType, string Status, DateTimeOffset CreatedAt, DateTimeOffset? DueAt);
public sealed record SyncItem(long Sequence, string ResourceType, string ResourceId, string ChangeType, long? ResourceVersion, object? Payload, DateTimeOffset OccurredAt);
public sealed record SyncResponse(IReadOnlyList<SyncItem> Items, string? NextCursor, bool HasMore);
public sealed record AdminEventCreateRequest(string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Description = null, string? VenueId = null, bool LiveModeEnabled = true, bool Publish = false);
public sealed record AdminEventUpdateRequest(string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Description = null, string? VenueId = null, bool LiveModeEnabled = true);
public sealed record AdminEventResponse(string EventId, string CommunityId, string Name, string? Description, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, bool LiveModeEnabled, string? VenueId, string? Venue, int AttendeeCount, int LiveCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AdminVenueCreateRequest(string Name, string CountryCode, string TimezoneId, string? City = null, string? Region = null);
public sealed record AdminVenueResponse(string VenueId, string Name, string CountryCode, string? Region, string? City, string TimezoneId, string Status, DateTimeOffset CreatedAt);
