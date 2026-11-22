using System.Text.Json;

namespace Olga.Core.Contracts;

public sealed record SciAdminOverviewResponse(
    int MemberStateCount,
    int ReadyCount,
    int RecalculatingCount,
    long TotalSciCount,
    int PendingApprovalCount,
    int FailedScoringJobCount,
    DateTimeOffset AsOf);

public sealed record SciAdminConfigurationResponse(
    string ScoringConfigId,
    string ScorerVersion,
    string Status,
    decimal SemanticWeight,
    decimal ComplementarityWeight,
    decimal RecencyWeight,
    short EstimatedScoreCap,
    short WaitlistThreshold,
    short NetworkThreshold,
    short RecencyFullDays,
    short RecencyDecayDays,
    decimal EstimatedRecency,
    string ProductConfigId,
    int InitialGoalValue,
    int? NextGoalIncrement,
    short RelationshipInactivityDays,
    short OutcomeValidityDays,
    short ReachOutDefaultThreshold,
    string ProductStatus,
    string ETag);

public sealed record SciAdminChangeProposalRequest(
    string ChangeType,
    string ResourceType,
    string ResourceId,
    JsonElement Payload);

public sealed record SciAdminChangeDecisionRequest(bool Approve);

public sealed record SciAdminChangeApprovalResponse(
    string ChangeApprovalId,
    string ChangeType,
    string ResourceType,
    string ResourceId,
    JsonElement Payload,
    string PayloadHash,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    string Status,
    DateTimeOffset? AppliedAt,
    IReadOnlyList<string> AllowedActions,
    string ETag);

public sealed record SciAdminChangeApprovalPage(IReadOnlyList<SciAdminChangeApprovalResponse> Items, string? NextCursor);
