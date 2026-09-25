using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class OpenApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    // Non-Development environment so a developer's user-secrets (real DB connection) never leak into these InMemory tests.
    public OpenApiContractTests(WebApplicationFactory<Program> factory) => this.factory = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing"));

    [Fact]
    public async Task Generated_document_uses_only_the_browser_https_origin_behind_a_proxy()
    {
        using var document = await GetDocumentAsync(forwardedProto: "https");

        var servers = document.RootElement.GetProperty("servers").EnumerateArray().ToArray();
        var server = Assert.Single(servers);
        var url = server.GetProperty("url").GetString();

        Assert.Equal("/", url);
        Assert.DoesNotContain("http://", url!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generated_document_exposes_required_client_headers_on_the_correct_operations()
    {
        using var document = await GetDocumentAsync();

        var memberRegistration = Operation(document, "/v1/members", "post");
        AssertHeader(memberRegistration, "Idempotency-Key", required: true, maxLength: 128);
        AssertNoHeader(memberRegistration, "X-Member-Id");

        var memberLookup = Operation(document, "/v1/members/{memberId}", "get");
        AssertHeader(memberLookup, "X-Member-Id", required: false, maxLength: 64);
        AssertNoHeader(memberLookup, "Idempotency-Key");

        var consent = Operation(document, "/v1/me/consents", "post");
        AssertHeader(consent, "X-Member-Id", required: false, maxLength: 64);
        AssertHeader(consent, "Idempotency-Key", required: true, maxLength: 128);

        var profileUpdate = Operation(document, "/v1/me/profile", "patch");
        AssertHeader(profileUpdate, "X-Member-Id", required: false, maxLength: 64);
        AssertHeader(profileUpdate, "Idempotency-Key", required: true, maxLength: 128);
        AssertHeader(profileUpdate, "If-Match", required: false);

        var publicEvents = Operation(document, "/v1/events", "get");
        AssertNoHeader(publicEvents, "X-Member-Id");

        var adminCreateEvent = Operation(document, "/v1/admin/events", "post");
        AssertHeader(adminCreateEvent, "X-Admin-Key", required: true);
        AssertNoHeader(adminCreateEvent, "X-Member-Id");
    }

    [Fact]
    public async Task Member_lookup_does_not_create_a_profile_for_the_caller()
    {
        var callerId = $"lookup-{Guid.NewGuid():N}";
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/members/A123");
        request.Headers.Add("X-Member-Id", callerId);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        Assert.False(await db.MemberProfiles.AnyAsync(profile => profile.MemberId == callerId));
    }

    [Fact]
    public async Task Member_routes_reject_an_oversized_member_header_before_data_access()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/members/A123");
        request.Headers.Add("X-Member-Id", new string('x', 65));

        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("MEMBER_ID_INVALID", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_routes_reject_a_missing_or_wrong_key()
    {
        using var client = factory.CreateClient();
        using var missing = await client.GetAsync("/v1/admin/events");
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/events");
        wrong.Headers.Add("X-Admin-Key", "nope");
        using var ok = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/events");
        ok.Headers.Add("X-Admin-Key", "local-admin-key");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.SendAsync(ok)).StatusCode);
    }

    private async Task<JsonDocument> GetDocumentAsync(string? forwardedProto = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/swagger/v1/swagger.json");
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static JsonElement Operation(JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static void AssertHeader(JsonElement operation, string name, bool required, int? maxLength = null)
    {
        var header = operation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("in").GetString() == "header"
                && string.Equals(parameter.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        var actualRequired = header.TryGetProperty("required", out var requiredProperty) && requiredProperty.GetBoolean();
        Assert.Equal(required, actualRequired);
        if (maxLength is not null)
            Assert.Equal(maxLength.Value, header.GetProperty("schema").GetProperty("maxLength").GetInt32());
    }

    private static void AssertNoHeader(JsonElement operation, string name)
    {
        if (!operation.TryGetProperty("parameters", out var parameters)) return;
        Assert.DoesNotContain(parameters.EnumerateArray(), parameter =>
            parameter.GetProperty("in").GetString() == "header"
            && string.Equals(parameter.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
    }
}
