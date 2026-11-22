namespace Olga.Core.Contracts;

public sealed record MemberDiscoveryPreferenceRequest(bool FindByPhone, bool FindByEmail);
public sealed record MemberDiscoveryPreferenceResponse(bool FindByPhone, bool FindByEmail, string ETag, DateTimeOffset UpdatedAt);

public sealed record VentureCreateRequest(
    string VentureId,
    string Name,
    string? OrganizationId = null,
    string? IndustryCode = null,
    string? StageCode = null,
    string? RevenueBandCode = null,
    string? TeamSizeBandCode = null,
    IReadOnlyList<string>? GeographyCodes = null);

public sealed record VentureResponse(
    string VentureId,
    string Name,
    string? OrganizationId,
    string? IndustryCode,
    string? StageCode,
    string? RevenueBandCode,
    string? TeamSizeBandCode,
    string Status,
    IReadOnlyList<string> GeographyCodes,
    DateTimeOffset UpdatedAt,
    string ETag);

public sealed record ContactImportItem(
    string ContactRecordId,
    string LinkId,
    string Label,
    string? Email = null,
    string? Phone = null,
    string? JobTitle = null,
    string? Company = null,
    string? Industry = null,
    string? City = null);
public sealed record ContactImportRequest(IReadOnlyList<ContactImportItem> Contacts, int RetentionDays = 365);
public sealed record ContactImportResponse(int ImportedCount, IReadOnlyList<string> LinkIds);

public sealed record InviteBatchCreateRequest(string BatchId, string? VentureId, IReadOnlyList<string> LinkIds, int ExpiresInDays = 14);
public sealed record InvitationDispatch(string InvitationId, string LinkId, string Token, DateTimeOffset ExpiresAt);
public sealed record InviteBatchResponse(string BatchId, string Status, IReadOnlyList<InvitationDispatch> Invitations, string ETag);
public sealed record InvitationAcceptResponse(string InvitationId, string ConnectionRequestId, string LifecycleState, string LinkId);

public sealed record SciContactResponse(
    string LinkId,
    string? ContactMemberId,
    string Label,
    string LifecycleState,
    int? Score,
    string Band,
    bool IsEstimated,
    bool CountsInSci,
    bool CountsInPotential,
    string ProjectionState,
    DateTimeOffset? FrozenAt,
    string? FreezeReason,
    string? LayerCode,
    short? LayerOrdinal,
    decimal OutcomePoints,
    decimal TieStrength,
    string? ReasonCode,
    string? ScorerVersion,
    string? ScoringConfigId,
    DateTimeOffset? ScoreCalculatedAt,
    DateTimeOffset? LastActivityAt,
    long ProjectionVersion,
    DateTimeOffset AsOf,
    string ETag);

public sealed record SciContactPage(IReadOnlyList<SciContactResponse> Items, string? NextCursor);

public sealed record SciGoalMilestoneResponse(short Percent, DateTimeOffset ReachedAt);
public sealed record SciGoalResponse(
    string GoalId,
    int GoalValue,
    int GoalSequence,
    string OwnerType,
    int ProgressPercent,
    DateTimeOffset? TargetAt,
    IReadOnlyList<SciGoalMilestoneResponse> Milestones);
public sealed record SciSummaryResponse(
    int SciCount,
    int PotentialCount,
    int WaitlistCount,
    int RepositoryCount,
    int TotalContactCount,
    int CoreCount,
    int MonthChange,
    int IntentUpdateCount,
    string ProjectionStatus,
    long ProjectionVersion,
    string? ScorerVersion,
    string? ScoringConfigId,
    DateTimeOffset AsOf,
    SciGoalResponse? Goal,
    string ETag);

public sealed record SciReachOutResponse(
    string RecommendationId,
    string LinkId,
    string ContactMemberId,
    string ReasonCode,
    short MatchScore,
    decimal RelevanceScore,
    int Rank,
    long ProjectionVersion,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt);
public sealed record SciReachOutPage(IReadOnlyList<SciReachOutResponse> Items, string? NextCursor);

public sealed record InAppNotificationResponse(
    string NotificationId,
    string PurposeCode,
    string? ResourceType,
    string? ResourceId,
    string TemplateCode,
    string Status,
    DateTimeOffset ScheduledAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string ETag);
public sealed record InAppNotificationPage(IReadOnlyList<InAppNotificationResponse> Items,string? NextCursor);

public sealed record RelationshipOutcomeCreateRequest(
    string OutcomeId,
    string ContactMemberId,
    string OutcomeType,
    DateTimeOffset OccurredAt,
    string? ResultType = null);
public sealed record RelationshipOutcomeDecisionRequest(string Decision);
public sealed record RelationshipOutcomeResponse(
    string OutcomeId,
    string OutcomeType,
    string Status,
    string InitiatedByMemberId,
    string ContactMemberId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ConfirmationDueAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? WithdrawalDueAt,
    DateTimeOffset? ValidUntil,
    decimal Points,
    int ConfirmationCount,
    string ETag);

public sealed record ImportedContactResponse(
    string LinkId,
    string ContactRecordId,
    string Label,
    string LifecycleState,
    string? ContactMemberId,
    bool HasEmail,
    bool HasPhone,
    string? InvitationId,
    string? InvitationStatus,
    DateTimeOffset? InvitationExpiresAt,
    DateTimeOffset ImportedAt,
    DateTimeOffset ExpiresAt);
public sealed record ImportedContactPage(IReadOnlyList<ImportedContactResponse> Items, string? NextCursor);

public sealed record InvitationStatusResponse(
    string InvitationId,
    string LinkId,
    string Label,
    string Status,
    DateTimeOffset? FirstOpenedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt);
public sealed record InviteBatchDetailResponse(string BatchId, string? VentureId, string Status, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, IReadOnlyList<InvitationStatusResponse> Invitations);
public sealed record InvitationResendRequest(int ExpiresInDays = 14);

public sealed record InvitationPreviewResponse(string InvitationId, string InviterMemberId, string InviterDisplayName, string? VentureName, string Status, DateTimeOffset ExpiresAt);
public sealed record PendingInvitationResponse(string InvitationId, string InviterMemberId, string InviterDisplayName, string? InviterHeadline, string? VentureName, DateTimeOffset InvitedAt, DateTimeOffset ExpiresAt);
