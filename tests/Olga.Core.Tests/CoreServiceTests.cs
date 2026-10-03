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

        RegisterAccountOnly(db, "NEW"); await service.ProvisionMemberAsync("NEW", default);
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
        RegisterAccountOnly(db, "NEW"); await service.ProvisionMemberAsync("NEW", default);

        var profile = await service.UpdateProfileAsync("NEW", new("New member", null, null, null), null, default);

        Assert.Equal("ACTIVE", profile.ProfileStatus);
        Assert.Equal("New member", profile.DisplayName);
    }

    [Fact]
    public async Task Draft_member_is_not_visible_to_other_members()
    {
        await using var db = Db();
        var service = Service(db);
        RegisterAccountOnly(db, "NEW"); await service.ProvisionMemberAsync("NEW", default);

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
        RegisterAccountOnly(db, "NEW"); await service.ProvisionMemberAsync("NEW", default);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateConnectionRequestAsync("NEW", new("A"), "request-key", default));

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
        var request = await service.CreateConnectionRequestAsync("A", new("B"), "request-key", default);
        var accepted = (await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), "accept-1", default))!;
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
        var request = await service.CreateConnectionRequestAsync("A", new("B"), "request-key", default);
        var accepted = (await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), "accept-2", default))!;
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
        var request = await service.CreateConnectionRequestAsync("A", new("B"), "request-key", default);
        var accepted = (await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), "accept-3", default))!;
        var message = await service.SendMessageAsync("A", accepted.ConversationId, new("m1", "Hello"), "message-3", default);
        var now = DateTimeOffset.UtcNow;
        var receipt = await service.SaveMessageReceiptAsync("B", message.MessageId, new(now, now.AddSeconds(1)), "receipt-1", default);
        Assert.NotNull(receipt.ReadAt);
        Assert.Equal(message.MessageId, db.ConversationParticipants.Single(x => x.MemberId == "B").LastReadMessageId);
    }

    [Fact]
    public async Task Conversation_list_shows_other_member_last_message_and_unread_count()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        var first = await service.SendMessageAsync("A", conversationId, new("m1", "Hello"), "list-m1", default);
        await service.SendMessageAsync("A", conversationId, new("m2", "Are you here?"), "list-m2", default);
        await service.SendMessageAsync("B", conversationId, new("m3", "Yes"), "list-m3", default);
        await service.SaveMessageReceiptAsync("B", first.MessageId, new(ReadAt: DateTimeOffset.UtcNow), "list-r1", default);

        var forB = await service.GetConversationsAsync("B", null, 20, default);
        var forA = await service.GetConversationsAsync("A", null, 20, default);

        var item = Assert.Single(forB.Items);
        Assert.Equal((conversationId, "A", "A", "m3", 1, true), (item.ConversationId, item.MemberId, item.DisplayName, item.LastMessage?.MessageId, item.UnreadCount, item.CanSend));
        Assert.Equal(item.LastMessage!.CreatedAt, item.LastActivityAt);
        Assert.Equal(("B", 1), (forA.Items.Single().MemberId, forA.Items.Single().UnreadCount));
        Assert.False(forB.HasMore); Assert.Null(forB.NextCursor);
    }

    [Fact]
    public async Task Conversation_list_orders_by_latest_activity_and_pages_with_an_opaque_cursor()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.MemberProfiles.AddRange(new MemberProfile { MemberId = "C", DisplayName = "C", Status = "ACTIVE" }, new MemberProfile { MemberId = "D", DisplayName = "D", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db);
        var withB = await ConnectAsync(service, "A", "B");
        var withC = await ConnectAsync(service, "A", "C");
        var withD = await ConnectAsync(service, "A", "D");
        await service.SendMessageAsync("C", withC, new("c1", "Newest"), "page-c1", default);

        var first = await service.GetConversationsAsync("A", null, 2, default);
        var second = await service.GetConversationsAsync("A", first.NextCursor, 2, default);

        Assert.True(first.HasMore); Assert.NotNull(first.NextCursor);
        Assert.Equal(withC, first.Items[0].ConversationId);
        Assert.False(second.HasMore); Assert.Null(second.NextCursor);
        Assert.Equal(new[] { withB, withC, withD }.Order(), first.Items.Concat(second.Items).Select(x => x.ConversationId).Order());
    }

    [Fact]
    public async Task Conversation_list_hides_blocked_or_disconnected_members_and_rejects_bad_cursors()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        await ConnectAsync(service, "A", "B");
        await service.BlockAsync("B", new("A"), default);

        Assert.Empty((await service.GetConversationsAsync("A", null, 20, default)).Items);
        var invalid = await Assert.ThrowsAsync<DomainException>(() => service.GetConversationsAsync("A", "not-a-cursor", 20, default));
        Assert.Equal(("CONVERSATION_CURSOR_INVALID", 400), (invalid.Code, invalid.StatusCode));
    }

    [Fact]
    public async Task Conversation_detail_is_only_visible_to_its_two_members()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.MemberProfiles.Add(new MemberProfile { MemberId = "C", DisplayName = "C", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        db.ChatConversations.Single(x => x.ConversationId == conversationId).Status = "RESTRICTED";
        await db.SaveChangesAsync();

        var detail = await service.GetConversationAsync("A", conversationId, default);
        var outsider = await Assert.ThrowsAsync<DomainException>(() => service.GetConversationAsync("C", conversationId, default));
        var missing = await Assert.ThrowsAsync<DomainException>(() => service.GetConversationAsync("A", "unknown", default));

        Assert.Equal(("B", "RESTRICTED", false, (MessageResponse?)null, 0), (detail.MemberId, detail.Status, detail.CanSend, detail.LastMessage, detail.UnreadCount));
        Assert.Equal(("CONVERSATION_FORBIDDEN", 403), (outsider.Code, outsider.StatusCode));
        Assert.Equal(("CONVERSATION_NOT_FOUND", 404), (missing.Code, missing.StatusCode));
    }

    [Fact]
    public async Task Marking_a_conversation_read_clears_unread_up_to_the_given_message_and_is_retry_safe()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        await service.SendMessageAsync("A", conversationId, new("r1", "One"), "read-m1", default);
        var second = await service.SendMessageAsync("A", conversationId, new("r2", "Two"), "read-m2", default);
        await service.SendMessageAsync("B", conversationId, new("r3", "Mine"), "read-m3", default);
        await service.SendMessageAsync("A", conversationId, new("r4", "Three"), "read-m4", default);

        var partial = await service.MarkConversationReadAsync("B", conversationId, new(second.MessageId), "read-1", default);
        var replay = await service.MarkConversationReadAsync("B", conversationId, new(second.MessageId), "read-1", default);
        var all = await service.MarkConversationReadAsync("B", conversationId, new("r4"), "read-2", default);

        Assert.Equal((1, 1, 0), (partial.UnreadCount, replay.UnreadCount, all.UnreadCount));
        Assert.Equal(3, db.MessageReceipts.Count(x => x.MemberId == "B" && x.ReadAt != null));
        Assert.Equal("r4", db.ConversationParticipants.Single(x => x.MemberId == "B").LastReadMessageId);
        Assert.Equal(1, (await service.GetConversationAsync("A", conversationId, default)).UnreadCount);
    }

    [Fact]
    public async Task Marking_read_rejects_unknown_messages_outsiders_and_blank_input()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.MemberProfiles.Add(new MemberProfile { MemberId = "C", DisplayName = "C", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        await service.SendMessageAsync("A", conversationId, new("x1", "Hi"), "read-x1", default);

        var unknown = await Assert.ThrowsAsync<DomainException>(() => service.MarkConversationReadAsync("B", conversationId, new("nope"), "read-x", default));
        var outsider = await Assert.ThrowsAsync<DomainException>(() => service.MarkConversationReadAsync("C", conversationId, new("x1"), "read-y", default));
        var blank = await Assert.ThrowsAsync<DomainException>(() => service.MarkConversationReadAsync("B", conversationId, new(" "), "read-z", default));

        Assert.Equal(("MESSAGE_NOT_FOUND", 404), (unknown.Code, unknown.StatusCode));
        Assert.Equal(("CONVERSATION_FORBIDDEN", 403), (outsider.Code, outsider.StatusCode));
        Assert.Equal(("CONVERSATION_READ_INVALID", 400), (blank.Code, blank.StatusCode));
    }

    [Fact]
    public async Task Muting_is_per_member_reported_until_it_expires_and_can_be_cleared()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        var until = DateTimeOffset.UtcNow.AddHours(8);

        var muted = await service.MuteConversationAsync("A", conversationId, new(until), default);
        var otherSide = await service.GetConversationAsync("B", conversationId, default);
        Assert.Equal(until, muted.MutedUntil);
        Assert.Null(otherSide.MutedUntil);
        Assert.Contains(db.Changes,x => x.MemberScopeId == "A" && x.ResourceType == "CONVERSATION" && x.ResourceId == conversationId);

        db.ConversationParticipants.Single(x => x.MemberId == "A").MutedUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Null((await service.GetConversationAsync("A", conversationId, default)).MutedUntil);

        await service.MuteConversationAsync("A", conversationId, new(until), default);
        var cleared = await service.MuteConversationAsync("A", conversationId, new(null), default);
        Assert.Null(cleared.MutedUntil);
        Assert.Null(db.ConversationParticipants.Single(x => x.MemberId == "A").MutedUntil);

        var past = await Assert.ThrowsAsync<DomainException>(() => service.MuteConversationAsync("A", conversationId, new(DateTimeOffset.UtcNow.AddMinutes(-5)), default));
        Assert.Equal(("CONVERSATION_MUTE_INVALID", 400), (past.Code, past.StatusCode));
    }

    [Fact]
    public async Task Sender_can_delete_own_message_which_stays_as_a_bodyless_tombstone()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        await service.SendMessageAsync("A", conversationId, new("d1", "Keep"), "del-m1", default);
        await service.SendMessageAsync("A", conversationId, new("d2", "Oops"), "del-m2", default);

        await service.DeleteMessageAsync("A", "d2", default);
        await service.DeleteMessageAsync("A", "d2", default);

        var messages = await service.GetMessagesAsync("B", conversationId, 0, 50, default);
        var deleted = messages.Single(x => x.MessageId == "d2");
        Assert.Equal(2, messages.Count);
        Assert.Null(deleted.Body); Assert.NotNull(deleted.DeletedAt);
        Assert.Equal("Oops", db.ChatMessages.Single(x => x.MessageId == "d2").Body);
        var forB = await service.GetConversationAsync("B", conversationId, default);
        Assert.Equal(("d1", 1), (forB.LastMessage?.MessageId, forB.UnreadCount));
        Assert.Equal(2, db.Changes.Count(x => x.ResourceType == "MESSAGE" && x.ResourceId == "d2" && x.ChangeType == "DELETE"));
    }

    [Fact]
    public async Task Only_the_sender_can_delete_a_message()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.MemberProfiles.Add(new MemberProfile { MemberId = "C", DisplayName = "C", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        await service.SendMessageAsync("A", conversationId, new("d3", "Mine"), "del-m3", default);

        var other = await Assert.ThrowsAsync<DomainException>(() => service.DeleteMessageAsync("B", "d3", default));
        var outsider = await Assert.ThrowsAsync<DomainException>(() => service.DeleteMessageAsync("C", "d3", default));
        var missing = await Assert.ThrowsAsync<DomainException>(() => service.DeleteMessageAsync("A", "nope", default));

        Assert.Equal(("MESSAGE_DELETE_FORBIDDEN", 403), (other.Code, other.StatusCode));
        Assert.Equal(("CONVERSATION_FORBIDDEN", 403), (outsider.Code, outsider.StatusCode));
        Assert.Equal(("MESSAGE_NOT_FOUND", 404), (missing.Code, missing.StatusCode));
        Assert.Null(db.ChatMessages.Single().DeletedAt);
    }

    [Fact]
    public async Task Reporting_a_message_opens_one_report_and_moderation_case_even_after_blocking()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        await service.SendMessageAsync("A", conversationId, new("p1", "Rude"), "rep-m1", default);
        await service.BlockAsync("B", new("A"), default);

        var report = await service.ReportMessageAsync("B", "p1", new("harassment", "  Abusive  "), default);
        var again = await service.ReportMessageAsync("B", "p1", new("SPAM"), default);

        Assert.Equal((report.ReportId, "HARASSMENT", "OPEN"), (again.ReportId, report.Category, report.Status));
        var row = Assert.Single(db.MemberReports);
        Assert.Equal(("B", "A", "MESSAGE", "p1", "Abusive"), (row.ReporterMemberId, row.ReportedMemberId, row.ResourceType, row.ResourceId, row.Description));
        var moderation = Assert.Single(db.ModerationCases);
        Assert.Equal(("MEMBER_REPORT", report.ReportId, "A", "MESSAGE", "p1"), (moderation.SourceType, moderation.SourceId, moderation.SubjectMemberId, moderation.ResourceType, moderation.ResourceId));
    }

    [Fact]
    public async Task Reporting_rejects_own_messages_outsiders_and_unknown_categories()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.MemberProfiles.Add(new MemberProfile { MemberId = "C", DisplayName = "C", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db);
        var conversationId = await ConnectAsync(service, "A", "B");
        await service.SendMessageAsync("A", conversationId, new("p2", "Hi"), "rep-m2", default);

        var own = await Assert.ThrowsAsync<DomainException>(() => service.ReportMessageAsync("A", "p2", new("SPAM"), default));
        var outsider = await Assert.ThrowsAsync<DomainException>(() => service.ReportMessageAsync("C", "p2", new("SPAM"), default));
        var category = await Assert.ThrowsAsync<DomainException>(() => service.ReportMessageAsync("B", "p2", new("BORING"), default));
        var missing = await Assert.ThrowsAsync<DomainException>(() => service.ReportMessageAsync("B", "nope", new("SPAM"), default));

        Assert.Equal(("MESSAGE_REPORT_FORBIDDEN", 403), (own.Code, own.StatusCode));
        Assert.Equal(("CONVERSATION_FORBIDDEN", 403), (outsider.Code, outsider.StatusCode));
        Assert.Equal(("MESSAGE_REPORT_INVALID", 400), (category.Code, category.StatusCode));
        Assert.Equal(("MESSAGE_NOT_FOUND", 404), (missing.Code, missing.StatusCode));
        Assert.Empty(db.MemberReports); Assert.Empty(db.ModerationCases);
    }

    [Fact]
    public async Task Commit_is_sent_by_a_live_registered_member_with_a_plan_and_replays_by_key()
    {
        await using var db = Db();
        var service = await CommitSetupAsync(db, "Z");
        db.EventRegistrations.Remove(db.EventRegistrations.Single(x => x.MemberId == "Z"));
        await db.SaveChangesAsync();
        var endsAt = db.EventRecords.Single().EndsAt;

        var sent = await service.CreateConnectionRequestAsync("A", Commit("B"), "commit-1", default);
        var replay = await service.CreateConnectionRequestAsync("A", Commit("B"), "commit-1", default);

        Assert.Equal(("PENDING", 4, endsAt), (sent.Status, sent.CommitsRemaining, sent.ExpiresAt));
        Assert.Equal((sent.RequestId, 4), (replay.RequestId, replay.CommitsRemaining));
        Assert.Single(db.SocialConnectionRequests);
        await AssertCode("COMMIT_ALREADY_SENT", 409, () => service.CreateConnectionRequestAsync("A", Commit("B"), "commit-2", default));
        await AssertCode("COMMIT_SENDER_NOT_LIVE", 403, () => service.CreateConnectionRequestAsync("B", Commit("A"), "commit-3", default));
        await AssertCode("EVENT_REGISTRATION_REQUIRED", 403, () => service.CreateConnectionRequestAsync("A", Commit("Z"), "commit-4", default));
        await AssertCode("PLAN_INVALID", 400, () => service.CreateConnectionRequestAsync("A", new("B", ContextId: "E", Plan: new(new("THEIR_CHOICE"), "TOMORROW")), "commit-5", default));
        await AssertCode("PLAN_INVALID", 400, () => service.CreateConnectionRequestAsync("A", new("B", ContextId: "E"), "commit-6", default));
        await AssertCode("MEETING_SPOT_NOT_FOUND", 404, () => service.CreateConnectionRequestAsync("A", new("B", ContextId: "E", Plan: new(new("SPOT", "bar"), "NOW")), "commit-7", default));
        await AssertCode("EVENT_NOT_FOUND", 404, () => service.CreateConnectionRequestAsync("A", new("B", ContextId: "nope", Plan: new(new("THEIR_CHOICE"), "NOW")), "commit-8", default));
    }

    [Fact]
    public async Task Commit_to_a_blocked_member_fails_like_an_unknown_member()
    {
        await using var db = Db();
        var service = await CommitSetupAsync(db);
        db.MemberBlocks.Add(new MemberBlock { BlockerMemberId = "B", BlockedMemberId = "A" });
        await db.SaveChangesAsync();

        await AssertCode("PROFILE_NOT_FOUND", 404, () => service.CreateConnectionRequestAsync("A", Commit("B"), "commit-blocked", default));
        await AssertCode("PROFILE_NOT_FOUND", 404, () => service.CreateConnectionRequestAsync("A", Commit("nobody"), "commit-unknown", default));
    }

    [Fact]
    public async Task Commit_limit_is_five_per_event_whatever_happened_to_them()
    {
        await using var db = Db();
        var service = await CommitSetupAsync(db, "C1", "C2", "C3", "C4", "C5");
        foreach (var id in new[] { "B", "C1", "C2", "C3", "C4" }) await service.CreateConnectionRequestAsync("A", Commit(id), $"limit-{id}", default);
        var declined = db.SocialConnectionRequests.Single(x => x.RecipientMemberId == "C1");
        await service.DecideConnectionRequestAsync("C1", declined.RequestId, new("DECLINE"), "limit-decline", default);

        await AssertCode("COMMIT_LIMIT_REACHED", 409, () => service.CreateConnectionRequestAsync("A", Commit("C5"), "limit-C5", default));
        Assert.Equal(new CommitQuotaResponse(5, 5, 0), await service.GetCommitQuotaAsync("A", "E", default));
        Assert.Equal(new CommitQuotaResponse(5, 0, 5), await service.GetCommitQuotaAsync("B", "E", default));
        Assert.Empty(await service.GetMeetingSpotsAsync("A", "E", default));
    }

    [Fact]
    public async Task Incoming_commit_shows_role_only_and_a_decline_stays_invisible_to_the_sender()
    {
        await using var db = Db();
        var service = await CommitSetupAsync(db);
        var sent = await service.CreateConnectionRequestAsync("A", Commit("B", "NEXT_BREAK"), "decline-1", default);

        var incoming = Assert.Single(await service.GetIncomingCommitsAsync("B", "E", default));
        Assert.Equal((sent.RequestId, "E", "Event", "THEIR_CHOICE", "NEXT_BREAK", "A headline", "SALES"), (incoming.RequestId, incoming.Event.EventId, incoming.Event.Name, incoming.Plan.Where, incoming.Plan.When, incoming.Sender.Headline, incoming.Sender.RoleCategory));
        Assert.Null((await service.GetVisibleProfileAsync("B", "A", default)).DisplayName);
        Assert.Equal("A", (await service.GetVisibleProfileAsync("A", "A", default)).DisplayName);
        Assert.DoesNotContain(db.Changes, x => x.ResourceType == "CONNECTION_REQUEST" && x.PayloadJson!.Contains("\"A\""));

        Assert.Null(await service.DecideConnectionRequestAsync("B", sent.RequestId, new("DECLINE"), "decline-2", default));

        Assert.Empty(await service.GetIncomingCommitsAsync("B", null, default));
        Assert.Equal("PENDING", Assert.Single(await service.GetOutgoingCommitsAsync("A", "E", default)).Status);
        Assert.Equal("PENDING", (await service.CreateConnectionRequestAsync("A", Commit("B", "NEXT_BREAK"), "decline-1", default)).Status);
        await AssertCode("COMMIT_ALREADY_SENT", 409, () => service.CreateConnectionRequestAsync("A", Commit("B"), "decline-3", default));
        db.SocialConnectionRequests.Single().ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Equal("EXPIRED", Assert.Single(await service.GetOutgoingCommitsAsync("A", null, default)).Status);
    }

    [Fact]
    public async Task Accepting_a_commit_reveals_names_and_pins_the_plan_on_the_conversation()
    {
        await using var db = Db();
        var service = await CommitSetupAsync(db, "C");
        var sent = await service.CreateConnectionRequestAsync("A", Commit("B"), "accept-c1", default);
        var stale = await service.CreateConnectionRequestAsync("A", Commit("C"), "accept-c2", default);
        db.SocialConnectionRequests.Single(x => x.RequestId == stale.RequestId).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var accepted = await service.DecideConnectionRequestAsync("B", sent.RequestId, new("ACCEPT"), "accept-c3", default);

        Assert.NotNull(accepted);
        Assert.Equal(("A", "A"), (accepted.MemberId, accepted.DisplayName));
        var conversation = await service.GetConversationAsync("B", accepted.ConversationId, default);
        Assert.Equal(("A", "THEIR_CHOICE", "IN_10_MIN", "E"), (conversation.DisplayName, conversation.Plan?.Where, conversation.Plan?.When, conversation.Plan?.EventId));
        var outgoing = (await service.GetOutgoingCommitsAsync("A", "E", default)).Single(x => x.RequestId == sent.RequestId);
        Assert.Equal(("ACCEPTED", "B", accepted.ConversationId), (outgoing.Status, outgoing.Recipient.DisplayName, outgoing.ConversationId));
        Assert.Equal("A", (await service.GetVisibleProfileAsync("B", "A", default)).DisplayName);
        await AssertCode("COMMIT_NOT_PENDING", 409, () => service.DecideConnectionRequestAsync("B", sent.RequestId, new("ACCEPT"), "accept-c4", default));
        await AssertCode("COMMIT_EXPIRED", 409, () => service.DecideConnectionRequestAsync("C", stale.RequestId, new("ACCEPT"), "accept-c5", default));
        Assert.Empty(await service.GetIncomingCommitsAsync("C", null, default));
    }

    [Fact]
    public async Task Going_live_again_extends_the_session_onto_the_latest_consent()
    {
        await using var db = Db(); SeedMembersAndEvent(db);
        db.EventRegistrations.Add(new EventRegistration { EventId = "E", MemberId = "A", Status = "REGISTERED" });
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        var first = await service.StartLiveModeAsync("A", "E", new(30), default);
        var latest = await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);

        var again = await service.StartLiveModeAsync("A", "E", new(60), default);

        Assert.Equal(first.SessionId, again.SessionId);
        Assert.Equal(latest.MemberConsentId, db.LiveModeSessions.Single().ConsentRecordId);
    }

    private static async Task<CoreService> CommitSetupAsync(CoreDbContext db, params string[] others)
    {
        SeedMembersAndEvent(db);
        await db.SaveChangesAsync();
        db.MemberProfiles.Single(x => x.MemberId == "A").Headline = "A headline";
        db.MemberProfiles.Single(x => x.MemberId == "A").Sector = "SALES";
        foreach (var id in others) db.MemberProfiles.Add(new MemberProfile { MemberId = id, DisplayName = id, Status = "ACTIVE" });
        foreach (var id in new[] { "A", "B" }.Concat(others)) db.EventRegistrations.Add(new EventRegistration { EventId = "E", MemberId = id, Status = "REGISTERED" });
        db.LiveModeSessions.Add(new LiveModeSession { EventId = "E", MemberId = "A", ActiveUntil = DateTimeOffset.UtcNow.AddMinutes(30) });
        await db.SaveChangesAsync();
        return Service(db);
    }

    private static ConnectionRequestCreate Commit(string recipient, string when = "IN_10_MIN") => new(recipient, ContextId: "E", Plan: new(new("THEIR_CHOICE"), when));

    private static async Task AssertCode(string code, int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<DomainException>(action);
        Assert.Equal((code, status), (error.Code, error.StatusCode));
    }

    private static async Task<string> ConnectAsync(CoreService service, string sender, string recipient)
    {
        var request = await service.CreateConnectionRequestAsync(sender, new(recipient), "request-key", default);
        return (await service.DecideConnectionRequestAsync(recipient, request.RequestId, new("ACCEPT"), $"accept-{sender}-{recipient}", default))!.ConversationId;
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

        Assert.Equal(("EVENT_NOT_LIVE", 409), (notStarted.Code, notStarted.StatusCode));
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

    [Fact]
    public async Task Outbox_aggregate_ids_fit_the_64_character_column_for_real_event_and_member_ids()
    {
        await using var db = Db();
        var eventId = "evt_" + new string('a', 32);
        var memberA = "mem_" + new string('b', 32);
        var memberB = "mem_" + new string('c', 32);
        db.MemberProfiles.AddRange(new MemberProfile { MemberId = memberA, DisplayName = "A", Status = "ACTIVE" }, new MemberProfile { MemberId = memberB, DisplayName = "B", Status = "ACTIVE" });
        db.EventRecords.Add(new EventRecord { EventId = eventId, Name = "Long ids", StartsAt = DateTimeOffset.UtcNow.AddHours(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var service = Service(db);

        await service.RegisterAsync(memberA, eventId, default);
        await service.RegisterAsync(memberA, eventId, default);
        await service.BlockAsync(memberA, new(memberB), default);

        var registration = db.OutboxEvents.Where(x => x.AggregateType == "EVENT_REGISTRATION").Select(x => x.AggregateId).ToList();
        var relationship = Assert.Single(db.OutboxEvents.Where(x => x.AggregateType == "MEMBER_RELATIONSHIP")).AggregateId;
        Assert.All(db.OutboxEvents, x => Assert.True(x.AggregateId.Length <= 64, x.AggregateId));
        Assert.Single(registration.Distinct());
        Assert.StartsWith("agg_", registration[0]);
        Assert.StartsWith("agg_", relationship);
    }

    [Fact]
    public async Task Short_outbox_aggregate_ids_keep_the_readable_pair_form()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();

        await Service(db).RegisterAsync("A", "E", default);

        Assert.Equal("E:A", Assert.Single(db.OutboxEvents.Where(x => x.AggregateType == "EVENT_REGISTRATION")).AggregateId);
    }

    [Fact]
    public async Task Unknown_member_is_not_created_and_returns_member_not_registered()
    {
        await using var db = Db();
        var service = Service(db);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.ProvisionMemberAsync("never-registered", default));

        Assert.Equal(("MEMBER_NOT_REGISTERED", 404), (error.Code, error.StatusCode));
        Assert.Empty(db.MemberProfiles);
    }

    // A registered account (identity row) without a profile yet, like iam.member before onboarding.
    private static void RegisterAccountOnly(CoreDbContext db, string memberId)
    {
        db.MemberIdentities.Add(new MemberIdentity { MemberId = memberId, Provider = "EMAIL", ProviderSubjectHash = new string('0', 64) + memberId });
        db.SaveChanges();
    }

    [Fact]
    public async Task Member_event_routes_only_see_events_in_the_member_community()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        db.MemberAccounts.Add(new MemberAccount { MemberId = "A", CommunityId = "olga" });
        db.EventRecords.Single(x => x.EventId == "E").CommunityId = "olga";
        db.EventRecords.Add(new EventRecord { EventId = "F", CommunityId = "other", Name = "Other", StartsAt = DateTimeOffset.UtcNow.AddHours(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1), LiveModeEnabled = true });
        await db.SaveChangesAsync();
        var service = Service(db);

        var listed = await service.GetEventsAsync("A", default);
        var register = await Assert.ThrowsAsync<DomainException>(() => service.RegisterAsync("A", "F", default));
        var attendees = await Assert.ThrowsAsync<DomainException>(() => service.GetEventAttendeesAsync("A", "F", default));
        var live = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "F", new(30), default));
        var presence = await Assert.ThrowsAsync<DomainException>(() => service.RecordPresenceAsync("A", "F", new("hall-a", DateTimeOffset.UtcNow), default));

        Assert.Equal("E", Assert.Single(listed).EventId);
        Assert.Equal(2, (await service.GetEventsAsync(null, default)).Count);
        Assert.All(new[] { register, attendees, live, presence }, e => Assert.Equal(("EVENT_NOT_FOUND", 404), (e.Code, e.StatusCode)));
    }

    [Fact]
    public async Task Live_mode_returns_specific_codes_before_reaching_the_database_rule()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        db.MemberAccounts.AddRange(new MemberAccount { MemberId = "A", CommunityId = "olga" }, new MemberAccount { MemberId = "B", CommunityId = "olga", Status = "SUSPENDED" });
        var evt = db.EventRecords.Single(x => x.EventId == "E"); evt.CommunityId = "olga";
        db.EventRecords.Add(new EventRecord { EventId = "OFF", CommunityId = "olga", Name = "No live", StartsAt = DateTimeOffset.UtcNow.AddHours(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1), LiveModeEnabled = false });
        db.EventRecords.Add(new EventRecord { EventId = "LATER", CommunityId = "olga", Name = "Later", StartsAt = DateTimeOffset.UtcNow.AddDays(1), EndsAt = DateTimeOffset.UtcNow.AddDays(2), LiveModeEnabled = true });
        await db.SaveChangesAsync();
        var service = Service(db);
        foreach (var e in new[] { "E", "OFF", "LATER" }) { await service.RegisterAsync("A", e, default); await service.RegisterAsync("B", e, default); }

        var noConsent = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "E", new(30), default));
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        await service.RecordConsentAsync("B", new("LIVE_MODE", "1", "GRANTED"), default);
        var disabled = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "OFF", new(30), default));
        var notLive = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "LATER", new(30), default));
        var suspended = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("B", "E", new(30), default));
        db.ConsentPolicies.Single(x => x.PolicyId == "live-mode-v1").RetiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var retired = await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "E", new(30), default));

        Assert.Equal(("LIVE_MODE_CONSENT_REQUIRED", 403), (noConsent.Code, noConsent.StatusCode));
        Assert.Equal(("LIVE_MODE_DISABLED", 409), (disabled.Code, disabled.StatusCode));
        Assert.Equal(("EVENT_NOT_LIVE", 409), (notLive.Code, notLive.StatusCode));
        Assert.Equal(("MEMBER_NOT_ACTIVE", 403), (suspended.Code, suspended.StatusCode));
        Assert.Equal(("LIVE_MODE_CONSENT_REQUIRED", 403), (retired.Code, retired.StatusCode));
    }

    [Fact]
    public async Task Live_mode_session_is_clamped_to_the_event_end()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var evt = db.EventRecords.Single(x => x.EventId == "E"); evt.EndsAt = DateTimeOffset.UtcNow.AddMinutes(20);
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.RegisterAsync("A", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);

        var session = await service.StartLiveModeAsync("A", "E", new(240), default);

        Assert.True(session.ActiveUntil <= evt.EndsAt);
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
