using SkiaSharp;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Imaging;

/// <summary>
/// Turns a downloaded item into the file that gets attached to the email (spec §6.4):
/// photos become upright, metadata-free JPEGs no larger than <see cref="ImageConfig.MaxLongEdge"/>
/// on the long edge; GIFs and videos under the cap are copied unchanged.
/// </summary>
public sealed class ImageNormalizer(IHeicConverter heic, ImageConfig cfg, long videoCapBytes = 22_000_000)
{
    private const int _idPrefixLength = 8;

    /// <summary>
    /// Writes the normalized file to <paramref name="destDir"/> as
    /// <c>yyyy-MM-dd_HHmmss_&lt;first 8 chars of Id&gt;.&lt;ext&gt;</c> (from <see cref="Item.CapturedAt"/>)
    /// and returns its path. If that name is already taken in <paramref name="destDir"/>, a numeric suffix
    /// (<c>-2</c>, <c>-3</c>, ...) is added before the extension, so an existing file is never overwritten. Throws <see cref="OversizeException"/> for a video over the cap and
    /// <see cref="UnsupportedMediaException"/> for anything that is not JPEG, PNG, HEIC/HEIF, GIF or video.
    /// <paramref name="destDir"/> is the attachments directory: HEIC conversion writes a short-lived
    /// <c>&lt;source name&gt;.jpg</c> there, so keep downloads in a separate directory.
    /// </summary>
    public async Task<string> NormalizeAsync(string src, string destDir, Item item, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var stem = OutputStem(item);

        if (item.Kind == ItemKind.Video)
        {
            if (item.SizeBytes > videoCapBytes)
            {
                throw new OversizeException(
                    $"Video '{item.Name}' is {item.SizeBytes:N0} bytes, over the {videoCapBytes:N0} byte cap.");
            }

            return await CopyAsync(src, FreePath(destDir, stem, Path.GetExtension(src).ToLowerInvariant()), ct);
        }

        switch (item.Mime.ToLowerInvariant())
        {
            case "image/gif":
                return await CopyAsync(src, FreePath(destDir, stem, ".gif"), ct);

            case "image/jpeg":
            case "image/png":
                return WriteUprightJpeg(src, FreePath(destDir, stem, ".jpg"), ct);

            case "image/heic":
            case "image/heif":
                var intermediate = await heic.ToJpegAsync(src, destDir, ct);
                var output = FreePath(destDir, stem, ".jpg", intermediate);
                try
                {
                    return WriteUprightJpeg(intermediate, output, ct);
                }
                finally
                {
                    if (!string.Equals(intermediate, output, StringComparison.Ordinal))
                    {
                        File.Delete(intermediate);
                    }
                }

            default:
                throw new UnsupportedMediaException($"'{item.Name}' has unsupported media type '{item.Mime}'.");
        }
    }

    /// <summary>
    /// Decode → apply the EXIF origin so pixels are upright → scale down to the long-edge cap (never up)
    /// → encode as JPEG. The re-encode writes only pixel data, so EXIF, XMP and GPS are gone.
    /// </summary>
    private string WriteUprightJpeg(string inputPath, string outputPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var codec = SKCodec.Create(inputPath)
            ?? throw new UnsupportedMediaException($"Cannot decode '{inputPath}' as an image.");
        using var source = SKBitmap.Decode(codec)
            ?? throw new UnsupportedMediaException($"Cannot decode the pixels of '{inputPath}'.");

        var orient = OriginMatrix(codec.EncodedOrigin, source.Width, source.Height, out var uprightWidth, out var uprightHeight);
        var scale = Math.Min(1.0, (double)cfg.MaxLongEdge / Math.Max(uprightWidth, uprightHeight));
        var outputWidth = Math.Max(1, (int)Math.Round(uprightWidth * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(uprightHeight * scale));

        // The output is tagged sRGB so Skia converts wide-gamut sources (iPhones shoot Display P3) while
        // drawing; without this the P3 numbers would be written out untagged and look washed out.
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque, srgb);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Cannot allocate a {outputWidth}x{outputHeight} surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);   // PNG transparency lands on white rather than black
        canvas.Scale((float)scale);
        canvas.Concat(in orient);
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        ct.ThrowIfCancellationRequested();
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, cfg.JpegQuality)
            ?? throw new InvalidOperationException($"JPEG encoding of '{inputPath}' failed.");
        using (var file = File.Create(outputPath))
        {
            data.SaveTo(file);
        }

        return outputPath;
    }

    /// <summary>
    /// The matrix that maps stored pixels to display orientation (Skia's SkEncodedOriginToMatrix),
    /// plus the upright size, which swaps width and height for the four transposing origins.
    /// </summary>
    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h, out int uprightWidth, out int uprightHeight)
    {
        uprightWidth = w;
        uprightHeight = h;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                return new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1);
            case SKEncodedOrigin.BottomRight:
                return new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1);
            case SKEncodedOrigin.BottomLeft:
                return new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1);
            case SKEncodedOrigin.LeftTop:
                (uprightWidth, uprightHeight) = (h, w);
                return new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1);
            case SKEncodedOrigin.RightTop:
                (uprightWidth, uprightHeight) = (h, w);
                return new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1);
            case SKEncodedOrigin.RightBottom:
                (uprightWidth, uprightHeight) = (h, w);
                return new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1);
            case SKEncodedOrigin.LeftBottom:
                (uprightWidth, uprightHeight) = (h, w);
                return new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1);
            default:
                return SKMatrix.Identity;
        }
    }

    private static string OutputStem(Item item)
    {
        var prefix = item.Id.Length <= _idPrefixLength ? item.Id : item.Id[.._idPrefixLength];
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(prefix.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '-' : c).ToArray());
        return $"{item.CapturedAt:yyyy-MM-dd_HHmmss}_{safe}";
    }

    /// <summary>
    /// <c>&lt;stem&gt;&lt;ext&gt;</c> in <paramref name="destDir"/>, or the first free <c>&lt;stem&gt;-N&lt;ext&gt;</c>
    /// (N = 2, 3, ...) when that is taken. <paramref name="reusable"/> is a path that may be written over,
    /// so the HEIC intermediate does not collide with itself.
    /// </summary>
    private static string FreePath(string destDir, string stem, string ext, string? reusable = null)
    {
        var path = Path.Combine(destDir, stem + ext);
        for (var n = 2; File.Exists(path) && !string.Equals(path, reusable, StringComparison.Ordinal); n++)
        {
            path = Path.Combine(destDir, $"{stem}-{n}{ext}");
        }

        return path;
    }

    private static async Task<string> CopyAsync(string src, string dest, CancellationToken ct)
    {
        await using var input = File.OpenRead(src);
        await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
        await input.CopyToAsync(output, ct);
        return dest;
    }
}
