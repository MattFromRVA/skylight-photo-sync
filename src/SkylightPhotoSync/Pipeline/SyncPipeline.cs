using System.Runtime.ExceptionServices;
using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Delivery;
using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Pipeline;

/// <summary>
/// One sync run (spec §6.3, §6.5, §6.7): list candidates, drop what was already sent or permanently
/// skipped, download and normalize the oldest ones into a temp directory and hand them to
/// <paramref name="delivery"/>, which emails or uploads them and marks each sent once it was accepted.
/// With <c>[rotation]</c> enabled, a run that runs out of new items tops up with the items sent longest
/// ago, and <paramref name="rotator"/> (when given) then prunes the frame down to the newest photos.
/// A refused Skylight sign-in is recorded in <paramref name="signInGate"/> and alerted once. Under email
/// delivery later runs keep sending but leave pruning out until the gate opens again; under
/// <c>[delivery] method = "api"</c> nothing can be sent without the sign-in, so later runs stop before
/// they start. <paramref name="reporter"/> (when given) is told about each delivery and each skipped item.
/// </summary>
public sealed class SyncPipeline(
    AppConfig cfg,
    IPhotoSource source,
    StateStore store,
    ImageNormalizer normalizer,
    IPhotoDelivery delivery,
    Alerter alerter,
    Func<DateTimeOffset> now,
    string lockPath,
    FrameRotator? rotator = null,
    SkylightSignInGate? signInGate = null,
    IRunReporter? reporter = null)
{
    /// <summary><see cref="RunResult.Error"/> when the run skipped the source because a login is needed.</summary>
    internal const string NeedsLoginError = "needs_login";

    /// <summary><see cref="RunResult.Error"/> when the source rejected the credentials during the run.</summary>
    internal const string AuthExpiredError = "auth_expired";

    /// <summary>Prefix of <see cref="RunResult.Error"/> when the SMTP server did not accept a batch.</summary>
    internal const string SendFailedErrorPrefix = "send_failed: ";

    /// <summary>Prefix of <see cref="RunResult.Error"/> when Skylight did not take an API upload (or could not list the frames for one).</summary>
    internal const string UploadFailedErrorPrefix = "upload_failed: ";

    /// <summary>
    /// <see cref="RunResult.Error"/> when API delivery is paused: an earlier run's Skylight sign-in was
    /// refused and <c>[skylight]</c> has not changed since, so the run never touched the source or Skylight.
    /// </summary>
    internal const string SkylightSignInNeededError = "skylight_sign_in_needed";

    /// <summary>Prefix of <see cref="RunResult.Error"/> when everything was sent but rotation failed.</summary>
    internal const string PruneFailedErrorPrefix = "prune_failed: ";

    /// <summary>
    /// Prefix of <see cref="RunResult.Error"/> when Skylight refused the sign-in: after everything was
    /// sent (pruning), or under API delivery before anything could be.
    /// </summary>
    internal const string SkylightSignInFailedErrorPrefix = "skylight_sign_in_failed: ";

    internal const string AuthExpiredTitle = "Amazon login expired";
    internal const string SkylightSignInTitle = "Skylight sign-in failed";
    internal const string RepeatedFailureTitle = "Sync failing";

    /// <summary>Consecutive failed runs that raise <see cref="AlertKind.RepeatedFailure"/>.</summary>
    internal const int RepeatedFailureThreshold = 3;

    /// <summary>Parent of the per-run working directories.</summary>
    internal static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "skylight-photo-sync");

    /// <summary>
    /// Performs one run. Throws <see cref="LockHeldException"/> when another run holds <c>lockPath</c>.
    /// Every other failure is recorded on the run and reported through <see cref="RunResult.Error"/>,
    /// except cancellation and unexpected exceptions, which are recorded and then rethrown.
    /// </summary>
    public async Task<RunResult> RunAsync(bool dryRun, int? limit, CancellationToken ct)
    {
        using var lockFile = AcquireLock();

        if (store.NeedsLogin())
        {
            return new RunResult(0, 0, 0, 0, dryRun, NeedsLoginError);
        }

        // Like needs_login: the refusal was recorded and alerted by the run that met it, so paused runs
        // leave no run row behind and never add up to a "sync failing" alert for the same cause.
        if (ApiDelivery && signInGate is not null && !signInGate.ShouldAttempt())
        {
            return new RunResult(0, 0, 0, 0, dryRun, SkylightSignInNeededError);
        }

        var runId = store.StartRun(cfg.Source.Mode);
        var counts = new Counts();
        var workDir = Path.Combine(TempRoot, $"run-{Guid.NewGuid():N}");
        var startedAt = now();
        try
        {
            await ProcessAsync(dryRun, limit, workDir, counts, ct);
            var pruneError = await PruneAsync(dryRun, startedAt, counts, ct);
            return await FinishAsync(runId, dryRun, counts, pruneError, ct);
        }
        catch (AuthExpiredException)
        {
            // Already-sent items stay sent; the flag stops later runs until `skylight-sync login`.
            store.SetNeedsLogin(true, now());
            await alerter.AlertAsync(AlertKind.AuthExpired, AuthExpiredTitle, AlertTexts.AuthExpiredBody, ct);
            return await FinishAsync(runId, dryRun, counts, AuthExpiredError, ct);
        }
        catch (SendFailedException ex)
        {
            return await FinishAsync(runId, dryRun, counts, SendFailedErrorPrefix + ex.Message, ct);
        }
        catch (SkylightSignInException ex)
        {
            // Only API delivery gets here (pruning handles its own sign-in failure): nothing can be sent
            // until the sign-in works, and trying the same password every hour could lock the account.
            signInGate?.RecordFailure(ex.Message);
            await alerter.AlertAsync(AlertKind.SkylightSignIn, SkylightSignInTitle, AlertTexts.SkylightSignInDeliveryBody(ex.Message), ct);
            return await FinishAsync(runId, dryRun, counts, SkylightSignInFailedErrorPrefix + ex.Message, ct);
        }
        catch (SkylightApiException ex)
        {
            return await FinishAsync(runId, dryRun, counts, UploadFailedErrorPrefix + ex.Message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RecordRun(runId, dryRun, counts, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            await FinishAsync(runId, dryRun, counts, $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
            throw;
        }
        finally
        {
            DeleteQuietly(workDir);
        }
    }

    private bool ApiDelivery => cfg.Delivery.Method == DeliveryMethod.Api;

    private async Task ProcessAsync(bool dryRun, int? limit, string workDir, Counts counts, CancellationToken ct)
    {
        // Asked first: under API delivery this is the Skylight sign-in, and a refused one must stop the
        // run before it downloads anything.
        var destinations = await delivery.DestinationsAsync(ct);
        if (ApiDelivery)
        {
            signInGate?.RecordSuccess();
        }

        var window = Window.FromLookback(cfg.Source.LookbackDays, now());
        var candidates = await source.ListCandidatesAsync(window, cfg.Source.Mode, cfg.Source.AlbumName, ct);

        // Byte-identical copies new in this run collapse to the oldest; later runs drop the rest via IsMd5Sent.
        var seenMd5 = new HashSet<string>(StringComparer.Ordinal);
        var fresh = candidates
            .Where(item => !store.IsSkipped(item.Id) && !destinations.All(destination => store.WasSentTo(item, destination.Key)))
            .OrderBy(item => item.CapturedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Where(item => item.Md5 is null || seenMd5.Add(item.Md5))
            .ToList();
        counts.Found = fresh.Count;

        var queue = new List<Item>(fresh.Count);
        foreach (var item in fresh)
        {
            if (item.Kind == ItemKind.Video && !cfg.Source.IncludeVideos)
            {
                Skip(item, SkipReason.VideoDisabled, "source.include_videos is false", counts);
            }
            else
            {
                queue.Add(item);
            }
        }

        var max = limit ?? cfg.Limits.MaxItemsPerRun;
        var take = Math.Min(max, queue.Count);
        var picked = queue.Take(take).ToList();
        var recycled = new HashSet<string>(StringComparer.Ordinal);
        if (cfg.Rotation.Enabled && picked.Count < max)
        {
            foreach (var item in Recyclable(candidates, fresh).Take(max - picked.Count))
            {
                picked.Add(item);
                recycled.Add(item.Id);
            }
        }

        var downloadDir = Path.Combine(workDir, "downloads");
        var attachmentDir = Path.Combine(workDir, "attachments");
        var prepared = new List<(Item Item, string File)>(picked.Count);
        foreach (var item in picked)
        {
            ct.ThrowIfCancellationRequested();
            if (await PrepareAsync(item, downloadDir, attachmentDir, counts, ct) is { } file)
            {
                prepared.Add((item, file));
            }
        }

        var result = await delivery.DeliverAsync(prepared, dryRun, ct);
        foreach (var skip in result.Skipped)
        {
            Skip(skip.Item, skip.Reason, skip.Detail, counts);
        }

        foreach (var batch in result.Batches)
        {
            counts.Batches++;
            counts.Sent += batch.Items.Count;
            reporter?.Delivered(new DeliveryReport(
                counts.Batches,
                batch.Destination.Name,
                batch.Items.Select(sent => new DeliveryItemReport(sent.Item, sent.Bytes, recycled.Contains(sent.Item.Id))).ToList(),
                batch.TotalBytes));
            foreach (var sent in batch.Items)
            {
                counts.SentIds.Add(sent.Item.Id);
                if (recycled.Contains(sent.Item.Id) && counts.RecycledIds.Add(sent.Item.Id))
                {
                    counts.Recycled++;
                }
            }
        }

        // What was delivered before the failure is counted above; the failure itself is handled by RunAsync.
        if (result.Failure is not null)
        {
            ExceptionDispatchInfo.Capture(result.Failure).Throw();
        }
    }

    /// <summary>
    /// Rotation's top-up when nothing new is left: candidates that were already sent, longest-ago send
    /// first, leaving out the <see cref="RotationConfig.Keep"/> most recently sent (they are presumably
    /// still on the frame). Byte-identical copies that were never sent under their own id stay out.
    /// </summary>
    private IEnumerable<Item> Recyclable(IReadOnlyList<Item> candidates, IReadOnlyList<Item> fresh)
    {
        var lastSent = store.LastSentTimes();
        var freshIds = fresh.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var sent = candidates
            .Where(item => !freshIds.Contains(item.Id)
                && lastSent.ContainsKey(item.Id)
                && seenIds.Add(item.Id)
                && !store.IsSkipped(item.Id)
                && (item.Kind != ItemKind.Video || cfg.Source.IncludeVideos))
            .OrderBy(item => lastSent[item.Id])
            .ThenBy(item => item.CapturedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        return sent.Take(Math.Max(0, sent.Count - cfg.Rotation.Keep));
    }

    /// <summary>
    /// Runs rotation after a successful send. Returns <see langword="null"/> when it worked (or there is
    /// no rotator), otherwise the run's error. A failure here never un-marks anything that was sent.
    /// </summary>
    private async Task<string?> PruneAsync(bool dryRun, DateTimeOffset startedAt, Counts counts, CancellationToken ct)
    {
        if (rotator is null)
        {
            return null;
        }

        if (signInGate is not null && !signInGate.ShouldAttempt())
        {
            counts.PrunePaused = true;
            return null;
        }

        try
        {
            var result = await rotator.PruneAsync(dryRun, counts.SentIds.Count, startedAt, ct);
            counts.Pruned = dryRun ? result.WouldDelete : result.Deleted;
            signInGate?.RecordSuccess();
            return null;
        }
        catch (SkylightSignInException ex)
        {
            signInGate?.RecordFailure(ex.Message);
            await alerter.AlertAsync(AlertKind.SkylightSignIn, SkylightSignInTitle, AlertTexts.SkylightSignInBody(ex.Message), ct);
            return SkylightSignInFailedErrorPrefix + ex.Message;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return PruneFailedErrorPrefix + ex.Message;
        }
    }

    /// <summary>
    /// Downloads and normalizes one item; returns the attachment path, or <see langword="null"/> after
    /// recording why the item was skipped. <see cref="AuthExpiredException"/> and cancellation propagate.
    /// </summary>
    private async Task<string?> PrepareAsync(Item item, string downloadDir, string attachmentDir, Counts counts, CancellationToken ct)
    {
        string downloaded;
        try
        {
            downloaded = await source.DownloadAsync(item, downloadDir, ct);
        }
        catch (Exception ex) when (IsItemFailure(ex, ct))
        {
            Skip(item, SkipReason.DownloadFailed, ex.Message, counts);
            return null;
        }

        try
        {
            return await normalizer.NormalizeAsync(downloaded, attachmentDir, item, ct);
        }
        catch (OversizeException ex)
        {
            Skip(item, SkipReason.Oversize, ex.Message, counts);
        }
        catch (UnsupportedMediaException ex)
        {
            Skip(item, SkipReason.UnsupportedType, ex.Message, counts);
        }
        catch (Exception ex) when (IsItemFailure(ex, ct))
        {
            Skip(item, SkipReason.ProcessingFailed, ex.Message, counts);
        }

        return null;
    }

    /// <summary>A failure that concerns one item only, so the run records it and moves on.</summary>
    private static bool IsItemFailure(Exception ex, CancellationToken ct) =>
        ex is not AuthExpiredException
        && !(ex is OperationCanceledException && ct.IsCancellationRequested);

    private void Skip(Item item, SkipReason reason, string detail, Counts counts)
    {
        store.MarkSkipped(item.Id, reason, detail);
        if (counts.SkippedIds.Add(item.Id))
        {
            counts.Skipped++;
            reporter?.Skipped(item, reason, detail);
        }
    }

    private async Task<RunResult> FinishAsync(long runId, bool dryRun, Counts counts, string? error, CancellationToken ct)
    {
        RecordRun(runId, dryRun, counts, error);

        // An expired login already raised its own alert and pauses every run until the user logs in again,
        // so counting it towards "repeated failures" would only produce a second alert for the same cause.
        // The same goes for a refused Skylight sign-in, which pauses pruning (or, under API delivery, the runs).
        if (error is not null && error != AuthExpiredError && !error.StartsWith(SkylightSignInFailedErrorPrefix, StringComparison.Ordinal))
        {
            var failures = store.ConsecutiveFailures();
            if (failures >= RepeatedFailureThreshold)
            {
                await alerter.AlertAsync(
                    AlertKind.RepeatedFailure,
                    RepeatedFailureTitle,
                    RepeatedFailureBody(failures, error),
                    ct);
            }
        }

        return new RunResult(
            counts.Found, counts.Sent, counts.Skipped, counts.Batches, dryRun, error, counts.Recycled, counts.Pruned, counts.PrunePaused);
    }

    internal static string RepeatedFailureBody(int failures, string error) =>
        $"{failures} scheduled sync runs in a row have failed. Last error: {error}";

    /// <summary>Closes the run's row. A dry run records zero sent, because nothing was.</summary>
    private void RecordRun(long runId, bool dryRun, Counts counts, string? error) =>
        store.FinishRun(runId, counts.Found, dryRun ? 0 : counts.Sent, counts.Skipped, error);

    private FileStream AcquireLock()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new LockHeldException($"Another sync run holds the lock file {lockPath}.", ex);
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover temp files are harmless; a cleanup failure must not fail the run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private sealed class Counts
    {
        public int Found { get; set; }

        public int Sent { get; set; }

        public int Skipped { get; set; }

        public int Batches { get; set; }

        public int Recycled { get; set; }

        public int Pruned { get; set; }

        public bool PrunePaused { get; set; }

        public HashSet<string> SentIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RecycledIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SkippedIds { get; } = new(StringComparer.Ordinal);
    }
}
