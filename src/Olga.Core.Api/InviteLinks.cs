using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Olga.Core.Api;

// Verified HTTPS invite links: https://<link-host>/i#<token>. The token sits in the URL fragment,
// which browsers never send, so no route here ever sees, stores or logs it.
public sealed class InviteLinkOptions
{
    public const string SectionName = "InviteLinks";
    // Custom URL scheme the app registers (olgaapp-dev / olgaapp).
    public string Scheme { get; set; } = "";
    // Android package the fallback page's intent:// link targets.
    public string AndroidPackage { get; set; } = "";
    public List<AndroidAppLink> Android { get; set; } = [];
    // TeamID.BundleID values for apple-app-site-association.
    public List<string> AppleAppIds { get; set; } = [];
    public string PlayStoreUrl { get; set; } = "";
    public string AppStoreUrl { get; set; } = "";
}

public sealed class AndroidAppLink
{
    public string PackageName { get; set; } = "";
    public List<string> Sha256Fingerprints { get; set; } = [];
}

public static partial class InviteLinks
{
    public const string PagePath = "/i";
    private const string JsonContentType = "application/json";

    // Static script: reads the token from the fragment and builds the app link from the body's data attributes.
    // Hashed for the page's Content-Security-Policy, so no other script can run.
    private const string Script = """
        (function () {
          var d = document.body.dataset, t = location.hash.slice(1), open = document.getElementById("open");
          if (!/^[0-9a-fA-F]{64}$/.test(t) || !d.scheme) {
            document.getElementById("invalid").hidden = false; open.hidden = true; return;
          }
          t = t.toLowerCase();
          open.href = /Android/i.test(navigator.userAgent) && d.package
            ? "intent://invitations/" + t + "#Intent;scheme=" + d.scheme + ";package=" + d.package + ";end"
            : d.scheme + "://invitations/" + t;
        })();
        """;

    private const string Style = """
        body{margin:0;font-family:system-ui,-apple-system,sans-serif;background:#fff;color:#111;display:flex;min-height:100vh;align-items:center;justify-content:center}
        main{max-width:420px;padding:24px 16px;text-align:center}
        h1{font-size:22px;margin:0 0 8px}p{color:#555;line-height:1.5}
        a.btn{display:block;margin:12px 0;padding:14px;border-radius:10px;background:#111;color:#fff;text-decoration:none;font-weight:600}
        a.alt{background:#f1f1f1;color:#111}
        @media (prefers-color-scheme:dark){body{background:#111;color:#eee}p{color:#aaa}a.btn{background:#eee;color:#111}a.alt{background:#2a2a2a;color:#eee}}
        """;

    private static readonly string ContentSecurityPolicy =
        $"default-src 'none'; script-src '{Sha256(Script)}'; style-src '{Sha256(Style)}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static void MapInviteLinks(this WebApplication app)
    {
        app.MapGet("/.well-known/assetlinks.json", (IOptions<InviteLinkOptions> options) =>
            AssetLinksJson(options.Value) is { } json ? Results.Text(json, JsonContentType) : Results.NotFound())
            .ExcludeFromDescription();
        app.MapGet("/.well-known/apple-app-site-association", (IOptions<InviteLinkOptions> options) =>
            AppleAppSiteAssociationJson(options.Value) is { } json ? Results.Text(json, JsonContentType) : Results.NotFound())
            .ExcludeFromDescription();
        app.MapGet(PagePath, (HttpContext context, IOptions<InviteLinkOptions> options) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            headers.CacheControl = "no-store";
            headers.XContentTypeOptions = "nosniff";
            return Results.Text(Page(options.Value), "text/html", Encoding.UTF8);
        }).ExcludeFromDescription();
    }

    public static string? AssetLinksJson(InviteLinkOptions options)
    {
        var apps = options.Android.Where(x => !string.IsNullOrWhiteSpace(x.PackageName) && x.Sha256Fingerprints.Count > 0).ToArray();
        if (apps.Length == 0) return null;
        var statements = new JsonArray();
        foreach (var item in apps)
            statements.Add(new JsonObject
            {
                ["relation"] = new JsonArray("delegate_permission/common.handle_all_urls"),
                ["target"] = new JsonObject
                {
                    ["namespace"] = "android_app",
                    ["package_name"] = item.PackageName,
                    ["sha256_cert_fingerprints"] = new JsonArray(item.Sha256Fingerprints.Select(x => (JsonNode)x.ToUpperInvariant()).ToArray()),
                },
            });
        return statements.ToJsonString();
    }

    public static string? AppleAppSiteAssociationJson(InviteLinkOptions options)
    {
        var appIds = options.AppleAppIds.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (appIds.Length == 0) return null;
        return new JsonObject
        {
            ["applinks"] = new JsonObject
            {
                ["details"] = new JsonArray(new JsonObject
                {
                    ["appIDs"] = new JsonArray(appIds.Select(x => (JsonNode)x).ToArray()),
                    ["components"] = new JsonArray(
                        new JsonObject { ["/"] = PagePath, ["#"] = "*" },
                        new JsonObject { ["/"] = $"{PagePath}/*" }),
                }),
            },
        }.ToJsonString();
    }

    // The page depends only on configuration, never on the request.
    public static string Page(InviteLinkOptions options)
    {
        var scheme = SchemePattern().IsMatch(options.Scheme) ? options.Scheme : "";
        var package = PackagePattern().IsMatch(options.AndroidPackage) ? options.AndroidPackage : "";
        var stores = new StringBuilder();
        if (IsHttps(options.PlayStoreUrl)) stores.Append($"""<a class="btn alt" href="{Html(options.PlayStoreUrl)}">Get Ol-ga on Google Play</a>""");
        if (IsHttps(options.AppStoreUrl)) stores.Append($"""<a class="btn alt" href="{Html(options.AppStoreUrl)}">Get Ol-ga on the App Store</a>""");
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>Ol-ga invitation</title>
            <style>{Style}</style>
            </head>
            <body data-scheme="{Html(scheme)}" data-package="{Html(package)}">
            <main>
            <h1>You've been invited to Ol-ga</h1>
            <p id="invalid" hidden>This invitation link isn't valid.</p>
            <a id="open" class="btn" href="#">Open in Ol-ga</a>
            {stores}
            <p>Already invited? Sign in with the email or phone the invite was sent to, and you'll find it in the app.</p>
            </main>
            <script>{Script}</script>
            </body>
            </html>
            """;
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    private static string Html(string value) => WebUtility.HtmlEncode(value);
    private static string Sha256(string value) => $"sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    [GeneratedRegex("^[a-z][a-z0-9+.-]{0,31}$")]
    private static partial Regex SchemePattern();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+$")]
    private static partial Regex PackagePattern();
}
