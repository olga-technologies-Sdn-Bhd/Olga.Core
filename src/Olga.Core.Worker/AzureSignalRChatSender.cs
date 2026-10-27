using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Olga.Core.Application;

namespace Olga.Core.Worker;

// Sends through Azure SignalR's REST API with the worker's managed identity (SignalR REST API Owner).
// Calling REST directly, not the Management SDK, keeps ASP.NET Core out of the worker so it runs on the plain .NET runtime image.
public sealed class AzureSignalRChatSender : IChatRealtimeSender
{
    private const string ApiVersion = "2022-11-01";
    private static readonly TokenRequestContext TokenRequest = new(["https://signalr.azure.com/.default"]);
    private readonly HttpClient http;
    private readonly TokenCredential credential;
    private readonly string endpoint;

    public AzureSignalRChatSender(HttpClient http, IConfiguration configuration)
        : this(http, configuration["SignalR:Endpoint"], Credential(configuration)) { }

    public AzureSignalRChatSender(HttpClient http, string? endpoint, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("SignalR:Endpoint is required for realtime chat.");
        this.http = http;
        this.endpoint = endpoint.TrimEnd('/');
        this.credential = credential;
    }

    public async Task SendAsync(IReadOnlyList<string> memberIds, JsonElement envelope, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(TokenRequest, cancellationToken);
        var message = new { target = ChatRealtime.ClientMethod, arguments = new object[] { envelope } };
        foreach (var memberId in memberIds.Distinct())
        {
            var url = $"{endpoint}/api/hubs/{Uri.EscapeDataString(ChatRealtime.HubName)}/users/{Uri.EscapeDataString(memberId)}/:send?api-version={ApiVersion}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(message) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await http.SendAsync(request, cancellationToken);
            // 202 means accepted, including for a member with no connected device.
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Azure SignalR returned {(int)response.StatusCode} for a user send.", null, response.StatusCode);
        }
    }

    private static TokenCredential Credential(IConfiguration configuration)
    {
        var clientId = configuration["SignalR:ManagedIdentityClientId"];
        if (string.IsNullOrWhiteSpace(clientId)) clientId = configuration["AZURE_CLIENT_ID"];
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
    }
}
