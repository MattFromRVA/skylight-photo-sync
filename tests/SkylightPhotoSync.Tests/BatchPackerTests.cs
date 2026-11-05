using SkylightPhotoSync.Batching;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class BatchPackerTests : IDisposable
{
    private static readonly DateTimeOffset _epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Splits_on_attachment_count()
    {
        var prepared = Enumerable.Range(0, 13).Select(i => Prepared(i, sizeBytes: 100)).ToList();

        var result = BatchPacker.Pack(prepared, maxAttachments: 12, maxTotalBytes: 1_000_000);

        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.Batches.Count);
        Assert.Equal(12, result.Batches[0].Items.Count);
        Assert.Single(result.Batches[1].Items);
        Assert.Equal(1200, result.Batches[0].TotalBytes);
        Assert.Equal(100, result.Batches[1].TotalBytes);
    }

    [Fact]
    public void Splits_on_total_bytes()
    {
        // 400 + 400 fit in 1000; adding the third (400) would make 1200, so it starts batch two.
        var prepared = Enumerable.Range(0, 5).Select(i => Prepared(i, sizeBytes: 400)).ToList();

        var result = BatchPacker.Pack(prepared, maxAttachments: 12, maxTotalBytes: 1000);

        Assert.Empty(result.Skipped);
        Assert.Equal(3, result.Batches.Count);
        Assert.Equal(new[] { 2, 2, 1 }, result.Batches.Select(b => b.Items.Count).ToArray());
        Assert.Equal(new long[] { 800, 800, 400 }, result.Batches.Select(b => b.TotalBytes).ToArray());
    }

    [Fact]
    public void Oversize_single_item_is_skipped_not_blocking()
    {
        var prepared = new List<(Item Item, string File)>
        {
            Prepared(0, sizeBytes: 100),
            Prepared(1, sizeBytes: 5000),
            Prepared(2, sizeBytes: 100),
        };

        var result = BatchPacker.Pack(prepared, maxAttachments: 12, maxTotalBytes: 1000);

        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("item-1", skipped.Item.Id);
        Assert.Equal(SkipReason.Oversize, skipped.Reason);

        var batch = Assert.Single(result.Batches);
        Assert.Equal(new[] { "item-0", "item-2" }, batch.Items.Select(i => i.Id).ToArray());
        Assert.Equal(200, batch.TotalBytes);
    }

    [Fact]
    public void Preserves_order()
    {
        var prepared = Enumerable.Range(0, 7).Select(i => Prepared(i, sizeBytes: 10)).ToList();

        var result = BatchPacker.Pack(prepared, maxAttachments: 3, maxTotalBytes: 1000);

        var ids = result.Batches.SelectMany(b => b.Items).Select(i => i.Id).ToArray();
        Assert.Equal(prepared.Select(p => p.Item.Id).ToArray(), ids);

        foreach (var batch in result.Batches)
        {
            Assert.Equal(batch.Items.Count, batch.Files.Count);
            Assert.Equal(prepared.Where(p => batch.Items.Contains(p.Item)).Select(p => p.File).ToArray(), batch.Files.ToArray());
        }
    }

    [Fact]
    public void Batch_id_is_twelve_hex_chars_and_unique()
    {
        var prepared = Enumerable.Range(0, 4).Select(i => Prepared(i, sizeBytes: 10)).ToList();

        var result = BatchPacker.Pack(prepared, maxAttachments: 2, maxTotalBytes: 1000);

        Assert.All(result.Batches, b => Assert.Matches("^[0-9a-f]{12}$", b.Id));
        Assert.Equal(result.Batches.Count, result.Batches.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void Empty_input_gives_no_batches()
    {
        var result = BatchPacker.Pack([], maxAttachments: 12, maxTotalBytes: 1000);

        Assert.Empty(result.Batches);
        Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(12, 0)]
    public void Rejects_non_positive_limits(int maxAttachments, long maxTotalBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BatchPacker.Pack([], maxAttachments, maxTotalBytes));
    }

    /// <summary>Writes a file of exactly <paramref name="sizeBytes"/> bytes and pairs it with an item.</summary>
    private (Item Item, string File) Prepared(int index, int sizeBytes)
    {
        var path = Path.Combine(_dir.Path, $"photo-{index}.jpg");
        File.WriteAllBytes(path, new byte[sizeBytes]);
        var item = new Item(
            Id: $"item-{index}",
            Name: $"photo-{index}.jpg",
            Kind: ItemKind.Photo,
            CapturedAt: _epoch.AddMinutes(index),
            SizeBytes: sizeBytes * 2, // deliberately differs from the file on disk: packing must use the real file length
            Md5: null,
            Mime: "image/jpeg");
        return (item, path);
    }
}
