using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class AdminEventServiceTests
{
    [Fact]
    public async Task Created_draft_is_hidden_until_published_and_create_is_idempotent()
    {
        await using var db = Db();
        var admin = new AdminEventService(db);
        var request = new AdminEventCreateRequest("Summit", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));

        var created = await admin.CreateEventAsync("olga", request, "create-1", default);
        var replay = await admin.CreateEventAsync("olga", request, "create-1", default);
        Assert.Equal(created.EventId, replay.EventId);
        Assert.Equal("DRAFT", created.Status);
        Assert.Empty(await Core(db).GetEventsAsync(default));

        await admin.PublishEventAsync(created.EventId, default);
        Assert.Single(await Core(db).GetEventsAsync(default));
        Assert.Contains(db.Changes, x => x.ResourceType == "EVENT" && x.MemberScopeId == null);
    }

    [Fact]
    public async Task Invalid_time_range_and_unknown_venue_are_rejected()
    {
        await using var db = Db();
        var admin = new AdminEventService(db);
        var now = DateTimeOffset.UtcNow;

        var range = await Assert.ThrowsAsync<DomainException>(() => admin.CreateEventAsync("olga", new("X", now.AddDays(2), now.AddDays(1)), "k1", default));
        var venue = await Assert.ThrowsAsync<DomainException>(() => admin.CreateEventAsync("olga", new("X", now, now.AddDays(1), VenueId: "missing"), "k2", default));

        Assert.Equal("EVENT_TIME_RANGE_INVALID", range.Code);
        Assert.Equal("VENUE_NOT_FOUND", venue.Code);
    }

    [Fact]
    public async Task Cancel_disables_active_live_sessions_and_blocks_further_edits()
    {
        await using var db = Db();
        var admin = new AdminEventService(db);
        var venue = await admin.CreateVenueAsync(new("Hall", "in", "UTC", "Pune"), "venue-1", default);
        var evt = await admin.CreateEventAsync("olga", new("Expo", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), VenueId: venue.VenueId, Publish: true), "create-2", default);
        db.LiveModeSessions.Add(new LiveModeSession { EventId = evt.EventId, MemberId = "A", Status = "ACTIVE", ActiveUntil = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();

        var cancelled = await admin.CancelEventAsync(evt.EventId, default);

        Assert.Equal("CANCELLED", cancelled.Status);
        Assert.Equal("Hall", cancelled.Venue);
        Assert.Equal("DISABLED", db.LiveModeSessions.Single().Status);
        var edit = await Assert.ThrowsAsync<DomainException>(() => admin.UpdateEventAsync(evt.EventId, new("Expo 2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), default));
        Assert.Equal("EVENT_NOT_EDITABLE", edit.Code);
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CoreService Core(CoreDbContext db) => new(db, new AesIdentityProtector(new byte[32]));
}
