using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public interface ICoreStore
{
    bool IsRelational { get; }
    IQueryable<MemberProfile> Profiles { get; }
    IQueryable<MemberAccount> Accounts { get; }
    IQueryable<MemberIdentity> Identities { get; }
    IQueryable<ConsentPolicy> ConsentPolicies { get; }
    IQueryable<MemberConsent> Consents { get; }
    IQueryable<EventRecord> Events { get; }
    IQueryable<Venue> Venues { get; }
    IQueryable<EventMatchingPolicy> MatchingPolicies { get; }
    IQueryable<EventRegistration> Registrations { get; }
    IQueryable<LiveModeSession> LiveSessions { get; }
    IQueryable<EventPresence> Presence { get; }
    IQueryable<ConnectionRequest> ConnectionRequests { get; }
    IQueryable<Connection> Connections { get; }
    IQueryable<MemberBlock> Blocks { get; }
    IQueryable<Conversation> Conversations { get; }
    IQueryable<ConversationParticipant> ConversationParticipants { get; }
    IQueryable<Message> Messages { get; }
    IQueryable<MessageReceipt> MessageReceipts { get; }
    IQueryable<NotificationPreference> NotificationPreferences { get; }
    IQueryable<PrivacyRequest> PrivacyRequests { get; }
    IQueryable<SyncChange> SyncChanges { get; }
    IQueryable<ModerationCase> ModerationCases { get; }
    IQueryable<MemberReport> MemberReports { get; }
    Task CreateMemberAsync(NewMemberRegistration member, CancellationToken ct);
    Task EnsureMemberAsync(string memberId, CancellationToken ct);
    void Add<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;
    Task SaveAsync(CancellationToken ct);
    Task<(string ConnectionId, string ConversationId)> AcceptConnectionRequestAsync(string requestId, string recipientId, string connectionId, string conversationId, string idempotencyKey, string requestHash, CancellationToken ct);
    Task<Message> SaveMessageAsync(string memberId, string conversationId, MessageCreateRequest request, string idempotencyKey, string requestHash, CancellationToken ct);
    Task<MessageReceipt> SaveMessageReceiptAsync(string memberId, string messageId, MessageReceiptRequest request, string idempotencyKey, string requestHash, CancellationToken ct);
    // Reads nlp.nlp_match_result scores; empty when the store has no NLP data.
    Task<IReadOnlyList<MatchScore>> GetMatchScoresAsync(long[] matchResultIds, CancellationToken ct);
}

public sealed record MatchScore(long MatchResultId, string RequesterId, string CandidateId, decimal Score);

