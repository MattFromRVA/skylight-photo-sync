using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Sources;

/// <summary>Where candidate photos come from: Amazon Photos, or a local folder in tests.</summary>
public interface IPhotoSource
{
    /// <summary>
    /// Lists the items in <paramref name="window"/>, oldest-first by <see cref="Item.CapturedAt"/>.
    /// In <see cref="SourceMode.Album"/> only items in <paramref name="albumName"/> are returned.
    /// </summary>
    /// <exception cref="AuthExpiredException">The source's credentials no longer work.</exception>
    Task<IReadOnlyList<Item>> ListCandidatesAsync(
        Window window, SourceMode mode, string? albumName, CancellationToken ct);

    /// <summary>
    /// Downloads <paramref name="item"/> into <paramref name="destDir"/> and returns the file path.
    /// The returned file name is unique within <paramref name="destDir"/> per item, because the
    /// pipeline downloads every item of a run into one temp dir and renames attachments itself.
    /// </summary>
    /// <exception cref="AuthExpiredException">The source's credentials no longer work.</exception>
    Task<string> DownloadAsync(Item item, string destDir, CancellationToken ct);
}

/// <summary>
/// The source rejected our credentials (for Amazon: the saved cookies expired). The pipeline
/// stops, sets <c>needs_login</c> and alerts once.
/// </summary>
public sealed class AuthExpiredException : Exception
{
    public AuthExpiredException()
        : base("The photo source rejected the saved credentials; run `skylight-sync login`.")
    {
    }

    public AuthExpiredException(string message)
        : base(message)
    {
    }

    public AuthExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
