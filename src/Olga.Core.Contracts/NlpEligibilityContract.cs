namespace Olga.Core.Contracts;

/// <summary>
/// Versioned database projection contract consumed by Olga.Nlp. These views remain read-only;
/// Core-owned policy state is the authority and an NLP score never grants eligibility.
/// </summary>
public static class NlpEligibilityContractV1
{
    public const string MemberContextView = "nlp.vw_member_context_eligibility";
    public static IReadOnlyList<string> MemberContextColumns { get; } =
    [
        "member_id", "context_id", "is_live", "is_visible", "has_consent",
        "is_suspended", "is_deleted", "community_id"
    ];

    public const string MemberRelationshipView = "nlp.vw_member_relationship";
    public static IReadOnlyList<string> MemberRelationshipColumns { get; } =
    [
        "member_id", "other_member_id", "context_id", "is_blocked", "is_connected"
    ];
}
