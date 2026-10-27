using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Infrastructure;

namespace Olga.Core.Worker;

public sealed class ChatAttachmentProcessor(CoreDbContext db, IChatFileStorage storage, IImageModerationClient moderation)
{
    public async Task<bool> ProcessAsync(ChatFileUploaded uploaded, CancellationToken ct)
    {
        var row = await db.FileAssets.SingleOrDefaultAsync(
            x => x.FileAssetId == uploaded.FileAssetId && x.PurposeCode == ChatFiles.Purpose,
            ct) ?? throw new InvalidOperationException("Chat attachment event references a missing database row.");
        if (!string.Equals(row.ContainerName, uploaded.ContainerName, StringComparison.Ordinal)
            || !string.Equals(row.MediaType, uploaded.MediaType, StringComparison.Ordinal)
            || !string.Equals(row.OwnerMemberId, uploaded.OwnerMemberId, StringComparison.Ordinal)
            || !string.Equals(row.BlobPath, ChatFiles.BlobPath(uploaded.ConversationId, uploaded.FileAssetId), StringComparison.Ordinal)
            || row.SizeBytes != uploaded.SizeBytes)
            throw new InvalidOperationException("Chat attachment event does not match the stored file metadata.");
        if (row.ScanStatus is "CLEAN" or "REJECTED" or "ERROR") return true;

        var malware = await storage.GetMalwareScanResultAsync(row.BlobPath, ct);
        if (malware == MalwareScanResult.Pending) return false;
        if (malware == MalwareScanResult.Malicious)
        {
            await MarkAsync(row, "REJECTED", "QUARANTINED", ct);
            return true;
        }
        if (malware == MalwareScanResult.Failed)
        {
            await MarkAsync(row, "ERROR", "QUARANTINED", ct);
            return true;
        }

        if (row.MediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            try
            {
                await using var source = await storage.OpenReadAsync(row.BlobPath, ct);
                var bytes = await SafeImageDecoder.ReadBoundedAsync(source, SafeImageDecoder.MaximumSourceBytes, ct);
                using var decoded = SafeImageDecoder.Decode(bytes);
                var verdict = await moderation.AnalyzeAsync(SafeImageDecoder.NormalizeForModeration(decoded), ct);
                if (!verdict.Allowed)
                {
                    await AddModerationCaseAsync(row, ct);
                    await MarkAsync(row, "REJECTED", "QUARANTINED", ct);
                    return true;
                }
            }
            catch (InvalidImageException)
            {
                await MarkAsync(row, "REJECTED", "QUARANTINED", ct);
                return true;
            }
        }

        await MarkAsync(row, "CLEAN", "AVAILABLE", ct);
        return true;
    }

    public async Task MarkTimedOutAsync(ChatFileUploaded uploaded, CancellationToken ct)
    {
        var row = await db.FileAssets.SingleOrDefaultAsync(x => x.FileAssetId == uploaded.FileAssetId, ct);
        if (row is not null && row.ScanStatus == "PENDING") await MarkAsync(row, "ERROR", "QUARANTINED", ct);
    }

    public Task MarkProcessingFailedAsync(ChatFileUploaded uploaded, CancellationToken ct) => MarkTimedOutAsync(uploaded, ct);

    private async Task AddModerationCaseAsync(Olga.Core.Domain.FileAsset row, CancellationToken ct)
    {
        var caseId = "case_scan_" + row.FileAssetId;
        if (caseId.Length > 64) caseId = caseId[..64];
        if (await db.ModerationCases.AnyAsync(x => x.ModerationCaseId == caseId, ct)) return;
        db.ModerationCases.Add(new Olga.Core.Domain.ModerationCase
        {
            ModerationCaseId = caseId,
            SourceType = "AUTOMATED_SCAN",
            SourceId = row.FileAssetId,
            SubjectMemberId = row.OwnerMemberId,
            ResourceType = "FILE_ASSET",
            ResourceId = row.FileAssetId,
            Priority = "HIGH"
        });
    }

    private async Task MarkAsync(Olga.Core.Domain.FileAsset row, string scanStatus, string lifecycleStatus, CancellationToken ct)
    {
        row.ScanStatus = scanStatus;
        row.LifecycleStatus = lifecycleStatus;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) row.RowVersion++;
        await db.SaveChangesAsync(ct);
    }
}
