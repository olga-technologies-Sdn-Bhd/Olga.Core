using Olga.Core.Domain;

namespace Olga.Core.Application;

public static class EventMatchingPolicies
{
    // Adds the default ACTIVE matching policy (database column defaults: registration required,
    // Live Mode required, VENUE proximity) when the event has none. Returns true when one was added.
    public static bool EnsureDefault(ICoreStore store, string eventId, DateTimeOffset now)
    {
        if (store.MatchingPolicies.Any(x => x.EventId == eventId && x.Status == "ACTIVE")) return false;
        store.Add(new EventMatchingPolicy { EventId = eventId, PolicyVersion = 1, Status = "ACTIVE", EffectiveFrom = now });
        return true;
    }
}
