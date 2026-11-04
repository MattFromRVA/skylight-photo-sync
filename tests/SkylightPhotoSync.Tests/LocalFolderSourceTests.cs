using System.Security.Cryptography;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class LocalFolderSourceTests
{
    private static readonly Window _unbounded = new(Since: null);

    // tiny.jpg and tiny_rot6.heic carry EXIF DateTimeOriginal 2020:01:02 03:04:05 (no zone, so
    // it is read as local wall-clock time); the other fixtures have no EXIF.
    private static readonly DateTime _exifCapture = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);

    private static readonly DateTime _y2019 = new(2019, 5, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _y2021 = new(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _y2022 = new(2022, 7, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task All_mode_lists_oldest_first()
    {
        using var root = new TempDir();
        root.AddFixture("tiny.png", "newest.png", _y2021);
        root.AddFixture("tiny.gif", "oldest.gif", _y2019);
        root.AddFixture("tiny.jpg", "album/exif-2020.jpg", _y2022);   // EXIF beats last-write time

        var items = await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None);

        Assert.Equal(["oldest.gif", "exif-2020.jpg", "newest.png"], items.Select(i => i.Name));
    }

    [Fact]
    public async Task All_mode_includes_videos_and_heic_and_ignores_other_files()
    {
        using var root = new TempDir();
        root.AddFixture("tiny.mp4", "clip.mp4", _y2019);
        root.AddFixture("tiny_rot6.heic", "phone.heic", _y2021);
        await File.WriteAllTextAsync(Path.Combine(root.Path, "notes.txt"), "not media");

        var items = await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None);

        Assert.Collection(
            items,
            clip =>
            {
                Assert.Equal(ItemKind.Video, clip.Kind);
                Assert.Equal("video/mp4", clip.Mime);
            },
            phone =>
            {
                Assert.Equal(ItemKind.Photo, phone.Kind);
                Assert.Equal("image/heic", phone.Mime);
            });
    }

    [Fact]
    public async Task Album_mode_only_subfolder()
    {
        using var root = new TempDir();
        root.AddFixture("tiny.png", "outside.png", _y2019);
        root.AddFixture("tiny.jpg", "Skylight/inside.jpg", _y2021);
        root.AddFixture("tiny.gif", "Other/elsewhere.gif", _y2021);

        var items = await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(_unbounded, SourceMode.Album, "Skylight", CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("inside.jpg", item.Name);
    }

    [Fact]
    public async Task Album_mode_requires_album_name()
    {
        using var root = new TempDir();
        var source = new LocalFolderSource(root.Path);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.ListCandidatesAsync(_unbounded, SourceMode.Album, albumName: null, CancellationToken.None));
    }

    [Fact]
    public async Task Album_mode_fails_when_album_folder_is_missing()
    {
        using var root = new TempDir();
        var source = new LocalFolderSource(root.Path);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            source.ListCandidatesAsync(_unbounded, SourceMode.Album, "Missing", CancellationToken.None));
    }

    [Fact]
    public async Task Window_filters_by_captured_at()
    {
        using var root = new TempDir();
        root.AddFixture("tiny.gif", "old.gif", _y2019);
        root.AddFixture("tiny.jpg", "exif-2020.jpg", _y2022);   // captured 2020 per EXIF
        root.AddFixture("tiny.png", "new.png", _y2021);
        var since = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var items = await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(new Window(since), SourceMode.All, albumName: null, CancellationToken.None);

        Assert.Equal(["new.png"], items.Select(i => i.Name));
    }

    [Fact]
    public async Task CapturedAt_comes_from_exif_DateTimeOriginal_when_present()
    {
        using var root = new TempDir();
        root.AddFixture("tiny.jpg", "exif.jpg", _y2022);

        var item = Assert.Single(await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None));

        Assert.Equal(new DateTimeOffset(_exifCapture), item.CapturedAt);
    }

    [Fact]
    public async Task CapturedAt_falls_back_to_last_write_time_without_exif()
    {
        using var root = new TempDir();
        root.AddFixture("tiny.png", "plain.png", _y2021);

        var item = Assert.Single(await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None));

        Assert.Equal(new DateTimeOffset(_y2021), item.CapturedAt);
    }

    [Fact]
    public async Task Items_carry_size_md5_and_mime()
    {
        using var root = new TempDir();
        var path = root.AddFixture("tiny.jpg", "photo.jpg", _y2021);
        var bytes = await File.ReadAllBytesAsync(path);

        var item = Assert.Single(await new LocalFolderSource(root.Path)
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None));

        Assert.Equal(bytes.LongLength, item.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), item.Md5);
        Assert.Equal("image/jpeg", item.Mime);
        Assert.Equal(ItemKind.Photo, item.Kind);
    }

    [Fact]
    public async Task Download_copies_file()
    {
        using var root = new TempDir();
        using var dest = new TempDir();
        var original = root.AddFixture("tiny.jpg", "photo.jpg", _y2021);
        var source = new LocalFolderSource(root.Path);
        var item = Assert.Single(await source
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None));
        var destDir = Path.Combine(dest.Path, "downloads");   // does not exist yet

        var downloaded = await source.DownloadAsync(item, destDir, CancellationToken.None);

        Assert.Equal(Path.Combine(destDir, "photo.jpg"), downloaded);
        Assert.Equal(await File.ReadAllBytesAsync(original), await File.ReadAllBytesAsync(downloaded));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task Download_keeps_same_named_files_from_different_folders_apart()
    {
        using var root = new TempDir();
        using var dest = new TempDir();
        var first = root.AddFixture("tiny.jpg", "a/IMG_0001.jpg", _y2019);
        var second = root.AddFixture("tiny.png", "b/IMG_0001.jpg", _y2021);
        var source = new LocalFolderSource(root.Path);
        var items = await source
            .ListCandidatesAsync(_unbounded, SourceMode.All, albumName: null, CancellationToken.None);

        var downloaded = new List<string>();
        foreach (var item in items)
        {
            downloaded.Add(await source.DownloadAsync(item, dest.Path, CancellationToken.None));
        }

        Assert.Equal(2, downloaded.Distinct().Count());
        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(downloaded[0]));
        Assert.Equal(await File.ReadAllBytesAsync(second), await File.ReadAllBytesAsync(downloaded[1]));
    }
}
