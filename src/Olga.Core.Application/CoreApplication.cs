using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public interface ICoreStore
{
    bool IsRelational { get; }
    IQueryable<MemberProfile> Profiles { get; }
    IQueryable<MemberIdentity> Identities { get; }
    IQueryable<ConsentPolicy> ConsentPolicies { get; }
    IQueryable<MemberConsent> Consents { get; }
    IQueryable<EventRecord> Events { get; }
    IQueryable<Venue> Venues { get; }
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
    Task CreateMemberAsync(NewMemberRegistration member, CancellationToken ct);
    Task EnsureMemberAsync(string memberId, CancellationToken ct);
    void Add<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;
    Task SaveAsync(CancellationToken ct);
    Task<(string ConnectionId, string ConversationId)> AcceptConnectionRequestAsync(string requestId, string recipientId, string connectionId, string conversationId, string idempotencyKey, string requestHash, CancellationToken ct);
    Task<Message> SaveMessageAsync(string memberId, string conversationId, MessageCreateRequest request, string idempotencyKey, string requestHash, CancellationToken ct);
    Task<MessageReceipt> SaveMessageReceiptAsync(string memberId, string messageId, MessageReceiptRequest request, string idempotencyKey, string requestHash, CancellationToken ct);
}

public interface ICoreService
{
    Task<MemberRegistrationResponse> RegisterMemberAsync(string communityId, MemberCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<MemberLookupResponse> LookupMemberByEmailAsync(MemberLookupRequest request, CancellationToken ct);
    Task ProvisionMemberAsync(string memberId, CancellationToken ct);
    Task<ProfileResponse> GetOwnProfileAsync(string memberId, CancellationToken ct);
    Task<ProfileResponse> GetVisibleProfileAsync(string actorId, string memberId, CancellationToken ct);
    Task<ProfileResponse> UpdateProfileAsync(string memberId, ProfileUpdateRequest request, string? ifMatch, CancellationToken ct);
    Task<ConsentResponse> RecordConsentAsync(string memberId, ConsentRequest request, CancellationToken ct);
    Task<IReadOnlyList<EventResponse>> GetEventsAsync(CancellationToken ct);
    Task<RegistrationResponse> RegisterAsync(string memberId, string eventId, CancellationToken ct);
    Task<LiveModeResponse> StartLiveModeAsync(string memberId, string eventId, LiveModeRequest request, CancellationToken ct);
    Task StopLiveModeAsync(string memberId, string eventId, CancellationToken ct);
    Task RecordPresenceAsync(string memberId, string eventId, PresenceRequest request, CancellationToken ct);
    Task<ConnectionRequestResponse> CreateConnectionRequestAsync(string senderId, ConnectionRequestCreate request, CancellationToken ct);
    Task<ConnectionResponse> DecideConnectionRequestAsync(string memberId, string requestId, ConnectionDecisionRequest request, string idempotencyKey, CancellationToken ct);
    Task BlockAsync(string memberId, BlockRequest request, CancellationToken ct);
    Task<IReadOnlyList<ConnectionResponse>> GetConnectionsAsync(string memberId, CancellationToken ct);
    Task<MessageResponse> SendMessageAsync(string memberId, string conversationId, MessageCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<MessageReceiptResponse> SaveMessageReceiptAsync(string memberId, string messageId, MessageReceiptRequest request, string idempotencyKey, CancellationToken ct);
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
        return Task.FromResult(new MemberLookupResponse(mapped.MemberId, mapped.DisplayName, mapped.ProfileStatus, mapped.ETag));
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
        return Task.FromResult(Map(profile));
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

    public Task<IReadOnlyList<EventResponse>> GetEventsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var events = store.Events.Where(x => x.Status == "PUBLISHED").OrderBy(x => x.StartsAt).ToArray();
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

        IReadOnlyList<EventResponse> result = events
            .Select(x => Map(x, x.VenueId is not null ? venueNames.GetValueOrDefault(x.VenueId) : null, attendeeCounts.GetValueOrDefault(x.EventId, 0), liveCounts.GetValueOrDefault(x.EventId, 0)))
            .ToArray();
        return Task.FromResult(result);
    }

    public async Task<RegistrationResponse> RegisterAsync(string memberId, string eventId, CancellationToken ct)
    {
        _ = FindEvent(eventId);
        var row = store.Registrations.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId);
        if (row is null) { row = new EventRegistration { EventId = eventId, MemberId = memberId }; store.Add(row); }
        AddChange(memberId, "EVENT_REGISTRATION", eventId, "UPSERT", new { event_id = eventId, status = row.Status });
        AddOutbox("EVENT_REGISTRATION", $"{eventId}:{memberId}", "EventRegistrationChanged.v1", new { event_id = eventId, member_id = memberId, status = row.Status });
        await store.SaveAsync(ct);
        return new(row.EventId, row.MemberId, row.Status, row.RegisteredAt);
    }