public interface ICoreService
{
    Task<MemberRegistrationResponse> RegisterMemberAsync(string communityId, MemberCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<MemberLookupResponse> LookupMemberByEmailAsync(MemberLookupRequest request, CancellationToken ct);
    Task ProvisionMemberAsync(string memberId, CancellationToken ct);
    Task<ProfileResponse> GetOwnProfileAsync(string memberId, CancellationToken ct);
    Task<ProfileResponse> GetVisibleProfileAsync(string actorId, string memberId, CancellationToken ct);
    Task<ProfileResponse> UpdateProfileAsync(string memberId, ProfileUpdateRequest request, string? ifMatch, CancellationToken ct);
    Task<ConsentResponse> RecordConsentAsync(string memberId, ConsentRequest request, CancellationToken ct);
    Task<ActiveConsentPolicyResponse> GetActiveConsentPolicyAsync(string purposeCode, CancellationToken ct);
    // memberId is optional: when supplied, each event carries is_registered for that member.
    Task<IReadOnlyList<EventResponse>> GetEventsAsync(string? memberId, CancellationToken ct);
    Task<RegistrationResponse> RegisterAsync(string memberId, string eventId, CancellationToken ct);
    Task<EventAttendeesResponse> GetEventAttendeesAsync(string memberId, string eventId, CancellationToken ct);
    Task<LiveModeResponse> StartLiveModeAsync(string memberId, string eventId, LiveModeRequest request, CancellationToken ct);
    Task StopLiveModeAsync(string memberId, string eventId, CancellationToken ct);
    Task RecordPresenceAsync(string memberId, string eventId, PresenceRequest request, CancellationToken ct);
    // With context_id set the request is a Commit for that event.
    Task<ConnectionRequestResponse> CreateConnectionRequestAsync(string senderId, ConnectionRequestCreate request, string idempotencyKey, CancellationToken ct);
    // Null when a Commit is declined: nothing is returned or observable to the sender.
    Task<ConnectionResponse?> DecideConnectionRequestAsync(string memberId, string requestId, ConnectionDecisionRequest request, string idempotencyKey, CancellationToken ct);
    Task<CommitQuotaResponse> GetCommitQuotaAsync(string memberId, string eventId, CancellationToken ct);
    Task<IReadOnlyList<MeetingSpotResponse>> GetMeetingSpotsAsync(string memberId, string eventId, CancellationToken ct);
    Task<IReadOnlyList<IncomingCommitResponse>> GetIncomingCommitsAsync(string memberId, string? eventId, CancellationToken ct);
    Task<IReadOnlyList<OutgoingCommitResponse>> GetOutgoingCommitsAsync(string memberId, string? eventId, CancellationToken ct);
    Task BlockAsync(string memberId, BlockRequest request, CancellationToken ct);
    Task<IReadOnlyList<ConnectionResponse>> GetConnectionsAsync(string memberId, CancellationToken ct);
    // Newest activity first; cursor is the opaque next_cursor from the previous page.
    Task<ConversationListResponse> GetConversationsAsync(string memberId, string? cursor, int limit, CancellationToken ct);
    Task<ConversationResponse> GetConversationAsync(string memberId, string conversationId, CancellationToken ct);
    Task<ConversationResponse> MarkConversationReadAsync(string memberId, string conversationId, ConversationReadRequest request, string idempotencyKey, CancellationToken ct);
    Task<ConversationResponse> MuteConversationAsync(string memberId, string conversationId, ConversationMuteRequest request, CancellationToken ct);
    Task<MessageResponse> SendMessageAsync(string memberId, string conversationId, MessageCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<MessageReceiptResponse> SaveMessageReceiptAsync(string memberId, string messageId, MessageReceiptRequest request, string idempotencyKey, CancellationToken ct);
    Task DeleteMessageAsync(string memberId, string messageId, CancellationToken ct);
    Task<MessageReportResponse> ReportMessageAsync(string memberId, string messageId, MessageReportRequest request, CancellationToken ct);
    Task<IReadOnlyList<MessageResponse>> GetMessagesAsync(string memberId, string conversationId, long after, int limit, CancellationToken ct);
    Task<NotificationPreferenceResponse> SetNotificationPreferenceAsync(string memberId, NotificationPreferenceRequest request, CancellationToken ct);
    Task<PrivacyRequestResponse> CreatePrivacyRequestAsync(string memberId, PrivacyRequestCreate request, CancellationToken ct);
    Task<SyncResponse> GetChangesAsync(string memberId, long after, int limit, CancellationToken ct);
}

public sealed record ProtectedIdentity(string Provider, string SubjectHash, byte[] SubjectCiphertext, string DisplayHint, bool IsPrimary, bool IsVerified);
public sealed record NewMemberRegistration(
    string MemberId,
    string CommunityId,
    string Locale,
    string DisplayName,
    string? Headline,
    string? ProfessionalSummary,
    string? RoleCategory,
    string Visibility,
    decimal CompletenessScore,
    IReadOnlyList<ProtectedIdentity> Identities);

public interface IIdentityProtector
{
    ProtectedIdentity ProtectEmail(string memberId, string value, bool isPrimary);
    ProtectedIdentity ProtectPhone(string memberId, string value, bool isPrimary);
    // Keyed lookup hash for an email, identical to the SubjectHash ProtectEmail stores.
    string EmailLookupHash(string email);
    string Unprotect(string memberId, string provider, byte[] ciphertext);
}

public sealed class CoreService(ICoreStore store, IIdentityProtector identityProtector) : ICoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<MemberRegistrationResponse> RegisterMemberAsync(string communityId, MemberCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(communityId) || communityId.Length > 64) throw new DomainException("COMMUNITY_ID_INVALID");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128) throw new DomainException("IDEMPOTENCY_KEY_REQUIRED");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 150) throw new DomainException("PROFILE_INVALID");
        if (request.Headline?.Length > 240 || request.ProfessionalSummary?.Length > 2000 || request.RoleCategory?.Length > 64) throw new DomainException("PROFILE_INVALID");
        if (request.Visibility is not ("PUBLIC" or "MEMBERS" or "CONNECTED" or "HIDDEN")) throw new DomainException("PROFILE_VISIBILITY_INVALID");
        if (string.IsNullOrWhiteSpace(request.Locale) || request.Locale.Length > 16) throw new DomainException("MEMBER_LOCALE_INVALID");
        if (string.IsNullOrWhiteSpace(request.Email) && string.IsNullOrWhiteSpace(request.Phone)) throw new DomainException("MEMBER_IDENTITY_REQUIRED");

        var memberHash = Hash("iam.register_member", communityId, idempotencyKey);
        var memberId = $"mem_{memberHash[..32]}";
        var identities = new List<ProtectedIdentity>(2);
        if (!string.IsNullOrWhiteSpace(request.Email)) identities.Add(identityProtector.ProtectEmail(memberId, request.Email, true));
        if (!string.IsNullOrWhiteSpace(request.Phone)) identities.Add(identityProtector.ProtectPhone(memberId, request.Phone, identities.Count == 0));

        var member = new NewMemberRegistration(
            memberId,
            communityId,
            request.Locale.Trim(),
            request.DisplayName.Trim(),
            request.Headline?.Trim(),
            request.ProfessionalSummary?.Trim(),
            request.RoleCategory?.Trim(),
            request.Visibility,
            new[] { request.DisplayName, request.Headline, request.ProfessionalSummary, request.RoleCategory }.Count(value => !string.IsNullOrWhiteSpace(value)) * 25m,
            identities);
        await store.CreateMemberAsync(member, ct);
        return new MemberRegistrationResponse(memberId, identities.FirstOrDefault(x => x.Provider == "EMAIL")?.DisplayHint,
            identities.FirstOrDefault(x => x.Provider == "PHONE")?.DisplayHint, "DRAFT", "\"1\"");
    }

    public Task<MemberLookupResponse> LookupMemberByEmailAsync(MemberLookupRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Email)) throw new DomainException("MEMBER_IDENTITY_REQUIRED");
        var subjectHash = identityProtector.EmailLookupHash(request.Email);
        var memberId = store.Identities
            .Where(x => x.Provider == "EMAIL" && x.ProviderSubjectHash == subjectHash && x.Status == "ACTIVE")
            .Select(x => x.MemberId)
            .FirstOrDefault() ?? throw new DomainException("MEMBER_NOT_REGISTERED", 404);
        var profile = store.Profiles.SingleOrDefault(x => x.MemberId == memberId) ?? throw new DomainException("MEMBER_NOT_REGISTERED", 404);
        var mapped = Map(profile);
        return Task.FromResult(new MemberLookupResponse(mapped.MemberId, profile.DisplayName, mapped.ProfileStatus, mapped.ETag));
    }

    public Task ProvisionMemberAsync(string memberId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 64) throw new DomainException("MEMBER_ID_INVALID");
        return store.EnsureMemberAsync(memberId, ct);
    }

    public Task<ProfileResponse> GetOwnProfileAsync(string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = store.Profiles.SingleOrDefault(x => x.MemberId == memberId) ?? throw new DomainException("PROFILE_NOT_FOUND", 404);
        return Task.FromResult(Map(profile));
    }

    public Task<ProfileResponse> GetVisibleProfileAsync(string actorId, string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = FindProfile(memberId);
        if ((profile.Visibility == "HIDDEN" || profile.Visibility == "CONNECTED" && !IsConnected(actorId, memberId)) && actorId != memberId)
            throw new DomainException("PROFILE_NOT_FOUND", 404);
        // Names stay hidden until members connect (a Commit is accepted).
        var mapped = Map(profile);
        return Task.FromResult(actorId == memberId || IsConnected(actorId, memberId) ? mapped : mapped with { DisplayName = null });
    }

    public async Task<ProfileResponse> UpdateProfileAsync(string memberId, ProfileUpdateRequest request, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200) throw new DomainException("PROFILE_INVALID");
        if (request.Visibility is not ("PUBLIC" or "MEMBERS" or "CONNECTED" or "HIDDEN")) throw new DomainException("PROFILE_VISIBILITY_INVALID");
        var profile = store.Profiles.SingleOrDefault(x => x.MemberId == memberId);
        if (profile is null)
        {
            if (!string.IsNullOrWhiteSpace(ifMatch)) throw new DomainException("RESOURCE_VERSION_CONFLICT", 409);
            profile = new MemberProfile { MemberId = memberId };
            store.Add(profile);
        }
        else
        {
            if (profile.Status is not ("DRAFT" or "ACTIVE")) throw new DomainException("PROFILE_NOT_EDITABLE", 409);
            var isInitialDraft = profile.Status == "DRAFT" && profile.PublishedAt is null && string.IsNullOrWhiteSpace(profile.DisplayName);
            if (string.IsNullOrWhiteSpace(ifMatch) && !isInitialDraft) throw new DomainException("IF_MATCH_REQUIRED", 428);
            if (!string.IsNullOrWhiteSpace(ifMatch) && !string.Equals(ifMatch.Trim('"'), profile.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new DomainException("RESOURCE_VERSION_CONFLICT", 409);
            if (!store.IsRelational) profile.Version++;
        }
        profile.DisplayName = request.DisplayName.Trim();
        profile.Headline = request.Headline?.Trim();
        profile.Biography = request.ProfessionalSummary?.Trim();
        profile.Sector = request.RoleCategory?.Trim();
        profile.Visibility = request.Visibility;
        profile.Status = "ACTIVE";
        profile.CompletenessScore = CalculateCompleteness(profile);
        profile.PublishedAt ??= DateTimeOffset.UtcNow;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        AddChange(memberId, "PROFILE", memberId, "UPSERT", new { profile.DisplayName, profile.Headline, professional_summary = profile.Biography, role_category = profile.Sector, profile.Visibility, profile.Version });
        AddOutbox("MEMBER", memberId, "MemberProfileChanged.v1", new { member_id = memberId, profile.Version });
        await store.SaveAsync(ct);
        return Map(profile);
    }

    public Task<ActiveConsentPolicyResponse> GetActiveConsentPolicyAsync(string purposeCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var purpose = purposeCode.Trim().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        // Same rule RecordConsentAsync uses to accept a policy version.
        var policy = store.ConsentPolicies
            .Where(x => x.PurposeCode == purpose && x.EffectiveFrom <= now && (x.RetiredAt == null || x.RetiredAt > now))
            .OrderByDescending(x => x.EffectiveFrom)
            .FirstOrDefault() ?? throw new DomainException("CONSENT_POLICY_NOT_ACTIVE", 404);
        return Task.FromResult(new ActiveConsentPolicyResponse(policy.PurposeCode, policy.Version, policy.Locale, policy.EffectiveFrom));
    }

    public async Task<ConsentResponse> RecordConsentAsync(string memberId, ConsentRequest request, CancellationToken ct)
    {
        if (request.Decision is not ("GRANTED" or "WITHDRAWN" or "DENIED") || string.IsNullOrWhiteSpace(request.PurposeCode))
            throw new DomainException("CONSENT_INVALID");
        if (request.CaptureChannel is not ("MOBILE" or "WEB_ADMIN" or "SUPPORT")) throw new DomainException("CONSENT_CAPTURE_CHANNEL_INVALID");
        var now = DateTimeOffset.UtcNow;
        var policy = store.ConsentPolicies.Where(x => x.PurposeCode == request.PurposeCode && x.Version == request.PolicyVersion && x.EffectiveFrom <= now && (x.RetiredAt == null || x.RetiredAt > now)).OrderByDescending(x => x.EffectiveFrom).FirstOrDefault()
            ?? throw new DomainException("CONSENT_POLICY_NOT_ACTIVE", 409);
        var row = new MemberConsent { MemberId = memberId, PolicyId = policy.PolicyId, Decision = request.Decision, CaptureChannel = request.CaptureChannel, EvidenceJson = request.Evidence is null ? null : JsonSerializer.Serialize(request.Evidence, JsonOptions), WithdrawnAt = request.Decision == "WITHDRAWN" ? now : null };
        store.Add(row);
        if (request.PurposeCode == "LIVE_MODE" && request.Decision != "GRANTED")
            foreach (var session in store.LiveSessions.Where(x => x.MemberId == memberId && x.Status == "ACTIVE").ToArray()) { session.Status = "DISABLED"; session.RevokedAt = DateTimeOffset.UtcNow; }
        AddChange(memberId, "CONSENT", request.PurposeCode, "UPSERT", new { request.PurposeCode, request.PolicyVersion, request.Decision, row.CapturedAt });
        AddOutbox("MEMBER", memberId, "MemberConsentChanged.v1", new { member_id = memberId, purpose_code = request.PurposeCode, decision = request.Decision });
        await store.SaveAsync(ct);
        return new(row.Id, row.PolicyId, request.PurposeCode, request.PolicyVersion, request.Decision, row.CapturedAt, row.WithdrawnAt);
    }

    public Task<IReadOnlyList<EventResponse>> GetEventsAsync(string? memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var community = memberId is null ? null : MemberCommunity(memberId);
        var events = store.Events.Where(x => (x.Status == "PUBLISHED" || x.Status == "ACTIVE") && (community == null || x.CommunityId == community)).OrderBy(x => x.StartsAt).ToArray();
        var eventIds = events.Select(x => x.EventId).ToArray();
        var now = DateTimeOffset.UtcNow;

        var liveCounts = store.LiveSessions
            .Where(x => eventIds.Contains(x.EventId) && x.Status == "ACTIVE" && x.ActiveUntil > now)
            .Select(x => x.EventId)
            .ToList()
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var attendeeCounts = store.Registrations
            .Where(x => eventIds.Contains(x.EventId) && x.Status != "CANCELLED")
            .Select(x => x.EventId)
            .ToList()
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var venueIds = events.Where(x => x.VenueId != null).Select(x => x.VenueId!).Distinct().ToArray();
        var venueNames = store.Venues.Where(x => venueIds.Contains(x.VenueId)).ToDictionary(x => x.VenueId, x => x.Name);

        // Same statuses that allow Live Mode, so is_registered matches what the member can do next.
        HashSet<string>? registeredEventIds = memberId is null ? null : store.Registrations
            .Where(x => eventIds.Contains(x.EventId) && x.MemberId == memberId && (x.Status == "REGISTERED" || x.Status == "CHECKED_IN"))
            .Select(x => x.EventId)
            .ToHashSet();

        IReadOnlyList<EventResponse> result = events
            .Select(x => Map(x, x.VenueId is not null ? venueNames.GetValueOrDefault(x.VenueId) : null, attendeeCounts.GetValueOrDefault(x.EventId, 0), liveCounts.GetValueOrDefault(x.EventId, 0), registeredEventIds?.Contains(x.EventId)))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<EventAttendeesResponse> GetEventAttendeesAsync(string memberId, string eventId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = FindEventFor(memberId, eventId);
        string[] attending = ["REGISTERED", "CHECKED_IN"];
        if (!store.Registrations.Any(x => x.EventId == eventId && x.MemberId == memberId && attending.Contains(x.Status)))
            throw new DomainException("EVENT_REGISTRATION_REQUIRED", 403);

        var registrations = store.Registrations
            .Where(x => x.EventId == eventId && x.MemberId != memberId && attending.Contains(x.Status))
            .OrderBy(x => x.RegisteredAt)
            .Select(x => new { x.MemberId, x.RegisteredAt })
            .ToList();
        var ids = registrations.Select(x => x.MemberId).ToArray();
        var blocked = store.Blocks
            .Where(x => x.RemovedAt == null && ((x.BlockerMemberId == memberId && ids.Contains(x.BlockedMemberId)) || (x.BlockedMemberId == memberId && ids.Contains(x.BlockerMemberId))))
            .Select(x => x.BlockerMemberId == memberId ? x.BlockedMemberId : x.BlockerMemberId)
            .ToHashSet();
        var profiles = store.Profiles
            .Where(x => ids.Contains(x.MemberId) && x.Status == "ACTIVE" && x.Visibility != "HIDDEN")
            .ToDictionary(x => x.MemberId);

        var visible = registrations
            .Where(x => !blocked.Contains(x.MemberId) && profiles.ContainsKey(x.MemberId))
            .Select(x => profiles[x.MemberId])
            .Select(p => new EventAttendeeResponse(p.MemberId, p.Headline, p.Sector))
            .ToList();
        return Task.FromResult(new EventAttendeesResponse(visible.Take(50).ToArray(), visible.Count));
    }

    public async Task<RegistrationResponse> RegisterAsync(string memberId, string eventId, CancellationToken ct)
    {
        _ = FindEventFor(memberId, eventId);
        var row = store.Registrations.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId);
        if (row is null) { row = new EventRegistration { EventId = eventId, MemberId = memberId }; store.Add(row); }
        AddChange(memberId, "EVENT_REGISTRATION", eventId, "UPSERT", new { event_id = eventId, status = row.Status });
        AddOutbox("EVENT_REGISTRATION", AggregateKey(eventId, memberId), "EventRegistrationChanged.v1", new { event_id = eventId, member_id = memberId, status = row.Status });
        await store.SaveAsync(ct);
        return new(row.EventId, row.MemberId, row.Status, row.RegisteredAt);
    }

    public async Task<LiveModeResponse> StartLiveModeAsync(string memberId, string eventId, LiveModeRequest request, CancellationToken ct)
    {
        var evt = FindEventFor(memberId, eventId);
        if (request.DurationMinutes is < 5 or > 240) throw new DomainException("LIVE_MODE_DURATION_INVALID");
        // Mirror event.enforce_live_mode_session_consent so callers get a specific code, not RESOURCE_STATE_CONFLICT.
        var account = store.Accounts.SingleOrDefault(x => x.MemberId == memberId);
        if (account is not null && account.Status != "ACTIVE") throw new DomainException("MEMBER_NOT_ACTIVE", 403);
        if (!store.Registrations.Any(x => x.EventId == eventId && x.MemberId == memberId && (x.Status == "REGISTERED" || x.Status == "CHECKED_IN"))) throw new DomainException("EVENT_REGISTRATION_REQUIRED", 403);
        if (!HasConsent(memberId, "LIVE_MODE")) throw new DomainException("LIVE_MODE_CONSENT_REQUIRED", 403);
        var now = DateTimeOffset.UtcNow;
        if (now < evt.StartsAt || evt.EndsAt <= now) throw new DomainException("EVENT_NOT_LIVE", 409);
        if (!evt.LiveModeEnabled) throw new DomainException("LIVE_MODE_DISABLED", 409);
        var consent = LatestGrantedConsent(memberId, "LIVE_MODE") ?? throw new DomainException("LIVE_MODE_CONSENT_REQUIRED", 403);
        // A published event becomes ACTIVE once it has started; the database only allows Live Mode
        // sessions (and matching eligibility) for ACTIVE events with an active matching policy.
        // Saved before the session so the session insert sees the ACTIVE event.
        var activated = false;
        if (evt.Status == "PUBLISHED") { evt.Status = "ACTIVE"; evt.UpdatedAt = now; activated = true; }
        if (EventMatchingPolicies.EnsureDefault(store, evt.EventId, now) || activated) await store.SaveAsync(ct);
        var existing = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE");
        // Extending also moves the session to the latest grant: the database rejects an active
        // session that points at an older LIVE_MODE consent (the app records one before each Go Live).
        if (existing is not null) { existing.ActiveUntil = Min(now.AddMinutes(request.DurationMinutes), evt.EndsAt); existing.ConsentRecordId = consent.Id; await store.SaveAsync(ct); return Map(existing); }
        var row = new LiveModeSession { EventId = eventId, MemberId = memberId, ConsentRecordId = consent.Id, ActiveUntil = Min(now.AddMinutes(request.DurationMinutes), evt.EndsAt) };
        store.Add(row);
        AddChange(memberId, "LIVE_MODE", eventId, "UPSERT", new { row.SessionId, event_id = eventId, row.Status, row.ActiveUntil });
        AddOutbox("LIVE_MODE", row.SessionId, "LiveModeChanged.v1", new { session_id = row.SessionId, event_id = eventId, member_id = memberId, row.Status, row.ActiveUntil });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public async Task StopLiveModeAsync(string memberId, string eventId, CancellationToken ct)
    {
        var session = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE") ?? throw new DomainException("LIVE_MODE_NOT_ACTIVE", 404);
        session.Status = "DISABLED"; session.RevokedAt = DateTimeOffset.UtcNow;
        AddChange(memberId, "LIVE_MODE", eventId, "DELETE", null);
        AddOutbox("LIVE_MODE", session.SessionId, "LiveModeChanged.v1", new { session_id = session.SessionId, event_id = eventId, member_id = memberId, session.Status });
        await store.SaveAsync(ct);
    }

    public async Task RecordPresenceAsync(string memberId, string eventId, PresenceRequest request, CancellationToken ct)
    {
        _ = FindEventFor(memberId, eventId);
        if (string.IsNullOrWhiteSpace(request.CoarseCell) || request.CoarseCell.Length > 32 || request.Source is not ("CHECK_IN" or "FOREGROUND_GEO" or "VENUE_ZONE")) throw new DomainException("PRESENCE_INVALID");
        var session = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE" && x.ActiveUntil > DateTimeOffset.UtcNow)
            ?? throw new DomainException("LIVE_MODE_NOT_ACTIVE", 403);
        var observed = request.ObservedAt == default ? DateTimeOffset.UtcNow : request.ObservedAt.ToUniversalTime();
        if (Math.Abs((DateTimeOffset.UtcNow - observed).TotalMinutes) > 10) throw new DomainException("PRESENCE_STALE");
        store.Add(new EventPresence { SessionId = session.SessionId, CoarseCell = request.CoarseCell, ObservedAt = observed, ExpiresAt = Min(session.ActiveUntil, observed.AddHours(2)), Source = request.Source });
        AddOutbox("LIVE_MODE", session.SessionId, "EventPresenceRefreshed.v1", new { session_id = session.SessionId, event_id = eventId, member_id = memberId, expires_at = Min(session.ActiveUntil, observed.AddHours(2)) });
        await store.SaveAsync(ct);
    }

    public async Task<ConnectionRequestResponse> CreateConnectionRequestAsync(string senderId, ConnectionRequestCreate request, string idempotencyKey, CancellationToken ct)
    {
        if (senderId == request.RecipientMemberId) throw new DomainException("SELF_CONNECTION_INVALID");
        if (request.ContextId is not null) return await CreateCommitAsync(senderId, request, idempotencyKey, ct);
        _ = FindProfile(senderId);
        _ = FindProfile(request.RecipientMemberId);
        if (IsBlocked(senderId, request.RecipientMemberId)) throw new DomainException("CONNECTION_NOT_ALLOWED", 403);
        if (IsConnected(senderId, request.RecipientMemberId)) throw new DomainException("CONNECTION_EXISTS", 409);
        var existing = store.ConnectionRequests.SingleOrDefault(x => x.Status == "PENDING" && ((x.SenderMemberId == senderId && x.RecipientMemberId == request.RecipientMemberId) || (x.SenderMemberId == request.RecipientMemberId && x.RecipientMemberId == senderId)));
        if (existing is not null) return Map(existing);
        if (request.Note?.Length > 500) throw new DomainException("CONNECTION_NOTE_INVALID");
        var row = new ConnectionRequest { SenderMemberId = senderId, RecipientMemberId = request.RecipientMemberId, ContextId = request.ContextId, MatchResultId = request.MatchResultId, Note = request.Note?.Trim(), ExpiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(request.ExpiresInDays, 1, 30)) };
        store.Add(row);
        AddChange(request.RecipientMemberId, "CONNECTION_REQUEST", row.RequestId, "UPSERT", new { row.RequestId, row.SenderMemberId, row.Status, row.ExpiresAt });
        AddOutbox("CONNECTION_REQUEST", row.RequestId, "ConnectionRequestCreated.v1", new { request_id = row.RequestId, sender_member_id = senderId, recipient_member_id = request.RecipientMemberId });
        await store.SaveAsync(ct);
        return Map(row);
    }

    private async Task<ConnectionRequestResponse> CreateCommitAsync(string senderId, ConnectionRequestCreate request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128) throw new DomainException("IDEMPOTENCY_KEY_REQUIRED");
        // The ID comes from the sender's Idempotency-Key, so a retry finds and returns the same Commit.
        var requestId = $"cmt_{Hash("social.commit", senderId, idempotencyKey)[..32]}";
        var replay = store.ConnectionRequests.SingleOrDefault(x => x.RequestId == requestId);
        if (replay is not null)
        {
            if (replay.RecipientMemberId != request.RecipientMemberId || replay.ContextId != request.ContextId) throw new DomainException("IDEMPOTENCY_KEY_REUSED", 409);
            return Map(replay, CommitPlans.LimitPerEvent - CommitsUsed(senderId, replay.ContextId!));
        }

        var plan = CommitPlans.Validate(request.Plan);
        var evt = FindEventFor(senderId, request.ContextId!);
        // No meeting spots exist yet, so any SPOT is unknown.
        if (plan.WhereType == "SPOT") throw new DomainException("MEETING_SPOT_NOT_FOUND", 404);
        _ = FindProfile(senderId);
        // A blocked or hidden recipient looks like an unknown one, so a block is never revealed.
        if (!store.Profiles.Any(x => x.MemberId == request.RecipientMemberId && x.Status == "ACTIVE") || IsBlocked(senderId, request.RecipientMemberId))
            throw new DomainException("PROFILE_NOT_FOUND", 404);
        if (!IsAttending(evt.EventId, senderId)) throw new DomainException("EVENT_REGISTRATION_REQUIRED", 403);
        var now = DateTimeOffset.UtcNow;
        if (!store.LiveSessions.Any(x => x.EventId == evt.EventId && x.MemberId == senderId && x.Status == "ACTIVE" && x.ActiveUntil > now))
            throw new DomainException("COMMIT_SENDER_NOT_LIVE", 403);
        if (!IsAttending(evt.EventId, request.RecipientMemberId)) throw new DomainException("EVENT_REGISTRATION_REQUIRED", 403);
        if (IsConnected(senderId, request.RecipientMemberId)) throw new DomainException("CONNECTION_EXISTS", 409);

        var toRecipient = store.ConnectionRequests.Where(x => x.SenderMemberId == senderId && x.RecipientMemberId == request.RecipientMemberId).ToArray();
        // A declined Commit still counts as sent until it expires, so the sender can't tell.
        if (toRecipient.Any(x => x.ExpiresAt > now && (x.Status == "PENDING" || (x.Status == "DECLINED" && x.ContextId == evt.EventId))))
            throw new DomainException("COMMIT_ALREADY_SENT", 409);
        var used = CommitsUsed(senderId, evt.EventId);
        if (used >= CommitPlans.LimitPerEvent) throw new DomainException("COMMIT_LIMIT_REACHED", 409);
        var note = CommitPlans.Encode(plan, request.Note);
        if (note.Length > 500) throw new DomainException("CONNECTION_NOTE_INVALID");

        // Expired requests are only filtered at read time; mark them so the open-pair index frees up.
        foreach (var stale in toRecipient.Where(x => x.Status == "PENDING" && x.ExpiresAt <= now)) { stale.Status = "EXPIRED"; stale.UpdatedAt = now; }
        var row = new ConnectionRequest { RequestId = requestId, SenderMemberId = senderId, RecipientMemberId = request.RecipientMemberId, ContextId = evt.EventId, MatchResultId = request.MatchResultId, Note = note, ExpiresAt = evt.EndsAt };
        store.Add(row);
        AddChange(request.RecipientMemberId, "CONNECTION_REQUEST", row.RequestId, "UPSERT", new { request_id = row.RequestId, event_id = evt.EventId, row.Status, row.ExpiresAt });
        AddOutbox("CONNECTION_REQUEST", row.RequestId, "ConnectionRequestCreated.v1", new { request_id = row.RequestId, sender_member_id = senderId, recipient_member_id = request.RecipientMemberId, context_id = evt.EventId });
        await store.SaveAsync(ct);
        return Map(row, CommitPlans.LimitPerEvent - used - 1);
    }

    public Task<CommitQuotaResponse> GetCommitQuotaAsync(string memberId, string eventId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var evt = FindEventFor(memberId, eventId);
        var used = CommitsUsed(memberId, evt.EventId);
        return Task.FromResult(new CommitQuotaResponse(CommitPlans.LimitPerEvent, used, Math.Max(0, CommitPlans.LimitPerEvent - used)));
    }

    public Task<IReadOnlyList<MeetingSpotResponse>> GetMeetingSpotsAsync(string memberId, string eventId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = FindEventFor(memberId, eventId);
        // The database has no meeting-spot table yet; the app always offers "Their choice".
        return Task.FromResult<IReadOnlyList<MeetingSpotResponse>>([]);
    }

    public async Task<IReadOnlyList<IncomingCommitResponse>> GetIncomingCommitsAsync(string memberId, string? eventId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = store.ConnectionRequests
            .Where(x => x.RecipientMemberId == memberId && x.Status == "PENDING" && x.ExpiresAt > now && x.ContextId != null && (eventId == null || x.ContextId == eventId))
            .OrderByDescending(x => x.CreatedAt).Take(100).ToArray();
        var blocked = BlockedAmong(memberId, rows.Select(x => x.SenderMemberId).Distinct().ToArray());
        rows = rows.Where(x => !blocked.Contains(x.SenderMemberId)).ToArray();
        var events = CommitEvents(rows);
        var profiles = ActiveProfiles(rows.Select(x => x.SenderMemberId));
        var matchIds = rows.Where(x => x.MatchResultId != null).Select(x => x.MatchResultId!.Value).Distinct().ToArray();
        var scores = matchIds.Length == 0 ? new Dictionary<long, MatchScore>() : (await store.GetMatchScoresAsync(matchIds, ct)).ToDictionary(x => x.MatchResultId);

        return rows.Select(x =>
        {
            var profile = profiles.GetValueOrDefault(x.SenderMemberId);
            // A score counts only when it is this sender's match for this recipient.
            var score = x.MatchResultId is { } id && scores.TryGetValue(id, out var s) && s.RequesterId == x.SenderMemberId && s.CandidateId == memberId ? s.Score : (decimal?)null;
            return new IncomingCommitResponse(x.RequestId, events[x.ContextId!], PlanOf(x), new CommitSenderSummary(profile?.Headline, profile?.Sector, Score: score), x.ExpiresAt, x.CreatedAt);
        }).ToArray();
    }

    public Task<IReadOnlyList<OutgoingCommitResponse>> GetOutgoingCommitsAsync(string memberId, string? eventId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var rows = store.ConnectionRequests
            .Where(x => x.SenderMemberId == memberId && x.ContextId != null && (eventId == null || x.ContextId == eventId))
            .OrderByDescending(x => x.CreatedAt).Take(100).ToArray();
        var events = CommitEvents(rows);
        var profiles = ActiveProfiles(rows.Select(x => x.RecipientMemberId));
        var acceptedIds = rows.Where(x => x.Status == "ACCEPTED").Select(x => x.RequestId).ToArray();
        var conversations = (from connection in store.Connections
                             join conversation in store.Conversations on connection.ConnectionId equals conversation.ConnectionId
                             where acceptedIds.Contains(connection.AcceptedRequestId)
                             select new { connection.AcceptedRequestId, conversation.ConversationId }).ToDictionary(x => x.AcceptedRequestId, x => x.ConversationId);

        IReadOnlyList<OutgoingCommitResponse> result = rows.Select(x =>
        {
            var profile = profiles.GetValueOrDefault(x.RecipientMemberId);
            var accepted = x.Status == "ACCEPTED";
            // DECLINED is reported exactly like PENDING, then EXPIRED once the room closes.
            var status = accepted ? "ACCEPTED" : x.Status is "PENDING" or "DECLINED" && x.ExpiresAt > now ? "PENDING" : "EXPIRED";
            return new OutgoingCommitResponse(x.RequestId, events[x.ContextId!], PlanOf(x), new CommitRecipientSummary(profile?.Headline, profile?.Sector, accepted ? profile?.DisplayName : null),
                status, x.ExpiresAt, x.CreatedAt, accepted ? conversations.GetValueOrDefault(x.RequestId) : null);
        }).ToArray();
        return Task.FromResult(result);
    }

    public async Task<ConnectionResponse?> DecideConnectionRequestAsync(string memberId, string requestId, ConnectionDecisionRequest request, string idempotencyKey, CancellationToken ct)
    {
        var row = store.ConnectionRequests.SingleOrDefault(x => x.RequestId == requestId && x.RecipientMemberId == memberId) ?? throw new DomainException("CONNECTION_REQUEST_NOT_FOUND", 404);
        var isCommit = row.ContextId is not null;
        if (isCommit && row.Status != "PENDING") throw new DomainException("COMMIT_NOT_PENDING", 409);
        if (isCommit && row.ExpiresAt <= DateTimeOffset.UtcNow) throw new DomainException("COMMIT_EXPIRED", 409);
        if (row.Status != "PENDING" || row.ExpiresAt <= DateTimeOffset.UtcNow) throw new DomainException("CONNECTION_REQUEST_NOT_PENDING", 409);
        if (request.Decision is not ("ACCEPT" or "DECLINE")) throw new DomainException("CONNECTION_DECISION_INVALID");
        if (request.Decision == "DECLINE")
        {
            row.Status = "DECLINED"; row.RespondedAt = DateTimeOffset.UtcNow; row.UpdatedAt = row.RespondedAt.Value;
            await store.SaveAsync(ct);
            return isCommit ? null : new("", row.SenderMemberId, row.Status, "");
        }
        var accepted = await AcceptAsync(memberId, row, requestId, idempotencyKey, ct);
        // Names are revealed from the moment of Accept.
        return accepted with { DisplayName = store.Profiles.Where(x => x.MemberId == row.SenderMemberId).Select(x => x.DisplayName).FirstOrDefault() };
    }

    private async Task<ConnectionResponse> AcceptAsync(string memberId, ConnectionRequest row, string requestId, string idempotencyKey, CancellationToken ct)
    {
        var request = new ConnectionDecisionRequest("ACCEPT");
        if (IsBlocked(row.SenderMemberId, row.RecipientMemberId)) throw new DomainException("CONNECTION_NOT_ALLOWED", 403);
        if (store.IsRelational)
        {
            var connectionId = Hash("connection", requestId, memberId, idempotencyKey)[..32];
            var conversationId = Hash("conversation", requestId, memberId, idempotencyKey)[..32];
            var saved = await store.AcceptConnectionRequestAsync(requestId, memberId, connectionId, conversationId, idempotencyKey, Hash(requestId, memberId, request.Decision), ct);
            return new(saved.ConnectionId, row.SenderMemberId, "ACTIVE", saved.ConversationId);
        }
        row.Status = "ACCEPTED";
        var pair = Pair(row.SenderMemberId, row.RecipientMemberId);
        row.RespondedAt = DateTimeOffset.UtcNow;
        var connection = new Connection { MemberLowId = pair.Low, MemberHighId = pair.High, AcceptedRequestId = row.RequestId };
        var conversation = new Conversation { ConnectionId = connection.ConnectionId };
        store.Add(connection); store.Add(conversation);
        store.Add(new ConversationParticipant { ConversationId = conversation.ConversationId, MemberId = row.SenderMemberId });
        store.Add(new ConversationParticipant { ConversationId = conversation.ConversationId, MemberId = row.RecipientMemberId });
        foreach (var id in new[] { row.SenderMemberId, row.RecipientMemberId }) AddChange(id, "CONNECTION", connection.ConnectionId, "UPSERT", new { connection.ConnectionId, member_id = id == row.SenderMemberId ? row.RecipientMemberId : row.SenderMemberId, conversation.ConversationId, connection.Status });
        AddOutbox("CONNECTION", connection.ConnectionId, "ConnectionAccepted.v1", new { connection_id = connection.ConnectionId, member_low_id = pair.Low, member_high_id = pair.High, conversation_id = conversation.ConversationId });
        await store.SaveAsync(ct);
        return new(connection.ConnectionId, row.SenderMemberId, connection.Status, conversation.ConversationId);
    }

    public async Task BlockAsync(string memberId, BlockRequest request, CancellationToken ct)
    {
        if (memberId == request.MemberId) throw new DomainException("SELF_BLOCK_INVALID");
        if (!store.Blocks.Any(x => x.BlockerMemberId == memberId && x.BlockedMemberId == request.MemberId && x.RemovedAt == null)) store.Add(new MemberBlock { BlockerMemberId = memberId, BlockedMemberId = request.MemberId });
        foreach (var connection in store.Connections.Where(x => x.Status == "ACTIVE" && ((x.MemberLowId == memberId && x.MemberHighId == request.MemberId) || (x.MemberLowId == request.MemberId && x.MemberHighId == memberId))).ToArray()) { connection.Status = "DISCONNECTED"; connection.DisconnectedAt = DateTimeOffset.UtcNow; }
        foreach (var id in new[] { memberId, request.MemberId }) AddChange(id, "CONNECTION", PairKey(memberId, request.MemberId), "DELETE", null);
        AddOutbox("MEMBER_RELATIONSHIP", AggregateKey(Pair(memberId, request.MemberId).Low, Pair(memberId, request.MemberId).High), "MemberBlocked.v1", new { actor_member_id = memberId, target_member_id = request.MemberId });
        await store.SaveAsync(ct);
    }

    public Task<IReadOnlyList<ConnectionResponse>> GetConnectionsAsync(string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = store.Connections.Where(x => x.Status == "ACTIVE" && (x.MemberLowId == memberId || x.MemberHighId == memberId)).AsEnumerable().Select(x => new ConnectionResponse(x.ConnectionId, x.MemberLowId == memberId ? x.MemberHighId : x.MemberLowId, x.Status, store.Conversations.Single(c => c.ConnectionId == x.ConnectionId).ConversationId)).ToArray();
        return Task.FromResult<IReadOnlyList<ConnectionResponse>>(result);
    }

    public Task<ConversationListResponse> GetConversationsAsync(string memberId, string? cursor, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bounded = Math.Clamp(limit, 1, 50);
        var after = DecodeConversationCursor(cursor);
        var connections = store.Connections.Where(x => x.Status == "ACTIVE" && (x.MemberLowId == memberId || x.MemberHighId == memberId)).ToArray();
        var blocked = BlockedAmong(memberId, connections.Select(x => OtherMember(x, memberId)).ToArray());
        // A member's active connections are bounded, so the page is cut after building every summary.
        var ordered = BuildConversations(memberId, connections.Where(x => !blocked.Contains(OtherMember(x, memberId))).ToArray())
            .OrderByDescending(x => x.LastActivityAt).ThenByDescending(x => x.ConversationId, StringComparer.Ordinal);
        var remaining = after is null ? ordered : ordered.Where(x => x.LastActivityAt < after.Value.At || (x.LastActivityAt == after.Value.At && string.CompareOrdinal(x.ConversationId, after.Value.Id) < 0));
        var rows = remaining.Take(bounded + 1).ToArray();
        var page = rows.Take(bounded).ToArray();
        var hasMore = rows.Length > bounded;
        return Task.FromResult(new ConversationListResponse(page, hasMore ? EncodeConversationCursor(page[^1]) : null, hasMore));
    }

    public Task<ConversationResponse> GetConversationAsync(string memberId, string conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireConversationMember(memberId, conversationId);
        return Task.FromResult(BuildConversations(memberId, [connection]).Single(x => x.ConversationId == conversationId));
    }

    public async Task<ConversationResponse> MarkConversationReadAsync(string memberId, string conversationId, ConversationReadRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.LastReadMessageId) || request.LastReadMessageId.Length > 64) throw new DomainException("CONVERSATION_READ_INVALID");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128) throw new DomainException("IDEMPOTENCY_KEY_REQUIRED");
        var connection = RequireConversationMember(memberId, conversationId);
        var upTo = store.Messages.SingleOrDefault(x => x.MessageId == request.LastReadMessageId && x.ConversationId == conversationId) ?? throw new DomainException("MESSAGE_NOT_FOUND", 404);
        // Only messages still unread are touched, so a retry after success does nothing.
        var unread = store.Messages
            .Where(x => x.ConversationId == conversationId && x.SenderMemberId != memberId && x.DeletedAt == null && x.ServerSequence <= upTo.ServerSequence
                && !store.MessageReceipts.Any(r => r.MessageId == x.MessageId && r.MemberId == memberId && r.ReadAt != null))
            .OrderBy(x => x.ServerSequence)
            .ToArray();
        var readAt = DateTimeOffset.UtcNow;
        if (store.IsRelational)
        {
            // chat.save_message_receipt writes the receipt, the read cursor, the outbox event and sync changes.
            // Per-message keys and hashes leave out readAt so a retry of the same request replays cleanly.
            foreach (var message in unread)
                await store.SaveMessageReceiptAsync(memberId, message.MessageId, new(ReadAt: readAt), $"read:{Hash(idempotencyKey, message.MessageId)[..59]}", Hash("chat.conversation_read", memberId, message.MessageId, idempotencyKey), ct);
        }
        else if (unread.Length > 0)
        {
            foreach (var message in unread)
            {
                var row = store.MessageReceipts.SingleOrDefault(x => x.MessageId == message.MessageId && x.MemberId == memberId);
                if (row is null) { row = new MessageReceipt { MessageId = message.MessageId, MemberId = memberId }; store.Add(row); }
                row.DeliveredAt ??= readAt; row.ReadAt = readAt; row.UpdatedAt = readAt;
            }
            var participant = store.ConversationParticipants.SingleOrDefault(x => x.ConversationId == conversationId && x.MemberId == memberId);
            var current = participant?.LastReadMessageId is null ? 0 : store.Messages.Where(x => x.MessageId == participant.LastReadMessageId).Select(x => x.ServerSequence).SingleOrDefault();
            if (participant is not null && unread[^1].ServerSequence > current) participant.LastReadMessageId = unread[^1].MessageId;
            await store.SaveAsync(ct);
        }
        return BuildConversations(memberId, [connection]).Single(x => x.ConversationId == conversationId);
    }

    public async Task<ConversationResponse> MuteConversationAsync(string memberId, string conversationId, ConversationMuteRequest request, CancellationToken ct)
    {
        if (request.MutedUntil is { } until && until <= DateTimeOffset.UtcNow) throw new DomainException("CONVERSATION_MUTE_INVALID");
        var connection = RequireConversationMember(memberId, conversationId);
        var participant = store.ConversationParticipants.SingleOrDefault(x => x.ConversationId == conversationId && x.MemberId == memberId) ?? throw new DomainException("CONVERSATION_FORBIDDEN", 403);
        participant.MutedUntil = request.MutedUntil?.ToUniversalTime();
        AddChange(memberId, "CONVERSATION", conversationId, "UPSERT", new { conversation_id = conversationId, muted_until = participant.MutedUntil });
        await store.SaveAsync(ct);
        return BuildConversations(memberId, [connection]).Single(x => x.ConversationId == conversationId);
    }

    public async Task<MessageResponse> SendMessageAsync(string memberId, string conversationId, MessageCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId) || request.MessageType is not ("TEXT" or "FILE" or "SYSTEM") || request.Body?.Length > 4000 || (request.MessageType == "TEXT" && string.IsNullOrWhiteSpace(request.Body))) throw new DomainException("MESSAGE_INVALID");
        if (store.IsRelational)
            return Map(await store.SaveMessageAsync(memberId, conversationId, request, idempotencyKey, Hash(memberId, conversationId, request.MessageId, request.MessageType, request.Body, request.ClientSentAt?.ToString("O")), ct));
        var conversation = store.Conversations.SingleOrDefault(x => x.ConversationId == conversationId) ?? throw new DomainException("CONVERSATION_NOT_FOUND", 404);
        var connection = store.Connections.Single(x => x.ConnectionId == conversation.ConnectionId);
        if (connection.Status != "ACTIVE" || (connection.MemberLowId != memberId && connection.MemberHighId != memberId)) throw new DomainException("CONVERSATION_FORBIDDEN", 403);
        var other = connection.MemberLowId == memberId ? connection.MemberHighId : connection.MemberLowId;
        if (IsBlocked(memberId, other)) throw new DomainException("CONVERSATION_FORBIDDEN", 403);
        var existing = store.Messages.SingleOrDefault(x => x.MessageId == request.MessageId);
        if (existing is not null) return Map(existing);
        var sequence = store.Messages.Where(x => x.ConversationId == conversationId).Select(x => x.ServerSequence).DefaultIfEmpty().Max() + 1;
        var row = new Message { MessageId = request.MessageId, ConversationId = conversationId, SenderMemberId = memberId, MessageType = request.MessageType, Body = request.Body, ClientSentAt = request.ClientSentAt, ServerSequence = sequence };
        store.Add(row);
        foreach (var id in new[] { memberId, other }) AddChange(id, "MESSAGE", row.MessageId, "UPSERT", new { row.MessageId, row.ConversationId, row.SenderMemberId, row.Body, row.ServerSequence, row.CreatedAt });
        AddOutbox("MESSAGE", row.MessageId, "MessageCreated.v1", new { message_id = row.MessageId, conversation_id = conversationId, sender_member_id = memberId, recipient_member_id = other, row.ServerSequence });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public async Task<MessageReceiptResponse> SaveMessageReceiptAsync(string memberId, string messageId, MessageReceiptRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (request.DeliveredAt is null && request.ReadAt is null) throw new DomainException("MESSAGE_RECEIPT_INVALID");
        if (store.IsRelational)
        {
            var saved = await store.SaveMessageReceiptAsync(memberId, messageId, request, idempotencyKey, Hash(memberId, messageId, request.DeliveredAt?.ToString("O"), request.ReadAt?.ToString("O")), ct);
            return new(saved.MessageId, saved.MemberId, saved.DeliveredAt, saved.ReadAt, saved.UpdatedAt);
        }
        var message = store.Messages.SingleOrDefault(x => x.MessageId == messageId && x.DeletedAt == null) ?? throw new DomainException("MESSAGE_NOT_FOUND", 404);
        _ = RequireConversationMember(memberId, message.ConversationId);
        if (message.SenderMemberId == memberId) throw new DomainException("MESSAGE_RECEIPT_FORBIDDEN", 403);
        var deliveredAt = request.DeliveredAt ?? request.ReadAt;
        if (request.ReadAt < deliveredAt) throw new DomainException("MESSAGE_RECEIPT_INVALID");
        var row = store.MessageReceipts.SingleOrDefault(x => x.MessageId == messageId && x.MemberId == memberId);
        if (row is null) { row = new MessageReceipt { MessageId = messageId, MemberId = memberId }; store.Add(row); }
        row.DeliveredAt ??= deliveredAt;
        row.ReadAt ??= request.ReadAt;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        var participant = store.ConversationParticipants.SingleOrDefault(x => x.ConversationId == message.ConversationId && x.MemberId == memberId);
        if (request.ReadAt is not null && participant is not null) participant.LastReadMessageId = messageId;
        await store.SaveAsync(ct);
        return new(row.MessageId, row.MemberId, row.DeliveredAt, row.ReadAt, row.UpdatedAt);
    }

    public async Task DeleteMessageAsync(string memberId, string messageId, CancellationToken ct)
    {
        var message = store.Messages.SingleOrDefault(x => x.MessageId == messageId) ?? throw new DomainException("MESSAGE_NOT_FOUND", 404);
        var connection = RequireConversationMember(memberId, message.ConversationId);
        if (message.SenderMemberId != memberId) throw new DomainException("MESSAGE_DELETE_FORBIDDEN", 403);
        if (message.DeletedAt is not null) return;
        // Soft delete: the body stays in the database for moderation but is never returned again.
        var now = DateTimeOffset.UtcNow;
        message.DeletedAt = now; message.UpdatedAt = now;
        foreach (var id in new[] { memberId, OtherMember(connection, memberId) }) AddChange(id, "MESSAGE", messageId, "DELETE", null);
        await store.SaveAsync(ct);
    }

    public async Task<MessageReportResponse> ReportMessageAsync(string memberId, string messageId, MessageReportRequest request, CancellationToken ct)
    {
        var category = request.Category?.Trim().ToUpperInvariant();
        if (category is not ("SPAM" or "HARASSMENT" or "INAPPROPRIATE" or "SCAM" or "OTHER") || request.Description?.Length > 2000) throw new DomainException("MESSAGE_REPORT_INVALID");
        var message = store.Messages.SingleOrDefault(x => x.MessageId == messageId) ?? throw new DomainException("MESSAGE_NOT_FOUND", 404);
        // Either participant may still report after a block or disconnect, so only membership is checked.
        var conversation = store.Conversations.SingleOrDefault(x => x.ConversationId == message.ConversationId) ?? throw new DomainException("MESSAGE_NOT_FOUND", 404);
        if (!store.Connections.Any(x => x.ConnectionId == conversation.ConnectionId && (x.MemberLowId == memberId || x.MemberHighId == memberId))) throw new DomainException("CONVERSATION_FORBIDDEN", 403);
        if (message.SenderMemberId == memberId) throw new DomainException("MESSAGE_REPORT_FORBIDDEN", 403);
        var existing = store.MemberReports.FirstOrDefault(x => x.ReporterMemberId == memberId && x.ResourceType == "MESSAGE" && x.ResourceId == messageId);
        if (existing is not null) return new(existing.ReportId, messageId, existing.Category, existing.Status, existing.CreatedAt);
        var report = new MemberReport { ReporterMemberId = memberId, ReportedMemberId = message.SenderMemberId, ResourceType = "MESSAGE", ResourceId = messageId, Category = category, Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim() };
        store.Add(report);
        // The admin reports list reads moderation cases, so each member report opens one.
        store.Add(new ModerationCase { SourceType = "MEMBER_REPORT", SourceId = report.ReportId, SubjectMemberId = message.SenderMemberId, ResourceType = "MESSAGE", ResourceId = messageId });
        await store.SaveAsync(ct);
        return new(report.ReportId, messageId, report.Category, report.Status, report.CreatedAt);
    }

    public Task<IReadOnlyList<MessageResponse>> GetMessagesAsync(string memberId, string conversationId, long after, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = RequireConversationMember(memberId, conversationId);
        IReadOnlyList<MessageResponse> result = store.Messages.Where(x => x.ConversationId == conversationId && x.ServerSequence > after).OrderBy(x => x.ServerSequence).Take(Math.Clamp(limit, 1, 100)).AsEnumerable().Select(Map).ToArray();
        return Task.FromResult(result);
    }

    public async Task<NotificationPreferenceResponse> SetNotificationPreferenceAsync(string memberId, NotificationPreferenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PurposeCode) || ((request.QuietStartLocal is null) != (request.QuietEndLocal is null)) || (request.QuietStartLocal is not null && string.IsNullOrWhiteSpace(request.TimezoneId))) throw new DomainException("NOTIFICATION_PREFERENCE_INVALID");
        var row = store.NotificationPreferences.SingleOrDefault(x => x.MemberId == memberId && x.PurposeCode == request.PurposeCode);
        if (row is null) { row = new NotificationPreference { MemberId = memberId, PurposeCode = request.PurposeCode }; store.Add(row); }
        row.PushEnabled = request.PushEnabled; row.EmailEnabled = request.EmailEnabled; row.QuietStartLocal = request.QuietStartLocal; row.QuietEndLocal = request.QuietEndLocal; row.TimezoneId = request.TimezoneId; row.UpdatedAt = DateTimeOffset.UtcNow;
        AddChange(memberId, "NOTIFICATION", request.PurposeCode, "UPSERT", new { request.PurposeCode, request.PushEnabled, request.EmailEnabled, request.QuietStartLocal, request.QuietEndLocal, request.TimezoneId, row.UpdatedAt });
        await store.SaveAsync(ct);
        return new(row.PurposeCode, row.PushEnabled, row.EmailEnabled, row.QuietStartLocal, row.QuietEndLocal, row.TimezoneId, $"\"{row.RowVersion}\"", row.UpdatedAt);
    }

    public async Task<PrivacyRequestResponse> CreatePrivacyRequestAsync(string memberId, PrivacyRequestCreate request, CancellationToken ct)
    {
        if (request.RequestType is not ("ACCESS" or "CORRECT" or "DELETE" or "EXPORT" or "CONSENT_SUPPORT")) throw new DomainException("PRIVACY_REQUEST_INVALID");
        var row = new PrivacyRequest { MemberId = memberId, RequestType = request.RequestType, DueAt = DateTimeOffset.UtcNow.AddDays(30) };
        store.Add(row);
        AddChange(memberId, "PRIVACY_REQUEST", row.PrivacyRequestId, "UPSERT", new { row.PrivacyRequestId, row.RequestType, row.Status, row.CreatedAt, row.DueAt });
        AddOutbox("PRIVACY_REQUEST", row.PrivacyRequestId, "PrivacyRequestCreated.v1", new { privacy_request_id = row.PrivacyRequestId, member_id = memberId, row.RequestType });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public Task<SyncResponse> GetChangesAsync(string memberId, long after, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bounded = Math.Clamp(limit, 1, 200);
        var query = store.SyncChanges.Where(x => x.SyncSequence > after && (x.MemberScopeId == null || x.MemberScopeId == memberId)).OrderBy(x => x.SyncSequence);
        var rows = query.Take(bounded + 1).ToArray();
        var hasMore = rows.Length > bounded;
        var page = rows.Take(bounded).Select(x => new SyncItem(x.SyncSequence, x.ResourceType, x.ResourceId, x.ChangeType, x.ResourceVersion, x.PayloadJson is null ? null : JsonSerializer.Deserialize<object>(x.PayloadJson, JsonOptions), x.OccurredAt)).ToArray();
        return Task.FromResult(new SyncResponse(page, page.Length == 0 ? null : EncodeCursor(page[^1].Sequence), hasMore));
    }

    private MemberProfile FindProfile(string id) => store.Profiles.SingleOrDefault(x => x.MemberId == id && x.Status == "ACTIVE") ?? throw new DomainException("PROFILE_NOT_FOUND", 404);
    // A member only sees events in their own community; others behave as unknown (the database
    // refuses Live Mode across communities). Unknown accounts (InMemory fixtures) aren't scoped.
    private string? MemberCommunity(string memberId) => store.Accounts.Where(x => x.MemberId == memberId).Select(x => x.CommunityId).FirstOrDefault();
    private EventRecord FindEventFor(string memberId, string eventId)
    {
        var evt = FindEvent(eventId);
        var community = MemberCommunity(memberId);
        return community is null || evt.CommunityId == community ? evt : throw new DomainException("EVENT_NOT_FOUND", 404);
    }
    private EventRecord FindEvent(string id) => store.Events.SingleOrDefault(x => x.EventId == id && (x.Status == "PUBLISHED" || x.Status == "ACTIVE")) ?? throw new DomainException("EVENT_NOT_FOUND", 404);
    private bool HasConsent(string memberId, string purpose) => LatestGrantedConsent(memberId, purpose) is not null;
    private MemberConsent? LatestGrantedConsent(string memberId, string purpose) => (from consent in store.Consents join policy in store.ConsentPolicies on consent.PolicyId equals policy.PolicyId where consent.MemberId == memberId && policy.PurposeCode == purpose && policy.EffectiveFrom <= DateTimeOffset.UtcNow && policy.RetiredAt == null orderby consent.CapturedAt descending, consent.Id descending select consent).FirstOrDefault() is { Decision: "GRANTED", WithdrawnAt: null } value ? value : null;
    private bool IsBlocked(string a, string b) => store.Blocks.Any(x => x.RemovedAt == null && ((x.BlockerMemberId == a && x.BlockedMemberId == b) || (x.BlockerMemberId == b && x.BlockedMemberId == a)));
    private bool IsConnected(string a, string b) { var p = Pair(a, b); return store.Connections.Any(x => x.MemberLowId == p.Low && x.MemberHighId == p.High && x.Status == "ACTIVE"); }
    private Connection RequireConversationMember(string memberId, string conversationId) { var c = store.Conversations.SingleOrDefault(x => x.ConversationId == conversationId) ?? throw new DomainException("CONVERSATION_NOT_FOUND", 404); var link = store.Connections.Single(x => x.ConnectionId == c.ConnectionId); return link.Status == "ACTIVE" && (link.MemberLowId == memberId || link.MemberHighId == memberId) ? link : throw new DomainException("CONVERSATION_FORBIDDEN", 403); }
    // Every Commit sent in the event counts, whatever happened to it.
    private int CommitsUsed(string senderId, string eventId) => store.ConnectionRequests.Count(x => x.SenderMemberId == senderId && x.ContextId == eventId);
    private bool IsAttending(string eventId, string memberId) => store.Registrations.Any(x => x.EventId == eventId && x.MemberId == memberId && (x.Status == "REGISTERED" || x.Status == "CHECKED_IN"));
    private Dictionary<string, CommitEventSummary> CommitEvents(ConnectionRequest[] rows)
    {
        var ids = rows.Select(x => x.ContextId!).Distinct().ToArray();
        var names = store.Events.Where(x => ids.Contains(x.EventId)).ToDictionary(x => x.EventId, x => x.Name);
        return ids.ToDictionary(id => id, id => new CommitEventSummary(id, names.GetValueOrDefault(id, "")));
    }
    private Dictionary<string, MemberProfile> ActiveProfiles(IEnumerable<string> memberIds)
    {
        var ids = memberIds.Distinct().ToArray();
        return store.Profiles.Where(x => ids.Contains(x.MemberId) && x.Status == "ACTIVE").ToDictionary(x => x.MemberId);
    }
    // Commits sent before plans existed default to "their choice, now".
    private static CommitPlanResponse PlanOf(ConnectionRequest x) => CommitPlans.ToResponse(CommitPlans.Decode(x.Note) ?? new(CommitPlans.TheirChoice, null, "NOW"), x.ContextId);

    private static string OtherMember(Connection x, string memberId) => x.MemberLowId == memberId ? x.MemberHighId : x.MemberLowId;
    private HashSet<string> BlockedAmong(string memberId, string[] ids) => store.Blocks
        .Where(x => x.RemovedAt == null && ((x.BlockerMemberId == memberId && ids.Contains(x.BlockedMemberId)) || (x.BlockedMemberId == memberId && ids.Contains(x.BlockerMemberId))))
        .Select(x => x.BlockerMemberId == memberId ? x.BlockedMemberId : x.BlockerMemberId)
        .ToHashSet();

    private ConversationResponse[] BuildConversations(string memberId, Connection[] connections)
    {
        var byConnection = connections.ToDictionary(x => x.ConnectionId);
        var connectionIds = byConnection.Keys.ToArray();
        var conversations = store.Conversations.Where(x => connectionIds.Contains(x.ConnectionId)).ToArray();
        var conversationIds = conversations.Select(x => x.ConversationId).ToArray();
        var otherIds = connections.Select(x => OtherMember(x, memberId)).ToArray();
        var profiles = store.Profiles.Where(x => otherIds.Contains(x.MemberId) && x.Status == "ACTIVE" && x.Visibility != "HIDDEN").ToDictionary(x => x.MemberId);
        var blocked = BlockedAmong(memberId, otherIds);
        var lastSequences = store.Messages
            .Where(x => conversationIds.Contains(x.ConversationId) && x.DeletedAt == null)
            .GroupBy(x => x.ConversationId)
            .Select(g => new { ConversationId = g.Key, Sequence = g.Max(x => x.ServerSequence) })
            .ToDictionary(x => x.ConversationId, x => x.Sequence);
        var sequences = lastSequences.Values.ToArray();
        var lastMessages = store.Messages
            .Where(x => conversationIds.Contains(x.ConversationId) && sequences.Contains(x.ServerSequence))
            .AsEnumerable()
            .Where(x => lastSequences.GetValueOrDefault(x.ConversationId) == x.ServerSequence)
            .ToDictionary(x => x.ConversationId);
        // Unread means a non-deleted message from the other member with no read receipt from this member.
        var unread = store.Messages
            .Where(x => conversationIds.Contains(x.ConversationId) && x.SenderMemberId != memberId && x.DeletedAt == null
                && !store.MessageReceipts.Any(r => r.MessageId == x.MessageId && r.MemberId == memberId && r.ReadAt != null))
            .GroupBy(x => x.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.ConversationId, x => x.Count);
        // An expired mute is reported as not muted.
        var now = DateTimeOffset.UtcNow;
        var mutedUntil = store.ConversationParticipants
            .Where(x => conversationIds.Contains(x.ConversationId) && x.MemberId == memberId && x.MutedUntil > now)
            .ToDictionary(x => x.ConversationId, x => x.MutedUntil);
        var requestIds = connections.Select(x => x.AcceptedRequestId).ToArray();
        var plans = store.ConnectionRequests.Where(x => requestIds.Contains(x.RequestId) && x.ContextId != null).AsEnumerable().ToDictionary(x => x.RequestId, PlanOf);

        return conversations.Select(c =>
        {
            var connection = byConnection[c.ConnectionId];
            var otherId = OtherMember(connection, memberId);
            var profile = profiles.GetValueOrDefault(otherId);
            var last = lastMessages.GetValueOrDefault(c.ConversationId);
            return new ConversationResponse(c.ConversationId, c.ConnectionId, c.Status, otherId, profile?.DisplayName, profile?.Headline, profile?.Sector,
                last is null ? null : Map(last), unread.GetValueOrDefault(c.ConversationId), last?.CreatedAt ?? c.CreatedAt,
                c.Status == "ACTIVE" && connection.Status == "ACTIVE" && !blocked.Contains(otherId), mutedUntil.GetValueOrDefault(c.ConversationId), plans.GetValueOrDefault(connection.AcceptedRequestId));
        }).ToArray();
    }

    private static string EncodeConversationCursor(ConversationResponse x) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x.LastActivityAt.UtcTicks}:{x.ConversationId}")));
    private static (DateTimeOffset At, string Id)? DecodeConversationCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks && parts[1].Length is > 0 and <= 64)
                return (new DateTimeOffset(ticks, TimeSpan.Zero), parts[1]);
        }
        catch (FormatException) { }
        throw new DomainException("CONVERSATION_CURSOR_INVALID");
    }

    private void AddChange(string? memberId, string type, string id, string change, object? payload) => store.Add(new SyncChange { MemberScopeId = memberId, ResourceType = type, ResourceId = id, ChangeType = change, PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions) });
    private void AddOutbox(string aggregateType, string aggregateId, string eventType, object payload) => store.Add(new OutboxEvent { AggregateType = aggregateType, AggregateId = aggregateId, EventType = eventType, PayloadJson = JsonSerializer.Serialize(payload, JsonOptions) });
    private static (string Low, string High) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
    // ops.outbox_event.aggregate_id is varchar(64). Keep the readable "a:b" key when it fits;
    // otherwise use a deterministic hash so real 36-character event/member IDs still fit.
    private static string AggregateKey(string a, string b)
    {
        var key = $"{a}:{b}";
        return key.Length <= 64 ? key : $"agg_{Hash("outbox.aggregate", a, b)[..60]}";
    }

    private static string PairKey(string a, string b) { var p = Pair(a, b); return $"{p.Low}:{p.High}"; }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static decimal CalculateCompleteness(MemberProfile x) => new[] { x.DisplayName, x.Headline, x.Biography, x.Sector }.Count(v => !string.IsNullOrWhiteSpace(v)) * 25m;
    private static string EncodeCursor(long value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static string Hash(params string?[] values) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();
    private static ProfileResponse Map(MemberProfile x) => new(x.MemberId, x.DisplayName, x.Headline, x.Biography, x.Sector, x.Status, x.Visibility, x.CompletenessScore, $"\"{x.Version}\"", x.UpdatedAt);
    private static EventResponse Map(EventRecord x, string? venue, int attendeeCount, int liveCount, bool? isRegistered) => new(x.EventId, x.Name, x.StartsAt, x.EndsAt, x.Status, x.LiveModeEnabled, venue, attendeeCount, liveCount, isRegistered);
    private static LiveModeResponse Map(LiveModeSession x) => new(x.SessionId, x.EventId, x.Status, x.ActiveUntil);
    private static ConnectionRequestResponse Map(ConnectionRequest x) => new(x.RequestId, x.SenderMemberId, x.RecipientMemberId, x.Status, x.ExpiresAt);
    // The sender sees a declined Commit as PENDING.
    private static ConnectionRequestResponse Map(ConnectionRequest x, int commitsRemaining) => new(x.RequestId, x.SenderMemberId, x.RecipientMemberId, x.Status == "DECLINED" ? "PENDING" : x.Status, x.ExpiresAt, Math.Max(0, commitsRemaining));
    private static MessageResponse Map(Message x) => new(x.MessageId, x.ConversationId, x.SenderMemberId, x.MessageType, x.DeletedAt is null ? x.Body : null, x.ServerSequence, x.ModerationStatus, x.CreatedAt, x.DeletedAt);
    private static PrivacyRequestResponse Map(PrivacyRequest x) => new(x.PrivacyRequestId, x.RequestType, x.Status, x.CreatedAt, x.DueAt);
}
