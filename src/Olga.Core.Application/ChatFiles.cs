using System.Buffers.Binary;
using System.Text;
using Olga.Core.Domain;

namespace Olga.Core.Application;

// Chat attachment limits, bound from the "Chat" configuration section.
public sealed class ChatFileOptions
{
    public const string SectionName = "Chat";
    public long ImageMaxBytes { get; init; } = 10 * 1024 * 1024;
    public long DocumentMaxBytes { get; init; } = 10 * 1024 * 1024;
    public long AudioMaxBytes { get; init; } = 5 * 1024 * 1024;
    public int AudioMaxSeconds { get; init; } = 120;
    // An upload that is never sent expires after this long.
    public int UnsentFileExpiryHours { get; init; } = 24;

    public long MaxBytes => Math.Max(ImageMaxBytes, Math.Max(DocumentMaxBytes, AudioMaxBytes));
}

public sealed record ChatFileContent(Stream Content, string MediaType, string FileName, string ETag);

public enum MalwareScanResult { Pending, Clean, Malicious, Failed }

public interface IChatFileStorage
{
    Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct);
    Task<MalwareScanResult> GetMalwareScanResultAsync(string blobPath, CancellationToken ct);
}

public static class ChatFiles
{
    public const string ContainerName = "chat-files";
    public const string Purpose = "CHAT_FILE";

    public enum Kind { Image, Document, Audio }
    public sealed record Detected(string MediaType, Kind Kind, string Extension, long? DurationMs = null);

    public static string BlobPath(string conversationId, string fileAssetId) => $"conversations/{Uri.EscapeDataString(conversationId)}/{Uri.EscapeDataString(fileAssetId)}";

