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
