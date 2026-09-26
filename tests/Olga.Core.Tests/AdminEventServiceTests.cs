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
        Assert.Empty(await Core(db).GetEventsAsync(null, default));

        await admin.PublishEventAsync(created.EventId, default);
        Assert.Single(await Core(db).GetEventsAsync(null, default));
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

    [Fact]
    public async Task Attendees_list_registrations_with_profile_and_live_state()
    {
        await using var db = Db();
        var admin = new AdminEventService(db);
        var evt = await admin.CreateEventAsync("olga", new("Meetup", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), Publish: true), "create-3", default);
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "Asha", Headline = "Engineer" });
        db.EventRegistrations.AddRange(new EventRegistration { EventId = evt.EventId, MemberId = "A" }, new EventRegistration { EventId = evt.EventId, MemberId = "B" });
        db.LiveModeSessions.Add(new LiveModeSession { EventId = evt.EventId, MemberId = "A", ActiveUntil = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();

        var attendees = await admin.GetAttendeesAsync(evt.EventId, default);
        var missing = await Assert.ThrowsAsync<DomainException>(() => admin.GetAttendeesAsync("missing", default));

        var asha = attendees.Single(x => x.MemberId == "A");
        var unknown = attendees.Single(x => x.MemberId == "B");
        Assert.Equal(("Asha", "Engineer", true), (asha.DisplayName, asha.Headline, asha.IsLive));
        Assert.Equal(("B", false), (unknown.DisplayName, unknown.IsLive));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Venue_create_is_idempotent_and_validates_country_and_timezone()
    {
        await using var db = Db();
        var admin = new AdminEventService(db);

        var created = await admin.CreateVenueAsync(new(" Hall ", "my", "UTC"), "venue-2", default);
        var replay = await admin.CreateVenueAsync(new("Other", "in", "UTC"), "venue-2", default);
        var country = await Assert.ThrowsAsync<DomainException>(() => admin.CreateVenueAsync(new("Hall", "MYS", "UTC"), "venue-3", default));
        var timezone = await Assert.ThrowsAsync<DomainException>(() => admin.CreateVenueAsync(new("Hall", "MY", "Mars/Base"), "venue-4", default));

        Assert.Equal(("Hall", "MY"), (created.Name, created.CountryCode));
        Assert.Equal(created.VenueId, replay.VenueId);
        Assert.Single(await admin.GetVenuesAsync(default));
        Assert.Equal("VENUE_COUNTRY_INVALID", country.Code);
        Assert.Equal("VENUE_TIMEZONE_INVALID", timezone.Code);
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CoreService Core(CoreDbContext db) => new(db, new AesIdentityProtector(new byte[32]));
}
