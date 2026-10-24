using System.Text.Json;
using Azure.Core.Serialization;
using Azure.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Management;
using Olga.Core.Application;

namespace Olga.Core.Worker;

// Sends through Azure SignalR's REST API (transient transport) with the worker's managed identity.
public sealed class AzureSignalRChatSender(IConfiguration configuration, ILoggerFactory loggerFactory) : IChatRealtimeSender, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServiceManager? serviceManager;
    private ServiceHubContext? hubContext;

    public async Task SendAsync(IReadOnlyList<string> memberIds, JsonElement envelope, CancellationToken cancellationToken)
    {
        var hub = await HubAsync(cancellationToken);
        await hub.Clients.Users(memberIds).SendAsync(ChatRealtime.ClientMethod, envelope, cancellationToken);
    }

    private async Task<ServiceHubContext> HubAsync(CancellationToken cancellationToken)
    {
        if (hubContext is not null) return hubContext;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (hubContext is not null) return hubContext;
            var endpoint = configuration["SignalR:Endpoint"] ?? throw new InvalidOperationException("SignalR:Endpoint is required for realtime chat.");
            var clientId = configuration["SignalR:ManagedIdentityClientId"];
            if (string.IsNullOrWhiteSpace(clientId)) clientId = configuration["AZURE_CLIENT_ID"];
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
            serviceManager = new ServiceManagerBuilder()
                .WithOptions(options =>
                {
                    options.ServiceEndpoints = [new ServiceEndpoint(new Uri(endpoint), credential)];
                    options.ServiceTransportType = ServiceTransportType.Transient;
                    // The envelope is a System.Text.Json element; keep it as-is on the wire.
                    options.UseJsonObjectSerializer(new JsonObjectSerializer());
                })
                .WithLoggerFactory(loggerFactory)
                .BuildServiceManager();
            hubContext = await serviceManager.CreateHubContextAsync(ChatRealtime.HubName, cancellationToken);
            return hubContext;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (hubContext is not null) await hubContext.DisposeAsync();
        serviceManager?.Dispose();
        gate.Dispose();
    }
}
