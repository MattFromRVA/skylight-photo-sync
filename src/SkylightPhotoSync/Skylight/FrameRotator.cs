using SkylightPhotoSync.Config;
using SkylightPhotoSync.Delivery;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Skylight;

/// <summary>One group of this tool's photos on a frame: how many are there and what rotation did to them.</summary>
/// <param name="OnFrame">Photos of the group on the frame before pruning.</param>
/// <param name="Deleted">Photos of the group deleted this run.</param>
/// <param name="WouldDelete">In a dry run, the photos of the group that would have been deleted; otherwise 0.</param>
public sealed record PruneGroup(int OnFrame, int Deleted, int WouldDelete);

/// <summary>What rotation did (or, in a dry run, would do) on one frame.</summary>
/// <param name="Emailed">Photos this tool emailed in (matched on <c>from_email</c>).</param>
/// <param name="Uploaded">Photos this tool uploaded through the API (matched on the photo ids recorded at upload).</param>
/// <param name="OtherSenders">Photos from anyone else, by sender address; never touched, listed so a dry run shows what was matched.</param>
public sealed record FramePruneResult(
    string FrameId,
    string FrameName,
    PruneGroup Emailed,
    PruneGroup Uploaded,
    IReadOnlyDictionary<string, int> OtherSenders)
{
    /// <summary>This tool's photos on the frame, both groups together; <c>keep</c> applies to this number.</summary>
    public int Ours => Emailed.OnFrame + Uploaded.OnFrame;

    public int Deleted => Emailed.Deleted + Uploaded.Deleted;

    public int WouldDelete => Emailed.WouldDelete + Uploaded.WouldDelete;
}

/// <summary>Rotation's outcome over every frame it looked at.</summary>
public sealed record PruneResult(IReadOnlyList<FramePruneResult> Frames)
{
    public int Deleted => Frames.Sum(frame => frame.Deleted);

    public int WouldDelete => Frames.Sum(frame => frame.WouldDelete);
}

