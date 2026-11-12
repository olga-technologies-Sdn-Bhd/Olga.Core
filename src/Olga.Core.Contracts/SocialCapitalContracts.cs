namespace Olga.Core.Contracts;

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

public sealed record ContactImportItem(string ContactRecordId, string LinkId, string Label, string? Email = null, string? Phone = null);
public sealed record ContactImportRequest(IReadOnlyList<ContactImportItem> Contacts, int RetentionDays = 365);
public sealed record ContactImportResponse(int ImportedCount, IReadOnlyList<string> LinkIds);

public sealed record InviteBatchCreateRequest(string BatchId, string? VentureId, IReadOnlyList<string> LinkIds, int ExpiresInDays = 14);
public sealed record InvitationDispatch(string InvitationId, string LinkId, string Token, DateTimeOffset ExpiresAt);
public sealed record InviteBatchResponse(string BatchId, string Status, IReadOnlyList<InvitationDispatch> Invitations, string ETag);
public sealed record InvitationAcceptResponse(string InvitationId, string ConnectionId, string ConversationId, string LinkId);

public sealed record SciContactResponse(
    string LinkId,
    string? ContactMemberId,
    string Label,
    decimal Score,
    string RelationshipRole,
    bool CompetitorOverlay,
    bool IsStale,
    string OriginType,
    DateTimeOffset ScoredAt,
    string ETag);

public sealed record SciContactPage(IReadOnlyList<SciContactResponse> Items, string? NextCursor);

// Score bands match the UI guide: Emerging 10-24, Relevant 25-49, Strong 50-74, Exact 75-100.
public sealed record SciBandCounts(int Emerging, int Relevant, int Strong, int Exact);
public sealed record SciVentureSummary(string VentureId, string Name, int ContactCount, decimal? TopScore, decimal? AverageScore, int StaleCount, DateTimeOffset? LastScoredAt);
public sealed record SciSummaryResponse(int VentureCount, int ContactCount, SciBandCounts Bands, decimal? AverageScore, DateTimeOffset? LastScoredAt, IReadOnlyList<SciVentureSummary> Ventures);

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
