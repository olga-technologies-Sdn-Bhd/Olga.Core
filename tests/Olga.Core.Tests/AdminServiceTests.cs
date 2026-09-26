using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class AdminServiceTests
{
    [Fact]
    public async Task Stats_count_members_upcoming_events_open_work_and_recent_connections()
    {
        await using var db = Db();
        db.MemberProfiles.AddRange(new MemberProfile { MemberId = "A", Status = "ACTIVE" }, new MemberProfile { MemberId = "B" });
        db.EventRecords.AddRange(
            new EventRecord { EventId = "future", Name = "F", Status = "PUBLISHED", StartsAt = DateTimeOffset.UtcNow.AddDays(1), EndsAt = DateTimeOffset.UtcNow.AddDays(2) },
            new EventRecord { EventId = "past", Name = "P", Status = "PUBLISHED", StartsAt = DateTimeOffset.UtcNow.AddDays(-2), EndsAt = DateTimeOffset.UtcNow.AddDays(-1) });
        db.ModerationCases.AddRange(new ModerationCase { Status = "OPEN" }, new ModerationCase { Status = "CLOSED" });
        db.MemberPrivacyRequests.AddRange(new PrivacyRequest { MemberId = "A" }, new PrivacyRequest { MemberId = "B", Status = "COMPLETED" });
        db.SocialConnections.AddRange(
            new Connection { MemberLowId = "A", MemberHighId = "B" },
            new Connection { MemberLowId = "A", MemberHighId = "C", ConnectedAt = DateTimeOffset.UtcNow.AddDays(-30) });
        await db.SaveChangesAsync();

        var stats = await new AdminService(db).GetStatsAsync(default);

        Assert.Equal(new AdminStatsResponse(2, 1, 1, 1, 1, 1), stats);
    }

    [Fact]
    public async Task Member_list_filters_by_status_and_search_and_rejects_unknown_status()
    {
        await using var db = Db();
        db.MemberProfiles.AddRange(
            new MemberProfile { MemberId = "A1", DisplayName = "Asha Rao", Status = "ACTIVE" },
            new MemberProfile { MemberId = "B2", DisplayName = "Ben Lee", Status = "ACTIVE" },
            new MemberProfile { MemberId = "C3", DisplayName = "Asha Iyer", Status = "HIDDEN" });
        await db.SaveChangesAsync();
        var admin = new AdminService(db);

        var result = await admin.GetMembersAsync("ACTIVE", "asha", default);
        var invalid = await Assert.ThrowsAsync<DomainException>(() => admin.GetMembersAsync("BANNED", null, default));

        Assert.Equal("A1", Assert.Single(result).MemberId);
        Assert.Equal("PROFILE_STATUS_INVALID", invalid.Code);
    }

    [Fact]
    public async Task Hiding_a_member_disables_live_sessions_and_emits_a_sync_change()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", Status = "ACTIVE" });
        db.LiveModeSessions.Add(new LiveModeSession { EventId = "e", MemberId = "A", ActiveUntil = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        var admin = new AdminService(db);

        var hidden = await admin.SetMemberStatusAsync("A", new("HIDDEN"), default);
        var invalid = await Assert.ThrowsAsync<DomainException>(() => admin.SetMemberStatusAsync("A", new("BANNED"), default));
        var missing = await Assert.ThrowsAsync<DomainException>(() => admin.SetMemberStatusAsync("missing", new("ACTIVE"), default));

        Assert.Equal("HIDDEN", hidden.ProfileStatus);
        Assert.Equal("DISABLED", db.LiveModeSessions.Single().Status);
        Assert.Contains(db.Changes, x => x.ResourceType == "PROFILE" && x.MemberScopeId == "A");
        Assert.Equal("PROFILE_STATUS_INVALID", invalid.Code);
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Closing_a_report_stamps_closed_at_and_reopening_clears_it()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "S", DisplayName = "Subject" });
        db.ModerationCases.Add(new ModerationCase { ModerationCaseId = "r1", SubjectMemberId = "S" });
        await db.SaveChangesAsync();
        var admin = new AdminService(db);

        var closed = await admin.SetReportStatusAsync("r1", new("CLOSED"), default);
        var reopened = await admin.SetReportStatusAsync("r1", new("TRIAGED"), default);
        var invalid = await Assert.ThrowsAsync<DomainException>(() => admin.SetReportStatusAsync("r1", new("DONE"), default));
        var missing = await Assert.ThrowsAsync<DomainException>(() => admin.SetReportStatusAsync("missing", new("CLOSED"), default));

        Assert.NotNull(closed.ClosedAt);
        Assert.Equal("Subject", closed.SubjectDisplayName);
        Assert.Null(reopened.ClosedAt);
        Assert.Single(await admin.GetReportsAsync("TRIAGED", default));
        Assert.Equal("REPORT_STATUS_INVALID", invalid.Code);
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Completed_privacy_request_is_stamped_and_cannot_be_reopened()
    {
        await using var db = Db();
        db.MemberPrivacyRequests.Add(new PrivacyRequest { PrivacyRequestId = "p1", MemberId = "A" });
        await db.SaveChangesAsync();
        var admin = new AdminService(db);

        var completed = await admin.SetPrivacyRequestStatusAsync("p1", new("COMPLETED"), default);
        var reopen = await Assert.ThrowsAsync<DomainException>(() => admin.SetPrivacyRequestStatusAsync("p1", new("PROCESSING"), default));

        Assert.NotNull(completed.VerifiedAt);
        Assert.NotNull(completed.CompletedAt);
        Assert.Contains(db.Changes, x => x.ResourceType == "PRIVACY_REQUEST" && x.MemberScopeId == "A");
        Assert.Equal("PRIVACY_REQUEST_CLOSED", reopen.Code);
        Assert.Equal(409, reopen.StatusCode);
        Assert.Empty(await admin.GetPrivacyRequestsAsync("OPEN", default));
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
