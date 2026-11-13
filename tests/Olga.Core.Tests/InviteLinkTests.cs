using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Olga.Core.Tests;

public sealed class InviteLinkTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Fingerprint = "FA:C6:17:45:DC:09:03:78:6F:B9:ED:E6:2A:96:2B:39:9F:73:48:F0:BB:6F:89:9B:83:32:66:75:91:03:3B:9C";

    private static readonly Dictionary<string, string?> DevSettings = new()
    {
        ["InviteLinks:Scheme"] = "olgaapp-dev",
        ["InviteLinks:AndroidPackage"] = "com.olga.connect.dev",
        ["InviteLinks:Android:0:PackageName"] = "com.olga.connect.dev",
        ["InviteLinks:Android:0:Sha256Fingerprints:0"] = Fingerprint.ToLowerInvariant(),
        ["InviteLinks:AppleAppIds:0"] = "Y3X4KPFWM9.com.olga.dev.connect",
        ["InviteLinks:PlayStoreUrl"] = "https://play.google.com/store/apps/details?id=com.olga.connect.dev",
    };

    [Fact]
    public async Task Asset_links_lists_each_configured_android_app_as_json_without_a_redirect()
    {
        using var response = await GetAsync("/.well-known/assetlinks.json", DevSettings, forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $$$"""[{"relation":["delegate_permission/common.handle_all_urls"],"target":{"namespace":"android_app","package_name":"com.olga.connect.dev","sha256_cert_fingerprints":["{{{Fingerprint}}}"]}}]""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Apple_app_site_association_matches_the_invite_path_as_json_without_a_redirect()
    {
        using var response = await GetAsync("/.well-known/apple-app-site-association", DevSettings, forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """{"applinks":{"details":[{"appIDs":["Y3X4KPFWM9.com.olga.dev.connect"],"components":[{"/":"/i","#":"*"},{"/":"/i/*"}]}]}}""",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/.well-known/assetlinks.json")]
    [InlineData("/.well-known/apple-app-site-association")]
    public async Task Well_known_files_are_404_when_no_apps_are_configured(string path)
    {
        using var response = await GetAsync(path, new());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invite_page_is_static_html_whatever_the_request_carries()
    {
        using var plain = await GetAsync("/i", DevSettings);
        using var noisy = await GetAsync("/i?token=" + new string('a', 64), DevSettings, forwardedProto: "https");
        var html = await plain.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("text/html", plain.Content.Headers.ContentType?.MediaType);
        Assert.Equal(html, await noisy.Content.ReadAsStringAsync());
        Assert.Contains("data-scheme=\"olgaapp-dev\"", html);
        Assert.Contains("data-package=\"com.olga.connect.dev\"", html);
        Assert.Contains("href=\"https://play.google.com/store/apps/details?id=com.olga.connect.dev\"", html);
        Assert.DoesNotContain("App Store", html);
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invite_page_only_allows_its_own_inline_script_and_sends_no_referrer()
    {
        using var response = await GetAsync("/i", DevSettings);
        var html = await response.Content.ReadAsStringAsync();
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        var script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

        Assert.Contains($"script-src 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'", csp);
        Assert.Contains("default-src 'none'", csp);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Invite_page_drops_a_scheme_or_package_that_is_not_a_plain_identifier()
    {
        var settings = new Dictionary<string, string?>(DevSettings)
        {
            ["InviteLinks:Scheme"] = "javascript:alert(1)",
            ["InviteLinks:AndroidPackage"] = "\"><script>",
        };

        var html = await (await GetAsync("/i", settings)).Content.ReadAsStringAsync();

        Assert.Contains("data-scheme=\"\"", html);
        Assert.Contains("data-package=\"\"", html);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Dictionary<string, string?> settings, string? forwardedProto = null)
    {
        var client = factory.WithWebHostBuilder(builder =>
        {
            // Non-Development, so appsettings.Development.json's dev apps don't leak into the "nothing configured" case.
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (forwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return await client.SendAsync(request);
    }
}
