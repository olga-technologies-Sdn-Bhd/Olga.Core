using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Olga.Core.Worker;

public sealed class ContentSafetyOptions
{
    public const string SectionName = "ContentSafety";
    public string Endpoint { get; init; } = "";
    public bool Required { get; init; } = true;
    public int SexualSeverityThreshold { get; init; } = 4;
    public int ViolenceSeverityThreshold { get; init; } = 4;
    public int HateSeverityThreshold { get; init; } = 4;
    public int SelfHarmSeverityThreshold { get; init; } = 4;
}

public sealed record ImageModerationResult(bool Allowed, IReadOnlyList<string> ReasonCodes);

public interface IImageModerationClient
{
    Task<ImageModerationResult> AnalyzeAsync(byte[] normalizedJpeg, CancellationToken ct);
}

public sealed class AzureContentSafetyImageClient(
    HttpClient http,
    TokenCredential credential,
    IOptions<ContentSafetyOptions> options) : IImageModerationClient
{
    private static readonly string[] Categories = ["Hate", "SelfHarm", "Sexual", "Violence"];
    private readonly ContentSafetyOptions settings = options.Value;

    public async Task<ImageModerationResult> AnalyzeAsync(byte[] normalizedJpeg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            if (settings.Required) throw new InvalidOperationException("ContentSafety:Endpoint is required for image uploads.");
            return new(true, []);
        }

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(settings.Endpoint.TrimEnd('/') + "/"), "contentsafety/image:analyze?api-version=2023-10-01"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new
        {
            image = new { content = Convert.ToBase64String(normalizedJpeg) },
            categories = Categories,
            outputType = "FourSeverityLevels"
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);

        var reasons = new List<string>();
        if (!json.RootElement.TryGetProperty("categoriesAnalysis", out var analyses))
            throw new InvalidOperationException("Azure Content Safety returned no category analysis.");
        foreach (var analysis in analyses.EnumerateArray())
        {
            var category = analysis.GetProperty("category").GetString() ?? "Unknown";
            var severity = analysis.GetProperty("severity").GetInt32();
            var threshold = category switch
            {
                "Sexual" => settings.SexualSeverityThreshold,
                "Violence" => settings.ViolenceSeverityThreshold,
                "Hate" => settings.HateSeverityThreshold,
                "SelfHarm" => settings.SelfHarmSeverityThreshold,
                _ => int.MaxValue
            };
            if (severity >= threshold) reasons.Add($"{category.ToUpperInvariant()}_{severity}");
        }
        return new(reasons.Count == 0, reasons);
    }
}

public static class SafeImageDecoder
{
    public const int MaximumSourceBytes = 10 * 1024 * 1024;
    private const int MaximumDimension = 12_000;
    private const long MaximumPixels = 20_000_000;
    private const int MaximumModerationBytes = 4 * 1024 * 1024;

    public static SKBitmap Decode(byte[] bytes)
    {
        using var encoded = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(encoded) ?? throw new InvalidImageException("IMAGE_INVALID");
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || info.Width > MaximumDimension || info.Height > MaximumDimension
            || (long)info.Width * info.Height > MaximumPixels)
            throw new InvalidImageException("IMAGE_DIMENSIONS_INVALID");
        var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            decoded.Dispose();
            throw new InvalidImageException("IMAGE_INVALID");
        }
        using (decoded)
            return AutoOrient(decoded, codec.EncodedOrigin);
    }

    public static byte[] NormalizeForModeration(SKBitmap source)
    {
        var maximumSide = 2048;
        var quality = 85;
        while (true)
        {
            var scale = Math.Min(1d, maximumSide / (double)Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var normalized = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(normalized))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                canvas.Flush();
            }
            using var image = SKImage.FromBitmap(normalized);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality)
                ?? throw new InvalidImageException("IMAGE_ENCODING_FAILED");
            var bytes = jpeg.ToArray();
            if (bytes.Length <= MaximumModerationBytes) return bytes;
            if (maximumSide <= 512) throw new InvalidImageException("IMAGE_MODERATION_SIZE_INVALID");
            maximumSide /= 2;
            quality = Math.Max(60, quality - 10);
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken ct)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var remaining = maximumBytes + 1 - (int)destination.Length;
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) return destination.ToArray();
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            if (destination.Length > maximumBytes) throw new InvalidImageException("IMAGE_SIZE_INVALID");
        }
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
}

public sealed class InvalidImageException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
