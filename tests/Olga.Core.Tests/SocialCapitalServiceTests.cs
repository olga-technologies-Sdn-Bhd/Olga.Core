using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class SocialCapitalServiceTests
{
    private const string Owner = "mem_owner";
    private const string InvitationId = "0123456789ABCDEF";
    private static readonly string Token = new('A', 64);

    [Fact]
    public async Task Contact_list_rejects_limits_outside_1_to_100()
    {
        var (service, repository) = Create();

        Assert.Equal("CONTACT_QUERY_INVALID", (await Assert.ThrowsAsync<DomainException>(() => service.ListContactsAsync(Owner, null, 0, default))).Code);
        Assert.Equal("CONTACT_QUERY_INVALID", (await Assert.ThrowsAsync<DomainException>(() => service.ListContactsAsync(Owner, null, 101, default))).Code);
        await service.ListContactsAsync(Owner, "cursor", 100, default);
        Assert.Equal(("ListContacts", Owner, "cursor"), repository.Calls.Single());
    }

    [Fact]
    public async Task Invite_batch_detail_is_404_when_the_owner_has_no_such_batch()
    {
        var (service, _) = Create();

        var missing = await Assert.ThrowsAsync<DomainException>(() => service.GetInviteBatchAsync(Owner, "batch_unknown", default));
        var invalid = await Assert.ThrowsAsync<DomainException>(() => service.GetInviteBatchAsync(Owner, new string('b', 65), default));

        Assert.Equal(("INVITE_BATCH_NOT_FOUND", 404), (missing.Code, missing.StatusCode));
        Assert.Equal("INVITE_BATCH_INVALID", invalid.Code);
    }

    [Theory]
    [InlineData("short", 14, "key", "INVITATION_RESEND_INVALID")]
    [InlineData(InvitationId, 0, "key", "INVITATION_RESEND_INVALID")]
    [InlineData(InvitationId, 31, "key", "INVITATION_RESEND_INVALID")]
    [InlineData(InvitationId, 14, "", "IDEMPOTENCY_KEY_REQUIRED")]
    public async Task Resend_validates_id_expiry_and_idempotency_key(string invitationId, int expiresInDays, string idempotencyKey, string code)
    {
        var (service, _) = Create();

        var error = await Assert.ThrowsAsync<DomainException>(() => service.ResendInvitationAsync(Owner, invitationId, new(expiresInDays), idempotencyKey, default));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Resend_and_pending_accept_pass_a_lowercase_invitation_id()
    {
        var (service, repository) = Create();

        await service.ResendInvitationAsync(Owner, InvitationId, new(), "key", default);
        await service.AcceptPendingInvitationAsync("mem_invitee", InvitationId, "key", default);

        Assert.All(repository.Calls, call => Assert.Equal(InvitationId.ToLowerInvariant(), call.Argument));
    }

    [Fact]
    public async Task Pending_accept_rejects_malformed_ids_as_not_found_and_requires_an_idempotency_key()
    {
        var (service, _) = Create();

        var malformed = await Assert.ThrowsAsync<DomainException>(() => service.AcceptPendingInvitationAsync("mem_invitee", "not-an-id", "key", default));
        var noKey = await Assert.ThrowsAsync<DomainException>(() => service.AcceptPendingInvitationAsync("mem_invitee", InvitationId, " ", default));

        Assert.Equal(("INVITATION_NOT_FOUND", 404), (malformed.Code, malformed.StatusCode));
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", noKey.Code);
    }

    [Fact]
    public async Task Open_requires_a_64_hex_token_and_passes_it_lowercase()
    {
        var (service, repository) = Create();

        var invalid = await Assert.ThrowsAsync<DomainException>(() => service.OpenInvitationAsync("mem_invitee", "zz", default));
        await service.OpenInvitationAsync("mem_invitee", Token, default);

        Assert.Equal("INVITATION_TOKEN_INVALID", invalid.Code);
        Assert.Equal(("OpenInvitation", "mem_invitee", Token.ToLowerInvariant()), repository.Calls.Single());
    }

    [Fact]
    public async Task Revoke_treats_malformed_ids_as_not_found_and_passes_a_lowercase_id()
    {
        var (service, repository) = Create();

        var malformed = await Assert.ThrowsAsync<DomainException>(() => service.RevokeInvitationAsync(Owner, "nope", default));
        await service.RevokeInvitationAsync(Owner, InvitationId, default);

        Assert.Equal(("INVITATION_NOT_FOUND", 404), (malformed.Code, malformed.StatusCode));
        Assert.Equal(("RevokeInvitation", Owner, InvitationId.ToLowerInvariant()), repository.Calls.Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Delete_contact_rejects_a_blank_or_oversized_link_id(string linkId)
    {
        var (service, repository) = Create();

        Assert.Equal("CONTACT_LINK_INVALID", (await Assert.ThrowsAsync<DomainException>(() => service.DeleteContactAsync(Owner, linkId, default))).Code);
        Assert.Equal("CONTACT_LINK_INVALID", (await Assert.ThrowsAsync<DomainException>(() => service.DeleteContactAsync(Owner, new string('l', 65), default))).Code);
        Assert.Empty(repository.Calls);
    }

    private static (SocialCapitalService Service, RecordingRepository Repository) Create()
    {
        var repository = new RecordingRepository();
        return (new SocialCapitalService(repository, new AesIdentityProtector(new byte[32])), repository);
    }

    private sealed class RecordingRepository : ISocialCapitalRepository
    {
        public List<(string Method, string Member, string? Argument)> Calls { get; } = [];
        private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

        public Task<ImportedContactPage> ListContactsAsync(string ownerMemberId, string? cursor, int limit, CancellationToken ct)
        { Calls.Add(("ListContacts", ownerMemberId, cursor)); return Task.FromResult(new ImportedContactPage([], null)); }
        public Task<InviteBatchDetailResponse?> GetInviteBatchAsync(string ownerMemberId, string batchId, CancellationToken ct) =>
            Task.FromResult<InviteBatchDetailResponse?>(null);
        public Task<InvitationDispatch> ResendInvitationAsync(string ownerMemberId, string invitationId, int expiresInDays, string idempotencyKey, CancellationToken ct)
        { Calls.Add(("ResendInvitation", ownerMemberId, invitationId)); return Task.FromResult(new InvitationDispatch(invitationId, "link", Token, Now)); }
        public Task<InvitationPreviewResponse> OpenInvitationAsync(string viewerMemberId, string token, CancellationToken ct)
        { Calls.Add(("OpenInvitation", viewerMemberId, token)); return Task.FromResult(new InvitationPreviewResponse(InvitationId, Owner, "Owner", null, "OPENED", Now)); }
        public Task<InvitationAcceptResponse> AcceptPendingInvitationAsync(string recipientMemberId, string invitationId, string idempotencyKey, CancellationToken ct)
        { Calls.Add(("AcceptPendingInvitation", recipientMemberId, invitationId)); return Task.FromResult(new InvitationAcceptResponse(invitationId, "conn", "chat", "link")); }

        public Task<VentureResponse> CreateVentureAsync(string ownerMemberId, VentureCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<VentureResponse>> ListVenturesAsync(string ownerMemberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<VentureResponse?> GetVentureAsync(string ownerMemberId, string ventureId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContactImportResponse> ImportContactsAsync(string ownerMemberId, IReadOnlyList<ProtectedContactImport> contacts, int retentionDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<InviteBatchResponse> CreateInviteBatchAsync(string ownerMemberId, InviteBatchCreateRequest request, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<InvitationAcceptResponse> AcceptInvitationAsync(string recipientMemberId, string token, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<SciContactPage> GetSciAsync(string ownerMemberId, string ventureId, string? cursor, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<SciSummaryResponse> GetSciSummaryAsync(string ownerMemberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PendingInvitationResponse>> ListPendingInvitationsAsync(string memberId, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeInvitationAsync(string ownerMemberId, string invitationId, CancellationToken ct)
        { Calls.Add(("RevokeInvitation", ownerMemberId, invitationId)); return Task.CompletedTask; }
        public Task DeleteContactAsync(string ownerMemberId, string linkId, CancellationToken ct)
        { Calls.Add(("DeleteContact", ownerMemberId, linkId)); return Task.CompletedTask; }
    }
}
