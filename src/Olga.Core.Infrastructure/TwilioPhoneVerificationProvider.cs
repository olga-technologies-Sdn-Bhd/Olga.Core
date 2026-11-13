using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olga.Core.Application;
using Olga.Core.Domain;

namespace Olga.Core.Infrastructure;

public sealed class TwilioVerifyOptions
{
    public const string SectionName = "Twilio";
    public bool Enabled { get; init; }
    public string AccountSid { get; init; } = "";
    public string ApiKeySid { get; init; } = "";
    public string ApiKeySecret { get; init; } = "";
    public string VerifyServiceSid { get; init; } = "";
    public string RateLimitName { get; init; } = "";
    public int TokenTtlSeconds { get; init; } = 300;
}

public sealed class TwilioPhoneVerificationProvider(
    HttpClient httpClient,
    TwilioVerifyOptions options,
    byte[] masterKey,
    TimeProvider timeProvider) : IPhoneVerificationProvider
{
    private readonly byte[] proofKey = DeriveProofKey(masterKey);

    public async Task StartAsync(string phone, string channel, string rateLimitValue, CancellationToken ct)
    {
        var normalized = NormalizePhone(phone);
        var values = new Dictionary<string, string>
        {
            ["To"] = normalized,
            ["Channel"] = channel,
            ["RiskCheck"] = "enable"
        };
        if (!string.IsNullOrWhiteSpace(options.RateLimitName))
            values[$"RateLimits[{options.RateLimitName}]"] = rateLimitValue;
        using var request = CreateRequest("Verifications", values);
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw ProviderError(response.StatusCode, checkingCode: false);
        using var payload = await ReadPayloadAsync(response, ct);
        if (!string.Equals(Status(payload), "pending", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("PHONE_VERIFICATION_REJECTED", 422);
    }

    public async Task<PhoneVerificationProof> CheckAsync(string phone, string code, CancellationToken ct)
    {
        var normalized = NormalizePhone(phone);
        using var request = CreateRequest("VerificationCheck", new Dictionary<string, string> { ["To"] = normalized, ["Code"] = code });
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw ProviderError(response.StatusCode, checkingCode: true);
        using var payload = await ReadPayloadAsync(response, ct);
        if (!string.Equals(Status(payload), "approved", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("PHONE_VERIFICATION_CODE_INVALID");

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().AddSeconds(options.TokenTtlSeconds).ToUnixTimeSeconds());
        return new(CreateProof(normalized, expiresAt), expiresAt);
    }

    public PhoneVerificationProof RestoreProof(string phone, DateTimeOffset expiresAt)
    {
        var normalized = NormalizePhone(phone);
        return new(CreateProof(normalized, expiresAt), expiresAt);
    }

    public bool ValidateProof(string phone, string token)
    {
        if (token.Length is 0 or > 1024) return false;
        string normalized;
        try { normalized = NormalizePhone(phone); }
        catch (DomainException) { return false; }
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !TryBase64UrlDecode(parts[0], out var payload) || !TryBase64UrlDecode(parts[1], out var signature)) return false;
        var expected = HMACSHA256.HashData(proofKey, payload);
        if (signature.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(signature, expected)) return false;
        var fields = Encoding.UTF8.GetString(payload).Split('\n', 3);
        return fields.Length == 3 && fields[0] == "1"
            && long.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expires)
            && timeProvider.GetUtcNow().ToUnixTimeSeconds() <= expires
            && FixedTimeEquals(fields[2], PhoneBinding(normalized));
    }

    private HttpRequestMessage CreateRequest(string operation, IReadOnlyDictionary<string, string> values)
    {
        var serviceSid = Uri.EscapeDataString(options.VerifyServiceSid);
        var request = new HttpRequestMessage(HttpMethod.Post, $"v2/Services/{serviceSid}/{operation}")
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.ApiKeySid}:{options.ApiKeySecret}")));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct); }
        catch (HttpRequestException) { throw new DomainException("PHONE_VERIFICATION_UNAVAILABLE", 503); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new DomainException("PHONE_VERIFICATION_UNAVAILABLE", 503); }
    }

    private string CreateProof(string phone, DateTimeOffset expiresAt)
    {
        var payload = Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"1\n{expiresAt.ToUnixTimeSeconds()}\n{PhoneBinding(phone)}"));
        return $"{Base64Url(payload)}.{Base64Url(HMACSHA256.HashData(proofKey, payload))}";
    }

    private string PhoneBinding(string phone) => Convert.ToHexString(HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes($"phone\n{phone}"))).ToLowerInvariant();

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task<JsonDocument> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException) { throw new DomainException("PHONE_VERIFICATION_UNAVAILABLE", 503); }
    }

    private static string? Status(JsonDocument payload) => payload.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;

    private static DomainException ProviderError(HttpStatusCode status, bool checkingCode) => status switch
    {
        HttpStatusCode.TooManyRequests => new("PHONE_VERIFICATION_RATE_LIMITED", 429),
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound when checkingCode => new("PHONE_VERIFICATION_CODE_INVALID"),
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound => new("PHONE_VERIFICATION_REJECTED", 422),
        _ => new("PHONE_VERIFICATION_UNAVAILABLE", 503)
    };

    private static string NormalizePhone(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length is < 9 or > 16 || candidate[0] != '+' || candidate[1] == '0' || candidate[1..].Any(c => !char.IsAsciiDigit(c)))
            throw new DomainException("MEMBER_PHONE_INVALID");
        return candidate;
    }

    private static byte[] DeriveProofKey(byte[] masterKey)
    {
        if (masterKey.Length != 32) throw new ArgumentException("Identity protection master key must be 32 bytes.", nameof(masterKey));
        return HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes("olga:phone-verification-proof:v1"));
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException) { bytes = []; return false; }
    }
}