/// <summary>
/// Keeps each frame at the newest <see cref="RotationConfig.Keep"/> photos this tool sent: after a run
/// sends new photos it waits (up to <see cref="RotationConfig.ArrivalWaitMinutes"/>) for them to show up
/// on the frame, then deletes this tool's older photos, oldest first, at most
/// <see cref="RotationConfig.MaxDeletesPerRun"/> per frame. Photos anyone else added are never touched.
/// Two kinds of photo count as this tool's: one uploaded through the API whose photo id was recorded
/// for that frame at upload (<see cref="StateStore.RecordedUploads"/>), and, when
/// <paramref name="senderEmail"/> is given, one whose <c>from_email</c> is that address
/// (<c>[email] from</c>). <c>keep</c> counts both kinds together. An uploaded photo's age is the time
/// its upload was recorded, an emailed photo's the time Skylight received it. A <c>test-upload</c>
/// image counts as older than everything else, so it is the first photo to go once a frame is over
/// <c>keep</c> and never pushes a real photo off the frame.
/// </summary>
public sealed class FrameRotator(
    RotationConfig cfg,
    string? senderEmail,
    ISkylightClient client,
    StateStore store,
    Func<DateTimeOffset> now,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Tolerance for the Mac's clock and Skylight's disagreeing when spotting just-sent photos.</summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private const string _unknownSender = "(no sender)";

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly string? _sender = NormalizeEmail(senderEmail);

    /// <summary>
    /// Prunes every target frame. <paramref name="expectedNew"/> photos were emailed at
    /// <paramref name="sentSince"/> or later; when it is positive (and this is not a dry run) the
    /// rotator first waits until every target frame shows that many of this tool's emailed photos newer
    /// than <paramref name="sentSince"/> (each frame is emailed the same photos, so the count is per frame,
    /// not a total), so no frame is pruned down to photos that are about to be replaced anyway. It
    /// prunes after the wait whether or not they all arrived; a target frame that was not emailed
    /// therefore costs the full wait, which <see cref="RotationConfig.FrameName"/> avoids.
    /// Uploads need no count: the rotator waits the same way until every upload recorded for a frame at
    /// <paramref name="sentSince"/> or later is in that frame's listing.
    /// </summary>
    public async Task<PruneResult> PruneAsync(bool dryRun, int expectedNew, DateTimeOffset sentSince, CancellationToken ct)
    {
        var frames = await TargetFramesAsync(ct);
        var listings = await ListAsync(frames, ct);

        if (!dryRun && cfg.ArrivalWaitMinutes > 0)
        {
            var polls = (int)Math.Ceiling(TimeSpan.FromMinutes(cfg.ArrivalWaitMinutes) / PollInterval);
            for (var poll = 0; poll < polls && !AllArrived(listings, expectedNew, sentSince); poll++)
            {
                await _delay(PollInterval, ct);
                listings = await ListAsync(frames, ct);
            }
        }

        var results = new List<FramePruneResult>(frames.Count);
        foreach (var (frame, messages) in listings)
        {
            results.Add(await PruneFrameAsync(frame, messages, dryRun, ct));
        }

        return new PruneResult(results);
    }

    /// <summary>The bare, lower-cased address of <c>a@b.c</c> or <c>Name &lt;a@b.c&gt;</c>; null when empty.</summary>
    internal static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var open = text.LastIndexOf('<');
        var close = text.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            text = text[(open + 1)..close].Trim();
        }

        return text.Length == 0 ? null : text.ToLowerInvariant();
    }

    private bool IsEmailed(SkylightMessage message) =>
        _sender is not null && !message.Destroyed && NormalizeEmail(message.FromEmail) == _sender;

    private bool AllArrived(
        IReadOnlyList<(SkylightFrame Frame, IReadOnlyList<SkylightMessage> Messages)> listings, int expectedNew, DateTimeOffset sentSince) =>
        listings.All(listing =>
        {
            var emailed = _sender is null
                || listing.Messages.Count(m => IsEmailed(m) && m.CreatedAt >= sentSince - ClockSkew) >= expectedNew;
            var listed = listing.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            return emailed && store.RecordedUploads(SkylightApiDelivery.RecipientFor(listing.Frame.Id))
                .Where(upload => upload.UploadedAt >= sentSince)
                .All(upload => listed.Contains(upload.MessageId));
        });

    private async Task<FramePruneResult> PruneFrameAsync(
        SkylightFrame frame, IReadOnlyList<SkylightMessage> messages, bool dryRun, CancellationToken ct)
    {
        // Only an id recorded for this frame makes a photo an upload of ours; a recorded id that is no
        // longer listed (deleted by hand) has nothing to match and is simply not there to delete.
        var recipient = SkylightApiDelivery.RecipientFor(frame.Id);
        var recorded = store.RecordedUploads(recipient)
            .Select((upload, order) => (upload, order))
            .ToDictionary(
                pair => pair.upload.MessageId,
                pair => (At: pair.upload.IsTest ? DateTimeOffset.MinValue : pair.upload.UploadedAt, Order: pair.order),
                StringComparer.Ordinal);

        var ours = new List<(SkylightMessage Message, bool Uploaded, DateTimeOffset At, int Order)>();
        var others = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in messages.Where(m => !m.Destroyed))
        {
            if (recorded.TryGetValue(message.Id, out var upload))
            {
                ours.Add((message, true, upload.At, upload.Order));
            }
            else if (IsEmailed(message))
            {
                ours.Add((message, false, message.CreatedAt, -1));
            }
            else
            {
                var sender = NormalizeEmail(message.FromEmail) ?? _unknownSender;
                others[sender] = others.GetValueOrDefault(sender) + 1;
            }
        }

        var newestFirst = ours
            .OrderByDescending(photo => photo.At)
            .ThenByDescending(photo => photo.Order)
            .ThenByDescending(photo => photo.Message.Id.Length)
            .ThenByDescending(photo => photo.Message.Id, StringComparer.Ordinal)
            .ToList();

        // Oldest first, so a run stopped by the safety brake still removes the stalest photos.
        var excess = newestFirst.Skip(cfg.Keep).Reverse().Take(cfg.MaxDeletesPerRun).ToList();
        var emailed = ours.Count(photo => !photo.Uploaded);
        var uploaded = ours.Count - emailed;
        if (dryRun)
        {
            var wouldDeleteEmailed = excess.Count(photo => !photo.Uploaded);
            return new FramePruneResult(
                frame.Id,
                frame.Name,
                new PruneGroup(emailed, Deleted: 0, wouldDeleteEmailed),
                new PruneGroup(uploaded, Deleted: 0, excess.Count - wouldDeleteEmailed),
                others);
        }

        var deletedEmailed = 0;
        var deletedUploaded = 0;
        foreach (var photo in excess)
        {
            ct.ThrowIfCancellationRequested();
            await client.DeleteMessageAsync(frame.Id, photo.Message.Id, ct);
            store.MarkPruned(frame.Id, photo.Message.Id, photo.Message.CreatedAt, now());
            if (photo.Uploaded)
            {
                store.ForgetUpload(recipient, photo.Message.Id);
                deletedUploaded++;
            }
            else
            {
                deletedEmailed++;
            }
        }

        return new FramePruneResult(
            frame.Id,
            frame.Name,
            new PruneGroup(emailed, deletedEmailed, WouldDelete: 0),
            new PruneGroup(uploaded, deletedUploaded, WouldDelete: 0),
            others);
    }

    private async Task<IReadOnlyList<SkylightFrame>> TargetFramesAsync(CancellationToken ct)
    {
        var frames = await client.ListFramesAsync(ct);
        if (cfg.FrameName is null)
        {
            return frames;
        }

        var match = frames.Where(f => string.Equals(f.Name.Trim(), cfg.FrameName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0)
        {
            var names = frames.Count == 0 ? "none" : string.Join(", ", frames.Select(f => $"\"{f.Name}\""));
            throw new SkylightApiException($"[rotation] frame_name \"{cfg.FrameName}\" matches no frame on the Skylight account (frames: {names}).");
        }

        return match;
    }

    private async Task<IReadOnlyList<(SkylightFrame Frame, IReadOnlyList<SkylightMessage> Messages)>> ListAsync(
        IReadOnlyList<SkylightFrame> frames, CancellationToken ct)
    {
        var listings = new List<(SkylightFrame, IReadOnlyList<SkylightMessage>)>(frames.Count);
        foreach (var frame in frames)
        {
            listings.Add((frame, await client.ListMessagesAsync(frame.Id, ct)));
        }

        return listings;
    }
}
