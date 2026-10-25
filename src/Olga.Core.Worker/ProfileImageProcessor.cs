using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;
using SkiaSharp;

namespace Olga.Core.Worker;

public sealed class ProfileImageProcessor(CoreDbContext db, IProfileImageStorage storage)
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private const int MaximumDimension = 12_000;
    // Decoding and auto-orientation can hold two RGBA bitmaps concurrently.
    private const long MaximumPixels = 20_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task ProcessAsync(ProfileImageUploaded uploaded, CancellationToken ct)
    {
        var row = await db.MemberProfileImages.SingleOrDefaultAsync(x => x.ImageId == uploaded.ImageId && x.MemberId == uploaded.MemberId, ct)
            ?? throw new InvalidOperationException("Profile image event references a missing database row.");
        if (row.Status is "APPROVED" or "REJECTED" or "FAILED") return;

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

            using var encodedSource = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(encodedSource) ?? throw new InvalidProfileImageException("PROFILE_IMAGE_INVALID");
            var info = codec.Info;
            if (info.Width > MaximumDimension || info.Height > MaximumDimension || (long)info.Width * info.Height > MaximumPixels)
                throw new InvalidProfileImageException("PROFILE_IMAGE_DIMENSIONS_INVALID");

            ct.ThrowIfCancellationRequested();
            using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
                throw new InvalidProfileImageException("PROFILE_IMAGE_INVALID");
            using var oriented = AutoOrient(decoded, codec.EncodedOrigin);

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
        catch (Exception ex)
        {
            await MarkRejectedAsync(row, ex is InvalidProfileImageException invalid ? invalid.Code : "PROFILE_IMAGE_INVALID", ct);
            return;
        }

        // Keep the pointer/outbox transaction outside the decode failure handler. A database
        // failure must be retried by Service Bus, never converted into a rejected image.
        await MarkReadyAsync(row, ct);
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

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken ct)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)destination.Length)), ct);
            if (read == 0) return destination.ToArray();
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            if (destination.Length > maximumBytes) throw new InvalidProfileImageException("PROFILE_IMAGE_SIZE_INVALID");
        }
    }

    private static string DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new InvalidProfileImageException("PROFILE_IMAGE_INVALID");
    }

    private static SKBitmap AutoOrient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swapsAxes ? source.Height : source.Width, swapsAxes ? source.Width : source.Height, source.ColorType, source.AlphaType);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix([-1, 0, source.Width, 0, 1, 0, 0, 0, 1]),
            SKEncodedOrigin.BottomRight => new SKMatrix([-1, 0, source.Width, 0, -1, source.Height, 0, 0, 1]),
            SKEncodedOrigin.BottomLeft => new SKMatrix([1, 0, 0, 0, -1, source.Height, 0, 0, 1]),
            SKEncodedOrigin.LeftTop => new SKMatrix([0, 1, 0, 1, 0, 0, 0, 0, 1]),
            SKEncodedOrigin.RightTop => new SKMatrix([0, -1, source.Height, 1, 0, 0, 0, 0, 1]),
            SKEncodedOrigin.RightBottom => new SKMatrix([0, -1, source.Height, -1, 0, source.Width, 0, 0, 1]),
            SKEncodedOrigin.LeftBottom => new SKMatrix([0, 1, 0, -1, 0, source.Width, 0, 0, 1]),
            _ => SKMatrix.CreateIdentity()
        };
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return result;
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
