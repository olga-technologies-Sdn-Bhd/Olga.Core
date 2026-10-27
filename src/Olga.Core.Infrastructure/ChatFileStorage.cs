using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Olga.Core.Application;

namespace Olga.Core.Infrastructure;

// Chat files live in the private chat-files container and are only ever streamed through the API: never a SAS URL.
public sealed class AzureChatFileStorage : IChatFileStorage
{
    private const string MalwareScanResultTag = "Malware Scanning scan result";
    private readonly BlobContainerClient container;
    private readonly bool bypassMalwareScanning;

    public AzureChatFileStorage(IConfiguration configuration, IHostEnvironment environment)
    {
        var endpoint = configuration["Storage:BlobServiceEndpoint"]
            ?? throw new InvalidOperationException("Storage:BlobServiceEndpoint is required.");
        var managedIdentityClientId = configuration["Storage:ManagedIdentityClientId"] ?? configuration["AZURE_CLIENT_ID"];
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId });
        container = new BlobServiceClient(new Uri(endpoint), credential).GetBlobContainerClient(configuration["Storage:ChatFilesContainerName"] ?? ChatFiles.ContainerName);
        var malwareScanningRequired = !bool.TryParse(configuration["MalwareScanning:Required"], out var required) || required;
        bypassMalwareScanning = environment.IsDevelopment() && !malwareScanningRequired;
    }

    public async Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(blobPath).UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // The name comes from the member and Idempotency-Key, so a retry finds its own earlier upload.
        }
    }

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct) =>
        (await container.GetBlobClient(blobPath).DownloadStreamingAsync(cancellationToken: ct)).Value.Content;

    public async Task<MalwareScanResult> GetMalwareScanResultAsync(string blobPath, CancellationToken ct)
    {
        if (bypassMalwareScanning) return MalwareScanResult.Clean;
        var tags = (await container.GetBlobClient(blobPath).GetTagsAsync(cancellationToken: ct)).Value.Tags;
        if (!tags.TryGetValue(MalwareScanResultTag, out var result)) return MalwareScanResult.Pending;
        if (string.Equals(result, "No threats found", StringComparison.OrdinalIgnoreCase)) return MalwareScanResult.Clean;
        if (string.Equals(result, "Malicious", StringComparison.OrdinalIgnoreCase)) return MalwareScanResult.Malicious;
        if (string.IsNullOrWhiteSpace(result) || string.Equals(result, "Pending", StringComparison.OrdinalIgnoreCase)) return MalwareScanResult.Pending;
        return MalwareScanResult.Failed;
    }
}

public sealed class InMemoryChatFileStorage : IChatFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

    public async Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        blobs.TryAdd(blobPath, buffer.ToArray());
    }

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!blobs.TryGetValue(blobPath, out var blob)) throw new FileNotFoundException("Chat file blob not found.", blobPath);
        return Task.FromResult<Stream>(new MemoryStream(blob, writable: false));
    }

    public Task<MalwareScanResult> GetMalwareScanResultAsync(string blobPath, CancellationToken ct) =>
        Task.FromResult(MalwareScanResult.Clean);
}
