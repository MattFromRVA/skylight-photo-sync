using MimeKit;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Delivery;

/// <summary>
/// Uploads the photos through Skylight's app API (spec §6.11): one upload per photo, oldest first, to
/// the frames <c>[delivery] frames</c> names (every frame when it is empty). A photo is marked sent to a
/// frame, with the photo id Skylight returned for it, only after Skylight accepted the upload; that id
/// is what lets rotation delete the photo later, and sending an item again keeps the earlier id on
/// record too, so the earlier copy is pruned like any other old photo. Only frames rotation prunes
/// (every frame, or the one <c>[rotation] frame_name</c> names) take an item sent again. A network
/// failure or timeout talking to Skylight surfaces as a <see cref="SkylightApiException"/>.
/// </summary>
public sealed class SkylightApiDelivery(
    DeliveryConfig cfg, RotationConfig rotation, ISkylightClient client, StateStore store, Func<DateTimeOffset> now)
    : IPhotoDelivery
{
    /// <summary>Prefix of the <c>recipient</c> recorded for a frame; the frame's Skylight id follows.</summary>
    public const string RecipientPrefix = "skylight:frame:";

    private IReadOnlyList<SkylightFrame>? _frames;

    /// <summary>The <c>recipient</c> recorded with items uploaded to the frame.</summary>
    public static string RecipientFor(string frameId) => RecipientPrefix + frameId;

    /// <summary>
    /// The frames to upload to; the first call signs in and lists them. Throws
    /// <see cref="SkylightSignInException"/> when Skylight refuses the sign-in and
    /// <see cref="SkylightApiException"/> when the account has no frame to send to.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryDestination>> DestinationsAsync(CancellationToken ct) =>
        (await FramesAsync(ct)).Select(Destination).ToList();

    public async Task<DeliveryResult> DeliverAsync(IReadOnlyList<(Item Item, string File)> prepared, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var batches = new List<DeliveredBatch>();
        try
        {
            var frames = await FramesAsync(ct);
            foreach (var (item, file) in prepared)
            {
                ct.ThrowIfCancellationRequested();

                // An item every frame already has is one rotation is sending round again: it goes only to the
                // frames rotation prunes, so no frame collects copies nothing deletes.
                var targets = frames.Where(frame => !store.WasSentTo(item, RecipientFor(frame.Id))).ToList();
                if (targets.Count == 0)
                {
                    targets = frames.Where(TakesRecycled).ToList();
                    if (targets.Count == 0)
                    {
                        continue;
                    }
                }

                var bytes = new FileInfo(file).Length;
                if (dryRun)
                {
                    batches.AddRange(targets.Select(frame => Batch(frame, item, bytes, messageId: null)));
                    continue;
                }

                var uploads = await SkylightAsync(
                    () => client.UploadPhotoAsync(
                        targets.Select(frame => frame.Id).ToList(), file, MimeTypes.GetMimeType(Path.GetFileName(file)), ct),
                    ct);
                var uploadId = Guid.NewGuid().ToString("N")[..12];
                var unconfirmed = new List<SkylightFrame>();
                foreach (var frame in targets)
                {
                    var messageId = uploads.FirstOrDefault(upload => upload.FrameId == frame.Id)?.MessageId;
                    if (string.IsNullOrWhiteSpace(messageId))
                    {
                        unconfirmed.Add(frame);
                        continue;
                    }

                    store.MarkSent([item], RecipientFor(frame.Id), uploadId, now(), messageId);
                    batches.Add(Batch(frame, item, bytes, messageId));
                }

                if (unconfirmed.Count > 0)
                {
                    // Without the photo's id nothing proves it is on that frame, so it stays unsent and is tried again.
                    throw new SkylightApiException(
                        $"Skylight accepted the upload of {item.Name} but returned no photo id for "
                        + $"{string.Join(", ", unconfirmed.Select(frame => $"\"{frame.Name}\""))}.");
                }
            }
        }
        catch (Exception ex)
        {
            return new DeliveryResult(batches, [], ex);
        }

        return new DeliveryResult(batches, []);
    }

    private DeliveryDestination Destination(SkylightFrame frame) => new(RecipientFor(frame.Id), frame.Name, TakesRecycled(frame));

    private bool TakesRecycled(SkylightFrame frame) =>
        rotation.Enabled && (rotation.FrameName is null || Named(frame, rotation.FrameName));

    private DeliveredBatch Batch(SkylightFrame frame, Item item, long bytes, string? messageId) =>
        new(Destination(frame), [new DeliveredItem(item, bytes, messageId)], bytes);

    private async Task<IReadOnlyList<SkylightFrame>> FramesAsync(CancellationToken ct) =>
        _frames ??= Select(await SkylightAsync(() => client.ListFramesAsync(ct), ct));

    /// <summary>
    /// Runs one Skylight call, turning a network failure or an HTTP timeout into a
    /// <see cref="SkylightApiException"/> so the run reports it like any other refused upload.
    /// </summary>
    private static async Task<T> SkylightAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new SkylightApiException($"Could not reach Skylight: {ex.Message}", ex);
        }
    }

    private IReadOnlyList<SkylightFrame> Select(IReadOnlyList<SkylightFrame> all)
    {
        if (all.Count == 0)
        {
            throw new SkylightApiException("The Skylight account has no frames to upload to.");
        }

        if (cfg.Frames.Count == 0)
        {
            return all;
        }

        var missing = cfg.Frames
            .Where(name => !all.Any(frame => Named(frame, name)))
            .Select(name => $"\"{name}\"")
            .ToList();
        if (missing.Count > 0)
        {
            throw new SkylightApiException(
                $"[delivery] frames names {string.Join(", ", missing)}, but the Skylight account has no frame called that. "
                + $"Its frames: {string.Join(", ", all.Select(frame => $"\"{frame.Name}\""))}.");
        }

        return all.Where(frame => cfg.Frames.Any(name => Named(frame, name))).ToList();
    }

    private static bool Named(SkylightFrame frame, string name) =>
        string.Equals(frame.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
}
