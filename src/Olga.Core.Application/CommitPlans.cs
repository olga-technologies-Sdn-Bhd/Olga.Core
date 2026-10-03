using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

// Interim storage: social.connection_request has no plan columns yet, so a Commit's plan is kept
// as a fixed first line of the note ("olga-plan:v1|<where>|<spot_id>|<when>"), followed by the
// member's own note. Replace with real columns once the database has them.
public static class CommitPlans
{
    public const int LimitPerEvent = 5;
    public const string TheirChoice = "THEIR_CHOICE";
    private const string Prefix = "olga-plan:v1|";
    private static readonly string[] Whens = ["NOW", "IN_10_MIN", "NEXT_BREAK", "AFTER_SESSION"];

    public sealed record Stored(string WhereType, string? SpotId, string When);

    public static Stored Validate(CommitPlan? plan)
    {
        if (plan?.Where is null || plan.When is null || !Whens.Contains(plan.When)) throw new DomainException("PLAN_INVALID");
        return plan.Where.Type switch
        {
            TheirChoice when plan.Where.SpotId is null => new(TheirChoice, null, plan.When),
            "SPOT" when !string.IsNullOrWhiteSpace(plan.Where.SpotId) && plan.Where.SpotId.Length <= 64 && !plan.Where.SpotId.Contains('|') => new("SPOT", plan.Where.SpotId, plan.When),
            _ => throw new DomainException("PLAN_INVALID")
        };
    }

    public static string Encode(Stored plan, string? note) => $"{Prefix}{plan.WhereType}|{plan.SpotId}|{plan.When}" + (string.IsNullOrWhiteSpace(note) ? "" : "\n" + note.Trim());

    public static Stored? Decode(string? note)
    {
        if (note is null || !note.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = note[Prefix.Length..].Split('\n', 2)[0].Split('|');
        return parts.Length == 3 ? new(parts[0], parts[1].Length == 0 ? null : parts[1], parts[2]) : null;
    }

    // No meeting spots exist yet (no table in the database), so a SPOT plan shows its ID.
    public static CommitPlanResponse ToResponse(Stored plan, string? eventId) =>
        new(plan.WhereType == "SPOT" ? plan.SpotId ?? TheirChoice : TheirChoice, plan.When, plan.SpotId, eventId);
}
