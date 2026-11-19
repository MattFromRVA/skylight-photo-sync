using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Pipeline;

/// <summary>
/// Hears about each delivery and each skipped item while <see cref="SyncPipeline.RunAsync"/> works, so the
/// caller can list them. <see cref="RunResult"/> carries only the totals.
/// </summary>
public interface IRunReporter
{
    /// <summary>
    /// One delivery (an email, or one photo uploaded to one frame): called after the other side accepted
    /// it, or in a dry run, when it would have been sent.
    /// </summary>
    void Delivered(DeliveryReport delivery);

    /// <summary>One item recorded as skipped, at most once per item per run.</summary>
    void Skipped(Item item, SkipReason reason, string detail);
}

/// <summary>One delivery of a run.</summary>
/// <param name="Number">1 for the run's first delivery, counting up across destinations.</param>
/// <param name="Destination">The address the email goes to, or the name of the frame the photo is uploaded to.</param>
/// <param name="Items">The photos, oldest-first.</param>
/// <param name="TotalBytes">Sum of the file sizes.</param>
public sealed record DeliveryReport(int Number, string Destination, IReadOnlyList<DeliveryItemReport> Items, long TotalBytes);

/// <summary>One photo of a <see cref="DeliveryReport"/>.</summary>
/// <param name="Item">The source item.</param>
/// <param name="Bytes">Size of the processed file that is sent, which can differ from the original's.</param>
/// <param name="Recycled">True when rotation sends an already-sent item again.</param>
public sealed record DeliveryItemReport(Item Item, long Bytes, bool Recycled);
