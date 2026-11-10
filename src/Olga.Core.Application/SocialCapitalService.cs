using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public sealed record ProtectedContactImport(string ContactRecordId, string LinkId, byte[] LabelCiphertext, string? EmailHash, string? PhoneHash);

public interface ISocialCapitalRepository
{
    Task<VentureResponse> CreateVentureAsync(string ownerMemberId, VentureCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<VentureResponse>> ListVenturesAsync(string ownerMemberId, CancellationToken ct);
    Task<VentureResponse?> GetVentureAsync(string ownerMemberId, string ventureId, CancellationToken ct);
    Task<ContactImportResponse> ImportContactsAsync(string ownerMemberId, IReadOnlyList<ProtectedContactImport> contacts, int retentionDays, CancellationToken ct);
    Task<InviteBatchResponse> CreateInviteBatchAsync(string ownerMemberId, InviteBatchCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<InvitationAcceptResponse> AcceptInvitationAsync(string recipientMemberId, string token, string idempotencyKey, CancellationToken ct);
    Task<SciContactPage> GetSciAsync(string ownerMemberId, string ventureId, string? cursor, int limit, CancellationToken ct);
}

public interface ISocialCapitalService
{
    Task<VentureResponse> CreateVentureAsync(string ownerMemberId, VentureCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<VentureResponse>> ListVenturesAsync(string ownerMemberId, CancellationToken ct);
    Task<VentureResponse> GetVentureAsync(string ownerMemberId, string ventureId, CancellationToken ct);
    Task<ContactImportResponse> ImportContactsAsync(string ownerMemberId, ContactImportRequest request, CancellationToken ct);
    Task<InviteBatchResponse> CreateInviteBatchAsync(string ownerMemberId, InviteBatchCreateRequest request, string idempotencyKey, CancellationToken ct);
    Task<InvitationAcceptResponse> AcceptInvitationAsync(string recipientMemberId, string token, string idempotencyKey, CancellationToken ct);
    Task<SciContactPage> GetSciAsync(string ownerMemberId, string ventureId, string? cursor, int limit, CancellationToken ct);
}

public sealed class SocialCapitalService(ISocialCapitalRepository repository, IIdentityProtector identityProtector) : ISocialCapitalService
{
    public Task<VentureResponse> CreateVentureAsync(string ownerMemberId, VentureCreateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.VentureId) || request.VentureId.Length > 64 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 250)
            throw new DomainException("VENTURE_INVALID");
        if (request.GeographyCodes?.Count > 20 || request.GeographyCodes?.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) == true)
            throw new DomainException("VENTURE_GEOGRAPHY_INVALID");
        return repository.CreateVentureAsync(ownerMemberId, request, ct);
    }

    public Task<IReadOnlyList<VentureResponse>> ListVenturesAsync(string ownerMemberId, CancellationToken ct) =>
        repository.ListVenturesAsync(ownerMemberId, ct);

    public async Task<VentureResponse> GetVentureAsync(string ownerMemberId, string ventureId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ventureId) || ventureId.Length > 64) throw new DomainException("VENTURE_INVALID");
        return await repository.GetVentureAsync(ownerMemberId, ventureId, ct)
            ?? throw new DomainException("VENTURE_NOT_FOUND", 404);
    }

    public Task<ContactImportResponse> ImportContactsAsync(string ownerMemberId, ContactImportRequest request, CancellationToken ct)
    {
        if (request.Contacts.Count is < 1 or > 500 || request.RetentionDays is < 30 or > 730)
            throw new DomainException("CONTACT_IMPORT_INVALID");
        var duplicateIds = request.Contacts.Select(x => x.ContactRecordId).Concat(request.Contacts.Select(x => x.LinkId)).GroupBy(x => x, StringComparer.Ordinal).Any(x => x.Count() > 1);
        if (duplicateIds) throw new DomainException("CONTACT_IMPORT_DUPLICATE_ID");

        var protectedContacts = request.Contacts.Select(item =>
        {
            if (string.IsNullOrWhiteSpace(item.ContactRecordId) || item.ContactRecordId.Length > 64
                || string.IsNullOrWhiteSpace(item.LinkId) || item.LinkId.Length > 64
                || string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 250
                || (string.IsNullOrWhiteSpace(item.Email) && string.IsNullOrWhiteSpace(item.Phone)))
                throw new DomainException("CONTACT_IMPORT_INVALID");
            return new ProtectedContactImport(
                item.ContactRecordId,
                item.LinkId,
                identityProtector.ProtectContactLabel(ownerMemberId, item.ContactRecordId, item.Label),
                string.IsNullOrWhiteSpace(item.Email) ? null : identityProtector.EmailLookupHash(item.Email),
                string.IsNullOrWhiteSpace(item.Phone) ? null : identityProtector.PhoneLookupHash(item.Phone));
        }).ToArray();
        return repository.ImportContactsAsync(ownerMemberId, protectedContacts, request.RetentionDays, ct);
    }

    public Task<InviteBatchResponse> CreateInviteBatchAsync(string ownerMemberId, InviteBatchCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BatchId) || request.BatchId.Length > 64 || request.LinkIds.Count is < 1 or > 20
            || request.LinkIds.Distinct(StringComparer.Ordinal).Count() != request.LinkIds.Count || request.ExpiresInDays is < 1 or > 30)
            throw new DomainException("INVITE_BATCH_INVALID");
        return repository.CreateInviteBatchAsync(ownerMemberId, request, idempotencyKey, ct);
    }

    public Task<InvitationAcceptResponse> AcceptInvitationAsync(string recipientMemberId, string token, string idempotencyKey, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))) throw new DomainException("INVITATION_TOKEN_INVALID");
        return repository.AcceptInvitationAsync(recipientMemberId, token.ToLowerInvariant(), idempotencyKey, ct);
    }

    public Task<SciContactPage> GetSciAsync(string ownerMemberId, string ventureId, string? cursor, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ventureId) || ventureId.Length > 64 || limit is < 1 or > 100) throw new DomainException("SCI_QUERY_INVALID");
        return repository.GetSciAsync(ownerMemberId, ventureId, cursor, limit, ct);
    }
}
