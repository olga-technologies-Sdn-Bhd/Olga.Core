using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;

namespace Olga.Core.Worker;

public sealed class ProfileImageProcessor(CoreDbContext db, IProfileImageStorage storage)
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private const int MaximumDimension = 12_000;
    private const long MaximumPixels = 40_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task ProcessAsync(ProfileImageUploaded uploaded, CancellationToken ct)
    {
        var row = await db.MemberProfileImages.SingleOrDefaultAsync(x => x.ImageId == uploaded.ImageId && x.MemberId == uploaded.MemberId, ct)
            ?? throw new InvalidOperationException("Profile image event references a missing database row.");
        if (row.Status is "READY" or "REJECTED" or "DELETED") return;

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

            using var input = new MemoryStream(bytes, writable: false);
            var configuration = Configuration.Default.Clone();
            configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                MaximumPoolSizeMegabytes = 64,
                AllocationLimitMegabytes = 192
            });
            var decoderOptions = new DecoderOptions { Configuration = configuration, MaxFrames = 1 };
            var info = Image.Identify(decoderOptions, input) ?? throw new InvalidProfileImageException("PROFILE_IMAGE_INVALID");
            if (info.Width > MaximumDimension || info.Height > MaximumDimension || (long)info.Width * info.Height > MaximumPixels)
                throw new InvalidProfileImageException("PROFILE_IMAGE_DIMENSIONS_INVALID");

            input.Position = 0;
            using var image = Image.Load(decoderOptions, input);
            image.Mutate(context => context.AutoOrient());
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            foreach (var pixels in new[] { 64, 256, 512 })
            {
                using var derivative = image.Clone(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(pixels, pixels),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));
                await using var encoded = new MemoryStream();
                await derivative.SaveAsWebpAsync(encoded, new WebpEncoder { Quality = 85 }, ct);
                encoded.Position = 0;
                await storage.UploadAsync(CoreService.DerivativeImagePath(row.MemberId, row.ImageId, pixels), encoded, "image/webp", ct);
            }

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
        if (row.Status == "DELETED") return;
        var previousImageId = profile.ProfileImageId;
        row.Status = "READY";
        row.FailureCode = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) row.RowVersion++;
        profile.ProfileImageId = row.ImageId;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational()) profile.Version++;
        if (previousImageId is not null && !string.Equals(previousImageId, row.ImageId, StringComparison.Ordinal))
        {
            var previous = await db.MemberProfileImages.SingleOrDefaultAsync(x => x.ImageId == previousImageId, ct);
            if (previous is not null) previous.DeleteAfter = DateTimeOffset.UtcNow.AddDays(7);
        }
        db.OutboxEvents.Add(new OutboxEvent
        {
            AggregateType = "MEMBER",
            AggregateId = row.MemberId,
            EventType = "MemberProfileImageChanged.v1",
            PayloadJson = JsonSerializer.Serialize(new { member_id = row.MemberId, image_id = row.ImageId, previous_image_id = previousImageId }, JsonOptions)
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task MarkRejectedAsync(MemberProfileImage row, string failureCode, CancellationToken ct)
    {
        row.Status = "REJECTED";
        row.FailureCode = failureCode;
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

    private sealed class InvalidProfileImageException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}
