using System.Security.Cryptography;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Sources;

/// <summary>
/// An <see cref="IPhotoSource"/> over a folder on disk, so the pipeline can be exercised without
/// Amazon. <see cref="SourceMode.All"/> lists every image and video under the root (recursively);
/// <see cref="SourceMode.Album"/> lists the files under <c>root/&lt;albumName&gt;</c>.
/// </summary>
public sealed class LocalFolderSource(string root) : IPhotoSource
{
    private static readonly Dictionary<string, (ItemKind Kind, string Mime)> _mediaTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = (ItemKind.Photo, "image/jpeg"),
            [".jpeg"] = (ItemKind.Photo, "image/jpeg"),
            [".png"] = (ItemKind.Photo, "image/png"),
            [".gif"] = (ItemKind.Photo, "image/gif"),
            [".heic"] = (ItemKind.Photo, "image/heic"),
            [".heif"] = (ItemKind.Photo, "image/heif"),
            [".mp4"] = (ItemKind.Video, "video/mp4"),
            [".mov"] = (ItemKind.Video, "video/quicktime"),
        };

    private const int _exifHeaderBytes = 256 * 1024;

    private readonly string _root = Path.GetFullPath(root);

    public async Task<IReadOnlyList<Item>> ListCandidatesAsync(
        Window window, SourceMode mode, string? albumName, CancellationToken ct)
    {
        var folder = ResolveFolder(mode, albumName);
        var items = new List<Item>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!_mediaTypes.TryGetValue(Path.GetExtension(path), out var media))
            {
                continue;
            }

            await using var stream = File.OpenRead(path);
            var capturedAt = await ReadCapturedAtAsync(path, media.Kind, stream, ct);
            if (!window.Contains(capturedAt))
            {
                continue;
            }

            stream.Position = 0;
            items.Add(new Item(
                Id: Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/'),
                Name: Path.GetFileName(path),
                Kind: media.Kind,
                CapturedAt: capturedAt,
                SizeBytes: stream.Length,
                Md5: Convert.ToHexStringLower(await MD5.HashDataAsync(stream, ct)),
                Mime: media.Mime));
        }

        return items.OrderBy(i => i.CapturedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<string> DownloadAsync(Item item, string destDir, CancellationToken ct)
    {
        var source = Path.Combine(_root, item.Id);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, item.Id.Replace('/', '_'));
        await using var input = File.OpenRead(source);
        await using var output = File.Create(dest);
        await input.CopyToAsync(output, ct);
        return dest;
    }

    private string ResolveFolder(SourceMode mode, string? albumName)
    {
        if (mode == SourceMode.All)
        {
            return _root;
        }

        if (string.IsNullOrWhiteSpace(albumName))
        {
            throw new ArgumentException("Album mode needs an album name.", nameof(albumName));
        }

        var folder = Path.Combine(_root, albumName);
        return Directory.Exists(folder)
            ? folder
            : throw new DirectoryNotFoundException($"Album folder not found: {folder}");
    }

    /// <summary>
    /// EXIF <c>DateTimeOriginal</c> for JPEGs that carry one in their first
    /// <see cref="_exifHeaderBytes"/>, else the file's last-write time.
    /// EXIF dates have no zone, so they are read as wall-clock time on this machine.
    /// </summary>
    private static async Task<DateTimeOffset> ReadCapturedAtAsync(
        string path, ItemKind kind, FileStream stream, CancellationToken ct)
    {
        if (kind == ItemKind.Photo)
        {
            var header = new byte[_exifHeaderBytes];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (JpegExifReader.ReadDateTimeOriginal(header.AsSpan(0, read)) is { } exifDate)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(exifDate, DateTimeKind.Local));
            }
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
    }
}
