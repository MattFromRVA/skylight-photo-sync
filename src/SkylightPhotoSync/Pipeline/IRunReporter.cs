using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Pipeline;

/// <summary>
/// Hears about each email and each skipped item while <see cref="SyncPipeline.RunAsync"/> works, so the
/// caller can list them. <see cref="RunResult"/> carries only the totals.
/// </summary>
public interface IRunReporter
{
    /// <summary>
    /// One email: called after the SMTP server accepted it, or in a dry run, when it would have been sent.
    /// </summary>
    void Email(EmailReport email);

    /// <summary>One item recorded as skipped, at most once per item per run.</summary>
    void Skipped(Item item, SkipReason reason, string detail);
}

/// <summary>One email of a run.</summary>
/// <param name="Number">1 for the run's first email, counting up across recipients.</param>
/// <param name="Recipient">The address the email goes to.</param>
/// <param name="Items">The attachments, oldest-first.</param>
/// <param name="TotalBytes">Sum of the attachment sizes.</param>
public sealed record EmailReport(int Number, string Recipient, IReadOnlyList<EmailItemReport> Items, long TotalBytes);

/// <summary>One attachment of an <see cref="EmailReport"/>.</summary>
/// <param name="Item">The source item.</param>
/// <param name="Bytes">Size of the processed file that is attached, which can differ from the original's.</param>
/// <param name="Recycled">True when rotation sends an already-sent item again.</param>
public sealed record EmailItemReport(Item Item, long Bytes, bool Recycled);
