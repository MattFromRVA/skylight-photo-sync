using SkiaSharp;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class ImageNormalizerTests
{
    private static readonly ImageConfig _cfg = new(MaxLongEdge: 2048, JpegQuality: 88);
    private static readonly DateTimeOffset _captured = new(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5));

    // All fixtures are 64x48 (see scripts/make-fixtures.sh); tiny_rot6.heic carries EXIF orientation 6.
    private const int _fixtureWidth = 64;
    private const int _fixtureHeight = 48;

    [MacOnlyFact]
    public async Task Heic_rot6_becomes_upright_jpeg_without_exif()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new SipsHeicConverter(new ProcessRunner()), _cfg);
        var item = Photo("tiny_rot6.heic", "image/heic");

        var output = await normalizer.NormalizeAsync(Fixtures.Image("tiny_rot6.heic"), dest.Path, item, CancellationToken.None);

        Assert.EndsWith(".jpg", output);
        Assert.Equal((_fixtureHeight, _fixtureWidth), ReadPixelSize(output));   // rotated 90°: width and height swap
        Assert.False(HasApp1Segment(output), "output JPEG still carries an APP1 (Exif/XMP) segment");
        Assert.Equal([output], Directory.GetFiles(dest.Path));                  // the sips intermediate is cleaned up
    }

    [Fact]
    public async Task Heic_path_uses_converter()
    {
        using var dest = new TempDir();
        var heic = new FakeHeicConverter();
        var normalizer = new ImageNormalizer(heic, _cfg);
        var item = Photo("tiny_rot6.heic", "image/heic");
        var src = Fixtures.Image("tiny_rot6.heic");

        var output = await normalizer.NormalizeAsync(src, dest.Path, item, CancellationToken.None);

        Assert.Equal([(src, dest.Path)], heic.Calls);
        Assert.EndsWith(".jpg", output);
        Assert.Equal((_fixtureWidth, _fixtureHeight), ReadPixelSize(output));
        Assert.False(HasApp1Segment(output));
        Assert.Equal([output], Directory.GetFiles(dest.Path));
    }

    [Fact]
    public async Task Jpeg_orientation_6_is_rotated_clockwise()
    {
        using var work = new TempDir();
        var src = Path.Combine(work.Path, "rot6.jpg");
        WriteJpeg(src, 32, 16, leftColor: SKColors.Red, rightColor: SKColors.Blue, exifOrientation: 6);
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);

        var output = await normalizer.NormalizeAsync(src, work.Path, Photo("rot6.jpg", "image/jpeg"), CancellationToken.None);

        // Orientation 6 means the stored image must be turned 90° clockwise to display, so the
        // left (red) half ends up on top and the right (blue) half at the bottom.
        Assert.Equal((16, 32), ReadPixelSize(output));
        using var bitmap = SKBitmap.Decode(output);
        AssertRoughly(SKColors.Red, bitmap.GetPixel(8, 8));
        AssertRoughly(SKColors.Blue, bitmap.GetPixel(8, 24));
    }

    [Fact]
    public async Task Png_to_jpeg()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);

        var output = await normalizer.NormalizeAsync(
            Fixtures.Image("tiny.png"), dest.Path, Photo("tiny.png", "image/png"), CancellationToken.None);

        Assert.EndsWith(".jpg", output);
        Assert.True(IsJpeg(output), "output does not start with the JPEG SOI marker");
        Assert.Equal((_fixtureWidth, _fixtureHeight), ReadPixelSize(output));
    }

    [Fact]
    public async Task Large_image_resized_to_max_long_edge()
    {
        using var work = new TempDir();
        var src = Path.Combine(work.Path, "big.jpg");
        WriteJpeg(src, 4000, 3000);
        var dest = Path.Combine(work.Path, "out");
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);

        var output = await normalizer.NormalizeAsync(src, dest, Photo("big.jpg", "image/jpeg"), CancellationToken.None);

        Assert.Equal((2048, 1536), ReadPixelSize(output));
    }

    [Fact]
    public async Task Small_image_not_upscaled()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);

        var output = await normalizer.NormalizeAsync(
            Fixtures.Image("tiny.jpg"), dest.Path, Photo("tiny.jpg", "image/jpeg"), CancellationToken.None);

        Assert.Equal((_fixtureWidth, _fixtureHeight), ReadPixelSize(output));
    }

    [Fact]
    public async Task Gif_passthrough()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);
        var src = Fixtures.Image("tiny.gif");

        var output = await normalizer.NormalizeAsync(src, dest.Path, Photo("tiny.gif", "image/gif"), CancellationToken.None);

        Assert.Equal("2020-01-02_030405_abcdefgh.gif", Path.GetFileName(output));
        Assert.Equal(await File.ReadAllBytesAsync(src), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Video_within_cap_copied_unchanged()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg, videoCapBytes: 1_000);
        var src = Fixtures.Image("tiny.mp4");
        var item = Video("tiny.mp4", sizeBytes: 959);

        var output = await normalizer.NormalizeAsync(src, dest.Path, item, CancellationToken.None);

        Assert.Equal("2020-01-02_030405_abcdefgh.mp4", Path.GetFileName(output));
        Assert.Equal(await File.ReadAllBytesAsync(src), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Video_over_cap_throws_oversize()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);
        var item = Video("tiny.mp4", sizeBytes: 22_000_001);

        await Assert.ThrowsAsync<OversizeException>(() =>
            normalizer.NormalizeAsync(Fixtures.Image("tiny.mp4"), dest.Path, item, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(dest.Path));
    }

    [Fact]
    public async Task Unsupported_media_throws()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);
        var item = Photo("tiny.jpg", "image/webp");

        await Assert.ThrowsAsync<UnsupportedMediaException>(() =>
            normalizer.NormalizeAsync(Fixtures.Image("tiny.jpg"), dest.Path, item, CancellationToken.None));
    }

    [Fact]
    public async Task Output_filename_pattern()
    {
        using var dest = new TempDir();
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);
        var item = Photo("tiny.jpg", "image/jpeg") with
        {
            Id = "AbCdEfGh1234567890",
            CapturedAt = new DateTimeOffset(2021, 12, 31, 23, 59, 7, TimeSpan.Zero),
        };

        var output = await normalizer.NormalizeAsync(Fixtures.Image("tiny.jpg"), dest.Path, item, CancellationToken.None);

        Assert.Equal(Path.Combine(dest.Path, "2021-12-31_235907_AbCdEfGh.jpg"), output);
    }

    [Fact]
    public async Task Same_second_items_get_distinct_names()
    {
        using var work = new TempDir();
        using var dest = new TempDir();
        var red = Path.Combine(work.Path, "red.jpg");
        var blue = Path.Combine(work.Path, "blue.jpg");
        WriteJpeg(red, 32, 16, leftColor: SKColors.Red);
        WriteJpeg(blue, 32, 16, leftColor: SKColors.Blue);
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), _cfg);
        var first = Photo("red.jpg", "image/jpeg") with { Id = "Skylight/red.jpg" };
        var second = Photo("blue.jpg", "image/jpeg") with { Id = "Skylight/blue.jpg" };

        var firstOutput = await normalizer.NormalizeAsync(red, dest.Path, first, CancellationToken.None);
        var secondOutput = await normalizer.NormalizeAsync(blue, dest.Path, second, CancellationToken.None);

        Assert.Equal(Path.Combine(dest.Path, "2020-01-02_030405_Skylight.jpg"), firstOutput);
        Assert.Equal(Path.Combine(dest.Path, "2020-01-02_030405_Skylight-2.jpg"), secondOutput);
        Assert.True(File.Exists(firstOutput));
        Assert.True(File.Exists(secondOutput));
        Assert.NotEqual(File.ReadAllBytes(firstOutput), File.ReadAllBytes(secondOutput));
    }

    private static Item Photo(string name, string mime) =>
        new(Id: "abcdefghijkl", Name: name, Kind: ItemKind.Photo, CapturedAt: _captured,
            SizeBytes: 0, Md5: null, Mime: mime);   // size only matters for videos

    private static Item Video(string name, long sizeBytes) =>
        new(Id: "abcdefghijkl", Name: name, Kind: ItemKind.Video, CapturedAt: _captured,
            SizeBytes: sizeBytes, Md5: null, Mime: "video/mp4");

    private static (int Width, int Height) ReadPixelSize(string path)
    {
        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        return (codec.Info.Width, codec.Info.Height);
    }

    private static bool IsJpeg(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
    }

    /// <summary>Walks the JPEG marker segments up to SOS and reports whether an APP1 (Exif/XMP) segment is present.</summary>
    private static bool HasApp1Segment(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 2;   // skip SOI
        while (pos + 4 <= bytes.Length && bytes[pos] == 0xFF)
        {
            var marker = bytes[pos + 1];
            if (marker == 0xDA)
            {
                return false;   // start of scan: no more metadata segments
            }

            if (marker == 0xE1)
            {
                return true;
            }

            var length = (bytes[pos + 2] << 8) | bytes[pos + 3];
            pos += 2 + length;
        }

        return false;
    }

    private static void WriteJpeg(
        string path, int width, int height, SKColor? leftColor = null, SKColor? rightColor = null, int exifOrientation = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(leftColor ?? SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = rightColor ?? leftColor ?? SKColors.CornflowerBlue };
            canvas.DrawRect(width / 2f, 0, width / 2f, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = data.ToArray();
        using var file = File.Create(path);
        file.Write(jpeg, 0, 2);   // SOI
        if (exifOrientation != 1)
        {
            file.Write(ExifApp1WithOrientation(exifOrientation));
        }

        file.Write(jpeg, 2, jpeg.Length - 2);
    }

    /// <summary>A minimal little-endian APP1 Exif segment whose only IFD0 entry is Orientation (0x0112).</summary>
    private static byte[] ExifApp1WithOrientation(int orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,     // TIFF header, IFD0 at offset 8
            0x01, 0x00,                                                   // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,               // tag 0x0112, SHORT, count 1
            (byte)orientation, 0x00, 0x00, 0x00,                          // value
            0x00, 0x00, 0x00, 0x00,                                       // no next IFD
        ];
        byte[] header = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00];
        var length = 2 + header.Length + tiff.Length;
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF), .. header, .. tiff];
    }

    private static void AssertRoughly(SKColor expected, SKColor actual)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) < 40
                && Math.Abs(expected.Green - actual.Green) < 40
                && Math.Abs(expected.Blue - actual.Blue) < 40,
            $"expected roughly {expected}, got {actual}");
    }

    /// <summary>Stands in for sips on Linux: "converts" by copying tiny.jpg to the sips output path.</summary>
    private sealed class FakeHeicConverter : IHeicConverter
    {
        public List<(string HeicPath, string DestDir)> Calls { get; } = [];

        public Task<string> ToJpegAsync(string heicPath, string destDir, CancellationToken ct)
        {
            Calls.Add((heicPath, destDir));
            Directory.CreateDirectory(destDir);
            var output = Path.Combine(destDir, Path.GetFileNameWithoutExtension(heicPath) + ".jpg");
            File.Copy(Fixtures.Image("tiny.jpg"), output, overwrite: true);
            return Task.FromResult(output);
        }
    }
}
