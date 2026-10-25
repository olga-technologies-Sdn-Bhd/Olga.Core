using System.Collections.Concurrent;
using Azure.Identity;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Olga.Core.Application;

namespace Olga.Core.Infrastructure;

public sealed class AzureProfileImageStorage : IProfileImageStorage
{
    private const string MalwareScanResultTag = "Malware Scanning scan result";
    private readonly BlobContainerClient pending;
    private readonly BlobContainerClient images;

    public AzureProfileImageStorage(IConfiguration configuration)
    {
        var endpoint = configuration["Storage:BlobServiceEndpoint"]
            ?? throw new InvalidOperationException("Storage:BlobServiceEndpoint is required.");
        var managedIdentityClientId = configuration["Storage:ManagedIdentityClientId"] ?? configuration["AZURE_CLIENT_ID"];
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId });
        var service = new BlobServiceClient(new Uri(endpoint), credential);
        pending = service.GetBlobContainerClient(configuration["Storage:PendingProfileImagesContainerName"] ?? "pending-profile-images");
        images = service.GetBlobContainerClient(configuration["Storage:ProfileImagesContainerName"] ?? "profile-images");
    }

    public async Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        try
        {
            await Container(blobPath).GetBlobClient(blobPath).UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Immutable names make a retry idempotent after a partial worker attempt.
        }
    }

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        var response = await Container(blobPath).GetBlobClient(blobPath).DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task<MalwareScanResult> GetMalwareScanResultAsync(string blobPath, CancellationToken ct)
    {
        var tags = (await Container(blobPath).GetBlobClient(blobPath).GetTagsAsync(cancellationToken: ct)).Value.Tags;
        if (!tags.TryGetValue(MalwareScanResultTag, out var result)) return MalwareScanResult.Pending;
        if (string.Equals(result, "No threats found", StringComparison.OrdinalIgnoreCase)) return MalwareScanResult.Clean;
        if (string.Equals(result, "Malicious", StringComparison.OrdinalIgnoreCase)) return MalwareScanResult.Malicious;
        if (string.IsNullOrWhiteSpace(result) || string.Equals(result, "Pending", StringComparison.OrdinalIgnoreCase)) return MalwareScanResult.Pending;
        return MalwareScanResult.Failed;
    }

    public async Task DeleteIfExistsAsync(string blobPath, CancellationToken ct) =>
        _ = await Container(blobPath).GetBlobClient(blobPath).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);

    private BlobContainerClient Container(string blobPath) => blobPath.EndsWith("/original", StringComparison.Ordinal) ? pending : images;
}

public sealed class InMemoryProfileImageStorage : IProfileImageStorage
{
    private readonly ConcurrentDictionary<string, (byte[] Content, string ContentType)> blobs = new(StringComparer.Ordinal);

    public async Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        blobs.TryAdd(blobPath, (buffer.ToArray(), contentType));
    }

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!blobs.TryGetValue(blobPath, out var blob)) throw new FileNotFoundException("Profile image blob not found.", blobPath);
        return Task.FromResult<Stream>(new MemoryStream(blob.Content, writable: false));
    }

    public Task<MalwareScanResult> GetMalwareScanResultAsync(string blobPath, CancellationToken ct) =>
        Task.FromResult(MalwareScanResult.Clean);

    public Task DeleteIfExistsAsync(string blobPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        blobs.TryRemove(blobPath, out _);
        return Task.CompletedTask;
    }
}
