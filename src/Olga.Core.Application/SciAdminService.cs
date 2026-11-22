using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public sealed record SciAdminActor(string ActorId, IReadOnlySet<string> Roles);

public interface ISciAdminRepository
{
    Task<SciAdminOverviewResponse> GetOverviewAsync(CancellationToken ct);
    Task<SciAdminConfigurationResponse> GetConfigurationAsync(CancellationToken ct);
    Task<SciAdminChangeApprovalPage> ListApprovalsAsync(string? status, string? cursor, int limit, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse?> GetApprovalAsync(string approvalId, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> CreateApprovalAsync(string approvalId, SciAdminActor actor, SciAdminChangeProposalRequest request, string payloadHash, bool autoApproved, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> DecideApprovalAsync(string approvalId, SciAdminActor actor, bool approve, string expectedETag, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> ApplyApprovalAsync(string approvalId, SciAdminActor actor, string expectedETag, CancellationToken ct);
}

public interface ISciAdminService
{
    Task<SciAdminOverviewResponse> GetOverviewAsync(SciAdminActor actor, CancellationToken ct);
    Task<SciAdminConfigurationResponse> GetConfigurationAsync(SciAdminActor actor, CancellationToken ct);
    Task<SciAdminChangeApprovalPage> ListApprovalsAsync(SciAdminActor actor, string? status, string? cursor, int limit, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> GetApprovalAsync(SciAdminActor actor, string approvalId, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> CreateApprovalAsync(SciAdminActor actor, SciAdminChangeProposalRequest request, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> DecideApprovalAsync(SciAdminActor actor, string approvalId, bool approve, string expectedETag, CancellationToken ct);
    Task<SciAdminChangeApprovalResponse> ApplyApprovalAsync(SciAdminActor actor, string approvalId, string expectedETag, CancellationToken ct);
}

public sealed class SciAdminService(ISciAdminRepository repository) : ISciAdminService
{
    private static readonly HashSet<string> ChangeTypes = ["CORE_RULE","OPERATIONAL_SETTING","SCORING_CALIBRATION","FULL_RESCORE","OUTCOME_CORRECTION"];
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedPayloadKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["CORE_RULE"] = ["semantic_weight", "complementarity_weight", "recency_weight", "estimated_score_cap", "waitlist_threshold", "network_threshold", "initial_goal_value", "layer_sizes", "sci_definition"],
        ["OPERATIONAL_SETTING"] = ["relationship_inactivity_days", "outcome_validity_days", "reach_out_default_threshold"],
        ["SCORING_CALIBRATION"] = ["cosine_floor", "cosine_span", "recency_full_days", "recency_decay_days", "estimated_recency"],
        ["FULL_RESCORE"] = [],
        ["OUTCOME_CORRECTION"] = ["corrected_status", "reason"]
    };

    public Task<SciAdminOverviewResponse> GetOverviewAsync(SciAdminActor actor, CancellationToken ct)
    { EnsureAdmin(actor); return repository.GetOverviewAsync(ct); }

    public Task<SciAdminConfigurationResponse> GetConfigurationAsync(SciAdminActor actor, CancellationToken ct)
    { EnsureAdmin(actor); return repository.GetConfigurationAsync(ct); }

    public async Task<SciAdminChangeApprovalPage> ListApprovalsAsync(SciAdminActor actor, string? status, string? cursor, int limit, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (limit is < 1 or > 100) throw new DomainException("SCI_ADMIN_QUERY_INVALID");
        var page = await repository.ListApprovalsAsync(status, cursor, limit, ct);
        return page with { Items = page.Items.Select(x => AddActions(actor, x)).ToArray() };
    }

    public async Task<SciAdminChangeApprovalResponse> GetApprovalAsync(SciAdminActor actor, string approvalId, CancellationToken ct)
    {
        EnsureAdmin(actor);
        var value = await repository.GetApprovalAsync(approvalId, ct) ?? throw new DomainException("SCI_CHANGE_APPROVAL_NOT_FOUND", 404);
        return AddActions(actor, value);
    }

    public async Task<SciAdminChangeApprovalResponse> CreateApprovalAsync(SciAdminActor actor, SciAdminChangeProposalRequest request, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (!ChangeTypes.Contains(request.ChangeType) || string.IsNullOrWhiteSpace(request.ResourceType) || request.ResourceType.Length > 64
            || string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 64
            || request.Payload.ValueKind != JsonValueKind.Object)
            throw new DomainException("SCI_CHANGE_PROPOSAL_INVALID");
        EnsureCanRequest(actor, request.ChangeType);
        ValidateProposal(request);
        var payload = request.Payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(payload) > 32_768) throw new DomainException("SCI_CHANGE_PROPOSAL_TOO_LARGE");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var id = $"approval_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{actor.ActorId}\n{request.ChangeType}\n{request.ResourceType}\n{request.ResourceId}\n{hash}"))).ToLowerInvariant()[..32]}";
        var autoApproved = true;
        return AddActions(actor, await repository.CreateApprovalAsync(id, actor, request, hash, autoApproved, ct));
    }

    public async Task<SciAdminChangeApprovalResponse> DecideApprovalAsync(SciAdminActor actor, string approvalId, bool approve, string expectedETag, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (string.IsNullOrWhiteSpace(expectedETag)) throw new DomainException("IF_MATCH_REQUIRED", 428);
        var current = await repository.GetApprovalAsync(approvalId, ct) ?? throw new DomainException("SCI_CHANGE_APPROVAL_NOT_FOUND", 404);
        EnsureCanApprove(actor, current);
        return AddActions(actor, await repository.DecideApprovalAsync(approvalId, actor, approve, expectedETag, ct));
    }

    public async Task<SciAdminChangeApprovalResponse> ApplyApprovalAsync(SciAdminActor actor, string approvalId, string expectedETag, CancellationToken ct)
    {
        EnsureAdmin(actor);
        if (string.IsNullOrWhiteSpace(expectedETag)) throw new DomainException("IF_MATCH_REQUIRED", 428);
        var current = await repository.GetApprovalAsync(approvalId, ct) ?? throw new DomainException("SCI_CHANGE_APPROVAL_NOT_FOUND", 404);
        EnsureCanApply(actor, current);
        return AddActions(actor, await repository.ApplyApprovalAsync(approvalId, actor, expectedETag, ct));
    }

    private static SciAdminChangeApprovalResponse AddActions(SciAdminActor actor, SciAdminChangeApprovalResponse value)
    {
        var actions = new List<string>();
        if (value.Status == "PENDING" && value.RequestedBy != actor.ActorId && CanApprove(actor, value.ChangeType)) { actions.Add("APPROVE"); actions.Add("REJECT"); }
        if (value.Status == "APPROVED" && CanApply(actor, value.ChangeType)) actions.Add("APPLY");
        return value with { AllowedActions = actions };
    }

    private static void EnsureAdmin(SciAdminActor actor)
    { if (!actor.Roles.Contains("Olga.Admin")) throw new DomainException("ADMIN_FORBIDDEN", 403); }

    private static void EnsureCanRequest(SciAdminActor actor, string changeType)
    {
        var permitted = changeType switch
        {
            "CORE_RULE" => actor.Roles.Contains("Olga.Founder"),
            "OPERATIONAL_SETTING" => actor.Roles.Contains("Olga.ProductOperations"),
            "SCORING_CALIBRATION" or "FULL_RESCORE" => actor.Roles.Contains("Olga.CTO") || actor.Roles.Contains("Olga.ProductOperations"),
            "OUTCOME_CORRECTION" => actor.Roles.Contains("Olga.Support"),
            _ => false
        };
        if (!permitted) throw new DomainException("SCI_ADMIN_ACTION_FORBIDDEN", 403);
    }

    private static bool CanApprove(SciAdminActor actor, string changeType) => changeType switch
    {
        "OPERATIONAL_SETTING" => actor.Roles.Contains("Olga.ProductOperations"),
        "SCORING_CALIBRATION" or "FULL_RESCORE" => actor.Roles.Contains("Olga.CTO"),
        "OUTCOME_CORRECTION" => actor.Roles.Contains("Olga.Support"),
        _ => false
    };

    private static bool CanApply(SciAdminActor actor, string changeType) => changeType switch
    {
        "CORE_RULE" => actor.Roles.Contains("Olga.Founder"),
        "OPERATIONAL_SETTING" => actor.Roles.Contains("Olga.ProductOperations"),
        "SCORING_CALIBRATION" or "FULL_RESCORE" => actor.Roles.Contains("Olga.CTO"),
        "OUTCOME_CORRECTION" => actor.Roles.Contains("Olga.Support"),
        _ => false
    };

    private static void EnsureCanApprove(SciAdminActor actor, SciAdminChangeApprovalResponse current)
    {
        if (current.RequestedBy == actor.ActorId || !CanApprove(actor, current.ChangeType)) throw new DomainException("SCI_ADMIN_ACTION_FORBIDDEN", 403);
    }

    private static void EnsureCanApply(SciAdminActor actor, SciAdminChangeApprovalResponse current)
    {
        if (current.Status != "APPROVED" || !CanApply(actor, current.ChangeType)) throw new DomainException("SCI_ADMIN_ACTION_FORBIDDEN", 403);
    }

    private static void ValidateProposal(SciAdminChangeProposalRequest request)
    {
        var expectedResourceType = request.ChangeType switch
        {
            "CORE_RULE" => request.ResourceType is "SCI_SCORING_CONFIG" or "SCI_PRODUCT_CONFIG",
            "OPERATIONAL_SETTING" => request.ResourceType == "SCI_PRODUCT_CONFIG",
            "SCORING_CALIBRATION" => request.ResourceType == "SCI_SCORING_CONFIG",
            "FULL_RESCORE" => request.ResourceType == "SCI_SCORING_SCOPE",
            "OUTCOME_CORRECTION" => request.ResourceType == "RELATIONSHIP_OUTCOME",
            _ => false
        };
        if (!expectedResourceType) throw new DomainException("SCI_CHANGE_RESOURCE_INVALID");

        var allowed = AllowedPayloadKeys[request.ChangeType];
        var keys = request.Payload.EnumerateObject().Select(x => x.Name).ToArray();
        if (keys.Any(key => !allowed.Contains(key)) || request.ChangeType != "FULL_RESCORE" && keys.Length == 0
            || request.ChangeType == "FULL_RESCORE" && keys.Length != 0)
            throw new DomainException("SCI_CHANGE_PAYLOAD_INVALID");
        if (request.ResourceType == "SCI_SCORING_CONFIG" && keys.Any(key => key is "initial_goal_value" or "layer_sizes" or "sci_definition"
                or "relationship_inactivity_days" or "outcome_validity_days" or "reach_out_default_threshold")
            || request.ResourceType == "SCI_PRODUCT_CONFIG" && keys.Any(key => key is "semantic_weight" or "complementarity_weight" or "recency_weight"
                or "estimated_score_cap" or "waitlist_threshold" or "network_threshold" or "cosine_floor" or "cosine_span"
                or "recency_full_days" or "recency_decay_days" or "estimated_recency"))
            throw new DomainException("SCI_CHANGE_PAYLOAD_INVALID");
        if (request.ChangeType == "OUTCOME_CORRECTION"
            && (!request.Payload.TryGetProperty("corrected_status",out var status) || status.ValueKind!=JsonValueKind.String
                || status.GetString() is not ("VERIFIED" or "WITHDRAWN")
                || !request.Payload.TryGetProperty("reason",out var reason) || reason.ValueKind!=JsonValueKind.String
                || string.IsNullOrWhiteSpace(reason.GetString()) || reason.GetString()!.Length>1000))
            throw new DomainException("SCI_CHANGE_PAYLOAD_INVALID");

        foreach (var property in request.Payload.EnumerateObject())
        {
            if (property.Name is "layer_sizes")
            {
                if (property.Value.ValueKind != JsonValueKind.Array
                    || property.Value.GetArrayLength() != 5
                    || property.Value.EnumerateArray().Any(x => !x.TryGetInt32(out _)))
                    throw new DomainException("SCI_CHANGE_PAYLOAD_INVALID");
            }
            else if (property.Name is "sci_definition")
            {
                if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                    throw new DomainException("SCI_CHANGE_PAYLOAD_INVALID");
            }
            else if (property.Value.ValueKind != JsonValueKind.Number)
                throw new DomainException("SCI_CHANGE_PAYLOAD_INVALID");
        }
    }
}
