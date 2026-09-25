using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public interface IAdminEventService
{
    Task<IReadOnlyList<AdminEventResponse>> GetEventsAsync(string? status, CancellationToken ct);
    Task<AdminEventResponse> GetEventAsync(string eventId, CancellationToken ct);
    Task<AdminEventResponse> CreateEventAsync(string communityId, AdminEventCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<AdminEventResponse> UpdateEventAsync(string eventId, AdminEventUpdateRequest request, CancellationToken ct);
    Task<AdminEventResponse> PublishEventAsync(string eventId, CancellationToken ct);
    Task<AdminEventResponse> CancelEventAsync(string eventId, CancellationToken ct);
    Task<IReadOnlyList<AdminAttendeeResponse>> GetAttendeesAsync(string eventId, CancellationToken ct);
    Task<IReadOnlyList<AdminVenueResponse>> GetVenuesAsync(CancellationToken ct);
    Task<AdminVenueResponse> CreateVenueAsync(AdminVenueCreateRequest request, string idempotencyKey, CancellationToken ct);
}

public sealed class AdminEventService(ICoreStore store) : IAdminEventService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] EventStatuses = ["DRAFT", "PUBLISHED", "ACTIVE", "COMPLETED", "CANCELLED"];

    public Task<IReadOnlyList<AdminEventResponse>> GetEventsAsync(string? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (status is not null && !EventStatuses.Contains(status)) throw new DomainException("EVENT_STATUS_INVALID");
        var events = store.Events.Where(x => status == null || x.Status == status).OrderByDescending(x => x.StartsAt).ToArray();
        return Task.FromResult(MapAll(events));
    }

    public Task<AdminEventResponse> GetEventAsync(string eventId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(MapAll([FindEvent(eventId)])[0]);
    }

    public async Task<AdminEventResponse> CreateEventAsync(string communityId, AdminEventCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        Validate(request.Name, request.Description, request.StartsAt, request.EndsAt, request.VenueId);
        // Event ID is derived from the idempotency key so a retried create returns the original event.
        var eventId = $"evt_{Hash("event.create", communityId, idempotencyKey)[..32]}";
        var existing = store.Events.SingleOrDefault(x => x.EventId == eventId);
        if (existing is not null) return MapAll([existing])[0];

        var row = new EventRecord
        {
            EventId = eventId,
            CommunityId = communityId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            VenueId = request.VenueId,
            LiveModeEnabled = request.LiveModeEnabled,
            Status = request.Publish ? "PUBLISHED" : "DRAFT"
        };
        store.Add(row);
        AddChange(row);
        await store.SaveAsync(ct);
        return MapAll([row])[0];
    }

    public async Task<AdminEventResponse> UpdateEventAsync(string eventId, AdminEventUpdateRequest request, CancellationToken ct)
    {
        var row = FindEvent(eventId);
        if (row.Status is "COMPLETED" or "CANCELLED") throw new DomainException("EVENT_NOT_EDITABLE", 409);
        Validate(request.Name, request.Description, request.StartsAt, request.EndsAt, request.VenueId);
        row.Name = request.Name.Trim();
        row.Description = request.Description?.Trim();
        row.StartsAt = request.StartsAt;
        row.EndsAt = request.EndsAt;
        row.VenueId = request.VenueId;
        row.LiveModeEnabled = request.LiveModeEnabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        AddChange(row);
        await store.SaveAsync(ct);
        return MapAll([row])[0];
    }

    public async Task<AdminEventResponse> PublishEventAsync(string eventId, CancellationToken ct)
    {
        var row = FindEvent(eventId);
        if (row.Status == "PUBLISHED") return MapAll([row])[0];
        if (row.Status != "DRAFT") throw new DomainException("EVENT_STATE_CONFLICT", 409);
        if (row.EndsAt <= DateTimeOffset.UtcNow) throw new DomainException("EVENT_ALREADY_ENDED", 409);
        row.Status = "PUBLISHED";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        AddChange(row);
        await store.SaveAsync(ct);
        return MapAll([row])[0];
    }

    public async Task<AdminEventResponse> CancelEventAsync(string eventId, CancellationToken ct)
    {
        var row = FindEvent(eventId);
        if (row.Status == "CANCELLED") return MapAll([row])[0];
        if (row.Status == "COMPLETED") throw new DomainException("EVENT_STATE_CONFLICT", 409);
        var now = DateTimeOffset.UtcNow;
        row.Status = "CANCELLED";
        row.UpdatedAt = now;
        // Nobody can stay live at a cancelled event.
        foreach (var session in store.LiveSessions.Where(x => x.EventId == eventId && x.Status == "ACTIVE").ToArray())
        {
            session.Status = "DISABLED";
            session.RevokedAt = now;
            session.UpdatedAt = now;
        }
        AddChange(row);
        await store.SaveAsync(ct);
        return MapAll([row])[0];
    }

    public Task<IReadOnlyList<AdminAttendeeResponse>> GetAttendeesAsync(string eventId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = FindEvent(eventId);
        var now = DateTimeOffset.UtcNow;
        var registrations = store.Registrations.Where(x => x.EventId == eventId).OrderByDescending(x => x.RegisteredAt).ToArray();
        var memberIds = registrations.Select(x => x.MemberId).ToArray();
        var profiles = store.Profiles.Where(x => memberIds.Contains(x.MemberId)).ToDictionary(x => x.MemberId);
        var live = store.LiveSessions.Where(x => x.EventId == eventId && x.Status == "ACTIVE" && x.ActiveUntil > now).Select(x => x.MemberId).ToHashSet();
        IReadOnlyList<AdminAttendeeResponse> result = registrations.Select(x =>
        {
            var p = profiles.GetValueOrDefault(x.MemberId);
            return new AdminAttendeeResponse(x.MemberId, p?.DisplayName ?? x.MemberId, p?.Headline, x.Status, x.RegisteredAt, x.CheckedInAt, live.Contains(x.MemberId));
        }).ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<AdminVenueResponse>> GetVenuesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<AdminVenueResponse> result = store.Venues.OrderBy(x => x.Name).AsEnumerable().Select(Map).ToArray();
        return Task.FromResult(result);
    }

    public async Task<AdminVenueResponse> CreateVenueAsync(AdminVenueCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200) throw new DomainException("VENUE_INVALID");
        if (request.CountryCode is not { Length: 2 } || !request.CountryCode.All(char.IsAsciiLetter)) throw new DomainException("VENUE_COUNTRY_INVALID");
        if (request.City?.Length > 120 || request.Region?.Length > 120) throw new DomainException("VENUE_INVALID");
        if (string.IsNullOrWhiteSpace(request.TimezoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimezoneId, out _)) throw new DomainException("VENUE_TIMEZONE_INVALID");

        var venueId = $"ven_{Hash("venue.create", idempotencyKey)[..32]}";
        var existing = store.Venues.SingleOrDefault(x => x.VenueId == venueId);
        if (existing is not null) return Map(existing);

        var row = new Venue
        {
            VenueId = venueId,
            Name = request.Name.Trim(),
            CountryCode = request.CountryCode.ToUpperInvariant(),
            Region = request.Region?.Trim(),
            City = request.City?.Trim(),
            TimezoneId = request.TimezoneId
        };
        store.Add(row);
        await store.SaveAsync(ct);
        return Map(row);
    }

    private void Validate(string name, string? description, DateTimeOffset startsAt, DateTimeOffset endsAt, string? venueId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 250 || description?.Length > 2000) throw new DomainException("EVENT_INVALID");
        if (endsAt <= startsAt) throw new DomainException("EVENT_TIME_RANGE_INVALID");
        if (venueId is not null && !store.Venues.Any(x => x.VenueId == venueId && x.Status == "ACTIVE")) throw new DomainException("VENUE_NOT_FOUND", 404);
    }

    private EventRecord FindEvent(string id) => store.Events.SingleOrDefault(x => x.EventId == id) ?? throw new DomainException("EVENT_NOT_FOUND", 404);

    // Events are community-wide, so the change has no member scope and reaches every device's sync feed.
    private void AddChange(EventRecord x) => store.Add(new SyncChange
    {
        ResourceType = "EVENT",
        ResourceId = x.EventId,
        ChangeType = "UPSERT",
        PayloadJson = JsonSerializer.Serialize(new { event_id = x.EventId, x.Name, x.StartsAt, x.EndsAt, x.Status, x.LiveModeEnabled, x.VenueId }, JsonOptions)
    });

    private IReadOnlyList<AdminEventResponse> MapAll(IReadOnlyList<EventRecord> events)
    {
        var eventIds = events.Select(x => x.EventId).ToArray();
        var now = DateTimeOffset.UtcNow;
        var liveCounts = store.LiveSessions
            .Where(x => eventIds.Contains(x.EventId) && x.Status == "ACTIVE" && x.ActiveUntil > now)
            .Select(x => x.EventId).ToList()
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var attendeeCounts = store.Registrations
            .Where(x => eventIds.Contains(x.EventId) && x.Status != "CANCELLED")
            .Select(x => x.EventId).ToList()
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var venueIds = events.Where(x => x.VenueId != null).Select(x => x.VenueId!).Distinct().ToArray();
        var venueNames = store.Venues.Where(x => venueIds.Contains(x.VenueId)).ToDictionary(x => x.VenueId, x => x.Name);

        return events.Select(x => new AdminEventResponse(
            x.EventId, x.CommunityId, x.Name, x.Description, x.StartsAt, x.EndsAt, x.Status, x.LiveModeEnabled,
            x.VenueId, x.VenueId is not null ? venueNames.GetValueOrDefault(x.VenueId) : null,
            attendeeCounts.GetValueOrDefault(x.EventId, 0), liveCounts.GetValueOrDefault(x.EventId, 0),
            x.CreatedAt, x.UpdatedAt)).ToArray();
    }

    private static AdminVenueResponse Map(Venue x) => new(x.VenueId, x.Name, x.CountryCode, x.Region, x.City, x.TimezoneId, x.Status, x.CreatedAt);
    private static string Hash(params string?[] values) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();
}
