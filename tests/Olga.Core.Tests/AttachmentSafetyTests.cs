using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;
using Olga.Core.Worker;
using SkiaSharp;

namespace Olga.Core.Tests;

public sealed class AttachmentSafetyTests
{
    [Fact]
    public async Task Unsafe_chat_image_is_quarantined()
    {
        await using var db = Db();
        var storage = new InMemoryChatFileStorage();
        var uploaded = Event("image/png");
        var bytes = Png();
        db.FileAssets.Add(Row(uploaded, bytes.Length));
        await db.SaveChangesAsync();
        await storage.UploadAsync(ChatFiles.BlobPath(uploaded.ConversationId, uploaded.FileAssetId), new MemoryStream(bytes), uploaded.MediaType, default);

        var completed = await new ChatAttachmentProcessor(db, storage, new RejectImages())
            .ProcessAsync(uploaded with { SizeBytes = bytes.Length }, default);

        Assert.True(completed);
        Assert.Equal(("REJECTED", "QUARANTINED"), (db.FileAssets.Single().ScanStatus, db.FileAssets.Single().LifecycleStatus));
        Assert.Equal("AUTOMATED_SCAN", Assert.Single(db.ModerationCases).SourceType);
    }

    [Fact]
    public async Task Malware_clean_non_image_becomes_available_without_image_moderation()
    {
        await using var db = Db();
        var storage = new InMemoryChatFileStorage();
        var uploaded = Event("application/pdf") with { SizeBytes = 5 };
        db.FileAssets.Add(Row(uploaded, uploaded.SizeBytes));
        await db.SaveChangesAsync();
        await storage.UploadAsync(ChatFiles.BlobPath(uploaded.ConversationId, uploaded.FileAssetId), new MemoryStream("%PDF"u8.ToArray()), uploaded.MediaType, default);

        var completed = await new ChatAttachmentProcessor(db, storage, new RejectImages()).ProcessAsync(uploaded, default);

        Assert.True(completed);
        Assert.Equal(("CLEAN", "AVAILABLE"), (db.FileAssets.Single().ScanStatus, db.FileAssets.Single().LifecycleStatus));
    }

    private static ChatFileUploaded Event(string mediaType) => new("fil_1", "con_1", "A", ChatFiles.ContainerName, mediaType, 1);

    private static FileAsset Row(ChatFileUploaded uploaded, long size) => new()
    {
        FileAssetId = uploaded.FileAssetId,
        CommunityId = "olga",
        OwnerMemberId = uploaded.OwnerMemberId,
        PurposeCode = ChatFiles.Purpose,
        ContainerName = uploaded.ContainerName,
        BlobPath = ChatFiles.BlobPath(uploaded.ConversationId, uploaded.FileAssetId),
        BlobPathHash = new string('a', 64),
        FileName = "upload",
        MediaType = uploaded.MediaType,
        SizeBytes = size,
        Sha256 = new string('b', 64)
    };

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RejectImages : IImageModerationClient
    {
        public Task<ImageModerationResult> AnalyzeAsync(byte[] normalizedJpeg, CancellationToken ct) =>
            Task.FromResult(new ImageModerationResult(false, ["SEXUAL_6"]));
    }
}
