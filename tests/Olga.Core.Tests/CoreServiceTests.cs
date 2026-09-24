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

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CoreService Service(CoreDbContext db) => new(db, IdentityProtector);
    private static void SeedMembersAndEvent(CoreDbContext db)
    {
        db.MemberProfiles.AddRange(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" }, new MemberProfile { MemberId = "B", DisplayName = "B", Status = "ACTIVE" });
        db.ConsentPolicies.AddRange(
            new ConsentPolicy { PolicyId = "live-mode-v1", PurposeCode = "LIVE_MODE", Version = "1", ContentHash = new string('0', 64), EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) },
            new ConsentPolicy { PolicyId = "matching-v1", PurposeCode = "MATCHING", Version = "1", ContentHash = new string('1', 64), EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) });
        db.EventRecords.Add(new EventRecord { EventId = "E", Name = "Event", StartsAt = DateTimeOffset.UtcNow.AddHours(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1), LiveModeEnabled = true });
    }
}
