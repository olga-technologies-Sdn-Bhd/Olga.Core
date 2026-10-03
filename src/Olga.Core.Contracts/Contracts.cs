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
public sealed record MemberLookupRequest(string? Email);
public sealed record MemberLookupResponse(string MemberId, string DisplayName, string ProfileStatus, string ETag);
// display_name is null when another member reads a profile they aren't connected to.
public sealed record ProfileResponse(string MemberId, string? DisplayName, string? Headline, string? ProfessionalSummary, string? RoleCategory, string ProfileStatus, string Visibility, decimal CompletenessScore, string ETag, DateTimeOffset UpdatedAt);
public sealed record ProfileUpdateRequest(string DisplayName, string? Headline, string? ProfessionalSummary, string? RoleCategory, string Visibility = "MEMBERS");
public sealed record ConsentRequest(string PurposeCode, string PolicyVersion, string Decision, string CaptureChannel = "MOBILE", object? Evidence = null);
public sealed record ConsentResponse(long MemberConsentId, string PolicyId, string PurposeCode, string PolicyVersion, string Decision, DateTimeOffset CapturedAt, DateTimeOffset? WithdrawnAt);
// check_in_required gates matching eligibility only; it does not gate registration or Live Mode.
public sealed record EventResponse(string EventId, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, bool LiveModeEnabled, bool CheckInRequired, string? Venue, int AttendeeCount, int LiveCount, bool? IsRegistered = null, string? RegistrationStatus = null);
// Blinded attendee card: no name, email or phone until members connect.
public sealed record EventAttendeeResponse(string MemberId, string? Headline, string? RoleCategory);
public sealed record EventAttendeesResponse(IReadOnlyList<EventAttendeeResponse> Attendees, int Total);
public sealed record RegistrationResponse(string EventId, string MemberId, string Status, DateTimeOffset RegisteredAt, DateTimeOffset? CheckedInAt = null);
public sealed record LiveModeRequest(int DurationMinutes = 60);
public sealed record LiveModeResponse(string SessionId, string EventId, string Status, DateTimeOffset ActiveUntil);
public sealed record PresenceRequest(string CoarseCell, DateTimeOffset ObservedAt, string Source = "FOREGROUND_GEO");
// A Commit is a connection request with context_id = event_id: a short meeting in that room.
// where.type: THEIR_CHOICE or SPOT (with spot_id); when: NOW, IN_10_MIN, NEXT_BREAK or AFTER_SESSION.
public sealed record CommitPlanWhere(string Type, string? SpotId = null);
public sealed record CommitPlan(CommitPlanWhere? Where, string? When);
public sealed record ConnectionRequestCreate(string RecipientMemberId, int ExpiresInDays = 14, string? ContextId = null, long? MatchResultId = null, string? Note = null, CommitPlan? Plan = null);
// commits_remaining is set for Commits only.
public sealed record ConnectionRequestResponse(string RequestId, string SenderMemberId, string RecipientMemberId, string Status, DateTimeOffset ExpiresAt, int? CommitsRemaining = null);
public sealed record ConnectionDecisionRequest(string Decision);
// display_name is the other member's name, returned once the request is accepted.
public sealed record ConnectionResponse(string ConnectionId, string MemberId, string Status, string ConversationId, string? DisplayName = null);
// where is the spot label, or THEIR_CHOICE.
public sealed record CommitPlanResponse(string Where, string When, string? SpotId = null, string? EventId = null);
public sealed record CommitEventSummary(string EventId, string Name);
// Before Accept the sender is described only by role: never name, photo, contact details or member ID.
public sealed record CommitSenderSummary(string? Headline, string? RoleCategory, string? Want = null, decimal? Score = null);
public sealed record IncomingCommitResponse(string RequestId, CommitEventSummary Event, CommitPlanResponse Plan, CommitSenderSummary Sender, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
// display_name and conversation_id are present only once ACCEPTED.
public sealed record CommitRecipientSummary(string? Headline, string? RoleCategory, string? DisplayName = null);
// status is only PENDING, ACCEPTED or EXPIRED: a decline looks like PENDING until it expires.
public sealed record OutgoingCommitResponse(string RequestId, CommitEventSummary Event, CommitPlanResponse Plan, CommitRecipientSummary Recipient, string Status, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, string? ConversationId = null);
public sealed record CommitQuotaResponse(int Limit, int Used, int Remaining);
public sealed record MeetingSpotResponse(string SpotId, string Label);
public sealed record BlockRequest(string MemberId);
public sealed record MessageCreateRequest(string MessageId, string? Body, string MessageType = "TEXT", DateTimeOffset? ClientSentAt = null);
// A deleted message keeps its place in the conversation with a null body and deleted_at set.
public sealed record MessageResponse(string MessageId, string ConversationId, string SenderMemberId, string MessageType, string? Body, long ServerSequence, string ModerationStatus, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt = null);
// Category: SPAM, HARASSMENT, INAPPROPRIATE, SCAM or OTHER.
public sealed record MessageReportRequest(string Category, string? Description = null);
public sealed record MessageReportResponse(string ReportId, string MessageId, string Category, string Status, DateTimeOffset CreatedAt);
// The other member is named here because only connected members share a conversation.
// plan is the accepted Commit's meeting plan, when the conversation came from a Commit.
public sealed record ConversationResponse(string ConversationId, string ConnectionId, string Status, string MemberId, string? DisplayName, string? Headline, string? RoleCategory, MessageResponse? LastMessage, int UnreadCount, DateTimeOffset LastActivityAt, bool CanSend, DateTimeOffset? MutedUntil = null, CommitPlanResponse? Plan = null);
public sealed record ConversationListResponse(IReadOnlyList<ConversationResponse> Items, string? NextCursor, bool HasMore);
// Marks every message from the other member up to and including this one as read.
public sealed record ConversationReadRequest(string LastReadMessageId);
// MutedUntil null unmutes; otherwise it must be in the future.
public sealed record ConversationMuteRequest(DateTimeOffset? MutedUntil);
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
public sealed record AdminEventResponse(string EventId, string CommunityId, string Name, string? Description, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, bool LiveModeEnabled, bool CheckInRequired, string? VenueId, string? Venue, int AttendeeCount, int LiveCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AdminEventMatchingPolicyUpdateRequest(bool CheckInRequired);
public sealed record AdminVenueCreateRequest(string Name, string CountryCode, string TimezoneId, string? City = null, string? Region = null);
public sealed record AdminVenueResponse(string VenueId, string Name, string CountryCode, string? Region, string? City, string TimezoneId, string Status, DateTimeOffset CreatedAt);
public sealed record AdminStatsResponse(int TotalMembers, int ActiveMembers, int UpcomingEvents, int OpenReports, int PendingPrivacyRequests, int ConnectionsLast7Days);
public sealed record AdminMemberResponse(string MemberId, string DisplayName, string? Headline, string? RoleCategory, string ProfileStatus, string Visibility, decimal CompletenessScore, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AdminMemberStatusRequest(string ProfileStatus);
public sealed record AdminReportResponse(string ReportId, string SourceType, string? SubjectMemberId, string? SubjectDisplayName, string? ResourceType, string? ResourceId, string Priority, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);
public sealed record AdminStatusRequest(string Status);
public sealed record AdminConsentPolicyCreateRequest(string PurposeCode, string Version, DateTimeOffset? EffectiveFrom = null, string Locale = "en", string? Text = null, string? ContentHash = null);
public sealed record AdminConsentPolicyResponse(string PolicyId, string PurposeCode, string Version, string Locale, string ContentHash, DateTimeOffset EffectiveFrom, DateTimeOffset? RetiredAt, string Status);
public sealed record ActiveConsentPolicyResponse(string PolicyId, string PurposeCode, string Version, string Locale, string ContentHash, DateTimeOffset EffectiveFrom, string? CurrentDecision, DateTimeOffset? DecisionCapturedAt);
public sealed record AdminPrivacyRequestResponse(string PrivacyRequestId, string MemberId, string RequestType, string Status, DateTimeOffset CreatedAt, DateTimeOffset? DueAt, DateTimeOffset? VerifiedAt, DateTimeOffset? CompletedAt);
public sealed record AdminAttendeeResponse(string MemberId, string DisplayName, string? Headline, string Status, DateTimeOffset RegisteredAt, DateTimeOffset? CheckedInAt, bool IsLive);