    public async Task<LiveModeResponse> StartLiveModeAsync(string memberId, string eventId, LiveModeRequest request, CancellationToken ct)
    {
        var evt = FindEvent(eventId);
        if (request.DurationMinutes is < 5 or > 240) throw new DomainException("LIVE_MODE_DURATION_INVALID");
        if (!store.Registrations.Any(x => x.EventId == eventId && x.MemberId == memberId && (x.Status == "REGISTERED" || x.Status == "CHECKED_IN"))) throw new DomainException("EVENT_REGISTRATION_REQUIRED", 403);
        if (!HasConsent(memberId, "LIVE_MODE")) throw new DomainException("LIVE_MODE_CONSENT_REQUIRED", 403);
        var now = DateTimeOffset.UtcNow;
        if (evt.EndsAt <= now) throw new DomainException("EVENT_NOT_ACTIVE", 409);
        if (!evt.LiveModeEnabled) throw new DomainException("LIVE_MODE_NOT_ENABLED", 409);
        var consent = LatestGrantedConsent(memberId, "LIVE_MODE") ?? throw new DomainException("LIVE_MODE_CONSENT_REQUIRED", 403);
        var existing = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE");
        if (existing is not null) { existing.ActiveUntil = Min(now.AddMinutes(request.DurationMinutes), evt.EndsAt); await store.SaveAsync(ct); return Map(existing); }
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
        if (string.IsNullOrWhiteSpace(request.CoarseCell) || request.CoarseCell.Length > 32 || request.Source is not ("CHECK_IN" or "FOREGROUND_GEO" or "VENUE_ZONE")) throw new DomainException("PRESENCE_INVALID");
        var session = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE" && x.ActiveUntil > DateTimeOffset.UtcNow)
            ?? throw new DomainException("LIVE_MODE_NOT_ACTIVE", 403);
        var observed = request.ObservedAt == default ? DateTimeOffset.UtcNow : request.ObservedAt.ToUniversalTime();
        if (Math.Abs((DateTimeOffset.UtcNow - observed).TotalMinutes) > 10) throw new DomainException("PRESENCE_STALE");
        store.Add(new EventPresence { SessionId = session.SessionId, CoarseCell = request.CoarseCell, ObservedAt = observed, ExpiresAt = Min(session.ActiveUntil, observed.AddHours(2)), Source = request.Source });
        AddOutbox("LIVE_MODE", session.SessionId, "EventPresenceRefreshed.v1", new { session_id = session.SessionId, event_id = eventId, member_id = memberId, expires_at = Min(session.ActiveUntil, observed.AddHours(2)) });
        await store.SaveAsync(ct);
    }

