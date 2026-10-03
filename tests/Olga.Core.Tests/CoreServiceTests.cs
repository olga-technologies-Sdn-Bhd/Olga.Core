using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class CoreServiceTests
{
    private static readonly AesIdentityProtector IdentityProtector = new(Enumerable.Repeat((byte)7, 32).ToArray());

    [Fact]
    public async Task Member_registration_is_idempotent_for_the_same_key()
    {
        await using var db = Db();
        var service = Service(db);
        var request = new MemberCreateRequest("New member", "Member@Example.com", "+919876543210");

        var first = await service.RegisterMemberAsync("olga", request, "register-1", default);
        var replay = await service.RegisterMemberAsync("olga", request, "register-1", default);

        Assert.Equal(first.MemberId, replay.MemberId);
        Assert.StartsWith("mem_", first.MemberId);
        Assert.Equal("m***@example.com", first.EmailHint);
        Assert.Equal("DRAFT", first.ProfileStatus);
        Assert.Single(db.MemberProfiles);
    }

    [Fact]
    public async Task Member_lookup_by_email_returns_the_registered_draft_member_for_any_case_or_whitespace()
    {
        await using var db = Db();
        var service = Service(db);
        var registered = await service.RegisterMemberAsync("olga", new MemberCreateRequest("Asha", "Asha@Example.com", "+60123456789"), "register-lookup", default);

        var exact = await service.LookupMemberByEmailAsync(new("asha@example.com"), default);
        var variant = await service.LookupMemberByEmailAsync(new("  ASHA@example.COM "), default);

        Assert.Equal(registered.MemberId, exact.MemberId);
        Assert.Equal(registered.MemberId, variant.MemberId);
        Assert.Equal(("Asha", "DRAFT", registered.ETag), (exact.DisplayName, exact.ProfileStatus, exact.ETag));
    }

    [Fact]
    public async Task Member_lookup_rejects_unknown_phone_only_missing_and_invalid_emails()
    {
        await using var db = Db();
        var service = Service(db);
        await service.RegisterMemberAsync("olga", new MemberCreateRequest("Phone only", Phone: "+60123456789"), "register-phone", default);

        var unknown = await Assert.ThrowsAsync<DomainException>(() => service.LookupMemberByEmailAsync(new("nobody@example.com"), default));
        var missing = await Assert.ThrowsAsync<DomainException>(() => service.LookupMemberByEmailAsync(new("  "), default));
        var invalid = await Assert.ThrowsAsync<DomainException>(() => service.LookupMemberByEmailAsync(new("not-an-email"), default));

        Assert.Equal(("MEMBER_NOT_REGISTERED", 404), (unknown.Code, unknown.StatusCode));
        Assert.Equal(("MEMBER_IDENTITY_REQUIRED", 400), (missing.Code, missing.StatusCode));
        Assert.Equal(("MEMBER_EMAIL_INVALID", 400), (invalid.Code, invalid.StatusCode));
    }

    [Fact]
    public async Task Registering_an_already_registered_email_with_a_new_key_is_still_a_conflict()
    {
        await using var db = Db();
        var service = Service(db);
        await service.RegisterMemberAsync("olga", new MemberCreateRequest("First", "taken@example.com"), "register-a", default);

        var conflict = await Assert.ThrowsAsync<DomainException>(() =>
            service.RegisterMemberAsync("olga", new MemberCreateRequest("Second", "TAKEN@example.com"), "register-b", default));

        Assert.Equal(("MEMBER_IDENTITY_ALREADY_REGISTERED", 409), (conflict.Code, conflict.StatusCode));
        Assert.Single(db.MemberProfiles);
    }

    [Fact]
    public void Member_identity_is_encrypted_and_can_be_decrypted_with_the_master_key()
    {
        var identity = IdentityProtector.ProtectEmail("member-1", "Member@Example.com", true);

        Assert.Equal("member@example.com", IdentityProtector.Unprotect("member-1", "EMAIL", identity.SubjectCiphertext));
        Assert.Equal(-1, identity.SubjectCiphertext.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("member@example.com")));
        Assert.Equal(64, identity.SubjectHash.Length);
        Assert.True(identity.IsVerified);
        Assert.False(IdentityProtector.ProtectPhone("member-1", "+919876543210", false).IsVerified);
    }

    [Fact]
    public async Task First_member_request_provisions_private_draft_idempotently()
    {
        await using var db = Db();
        var service = Service(db);

        await service.ProvisionMemberAsync("NEW", default);
        await service.ProvisionMemberAsync("NEW", default);

        var profile = await service.GetOwnProfileAsync("NEW", default);
        Assert.Equal("DRAFT", profile.ProfileStatus);
        Assert.Equal("HIDDEN", profile.Visibility);
        Assert.Equal("", profile.DisplayName);
        Assert.Single(db.MemberProfiles);
    }

    [Fact]
    public async Task Initial_draft_can_be_completed_without_an_etag()
    {
        await using var db = Db();
        var service = Service(db);
        await service.ProvisionMemberAsync("NEW", default);

        var profile = await service.UpdateProfileAsync("NEW", new("New member", null, null, null), null, default);

        Assert.Equal("ACTIVE", profile.ProfileStatus);
        Assert.Equal("New member", profile.DisplayName);
    }

    [Fact]
    public async Task Draft_member_is_not_visible_to_other_members()
    {
        await using var db = Db();
        var service = Service(db);
        await service.ProvisionMemberAsync("NEW", default);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.GetVisibleProfileAsync("A", "NEW", default));

        Assert.Equal("PROFILE_NOT_FOUND", error.Code);
    }

    [Theory]
    [InlineData("SUSPENDED")]
    [InlineData("ANONYMIZED")]
    [InlineData("DELETED")]
    public async Task Provisioning_does_not_reactivate_an_existing_member(string status)
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "S", DisplayName = "Existing", Status = status, Visibility = "HIDDEN" });
        await db.SaveChangesAsync();
        var service = Service(db);

        await service.ProvisionMemberAsync("S", default);

        Assert.Equal(status, db.MemberProfiles.Single().Status);
        Assert.Single(db.MemberProfiles);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.UpdateProfileAsync("S", new("Reactivate", null, null, null), "\"1\"", default));
        Assert.Equal("PROFILE_NOT_EDITABLE", error.Code);
    }

    [Fact]
    public async Task Draft_member_cannot_create_a_connection_request()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.ProvisionMemberAsync("NEW", default);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateConnectionRequestAsync("NEW", new("A"), default));

        Assert.Equal("PROFILE_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task Profile_update_requires_matching_etag()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A" }); await db.SaveChangesAsync();
        var service = Service(db);
        await Assert.ThrowsAsync<DomainException>(() => service.UpdateProfileAsync("A", new("New", null, null, null), null, default));
        var updated = await service.UpdateProfileAsync("A", new("New", null, null, null), "\"1\"", default);
        Assert.Equal("\"2\"", updated.ETag);
    }

    [Fact]
    public async Task Live_mode_requires_registration_and_latest_consent()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "E", new(), default));
        await service.RegisterAsync("A", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        var session = await service.StartLiveModeAsync("A", "E", new(30), default);
        Assert.Equal("ACTIVE", session.Status);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "WITHDRAWN"), default);
        Assert.Equal("DISABLED", db.LiveModeSessions.Single().Status);
    }

    [Fact]
    public async Task Accepting_request_creates_one_connection_and_conversation()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var request = await service.CreateConnectionRequestAsync("A", new("B"), default);
        var accepted = await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), "accept-1", default);
        Assert.Single(db.SocialConnections); Assert.Single(db.ChatConversations); Assert.NotEmpty(accepted.ConversationId);
        var message = await service.SendMessageAsync("A", accepted.ConversationId, new("m1", "Hello"), "message-1", default);
        var replay = await service.SendMessageAsync("A", accepted.ConversationId, new("m1", "Hello"), "message-1", default);
        Assert.Equal(message.ServerSequence, replay.ServerSequence); Assert.Single(db.ChatMessages);
    }

    [Fact]
    public async Task Block_immediately_prevents_relationship_and_chat()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var request = await service.CreateConnectionRequestAsync("A", new("B"), default);
        var accepted = await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), "accept-2", default);
        await service.BlockAsync("A", new("B"), default);
        Assert.Equal("DISCONNECTED", db.SocialConnections.Single().Status);
        Assert.Contains(db.MemberBlocks, x => x.BlockerMemberId == "A" && x.BlockedMemberId == "B" && x.RemovedAt == null);
        await Assert.ThrowsAsync<DomainException>(() => service.SendMessageAsync("B", accepted.ConversationId, new("m2", "No"), "message-2", default));
    }

    [Fact]
    public async Task Message_receipt_is_monotonic_and_updates_participant_cursor()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var request = await service.CreateConnectionRequestAsync("A", new("B"), default);
        var accepted = await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), "accept-3", default);
        var message = await service.SendMessageAsync("A", accepted.ConversationId, new("m1", "Hello"), "message-3", default);
        var now = DateTimeOffset.UtcNow;
        var receipt = await service.SaveMessageReceiptAsync("B", message.MessageId, new(now, now.AddSeconds(1)), "receipt-1", default);
        Assert.NotNull(receipt.ReadAt);
        Assert.Equal(message.MessageId, db.ConversationParticipants.Single(x => x.MemberId == "B").LastReadMessageId);
    }

    [Fact]
    public void PostgreSql_model_matches_database_source_names_and_concurrency()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>().UseNpgsql("Host=localhost;Database=model_check;Username=model_check").UseSnakeCaseNamingConvention().Options;
        using var db = new CoreDbContext(options);
        var profile = db.Model.FindEntityType(typeof(MemberProfile))!;
        var consent = db.Model.FindEntityType(typeof(MemberConsent))!;
        var live = db.Model.FindEntityType(typeof(LiveModeSession))!;
        Assert.Equal("member_profile", profile.GetTableName());
        Assert.Equal("professional_summary", profile.FindProperty(nameof(MemberProfile.Biography))!.GetColumnName());
        Assert.True(profile.FindProperty(nameof(MemberProfile.Version))!.IsConcurrencyToken);
        Assert.Equal("member_consent_id", consent.FindProperty(nameof(MemberConsent.Id))!.GetColumnName());
        Assert.Equal("live_session_id", live.FindProperty(nameof(LiveModeSession.SessionId))!.GetColumnName());
    }

    [Fact]
    public async Task Events_list_includes_venue_name_and_attendee_count()
    {
        await using var db = Db();
        var service = Service(db);
        SeedMembersAndEvent(db);
        await db.SaveChangesAsync();
        db.Venues.Add(new Venue { VenueId = "V", Name = "Grand Hyatt KL", City = "Kuala Lumpur", CountryCode = "MY" });
        db.EventRecords.Single(x => x.EventId == "E").VenueId = "V";
        db.EventRegistrations.AddRange(
            new EventRegistration { EventId = "E", MemberId = "A", Status = "REGISTERED" },
            new EventRegistration { EventId = "E", MemberId = "B", Status = "CHECKED_IN" });
        await db.SaveChangesAsync();

        var events = await service.GetEventsAsync(null, default);
        var result = Assert.Single(events);

        Assert.Equal("Grand Hyatt KL", result.Venue);
        Assert.Equal(2, result.AttendeeCount);
    }

    [Fact]
    public async Task Events_list_excludes_cancelled_registrations_from_attendee_count()
    {
        await using var db = Db();
        var service = Service(db);
        SeedMembersAndEvent(db);
        db.EventRegistrations.AddRange(
            new EventRegistration { EventId = "E", MemberId = "A", Status = "REGISTERED" },
            new EventRegistration { EventId = "E", MemberId = "B", Status = "CANCELLED" });
        await db.SaveChangesAsync();

        var events = await service.GetEventsAsync(null, default);
        var result = Assert.Single(events);

        Assert.Equal(1, result.AttendeeCount);
    }

    [Fact]
    public async Task Events_list_reports_no_venue_when_event_has_none_assigned()
    {
        await using var db = Db();
        var service = Service(db);
        SeedMembersAndEvent(db);
        await db.SaveChangesAsync();

        var events = await service.GetEventsAsync(null, default);
        var result = Assert.Single(events);

        Assert.Null(result.Venue);
        Assert.Equal(0, result.AttendeeCount);
    }

    [Fact]
    public async Task Events_list_counts_currently_active_live_sessions()
    {
        await using var db = Db();
        var service = Service(db);
        SeedMembersAndEvent(db);
        await db.SaveChangesAsync();

        await service.RegisterAsync("A", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        await service.StartLiveModeAsync("A", "E", new(30), default);

        await service.RegisterAsync("B", "E", default);
        await service.RecordConsentAsync("B", new("LIVE_MODE", "1", "GRANTED"), default);
        await service.StartLiveModeAsync("B", "E", new(30), default);

        var events = await service.GetEventsAsync(null, default);
        var result = Assert.Single(events);

        Assert.Equal(2, result.LiveCount);
    }

    [Fact]
    public async Task Events_list_excludes_expired_and_stopped_live_sessions_from_count()
    {
        await using var db = Db();
        var service = Service(db);
        SeedMembersAndEvent(db);
        await db.SaveChangesAsync();

        await service.RegisterAsync("A", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        await service.StartLiveModeAsync("A", "E", new(30), default);
        await service.StopLiveModeAsync("A", "E", default);

        db.LiveModeSessions.Add(new LiveModeSession { EventId = "E", MemberId = "B", ConsentRecordId = 1, Status = "ACTIVE", ActiveUntil = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await db.SaveChangesAsync();

        var events = await service.GetEventsAsync(null, default);
        var result = Assert.Single(events);

        Assert.Equal(0, result.LiveCount);
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CoreService Service(CoreDbContext db) => new(db, IdentityProtector);
    [Fact]
    public async Task Event_list_marks_registration_only_for_the_supplied_member()
    {
        await using var db = Db();
        var service = Service(db);
        SeedMembersAndEvent(db);
        db.EventRecords.Add(new EventRecord { EventId = "F", Name = "Other", StartsAt = DateTimeOffset.UtcNow.AddDays(1), EndsAt = DateTimeOffset.UtcNow.AddDays(2) });
        db.EventRegistrations.AddRange(
            new EventRegistration { EventId = "E", MemberId = "A", Status = "REGISTERED" },
            new EventRegistration { EventId = "F", MemberId = "A", Status = "CANCELLED" },
            new EventRegistration { EventId = "F", MemberId = "B", Status = "CHECKED_IN" });
        await db.SaveChangesAsync();

        var forA = (await service.GetEventsAsync("A", default)).ToDictionary(x => x.EventId, x => x.IsRegistered);
        var forB = (await service.GetEventsAsync("B", default)).ToDictionary(x => x.EventId, x => x.IsRegistered);
        var forUnknown = await service.GetEventsAsync("nobody", default);
        var anonymous = await service.GetEventsAsync(null, default);

        Assert.Equal(new Dictionary<string, bool?> { ["E"] = true, ["F"] = false }, forA);
        Assert.Equal(new Dictionary<string, bool?> { ["E"] = false, ["F"] = true }, forB);
        Assert.All(forUnknown, x => Assert.False(x.IsRegistered));
        Assert.All(anonymous, x => Assert.Null(x.IsRegistered));
    }

    [Fact]
    public async Task Going_live_activates_a_started_published_event_and_adds_one_default_matching_policy()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        await service.RegisterAsync("A", "E", default);
        await service.RegisterAsync("B", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        await service.RecordConsentAsync("B", new("LIVE_MODE", "1", "GRANTED"), default);

        await service.StartLiveModeAsync("A", "E", new(30), default);
        await service.StartLiveModeAsync("B", "E", new(30), default);

        Assert.Equal("ACTIVE", db.EventRecords.Single(x => x.EventId == "E").Status);
        var policy = Assert.Single(db.EventMatchingPolicies);
        Assert.Equal(("E", "ACTIVE", (short)1), (policy.EventId, policy.Status, policy.PolicyVersion));
        var listed = Assert.Single(await service.GetEventsAsync("A", default));
        Assert.Equal(("ACTIVE", 2, true), (listed.Status, listed.LiveCount, listed.IsRegistered));
    }

    [Fact]
    public async Task Live_mode_is_rejected_before_the_event_starts_and_leaves_it_published()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var evt = db.EventRecords.Single(x => x.EventId == "E");
        evt.StartsAt = DateTimeOffset.UtcNow.AddHours(2);
        evt.EndsAt = DateTimeOffset.UtcNow.AddHours(6);
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.RegisterAsync("A", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);

        var notStarted = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "E", new(30), default));

        Assert.Equal(("EVENT_NOT_ACTIVE", 409), (notStarted.Code, notStarted.StatusCode));
        Assert.Equal("PUBLISHED", db.EventRecords.Single(x => x.EventId == "E").Status);
        Assert.Empty(db.EventMatchingPolicies);
    }

    [Fact]
    public async Task Event_attendees_are_blinded_ordered_and_exclude_caller_blocked_hidden_and_inactive_members()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        db.MemberProfiles.AddRange(
            new MemberProfile { MemberId = "C", DisplayName = "Carol", Headline = "Investor", Sector = "INVESTOR", Status = "ACTIVE" },
            new MemberProfile { MemberId = "H", DisplayName = "Hidden", Status = "ACTIVE", Visibility = "HIDDEN" },
            new MemberProfile { MemberId = "D", DisplayName = "Draft", Status = "DRAFT" },
            new MemberProfile { MemberId = "X", DisplayName = "Blocked by A", Status = "ACTIVE" },
            new MemberProfile { MemberId = "Y", DisplayName = "Blocked A", Status = "ACTIVE" },
            new MemberProfile { MemberId = "Z", DisplayName = "Cancelled", Status = "ACTIVE" });
        db.EventRegistrations.AddRange(
            new EventRegistration { EventId = "E", MemberId = "A", RegisteredAt = t0 },
            new EventRegistration { EventId = "E", MemberId = "C", Status = "CHECKED_IN", RegisteredAt = t0.AddMinutes(1) },
            new EventRegistration { EventId = "E", MemberId = "B", RegisteredAt = t0.AddMinutes(2) },
            new EventRegistration { EventId = "E", MemberId = "H", RegisteredAt = t0.AddMinutes(3) },
            new EventRegistration { EventId = "E", MemberId = "D", RegisteredAt = t0.AddMinutes(4) },
            new EventRegistration { EventId = "E", MemberId = "X", RegisteredAt = t0.AddMinutes(5) },
            new EventRegistration { EventId = "E", MemberId = "Y", RegisteredAt = t0.AddMinutes(6) },
            new EventRegistration { EventId = "E", MemberId = "Z", Status = "CANCELLED", RegisteredAt = t0.AddMinutes(7) });
        db.MemberBlocks.AddRange(
            new MemberBlock { BlockerMemberId = "A", BlockedMemberId = "X" },
            new MemberBlock { BlockerMemberId = "Y", BlockedMemberId = "A" });
        await db.SaveChangesAsync();

        var result = await Service(db).GetEventAttendeesAsync("A", "E", default);

        Assert.Equal(new[] { "C", "B" }, result.Attendees.Select(x => x.MemberId));
        Assert.Equal(2, result.Total);
        Assert.Equal(("Investor", "INVESTOR"), (result.Attendees[0].Headline, result.Attendees[0].RoleCategory));
    }

    [Fact]
    public async Task Event_attendees_require_the_caller_registration_and_a_known_event()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.EventRegistrations.Add(new EventRegistration { EventId = "E", MemberId = "B" });
        await db.SaveChangesAsync();
        var service = Service(db);

        var notRegistered = await Assert.ThrowsAsync<DomainException>(() => service.GetEventAttendeesAsync("A", "E", default));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => service.GetEventAttendeesAsync("A", "missing", default));

        Assert.Equal(("EVENT_REGISTRATION_REQUIRED", 403), (notRegistered.Code, notRegistered.StatusCode));
        Assert.Equal(("EVENT_NOT_FOUND", 404), (unknown.Code, unknown.StatusCode));
    }

    [Fact]
    public async Task Event_attendees_are_capped_at_fifty_with_the_full_total()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.EventRegistrations.Add(new EventRegistration { EventId = "E", MemberId = "A" });
        for (var i = 0; i < 55; i++)
        {
            db.MemberProfiles.Add(new MemberProfile { MemberId = $"M{i:00}", DisplayName = $"M{i}", Status = "ACTIVE" });
            db.EventRegistrations.Add(new EventRegistration { EventId = "E", MemberId = $"M{i:00}", RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-100 + i) });
        }
        await db.SaveChangesAsync();

        var result = await Service(db).GetEventAttendeesAsync("A", "E", default);

        Assert.Equal(50, result.Attendees.Count);
        Assert.Equal(55, result.Total);
        Assert.Equal("M00", result.Attendees[0].MemberId);
    }

    [Fact]
    public async Task Admin_created_consent_policy_unblocks_consent_and_is_returned_as_active()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var admin = new AdminService(db);
        var service = Service(db);

        var missing = await Assert.ThrowsAsync<DomainException>(() => service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default));
        var noActive = await Assert.ThrowsAsync<DomainException>(() => service.GetActiveConsentPolicyAsync("LIVE_MODE", default));
        var created = await admin.CreateConsentPolicyAsync(new("live_mode", "1", Text: "Live Mode terms v1"), "policy-1", default);
        var replay = await admin.CreateConsentPolicyAsync(new("LIVE_MODE", "1"), "policy-1", default);
        var consent = await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        var active = await service.GetActiveConsentPolicyAsync("live_mode", default);

        Assert.Equal(("CONSENT_POLICY_NOT_ACTIVE", 409), (missing.Code, missing.StatusCode));
        Assert.Equal(("CONSENT_POLICY_NOT_ACTIVE", 404), (noActive.Code, noActive.StatusCode));
        Assert.Equal(("LIVE_MODE", "1", "en", "ACTIVE"), (created.PurposeCode, created.Version, created.Locale, created.Status));
        Assert.Equal(64, created.ContentHash.Length);
        Assert.Equal(created.PolicyId, replay.PolicyId);
        Assert.Equal(created.PolicyId, consent.PolicyId);
        Assert.Equal(("LIVE_MODE", "1"), (active.PurposeCode, active.Version));
        Assert.Single(await admin.GetConsentPoliciesAsync(default));
    }

    [Fact]
    public async Task Duplicate_consent_policy_is_a_conflict_and_invalid_input_is_rejected()
    {
        await using var db = Db();
        var admin = new AdminService(db);
        await admin.CreateConsentPolicyAsync(new("MATCHING", "1"), "policy-a", default);

        var duplicate = await Assert.ThrowsAsync<DomainException>(() => admin.CreateConsentPolicyAsync(new("matching", "1"), "policy-b", default));
        var badPurpose = await Assert.ThrowsAsync<DomainException>(() => admin.CreateConsentPolicyAsync(new("live mode!", "1"), "policy-c", default));
        var badHash = await Assert.ThrowsAsync<DomainException>(() => admin.CreateConsentPolicyAsync(new("MATCHING", "2", ContentHash: "xyz"), "policy-d", default));

        Assert.Equal(("CONSENT_POLICY_EXISTS", 409), (duplicate.Code, duplicate.StatusCode));
        Assert.Equal("CONSENT_POLICY_INVALID", badPurpose.Code);
        Assert.Equal("CONSENT_POLICY_INVALID", badHash.Code);
    }

    [Fact]
    public async Task Retired_consent_policy_is_no_longer_accepted_or_active()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var admin = new AdminService(db);
        var service = Service(db);
        var policy = await admin.CreateConsentPolicyAsync(new("LIVE_MODE", "1"), "policy-r", default);

        var retired = await admin.RetireConsentPolicyAsync(policy.PolicyId, default);
        var again = await admin.RetireConsentPolicyAsync(policy.PolicyId, default);
        var consent = await Assert.ThrowsAsync<DomainException>(() => service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default));
        var active = await Assert.ThrowsAsync<DomainException>(() => service.GetActiveConsentPolicyAsync("LIVE_MODE", default));
        var unknown = await Assert.ThrowsAsync<DomainException>(() => admin.RetireConsentPolicyAsync("missing", default));

        Assert.Equal("RETIRED", retired.Status);
        Assert.Equal(retired.RetiredAt, again.RetiredAt);
        Assert.Equal(("CONSENT_POLICY_NOT_ACTIVE", 409), (consent.Code, consent.StatusCode));
        Assert.Equal(404, active.StatusCode);
        Assert.Equal(("CONSENT_POLICY_NOT_FOUND", 404), (unknown.Code, unknown.StatusCode));
    }

    private static void SeedMembersAndEvent(CoreDbContext db)
    {
        db.MemberProfiles.AddRange(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" }, new MemberProfile { MemberId = "B", DisplayName = "B", Status = "ACTIVE" });
        db.ConsentPolicies.AddRange(
            new ConsentPolicy { PolicyId = "live-mode-v1", PurposeCode = "LIVE_MODE", Version = "1", ContentHash = new string('0', 64), EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) },
            new ConsentPolicy { PolicyId = "matching-v1", PurposeCode = "MATCHING", Version = "1", ContentHash = new string('1', 64), EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) });
        db.EventRecords.Add(new EventRecord { EventId = "E", Name = "Event", StartsAt = DateTimeOffset.UtcNow.AddHours(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1), LiveModeEnabled = true });
    }
}
