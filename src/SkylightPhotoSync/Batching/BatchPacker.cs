using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Batching;

/// <summary>What <see cref="BatchPacker.Pack"/> made of the prepared files.</summary>
/// <param name="Batches">Email-sized batches, in input order.</param>
/// <param name="Skipped">Items that could not be placed in any batch, with the reason.</param>
public sealed record PackResult(
    IReadOnlyList<Batch> Batches,
    IReadOnlyList<(Item Item, SkipReason Reason)> Skipped);

/// <summary>Packs processed files into batches that fit one email each.</summary>
public static class BatchPacker
{
    /// <summary>
    /// Groups <paramref name="prepared"/> (oldest-first) into batches of at most
    /// <paramref name="maxAttachments"/> files and <paramref name="maxTotalBytes"/> bytes,
    /// measured from each file's length on disk. A batch is closed as soon as the next file
    /// would exceed either limit, so order is preserved. A file that is alone larger than
    /// <paramref name="maxTotalBytes"/> is reported as <see cref="SkipReason.Oversize"/> and
    /// never blocks the items after it.
    /// </summary>
    public static PackResult Pack(
        IReadOnlyList<(Item Item, string File)> prepared,
        int maxAttachments,
        long maxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttachments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalBytes);

        var batches = new List<Batch>();
        var skipped = new List<(Item Item, SkipReason Reason)>();

        var items = new List<Item>();
        var files = new List<string>();
        long totalBytes = 0;

        foreach (var (item, file) in prepared)
        {
            var length = new FileInfo(file).Length;
            if (length > maxTotalBytes)
            {
                skipped.Add((item, SkipReason.Oversize));
                continue;
            }

            if (items.Count == maxAttachments || totalBytes + length > maxTotalBytes)
            {
                Close();
            }

            items.Add(item);
            files.Add(file);
            totalBytes += length;
        }

        if (items.Count > 0)
        {
            Close();
        }

        return new PackResult(batches, skipped);

        void Close()
        {
            batches.Add(new Batch(NewId(), items, files, totalBytes));
            items = [];
            files = [];
            totalBytes = 0;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
