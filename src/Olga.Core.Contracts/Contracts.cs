namespace Olga.Core.Contracts;

public sealed record ApiError(
    string Code,
    string Message,
    string CorrelationId,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null,
    string? StackTrace = null,
    object? Details = null);
// details of 409 MEMBER_DELETION_PENDING: when the open deletion request is due to complete.
public sealed record DeletionPendingDetails(DateTimeOffset? DueAt);
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
// email_hint and phone_hint are the member's own masked contact details: set only on /v1/me/profile, never for another member.
public sealed record ProfileResponse(string MemberId, string? DisplayName, string? Headline, string? ProfessionalSummary, string? RoleCategory, string ProfileStatus, string Visibility, decimal CompletenessScore, string ETag, DateTimeOffset UpdatedAt, string? ProfileImageUrl = null, string? ProfileImageStatus = null, string? EmailHint = null, string? PhoneHint = null);
public sealed record ProfileImageUploadResponse(string ImageId, string Status, string ETag);
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
// Adds duration_minutes (5-240) to the current active_until, capped at the event's end.
public sealed record LiveModeExtendRequest(int DurationMinutes);
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
// A member's own block list: the blocker knows who they blocked, so the name is included.
public sealed record BlockedMemberResponse(string MemberId, string? DisplayName, DateTimeOffset BlockedAt);
// message_type FILE needs file_asset_id from POST /v1/conversations/{id}/attachments; body is then an optional caption.
public sealed record MessageCreateRequest(string MessageId, string? Body, string MessageType = "TEXT", DateTimeOffset? ClientSentAt = null, string? FileAssetId = null);
// A sent file. media_type is detected from the file itself: image/jpeg, image/png, image/webp, image/heic, application/pdf, audio/mp4 or audio/aac.
public sealed record MessageAttachment(string FileAssetId, string MediaType, long SizeBytes, string FileName);
public sealed record ChatAttachmentUploadResponse(string FileAssetId, string MediaType, long SizeBytes, string FileName);
// A deleted message keeps its place in the conversation with a null body and deleted_at set.
// attachment is set on a FILE message until it is deleted; the file itself comes from GET /v1/messages/{id}/attachment.
public sealed record MessageResponse(string MessageId, string ConversationId, string SenderMemberId, string MessageType, string? Body, long ServerSequence, string ModerationStatus, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt = null, MessageAttachment? Attachment = null);
// Category: SPAM, HARASSMENT, INAPPROPRIATE, SCAM or OTHER.
public sealed record MessageReportRequest(string Category, string? Description = null);
// Category: SPAM, HARASSMENT, INAPPROPRIATE, FAKE_PROFILE or OTHER. resource_type: PROFILE, REQUEST or MESSAGE (optional).
public sealed record MemberReportRequest(string Category, string? Description = null, string? ResourceType = null, string? ResourceId = null);
// Never carries the reported member's name or other details.
// Reports the sender of an incoming Commit without naming them.
public sealed record CommitReportRequest(string Category, string? Description = null);
public sealed record MemberReportResponse(string ReportId, string Category, string Status, DateTimeOffset CreatedAt);
public sealed record MessageReportResponse(string ReportId, string MessageId, string Category, string Status, DateTimeOffset CreatedAt);
// The other member is named here because only connected members share a conversation.
// plan is the accepted Commit's meeting plan, when the conversation came from a Commit.
// other_delivered_through_sequence / other_read_through_sequence: the highest server_sequence of my messages the other
// member has received / read; my messages at or below it count as delivered / read. Null until the first receipt.
public sealed record ConversationResponse(string ConversationId, string ConnectionId, string Status, string MemberId, string? DisplayName, string? Headline, string? RoleCategory, MessageResponse? LastMessage, int UnreadCount, DateTimeOffset LastActivityAt, bool CanSend, DateTimeOffset? MutedUntil = null, CommitPlanResponse? Plan = null, long? OtherDeliveredThroughSequence = null, long? OtherReadThroughSequence = null);
public sealed record ConversationListResponse(IReadOnlyList<ConversationResponse> Items, string? NextCursor, bool HasMore);
// Marks every message from the other member up to and including this one as read.
public sealed record ConversationReadRequest(string LastReadMessageId);
// MutedUntil null unmutes; otherwise it must be in the future.
public sealed record ConversationMuteRequest(DateTimeOffset? MutedUntil);
public sealed record MessageReceiptRequest(DateTimeOffset? DeliveredAt = null, DateTimeOffset? ReadAt = null);
public sealed record MessageReceiptResponse(string MessageId, string MemberId, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt, DateTimeOffset UpdatedAt);
// platform: IOS or ANDROID. push_provider (APNS or FCM) and push_token go together; leave both out to register the device only.
public sealed record DeviceRegistrationRequest(string Platform, string AppVersion, string? PushProvider = null, string? PushToken = null);
// The push token is never returned; push_enabled says whether the device has an active one.
public sealed record DeviceResponse(string DeviceId, string Platform, string AppVersion, string Status, bool PushEnabled, DateTimeOffset LastSeenAt);
public sealed record NotificationPreferenceRequest(string PurposeCode, bool PushEnabled, bool EmailEnabled, TimeOnly? QuietStartLocal = null, TimeOnly? QuietEndLocal = null, string? TimezoneId = null);
public sealed record NotificationPreferenceResponse(string PurposeCode, bool PushEnabled, bool EmailEnabled, TimeOnly? QuietStartLocal, TimeOnly? QuietEndLocal, string? TimezoneId, string ETag, DateTimeOffset UpdatedAt);
public sealed record PrivacyRequestCreate(string RequestType);
public sealed record PrivacyRequestResponse(string PrivacyRequestId, string RequestType, string Status, DateTimeOffset CreatedAt, DateTimeOffset? DueAt);
// GET /v1/me/export: the member's own data only. Another member is named only while connected; contact details are masked hints.
public sealed record DataExportResponse(
    DateTimeOffset ExportedAt,
    string MemberId,
    ProfileResponse? Profile,
    IReadOnlyList<ExportIntent> Intents,
    IReadOnlyList<ExportRegistration> EventRegistrations,
    IReadOnlyList<ExportRequest> CommitsSent,
    IReadOnlyList<ExportRequest> CommitsReceived,
    IReadOnlyList<ExportConnection> Connections,
    IReadOnlyList<ExportMessage> Messages,
    IReadOnlyList<ExportConsent> Consents,
    IReadOnlyList<PrivacyRequestResponse> PrivacyRequests);