    // The file's real type from its bytes; the client's content type only separates MP4 audio from other MP4 files.
    public static Detected? Detect(ReadOnlySpan<byte> content, string? declaredContentType)
    {
        if (content.StartsWith<byte>([0xFF, 0xD8, 0xFF])) return new("image/jpeg", Kind.Image, ".jpg");
        if (content.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return new("image/png", Kind.Image, ".png");
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8)) return new("image/webp", Kind.Image, ".webp");
        if (content.StartsWith("%PDF-"u8)) return new("application/pdf", Kind.Document, ".pdf");
        if (content.Length >= 12 && content[4..8].SequenceEqual("ftyp"u8))
        {
            var brands = FtypBrands(content);
            // HEIC/HEIF is rejected until the Linux worker has a deterministic decoder that can
            // normalize it to a Content Safety-supported format without relying on host codecs.
            if (brands.Overlaps(["heic", "heix", "heim", "heis", "hevc", "hevx"])) return null;
            var audio = brands.Overlaps(["M4A ", "M4B "]) || declaredContentType?.Trim().StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;
            return audio && Mp4AudioDuration(content) is { } ms ? new("audio/mp4", Kind.Audio, ".m4a", ms) : null;
        }
        return AdtsDuration(content) is { } adts ? new("audio/aac", Kind.Audio, ".aac", adts) : null;
    }

    public static long MaxBytes(ChatFileOptions options, Kind kind) => kind switch
    {
        Kind.Image => options.ImageMaxBytes,
        Kind.Document => options.DocumentMaxBytes,
        _ => options.AudioMaxBytes
    };

    // A display name without paths or control characters, ending in the detected type's extension.
    public static string SafeFileName(string? fileName, Detected detected)
    {
        var name = Path.GetFileNameWithoutExtension((fileName ?? "").Replace('\\', '/').Split('/')[^1]);
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormC))
            if (char.IsWhiteSpace(c)) cleaned.Append(' ');
            else if (!char.IsControl(c) && c is not ('"' or '<' or '>' or ':' or '|' or '?' or '*')) cleaned.Append(c);
        var stem = string.Join(' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (stem.Length == 0) stem = detected.Kind switch { Kind.Image => "photo", Kind.Audio => "voice-note", _ => "file" };
        var max = 255 - detected.Extension.Length;
        if (stem.Length > max) stem = stem[..max];
        return stem + detected.Extension;
    }

    // Whether a member may attach this upload to a message in this conversation. linkedMessageId is the
    // message it is already attached to, if any; attaching it again to that same message is a replay.
    public static void EnsureAttachable(FileAsset? asset, string? linkedMessageId, string memberId, string conversationId, string messageId, DateTimeOffset now)
    {
        if (asset is null || asset.OwnerMemberId != memberId || asset.PurposeCode != Purpose || asset.BlobPath != BlobPath(conversationId, asset.FileAssetId))
            throw new DomainException("ATTACHMENT_NOT_FOUND", 404);
        if (linkedMessageId == messageId) return;
        if (linkedMessageId is not null) throw new DomainException("ATTACHMENT_ALREADY_USED", 409);
        if (asset.ScanStatus is "REJECTED" or "ERROR" || asset.LifecycleStatus is "QUARANTINED" or "DELETED" or "EXPIRED" || asset.ExpiresAt <= now)
            throw new DomainException("ATTACHMENT_NOT_AVAILABLE", 409);
    }

    private static HashSet<string> FtypBrands(ReadOnlySpan<byte> content)
    {
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(content), (uint)content.Length);
        var brands = new HashSet<string>(StringComparer.Ordinal) { Encoding.ASCII.GetString(content[8..12]) };
        for (var i = 16; i + 4 <= size; i += 4) brands.Add(Encoding.ASCII.GetString(content[i..(i + 4)]));
        return brands;
    }

    // MP4 audio: the duration in moov/mvhd. Null when there is no movie header or the file has a video track.
    private static long? Mp4AudioDuration(ReadOnlySpan<byte> content)
    {
        var moov = FindBox(content, "moov"u8);
        if (moov.IsEmpty || moov.IndexOf("vmhd"u8) >= 0) return null;
        var mvhd = FindBox(moov, "mvhd"u8);
        // Version 1 has 64-bit times and duration; version 0 has 32-bit ones.
        if (mvhd.Length < (mvhd.IsEmpty || mvhd[0] != 1 ? 20 : 32)) return null;
        uint timescale; ulong duration;
        if (mvhd[0] == 1) { timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[20..]); duration = BinaryPrimitives.ReadUInt64BigEndian(mvhd[24..]); }
        else { timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd[12..]); duration = BinaryPrimitives.ReadUInt32BigEndian(mvhd[16..]); }
        return timescale == 0 || duration == 0 || duration == ulong.MaxValue ? null : (long)Math.Min(duration * 1000.0 / timescale, long.MaxValue);
    }

    // The body of the first box of this type at this level.
    private static ReadOnlySpan<byte> FindBox(ReadOnlySpan<byte> content, ReadOnlySpan<byte> type)
    {
        var offset = 0;
        while (offset + 8 <= content.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(content[offset..]);
            var header = 8;
            if (size == 1)
            {
                if (offset + 16 > content.Length) return default;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(content[(offset + 8)..]);
                header = 16;
            }
            else if (size == 0) size = content.Length - offset;
            if (size < header || size > content.Length - offset) return default;
            if (content.Slice(offset + 4, 4).SequenceEqual(type)) return content.Slice(offset + header, (int)size - header);
            offset += (int)size;
        }
        return default;
    }

    // Raw AAC (ADTS): every frame holds 1024 samples per raw data block. Null unless the whole file is ADTS frames.
    private static long? AdtsDuration(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        var offset = 0;
        if (content.StartsWith("ID3"u8) && content.Length >= 10)
            offset = 10 + ((content[6] & 0x7F) << 21 | (content[7] & 0x7F) << 14 | (content[8] & 0x7F) << 7 | (content[9] & 0x7F));
        double seconds = 0;
        var frames = 0;
        while (offset + 7 <= content.Length)
        {
            var frame = content[offset..];
            if (frame[0] != 0xFF || (frame[1] & 0xF6) != 0xF0) return null;
            var rateIndex = (frame[2] >> 2) & 0x0F;
            var length = ((frame[3] & 0x03) << 11) | (frame[4] << 3) | (frame[5] >> 5);
            if (rateIndex >= rates.Length || length < 7) return null;
            seconds += 1024.0 * ((frame[6] & 0x03) + 1) / rates[rateIndex];
            offset += length;
            frames++;
        }
        return frames == 0 || offset != content.Length ? null : (long)(seconds * 1000);
    }
}
