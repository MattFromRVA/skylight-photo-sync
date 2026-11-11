using SkylightPhotoSync.Config;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Skylight;

/// <summary>What rotation did (or, in a dry run, would do) on one frame.</summary>
/// <param name="Ours">Photos on the frame that this tool emailed in (matched on <c>from_email</c>).</param>
/// <param name="Deleted">Photos deleted this run.</param>
/// <param name="WouldDelete">In a dry run, the photos that would have been deleted; otherwise 0.</param>
/// <param name="OtherSenders">Photos from anyone else, by sender address; never touched, listed so a dry run shows what was matched.</param>
public sealed record FramePruneResult(
    string FrameId,
    string FrameName,
    int Ours,
    int Deleted,
    int WouldDelete,
    IReadOnlyDictionary<string, int> OtherSenders);

/// <summary>Rotation's outcome over every frame it looked at.</summary>
public sealed record PruneResult(IReadOnlyList<FramePruneResult> Frames)
{
    public int Deleted => Frames.Sum(frame => frame.Deleted);

    public int WouldDelete => Frames.Sum(frame => frame.WouldDelete);
}

/// <summary>
/// Keeps each frame at the newest <see cref="RotationConfig.Keep"/> photos this tool sent: after a run
/// emails new photos it waits (up to <see cref="RotationConfig.ArrivalWaitMinutes"/>) for them to show up
/// on the frame, then deletes this tool's older photos, oldest first, at most
/// <see cref="RotationConfig.MaxDeletesPerRun"/> per frame. Photos anyone else added are never touched:
/// only photos whose <c>from_email</c> is <c>[email] from</c> count as this tool's.
/// </summary>
public sealed class FrameRotator(
    RotationConfig cfg,
    string senderEmail,
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
    private readonly string _sender = NormalizeEmail(senderEmail) ?? throw new ArgumentException("A sender address is required.", nameof(senderEmail));

    /// <summary>
    /// Prunes every target frame. <paramref name="expectedNew"/> photos were emailed at
    /// <paramref name="sentSince"/> or later; when it is positive (and this is not a dry run) the
    /// rotator first waits for that many of this tool's photos newer than <paramref name="sentSince"/>
    /// to appear, so the frame is never pruned down to photos that are about to be replaced anyway.
    /// It prunes after the wait whether or not they all arrived.
    /// </summary>
    public async Task<PruneResult> PruneAsync(bool dryRun, int expectedNew, DateTimeOffset sentSince, CancellationToken ct)
    {
        var frames = await TargetFramesAsync(ct);
        var listings = await ListAsync(frames, ct);

        if (!dryRun && expectedNew > 0 && cfg.ArrivalWaitMinutes > 0)
        {
            var polls = (int)Math.Ceiling(TimeSpan.FromMinutes(cfg.ArrivalWaitMinutes) / PollInterval);
            for (var poll = 0; poll < polls && Arrived(listings, sentSince) < expectedNew; poll++)
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

    private bool IsOurs(SkylightMessage message) =>
        !message.Destroyed && NormalizeEmail(message.FromEmail) == _sender;

    private int Arrived(IReadOnlyList<(SkylightFrame Frame, IReadOnlyList<SkylightMessage> Messages)> listings, DateTimeOffset sentSince) =>
        listings.Sum(listing => listing.Messages.Count(m => IsOurs(m) && m.CreatedAt >= sentSince - ClockSkew));

    private async Task<FramePruneResult> PruneFrameAsync(
        SkylightFrame frame, IReadOnlyList<SkylightMessage> messages, bool dryRun, CancellationToken ct)
    {
        var ours = messages
            .Where(IsOurs)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id.Length)
            .ThenByDescending(m => m.Id, StringComparer.Ordinal)
            .ToList();
        var others = messages
            .Where(m => !m.Destroyed && !IsOurs(m))
            .GroupBy(m => NormalizeEmail(m.FromEmail) ?? _unknownSender, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // Oldest first, so a run stopped by the safety brake still removes the stalest photos.
        var excess = ours.Skip(cfg.Keep).Reverse().Take(cfg.MaxDeletesPerRun).ToList();
        if (dryRun)
        {
            return new FramePruneResult(frame.Id, frame.Name, ours.Count, Deleted: 0, WouldDelete: excess.Count, others);
        }

        var deleted = 0;
        foreach (var message in excess)
        {
            ct.ThrowIfCancellationRequested();
            await client.DeleteMessageAsync(frame.Id, message.Id, ct);
            store.MarkPruned(frame.Id, message.Id, message.CreatedAt, now());
            deleted++;
        }

        return new FramePruneResult(frame.Id, frame.Name, ours.Count, deleted, WouldDelete: 0, others);
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