public sealed record ExportIntent(string IntentId, string ContextId, string IntentType, string Text, string Status, string? Category, string? Industry, string? Geography, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
public sealed record ExportRegistration(string EventId, string? EventName, string Status, DateTimeOffset RegisteredAt, DateTimeOffset? CheckedInAt);
// event_id is set for Commits and null for plain connection requests. A sent Commit's DECLINED shows as PENDING, then EXPIRED, as in the app.
public sealed record ExportRequest(string RequestId, string? EventId, string Status, CommitPlanResponse? Plan, string? Note, string? MemberId, string? DisplayName, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record ExportConnection(string ConnectionId, string Status, string? MemberId, string? DisplayName, DateTimeOffset ConnectedAt, DateTimeOffset? DisconnectedAt);
// Only messages the member sent; a deleted message has a null body.
public sealed record ExportMessage(string MessageId, string ConversationId, string MessageType, string? Body, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt);
public sealed record ExportConsent(string PurposeCode, string PolicyVersion, string Decision, DateTimeOffset CapturedAt, DateTimeOffset? WithdrawnAt);
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
// Live-ops: a member live in the event now. activated_at is when the session started.
public sealed record AdminLiveSessionResponse(string MemberId, string? DisplayName, string SessionId, DateTimeOffset ActivatedAt, DateTimeOffset ActiveUntil);
public sealed record AdminAttendeeResponse(string MemberId, string DisplayName, string? Headline, string Status, DateTimeOffset RegisteredAt, DateTimeOffset? CheckedInAt, bool IsLive);
