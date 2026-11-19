using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Delivery;

/// <summary>
/// How prepared photos reach the frame: by email (<see cref="EmailDelivery"/>) or through Skylight's
/// app API (<see cref="SkylightApiDelivery"/>), chosen by <c>[delivery] method</c>. A delivery records
/// each photo as sent in the state store itself, and only once the other side accepted it.
/// </summary>
public interface IPhotoDelivery
{
    /// <summary>
    /// Where this delivery sends to: the email recipients, or the Skylight frames. The run asks before
    /// it downloads anything, to tell which candidates are still new for at least one destination.
    /// </summary>
    Task<IReadOnlyList<DeliveryDestination>> DestinationsAsync(CancellationToken ct);

    /// <summary>
    /// Sends <paramref name="prepared"/> (oldest first) to every destination that does not have it yet.
    /// A photo that every destination already has is one rotation chose to send again, and goes to every
    /// destination that <see cref="DeliveryDestination.TakesRecycled"/>. A dry run sends and records nothing and reports what would have been sent.
    /// Never throws for a failed send: whatever stopped it comes back in <see cref="DeliveryResult.Failure"/>,
    /// next to what was sent before it.
    /// </summary>
    Task<DeliveryResult> DeliverAsync(IReadOnlyList<(Item Item, string File)> prepared, bool dryRun, CancellationToken ct);
}

/// <summary>One place photos are delivered to.</summary>
/// <param name="Key">
/// The <c>recipient</c> recorded with each sent item: the email address, or
/// <c>skylight:frame:&lt;id&gt;</c> for a frame reached through the API.
/// </param>
/// <param name="Name">What to call it in output: the email address, or the frame's name.</param>
/// <param name="TakesRecycled">
/// Whether photos rotation sends again go here. False for a frame reached through the API that
/// <c>[rotation]</c> does not prune, where nothing would ever delete the earlier copies.
/// </param>
public sealed record DeliveryDestination(string Key, string Name, bool TakesRecycled = true);

/// <summary>What <see cref="IPhotoDelivery.DeliverAsync"/> did.</summary>
/// <param name="Batches">Each accepted send in order: one email, or one photo uploaded to one frame.</param>
/// <param name="Skipped">Photos this delivery can never send, with the reason.</param>
/// <param name="Failure">
/// What stopped the delivery early (cancellation included), or <see langword="null"/> when everything
/// went out. <paramref name="Batches"/> and <paramref name="Skipped"/> still describe what happened before it.
/// </param>
public sealed record DeliveryResult(
    IReadOnlyList<DeliveredBatch> Batches,
    IReadOnlyList<DeliverySkip> Skipped,
    Exception? Failure = null);

/// <summary>One accepted send to one destination (or, in a dry run, one that would have been made).</summary>
/// <param name="TotalBytes">Sum of the file sizes.</param>
public sealed record DeliveredBatch(DeliveryDestination Destination, IReadOnlyList<DeliveredItem> Items, long TotalBytes);

/// <summary>One photo of a <see cref="DeliveredBatch"/>.</summary>
/// <param name="Bytes">Size of the processed file that was sent.</param>
/// <param name="MessageId">The Skylight photo id an API upload returned; <see langword="null"/> for email and in a dry run.</param>
public sealed record DeliveredItem(Item Item, long Bytes, string? MessageId);

/// <summary>A photo the delivery left out for good.</summary>
public sealed record DeliverySkip(Item Item, SkipReason Reason, string Detail);