    public async Task<ConnectionRequestResponse> CreateConnectionRequestAsync(string senderId, ConnectionRequestCreate request, CancellationToken ct)
    {
        if (senderId == request.RecipientMemberId) throw new DomainException("SELF_CONNECTION_INVALID");
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

    public async Task<ConnectionResponse> DecideConnectionRequestAsync(string memberId, string requestId, ConnectionDecisionRequest request, string idempotencyKey, CancellationToken ct)
    {
        var row = store.ConnectionRequests.SingleOrDefault(x => x.RequestId == requestId && x.RecipientMemberId == memberId) ?? throw new DomainException("CONNECTION_REQUEST_NOT_FOUND", 404);
        if (row.Status != "PENDING" || row.ExpiresAt <= DateTimeOffset.UtcNow) throw new DomainException("CONNECTION_REQUEST_NOT_PENDING", 409);
        if (request.Decision is not ("ACCEPT" or "DECLINE")) throw new DomainException("CONNECTION_DECISION_INVALID");
        if (request.Decision == "DECLINE") { row.Status = "DECLINED"; row.RespondedAt = DateTimeOffset.UtcNow; await store.SaveAsync(ct); return new("", row.SenderMemberId, row.Status, ""); }
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
        AddOutbox("MEMBER_RELATIONSHIP", PairKey(memberId, request.MemberId), "MemberBlocked.v1", new { actor_member_id = memberId, target_member_id = request.MemberId });
        await store.SaveAsync(ct);
    }

    public Task<IReadOnlyList<ConnectionResponse>> GetConnectionsAsync(string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = store.Connections.Where(x => x.Status == "ACTIVE" && (x.MemberLowId == memberId || x.MemberHighId == memberId)).AsEnumerable().Select(x => new ConnectionResponse(x.ConnectionId, x.MemberLowId == memberId ? x.MemberHighId : x.MemberLowId, x.Status, store.Conversations.Single(c => c.ConnectionId == x.ConnectionId).ConversationId)).ToArray();
        return Task.FromResult<IReadOnlyList<ConnectionResponse>>(result);
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
    private EventRecord FindEvent(string id) => store.Events.SingleOrDefault(x => x.EventId == id && (x.Status == "PUBLISHED" || x.Status == "ACTIVE")) ?? throw new DomainException("EVENT_NOT_FOUND", 404);
    private bool HasConsent(string memberId, string purpose) => LatestGrantedConsent(memberId, purpose) is not null;
    private MemberConsent? LatestGrantedConsent(string memberId, string purpose) => (from consent in store.Consents join policy in store.ConsentPolicies on consent.PolicyId equals policy.PolicyId where consent.MemberId == memberId && policy.PurposeCode == purpose orderby consent.CapturedAt descending, consent.Id descending select consent).FirstOrDefault() is { Decision: "GRANTED", WithdrawnAt: null } value ? value : null;
    private bool IsBlocked(string a, string b) => store.Blocks.Any(x => x.RemovedAt == null && ((x.BlockerMemberId == a && x.BlockedMemberId == b) || (x.BlockerMemberId == b && x.BlockedMemberId == a)));
    private bool IsConnected(string a, string b) { var p = Pair(a, b); return store.Connections.Any(x => x.MemberLowId == p.Low && x.MemberHighId == p.High && x.Status == "ACTIVE"); }
    private Connection RequireConversationMember(string memberId, string conversationId) { var c = store.Conversations.SingleOrDefault(x => x.ConversationId == conversationId) ?? throw new DomainException("CONVERSATION_NOT_FOUND", 404); var link = store.Connections.Single(x => x.ConnectionId == c.ConnectionId); return link.Status == "ACTIVE" && (link.MemberLowId == memberId || link.MemberHighId == memberId) ? link : throw new DomainException("CONVERSATION_FORBIDDEN", 403); }
    private void AddChange(string? memberId, string type, string id, string change, object? payload) => store.Add(new SyncChange { MemberScopeId = memberId, ResourceType = type, ResourceId = id, ChangeType = change, PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions) });
    private void AddOutbox(string aggregateType, string aggregateId, string eventType, object payload) => store.Add(new OutboxEvent { AggregateType = aggregateType, AggregateId = aggregateId, EventType = eventType, PayloadJson = JsonSerializer.Serialize(payload, JsonOptions) });
    private static (string Low, string High) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
    private static string PairKey(string a, string b) { var p = Pair(a, b); return $"{p.Low}:{p.High}"; }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static decimal CalculateCompleteness(MemberProfile x) => new[] { x.DisplayName, x.Headline, x.Biography, x.Sector }.Count(v => !string.IsNullOrWhiteSpace(v)) * 25m;
    private static string EncodeCursor(long value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    private static string Hash(params string?[] values) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();
    private static ProfileResponse Map(MemberProfile x) => new(x.MemberId, x.DisplayName, x.Headline, x.Biography, x.Sector, x.Status, x.Visibility, x.CompletenessScore, $"\"{x.Version}\"", x.UpdatedAt);
    private static EventResponse Map(EventRecord x, string? venue, int attendeeCount, int liveCount) => new(x.EventId, x.Name, x.StartsAt, x.EndsAt, x.Status, x.LiveModeEnabled, venue, attendeeCount, liveCount);
    private static LiveModeResponse Map(LiveModeSession x) => new(x.SessionId, x.EventId, x.Status, x.ActiveUntil);
    private static ConnectionRequestResponse Map(ConnectionRequest x) => new(x.RequestId, x.SenderMemberId, x.RecipientMemberId, x.Status, x.ExpiresAt);
    private static MessageResponse Map(Message x) => new(x.MessageId, x.ConversationId, x.SenderMemberId, x.MessageType, x.Body, x.ServerSequence, x.ModerationStatus, x.CreatedAt);
    private static PrivacyRequestResponse Map(PrivacyRequest x) => new(x.PrivacyRequestId, x.RequestType, x.Status, x.CreatedAt, x.DueAt);
}
