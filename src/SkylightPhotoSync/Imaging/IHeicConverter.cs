namespace SkylightPhotoSync.Imaging;

/// <summary>
/// Converts a HEIC/HEIF file to an intermediate JPEG. .NET imaging libraries cannot decode HEIC,
/// so on macOS this is done by <c>sips</c>; the interface lets Linux CI fake it.
/// </summary>
public interface IHeicConverter
{
    /// <summary>
    /// Writes <c>&lt;destDir&gt;/&lt;name&gt;.jpg</c> from <paramref name="heicPath"/> (EXIF preserved)
    /// and returns that path. Throws <see cref="UnsupportedMediaException"/> when the file cannot be converted.
    /// </summary>
    Task<string> ToJpegAsync(string heicPath, string destDir, CancellationToken ct);
}
