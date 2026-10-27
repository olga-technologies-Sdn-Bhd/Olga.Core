using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;
using SkiaSharp;

namespace Olga.Core.Worker;

public sealed class ProfileImageProcessor(CoreDbContext db, IProfileImageStorage storage, IImageModerationClient moderation)
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<bool> ProcessAsync(ProfileImageUploaded uploaded, CancellationToken ct)
    {
        var row = await db.MemberProfileImages.SingleOrDefaultAsync(x => x.ImageId == uploaded.ImageId && x.MemberId == uploaded.MemberId, ct)
            ?? throw new InvalidOperationException("Profile image event references a missing database row.");
        if (row.Status is "APPROVED" or "REJECTED" or "FAILED") return true;

        var malware = await storage.GetMalwareScanResultAsync(row.OriginalBlobPath, ct);
        if (malware == MalwareScanResult.Pending) return false;
        if (malware != MalwareScanResult.Clean)
        {
            await MarkRejectedAsync(row, malware == MalwareScanResult.Malicious ? "MALWARE_DETECTED" : "MALWARE_SCAN_FAILED", ct);
            return true;
        }

        row.Status = "PROCESSING";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            await using var source = await storage.OpenReadAsync(row.OriginalBlobPath, ct);
            var bytes = await ReadBoundedAsync(source, MaximumBytes, ct);
            var actualContentType = DetectContentType(bytes);
            if (!string.Equals(actualContentType, row.DeclaredContentType, StringComparison.Ordinal))
                throw new InvalidProfileImageException("PROFILE_IMAGE_SIGNATURE_MISMATCH");

            ct.ThrowIfCancellationRequested();
            using var oriented = SafeImageDecoder.Decode(bytes);
            var verdict = await moderation.AnalyzeAsync(SafeImageDecoder.NormalizeForModeration(oriented), ct);
            if (!verdict.Allowed)
            {
                await AddModerationCaseAsync(row, ct);
                await MarkRejectedAsync(row, "CONTENT_UNSAFE:" + string.Join(',', verdict.ReasonCodes), ct);
                return true;
            }

            foreach (var pixels in new[] { 64, 256, 512 })
            {
                ct.ThrowIfCancellationRequested();
                using var derivative = ResizeSquare(oriented, pixels);
                using var image = SKImage.FromBitmap(derivative);
                using var webp = image.Encode(SKEncodedImageFormat.Webp, 85)
                    ?? throw new InvalidProfileImageException("PROFILE_IMAGE_ENCODING_FAILED");
                await using var encoded = new MemoryStream();
                webp.SaveTo(encoded);
                encoded.Position = 0;
                await storage.UploadAsync(CoreService.DerivativeImagePath(row.MemberId, row.ImageId, pixels), encoded, "image/webp", ct);
            }
            row.ThumbnailBlobPath = CoreService.DerivativeImagePath(row.MemberId, row.ImageId, 64);
            row.DisplayBlobPath = CoreService.DerivativeImagePath(row.MemberId, row.ImageId, 512);

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidImageException ex)
        {
            await MarkRejectedAsync(row, "PROFILE_" + ex.Code, ct);
            return true;
        }
        catch (InvalidProfileImageException ex)
        {
            await MarkRejectedAsync(row, ex.Code, ct);
            return true;
        }

        // Keep the pointer/outbox transaction outside the decode failure handler. A database
        // failure must be retried by Service Bus, never converted into a rejected image.
        await MarkReadyAsync(row, ct);
        return true;
    }

    public async Task MarkTimedOutAsync(ProfileImageUploaded uploaded, CancellationToken ct)
    {
        var row = await db.MemberProfileImages.SingleOrDefaultAsync(
            x => x.ImageId == uploaded.ImageId && x.MemberId == uploaded.MemberId, ct);
        if (row is not null && row.Status is "PENDING" or "PROCESSING")
            await MarkRejectedAsync(row, "MALWARE_SCAN_TIMEOUT", ct);
    }

    public async Task MarkProcessingFailedAsync(ProfileImageUploaded uploaded, CancellationToken ct)
    {
        var row = await db.MemberProfileImages.SingleOrDefaultAsync(
            x => x.ImageId == uploaded.ImageId && x.MemberId == uploaded.MemberId, ct);
        if (row is null || row.Status is not ("PENDING" or "PROCESSING")) return;
        row.Status = "FAILED";
        row.FailureCode = "CONTENT_SCAN_FAILED";
        row.ProcessedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) row.RowVersion++;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkReadyAsync(MemberProfileImage row, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var profile = await db.MemberProfiles.SingleAsync(x => x.MemberId == row.MemberId, ct);
        if (row.Status == "FAILED") return;
        var previousImage = profile.ProfileImageId is { } previousId
            ? await db.MemberProfileImages.SingleOrDefaultAsync(x => x.Id == previousId, ct)
            : null;
        row.Status = "APPROVED";
        row.FailureCode = null;
        row.ProcessedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) row.RowVersion++;
        profile.ProfileImageId = row.Id;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) profile.Version++;
        db.OutboxEvents.Add(new OutboxEvent
        {
            AggregateType = "MEMBER",
            AggregateId = row.MemberId,
            EventType = "MemberProfileImageChanged.v1",
            PayloadJson = JsonSerializer.Serialize(new { member_id = row.MemberId, image_id = row.ImageId, previous_image_id = previousImage?.ImageId }, JsonOptions)
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task MarkRejectedAsync(MemberProfileImage row, string failureCode, CancellationToken ct)
    {
        row.Status = "REJECTED";
        row.FailureCode = failureCode;
        row.ProcessedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) row.RowVersion++;
        await db.SaveChangesAsync(ct);
    }

    private async Task AddModerationCaseAsync(MemberProfileImage row, CancellationToken ct)
    {
        var caseId = "case_scan_" + row.ImageId;
        if (caseId.Length > 64) caseId = caseId[..64];
        if (await db.ModerationCases.AnyAsync(x => x.ModerationCaseId == caseId, ct)) return;
        db.ModerationCases.Add(new ModerationCase
        {
            ModerationCaseId = caseId,
            SourceType = "AUTOMATED_SCAN",
            SourceId = row.ImageId,
            SubjectMemberId = row.MemberId,
            ResourceType = "PROFILE_IMAGE",
            ResourceId = row.ImageId,
            Priority = "HIGH"
        });
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken ct) =>
        await SafeImageDecoder.ReadBoundedAsync(source, maximumBytes, ct);

    private static string DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new InvalidProfileImageException("PROFILE_IMAGE_INVALID");
    }

    private static SKBitmap ResizeSquare(SKBitmap source, int pixels)
    {
        var crop = Math.Min(source.Width, source.Height);
        var sourceRect = new SKRect((source.Width - crop) / 2f, (source.Height - crop) / 2f, (source.Width + crop) / 2f, (source.Height + crop) / 2f);
        var result = new SKBitmap(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, sourceRect, new SKRect(0, 0, pixels, pixels), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Flush();
        return result;
    }

    private sealed class InvalidProfileImageException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}
