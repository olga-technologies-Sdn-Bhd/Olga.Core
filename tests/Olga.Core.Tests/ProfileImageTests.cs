using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;
using Olga.Core.Worker;
using SkiaSharp;

namespace Olga.Core.Tests;

public sealed class ProfileImageTests
{
    private static readonly AesIdentityProtector IdentityProtector = new(Enumerable.Repeat((byte)9, 32).ToArray());

    [Fact]
    public async Task Upload_rejects_missing_and_stale_preconditions()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var service = Service(db, new InMemoryProfileImageStorage());

        var missing = await Assert.ThrowsAsync<DomainException>(() => service.UploadProfileImageAsync("A", new MemoryStream([1]), 1, "image/png", "key", null, default));
        var stale = await Assert.ThrowsAsync<DomainException>(() => service.UploadProfileImageAsync("A", new MemoryStream([1]), 1, "image/png", "key", "\"99\"", default));

        Assert.Equal(("IF_MATCH_REQUIRED", 428), (missing.Code, missing.StatusCode));
        Assert.Equal(("RESOURCE_VERSION_CONFLICT", 409), (stale.Code, stale.StatusCode));
    }

    [Fact]
    public async Task Upload_rejects_oversized_file_before_blob_access()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => Service(db, new InMemoryProfileImageStorage())
            .UploadProfileImageAsync("A", Stream.Null, 5 * 1024 * 1024 + 1L, "image/png", "key", "\"1\"", default));

        Assert.Equal(("PROFILE_IMAGE_SIZE_INVALID", 413), (error.Code, error.StatusCode));
        Assert.Empty(db.MemberProfileImages);
    }

    [Fact]
    public async Task Processor_rejects_a_mime_signature_mismatch()
    {
        var (db, storage, row) = await PendingAsync("image/jpeg", PngAsync);
        await using (db)
        {
            await new ProfileImageProcessor(db, storage).ProcessAsync(new(row.ImageId, row.MemberId), default);
            Assert.Equal("REJECTED", row.Status);
            Assert.Equal("PROFILE_IMAGE_SIGNATURE_MISMATCH", row.FailureCode);
        }
    }

    [Fact]
    public async Task Processor_rejects_an_invalid_image()
    {
        var (db, storage, row) = await PendingAsync("image/png", () => Task.FromResult<byte[]>([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]));
        await using (db)
        {
            await new ProfileImageProcessor(db, storage).ProcessAsync(new(row.ImageId, row.MemberId), default);
            Assert.Equal("REJECTED", row.Status);
            Assert.Equal("PROFILE_IMAGE_INVALID", row.FailureCode);
        }
    }

    [Fact]
    public async Task Processor_creates_variants_and_switches_the_profile_atomically()
    {
        var (db, storage, row) = await PendingAsync("image/png", PngAsync);
        await using (db)
        {
            await new ProfileImageProcessor(db, storage).ProcessAsync(new(row.ImageId, row.MemberId), default);

            Assert.Equal("APPROVED", row.Status);
            Assert.Equal(row.Id, db.MemberProfiles.Single().ProfileImageId);
            Assert.Equal("MemberProfileImageChanged.v1", Assert.Single(db.OutboxEvents).EventType);
            foreach (var pixels in new[] { 64, 256, 512 })
                await using (await storage.OpenReadAsync(CoreService.DerivativeImagePath(row.MemberId, row.ImageId, pixels), default)) { }
        }
    }

    [Fact]
    public async Task Processing_failure_keeps_the_old_profile_picture()
    {
        var (db, storage, row) = await PendingAsync("image/png", () => Task.FromResult<byte[]>([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
        await using (db)
        {
            db.MemberProfiles.Single().ProfileImageId = 999L;
            await db.SaveChangesAsync();

            await new ProfileImageProcessor(db, storage).ProcessAsync(new(row.ImageId, row.MemberId), default);

            Assert.Equal(999L, db.MemberProfiles.Single().ProfileImageId);
            Assert.Equal("REJECTED", row.Status);
        }
    }

    [Fact]
    public async Task Profile_image_obeys_profile_visibility()
    {
        await using var db = Db();
        var storage = new InMemoryProfileImageStorage();
        var profile = new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE", Visibility = "HIDDEN" };
        var image = new MemberProfileImage { ImageId = "img_a", MemberId = "A", Status = "APPROVED", OriginalBlobPath = "private" };
        db.MemberProfiles.Add(profile);
        db.MemberProfileImages.Add(image);
        await db.SaveChangesAsync();
        profile.ProfileImageId = image.Id;
        await db.SaveChangesAsync();
        await storage.UploadAsync(CoreService.DerivativeImagePath("A", "img_a", 64), new MemoryStream([1]), "image/webp", default);
        var service = Service(db, storage);

        var hidden = await Assert.ThrowsAsync<DomainException>(() => service.GetVisibleProfileImageAsync("B", "A", "thumbnail", default));
        await using var own = (await service.GetVisibleProfileImageAsync("A", "A", "thumbnail", default)).Content;

        Assert.Equal("PROFILE_NOT_FOUND", hidden.Code);
        Assert.Equal(1, own.ReadByte());
    }

    [Fact]
    public async Task Delete_clears_pointer_rejects_pending_work_and_emits_event()
    {
        await using var db = Db();
        var profile = new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" };
        var approved = new MemberProfileImage { ImageId = "img_old", MemberId = "A", Status = "APPROVED", OriginalBlobPath = "old" };
        var pending = new MemberProfileImage { ImageId = "img_pending", MemberId = "A", Status = "PENDING", OriginalBlobPath = "pending" };
        db.MemberProfiles.Add(profile);
        db.MemberProfileImages.AddRange(approved, pending);
        await db.SaveChangesAsync();
        profile.ProfileImageId = approved.Id;
        await db.SaveChangesAsync();

        var service = Service(db, new InMemoryProfileImageStorage());
        var response = await service.DeleteProfileImageAsync("A", "delete-1", "\"1\"", default);
        var replay = await service.DeleteProfileImageAsync("A", "delete-1", "\"1\"", default);

        Assert.Null(db.MemberProfiles.Single().ProfileImageId);
        Assert.Equal("APPROVED", approved.Status);
        Assert.Equal("FAILED", pending.Status);
        Assert.Equal("ProfileImageRemoved.v1", Assert.Single(db.OutboxEvents).EventType);
        Assert.Null(response.ProfileImageUrl);
        Assert.Equal(response.ETag, replay.ETag);
    }

    [Fact]
    public async Task Profile_contract_never_exposes_a_blob_path()
    {
        await using var db = Db();
        var profile = new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" };
        var image = new MemberProfileImage { ImageId = "img_a", MemberId = "A", Status = "APPROVED", OriginalBlobPath = "members/A/img_a/original" };
        db.MemberProfiles.Add(profile);
        db.MemberProfileImages.Add(image);
        await db.SaveChangesAsync();
        profile.ProfileImageId = image.Id;
        await db.SaveChangesAsync();

        var response = await Service(db, new InMemoryProfileImageStorage()).GetOwnProfileAsync("A", default);
        var json = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.Equal("/v1/members/A/profile-picture?size=thumbnail", response.ProfileImageUrl);
        Assert.Equal("READY", response.ProfileImageStatus);
        Assert.DoesNotContain("members/A/img_a/original", json, StringComparison.Ordinal);
    }

    private static async Task<(CoreDbContext Db, InMemoryProfileImageStorage Storage, MemberProfileImage Row)> PendingAsync(string contentType, Func<Task<byte[]>> content)
    {
        var db = Db();
        var storage = new InMemoryProfileImageStorage();
        var row = new MemberProfileImage { ImageId = $"img_{Guid.NewGuid():N}", MemberId = "A", Status = "PENDING", DeclaredContentType = contentType };
        row.OriginalBlobPath = CoreService.OriginalImagePath(row.MemberId, row.ImageId);
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A", Status = "ACTIVE" });
        db.MemberProfileImages.Add(row);
        await db.SaveChangesAsync();
        var bytes = await content();
        row.ContentLength = bytes.Length;
        row.ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        await storage.UploadAsync(row.OriginalBlobPath, new MemoryStream(bytes), contentType, default);
        return (db, storage, row);
    }

    private static Task<byte[]> PngAsync()
    {
        using var bitmap = new SKBitmap(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return Task.FromResult(encoded.ToArray());
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CoreService Service(CoreDbContext db, IProfileImageStorage storage) => new(db, IdentityProtector, storage);
}

public sealed class ProfileImageApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;
    public ProfileImageApiTests(WebApplicationFactory<Program> factory) => this.factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    [Fact]
    public async Task Upload_requires_an_idempotency_key()
    {
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "ignored.png" } };

        using var response = await client.PutAsync("/v1/me/profile-picture", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
