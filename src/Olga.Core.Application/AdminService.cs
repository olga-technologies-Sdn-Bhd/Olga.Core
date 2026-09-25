using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public interface IAdminService
{
    Task<AdminStatsResponse> GetStatsAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminMemberResponse>> GetMembersAsync(string? status, string? search, CancellationToken ct);
    Task<AdminMemberResponse> SetMemberStatusAsync(string memberId, AdminMemberStatusRequest request, CancellationToken ct);
    Task<IReadOnlyList<AdminReportResponse>> GetReportsAsync(string? status, CancellationToken ct);
    Task<AdminReportResponse> SetReportStatusAsync(string reportId, AdminStatusRequest request, CancellationToken ct);
    Task<IReadOnlyList<AdminPrivacyRequestResponse>> GetPrivacyRequestsAsync(string? status, CancellationToken ct);
    Task<AdminPrivacyRequestResponse> SetPrivacyRequestStatusAsync(string privacyRequestId, AdminStatusRequest request, CancellationToken ct);
}

public sealed class AdminService(ICoreStore store) : IAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    // Allowed values mirror the DB check constraints.
    private static readonly string[] ProfileStatuses = ["DRAFT", "PENDING_REVIEW", "ACTIVE", "HIDDEN"];
    private static readonly string[] ReportStatuses = ["OPEN", "TRIAGED", "ACTIONED", "CLOSED"];
    private static readonly string[] PrivacyStatuses = ["OPEN", "VERIFIED", "PROCESSING", "COMPLETED", "REJECTED"];

    public Task<AdminStatsResponse> GetStatsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var weekAgo = now.AddDays(-7);
        return Task.FromResult(new AdminStatsResponse(
            store.Profiles.Count(),
            store.Profiles.Count(x => x.Status == "ACTIVE"),
            store.Events.Count(x => x.Status == "PUBLISHED" && x.StartsAt > now),
            store.ModerationCases.Count(x => x.Status == "OPEN" || x.Status == "TRIAGED"),
            store.PrivacyRequests.Count(x => x.Status != "COMPLETED" && x.Status != "REJECTED"),
            store.Connections.Count(x => x.ConnectedAt >= weekAgo)));
    }

    public Task<IReadOnlyList<AdminMemberResponse>> GetMembersAsync(string? status, string? search, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (status is not null && !ProfileStatuses.Contains(status)) throw new DomainException("PROFILE_STATUS_INVALID");
        var query = store.Profiles.Where(x => status == null || x.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.MemberId.ToLower().Contains(term) || x.DisplayName.ToLower().Contains(term));
        }
        IReadOnlyList<AdminMemberResponse> result = query.OrderByDescending(x => x.CreatedAt).Take(500).AsEnumerable().Select(Map).ToArray();
        return Task.FromResult(result);
    }

    public async Task<AdminMemberResponse> SetMemberStatusAsync(string memberId, AdminMemberStatusRequest request, CancellationToken ct)
    {
        if (!ProfileStatuses.Contains(request.ProfileStatus)) throw new DomainException("PROFILE_STATUS_INVALID");
        var row = store.Profiles.SingleOrDefault(x => x.MemberId == memberId) ?? throw new DomainException("PROFILE_NOT_FOUND", 404);
        var now = DateTimeOffset.UtcNow;
        row.Status = request.ProfileStatus;
        row.UpdatedAt = now;
        if (row.Status == "ACTIVE") row.PublishedAt ??= now;
        // A member taken out of ACTIVE must not stay visible in any live event.
        if (row.Status != "ACTIVE")
            foreach (var session in store.LiveSessions.Where(x => x.MemberId == memberId && x.Status == "ACTIVE").ToArray())
            {
                session.Status = "DISABLED";
                session.RevokedAt = now;
                session.UpdatedAt = now;
            }
        AddChange(memberId, "PROFILE", memberId, new { member_id = memberId, profile_status = row.Status, row.UpdatedAt });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public Task<IReadOnlyList<AdminReportResponse>> GetReportsAsync(string? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (status is not null && !ReportStatuses.Contains(status)) throw new DomainException("REPORT_STATUS_INVALID");
        var cases = store.ModerationCases.Where(x => status == null || x.Status == status).OrderByDescending(x => x.CreatedAt).Take(500).ToArray();
        var subjectIds = cases.Where(x => x.SubjectMemberId != null).Select(x => x.SubjectMemberId!).Distinct().ToArray();
        var names = store.Profiles.Where(x => subjectIds.Contains(x.MemberId)).ToDictionary(x => x.MemberId, x => x.DisplayName);
        IReadOnlyList<AdminReportResponse> result = cases.Select(x => Map(x, x.SubjectMemberId is null ? null : names.GetValueOrDefault(x.SubjectMemberId))).ToArray();
        return Task.FromResult(result);
    }

    public async Task<AdminReportResponse> SetReportStatusAsync(string reportId, AdminStatusRequest request, CancellationToken ct)
    {
        if (!ReportStatuses.Contains(request.Status)) throw new DomainException("REPORT_STATUS_INVALID");
        var row = store.ModerationCases.SingleOrDefault(x => x.ModerationCaseId == reportId) ?? throw new DomainException("REPORT_NOT_FOUND", 404);
        var now = DateTimeOffset.UtcNow;
        row.Status = request.Status;
        row.ClosedAt = request.Status is "ACTIONED" or "CLOSED" ? row.ClosedAt ?? now : null;
        row.UpdatedAt = now;
        await store.SaveAsync(ct);
        var name = row.SubjectMemberId is null ? null : store.Profiles.Where(x => x.MemberId == row.SubjectMemberId).Select(x => x.DisplayName).FirstOrDefault();
        return Map(row, name);
    }

    public Task<IReadOnlyList<AdminPrivacyRequestResponse>> GetPrivacyRequestsAsync(string? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (status is not null && !PrivacyStatuses.Contains(status)) throw new DomainException("PRIVACY_REQUEST_STATUS_INVALID");
        IReadOnlyList<AdminPrivacyRequestResponse> result = store.PrivacyRequests.Where(x => status == null || x.Status == status).OrderBy(x => x.DueAt).Take(500).AsEnumerable().Select(Map).ToArray();
        return Task.FromResult(result);
    }

    public async Task<AdminPrivacyRequestResponse> SetPrivacyRequestStatusAsync(string privacyRequestId, AdminStatusRequest request, CancellationToken ct)
    {
        if (!PrivacyStatuses.Contains(request.Status)) throw new DomainException("PRIVACY_REQUEST_STATUS_INVALID");
        var row = store.PrivacyRequests.SingleOrDefault(x => x.PrivacyRequestId == privacyRequestId) ?? throw new DomainException("PRIVACY_REQUEST_NOT_FOUND", 404);
        if (row.Status is "COMPLETED" or "REJECTED" && row.Status != request.Status) throw new DomainException("PRIVACY_REQUEST_CLOSED", 409);
        var now = DateTimeOffset.UtcNow;
        row.Status = request.Status;
        if (request.Status is "VERIFIED" or "PROCESSING" or "COMPLETED") row.VerifiedAt ??= now;
        if (request.Status is "COMPLETED" or "REJECTED") row.CompletedAt ??= now;
        row.UpdatedAt = now;
        AddChange(row.MemberId, "PRIVACY_REQUEST", row.PrivacyRequestId, new { row.PrivacyRequestId, row.RequestType, row.Status, row.CreatedAt, row.DueAt });
        await store.SaveAsync(ct);
        return Map(row);
    }

    private void AddChange(string memberId, string type, string id, object payload) => store.Add(new SyncChange { MemberScopeId = memberId, ResourceType = type, ResourceId = id, ChangeType = "UPSERT", PayloadJson = JsonSerializer.Serialize(payload, JsonOptions) });

    private static AdminMemberResponse Map(MemberProfile x) => new(x.MemberId, x.DisplayName, x.Headline, x.Sector, x.Status, x.Visibility, x.CompletenessScore, x.CreatedAt, x.UpdatedAt);
    private static AdminReportResponse Map(ModerationCase x, string? subjectName) => new(x.ModerationCaseId, x.SourceType, x.SubjectMemberId, subjectName, x.ResourceType, x.ResourceId, x.Priority, x.Status, x.CreatedAt, x.ClosedAt);
    private static AdminPrivacyRequestResponse Map(PrivacyRequest x) => new(x.PrivacyRequestId, x.MemberId, x.RequestType, x.Status, x.CreatedAt, x.DueAt, x.VerifiedAt, x.CompletedAt);
}
