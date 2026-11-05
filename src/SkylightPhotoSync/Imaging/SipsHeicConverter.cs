using SkylightPhotoSync.Infrastructure;

namespace SkylightPhotoSync.Imaging;

/// <summary>
/// HEIC → JPEG through the macOS <c>sips</c> tool, at full quality so the only lossy step is the
/// final re-encode in <see cref="ImageNormalizer"/>.
/// </summary>
public sealed class SipsHeicConverter(IProcessRunner runner) : IHeicConverter
{
    public async Task<string> ToJpegAsync(string heicPath, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var output = Path.Combine(destDir, Path.GetFileNameWithoutExtension(heicPath) + ".jpg");

        var result = await runner.RunAsync(
            "sips",
            ["-s", "format", "jpeg", "-s", "formatOptions", "100", heicPath, "--out", output],
            ct);

        if (result.ExitCode != 0)
        {
            throw new UnsupportedMediaException(
                $"sips could not convert '{heicPath}' to JPEG (exit code {result.ExitCode}): {result.StdErr.Trim()}");
        }

        return output;
    }
}
