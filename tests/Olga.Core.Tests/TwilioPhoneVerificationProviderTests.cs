using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class TwilioPhoneVerificationProviderTests
{
    private static readonly TwilioVerifyOptions Options = new()
    {
        Enabled = true,
        AccountSid = "AC00000000000000000000000000000000",
        ApiKeySid = "SK00000000000000000000000000000000",
        ApiKeySecret = "secret",
        VerifyServiceSid = "VA00000000000000000000000000000000",
        TokenTtlSeconds = 300
    };

    [Fact]
    public async Task Start_uses_the_verify_api_with_basic_auth_and_form_encoded_values()
    {
        var handler = new RecordingHandler("pending");
        var provider = Provider(handler);

        await provider.StartAsync("+60123456789", "sms", "phone-hash", default);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://verify.twilio.com/v2/Services/VA00000000000000000000000000000000/Verifications", handler.Uri?.ToString());
        Assert.Equal("Basic", handler.Authorization?.Scheme);
        Assert.Equal("SK00000000000000000000000000000000:secret",
            Encoding.ASCII.GetString(Convert.FromBase64String(handler.Authorization!.Parameter!)));
        Assert.Equal("To=%2B60123456789&Channel=sms&RiskCheck=enable", handler.Body);
    }

    [Fact]
    public async Task Approved_check_issues_a_short_lived_phone_bound_proof()
    {
        var provider = Provider(new RecordingHandler("approved"));

        var proof = await provider.CheckAsync("+60123456789", "123456", default);

        Assert.Equal(new DateTimeOffset(2026, 10, 29, 0, 5, 0, TimeSpan.Zero), proof.ExpiresAt);
        Assert.True(provider.ValidateProof("+60123456789", proof.Token));
        Assert.False(provider.ValidateProof("+60123456780", proof.Token));
        Assert.DoesNotContain("123456", proof.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("60123456789", Encoding.UTF8.GetString(Convert.FromBase64String(Pad(proof.Token.Split('.')[0]))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compliance_rejection_is_not_reported_as_provider_unavailability()
    {
        var provider = Provider(new RecordingHandler(HttpStatusCode.Forbidden, "{\"code\":21608}"));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            provider.StartAsync("+919885554624", "sms", "phone-hash", default));

        Assert.Equal("PHONE_VERIFICATION_RECIPIENT_NOT_ALLOWED", error.Code);
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public async Task Authentication_rejection_has_a_distinct_configuration_error()
    {
        var provider = Provider(new RecordingHandler(HttpStatusCode.Unauthorized, "{\"code\":20003}"));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            provider.StartAsync("+60123456789", "sms", "phone-hash", default));

        Assert.Equal("PHONE_VERIFICATION_CONFIGURATION_ERROR", error.Code);
        Assert.Equal(503, error.StatusCode);
    }

    private static TwilioPhoneVerificationProvider Provider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://verify.twilio.com/") };
        return new(client, Options, Enumerable.Repeat((byte)3, 32).ToArray(), new FixedTimeProvider(), NullLogger<TwilioPhoneVerificationProvider>.Instance);
    }

    private static string Pad(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return base64 + new string('=', (4 - base64.Length % 4) % 4);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode responseStatus;
        private readonly string responseBody;

        public RecordingHandler(string status) : this(HttpStatusCode.OK, $"{{\"status\":\"{status}\"}}") { }
        public RecordingHandler(HttpStatusCode responseStatus, string responseBody)
        {
            this.responseStatus = responseStatus;
            this.responseBody = responseBody;
        }

        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(responseStatus)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
