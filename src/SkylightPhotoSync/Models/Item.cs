namespace SkylightPhotoSync.Models;

/// <summary>
/// One candidate photo or video as reported by an <see cref="Sources.IPhotoSource"/>.
/// </summary>
/// <param name="Id">Source-specific stable identifier (Amazon node id, or a relative path).</param>
/// <param name="Name">Original file name, used for the attachment.</param>
/// <param name="Kind">Photo or video.</param>
/// <param name="CapturedAt">Capture date; items are processed oldest-first by this value.</param>
/// <param name="SizeBytes">Size of the original file.</param>
/// <param name="Md5">Lowercase hex MD5 of the original bytes when the source knows it.</param>
/// <param name="Mime">Media type of the original file, for example <c>image/jpeg</c>.</param>
public sealed record Item(
    string Id,
    string Name,
    ItemKind Kind,
    DateTimeOffset CapturedAt,
    long SizeBytes,
    string? Md5,
    string Mime);
