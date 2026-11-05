using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Batching;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Pipeline;

/// <summary>
/// One sync run (spec §6.3, §6.5, §6.7): list candidates, drop what was already sent or permanently
/// skipped, download and normalize the oldest ones into a temp directory, pack them into emails and send
/// one email per batch per recipient, marking items sent only after the SMTP server accepts the message.
/// </summary>
public sealed class SyncPipeline(
    AppConfig cfg,
    IPhotoSource source,
    StateStore store,
    ImageNormalizer normalizer,
    IMailer mailer,
    Alerter alerter,
    Func<DateTimeOffset> now,
    string lockPath)
{
    /// <summary><see cref="RunResult.Error"/> when the run skipped the source because a login is needed.</summary>
    internal const string NeedsLoginError = "needs_login";

    /// <summary><see cref="RunResult.Error"/> when the source rejected the credentials during the run.</summary>
    internal const string AuthExpiredError = "auth_expired";

    /// <summary>Prefix of <see cref="RunResult.Error"/> when the SMTP server did not accept a batch.</summary>
    internal const string SendFailedErrorPrefix = "send_failed: ";

    internal const string AuthExpiredTitle = "Amazon login expired";
    internal const string RepeatedFailureTitle = "Sync failing";

    private const int _repeatedFailureThreshold = 3;

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

        var runId = store.StartRun(cfg.Source.Mode);
        var counts = new Counts();
        var workDir = Path.Combine(TempRoot, $"run-{Guid.NewGuid():N}");
        try
        {
            await ProcessAsync(dryRun, limit, workDir, counts, ct);
            return await FinishAsync(runId, dryRun, counts, error: null, ct);
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

    private async Task ProcessAsync(bool dryRun, int? limit, string workDir, Counts counts, CancellationToken ct)
    {
        var window = Window.FromLookback(cfg.Source.LookbackDays, now());
        var candidates = await source.ListCandidatesAsync(window, cfg.Source.Mode, cfg.Source.AlbumName, ct);

        // Byte-identical copies new in this run collapse to the oldest; later runs drop the rest via IsMd5Sent.
        var seenMd5 = new HashSet<string>(StringComparer.Ordinal);
        var fresh = candidates
            .Where(item => !store.IsSkipped(item.Id) && !cfg.Email.To.All(recipient => AlreadySent(item, recipient)))
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

        var take = Math.Min(limit ?? cfg.Limits.MaxItemsPerRun, queue.Count);
        var downloadDir = Path.Combine(workDir, "downloads");
        var attachmentDir = Path.Combine(workDir, "attachments");
        var prepared = new List<(Item Item, string File)>(take);
        foreach (var item in queue.Take(take))
        {
            ct.ThrowIfCancellationRequested();
            if (await PrepareAsync(item, downloadDir, attachmentDir, counts, ct) is { } file)
            {
                prepared.Add((item, file));
            }
        }

        foreach (var recipient in cfg.Email.To)
        {
            var pending = prepared.Where(p => !AlreadySent(p.Item, recipient)).ToList();
            var pack = BatchPacker.Pack(pending, cfg.Email.MaxAttachments, cfg.Email.MaxTotalBytes);
            foreach (var (item, reason) in pack.Skipped)
            {
                var file = pending.First(p => p.Item.Id == item.Id).File;
                var detail = $"{new FileInfo(file).Length:N0} bytes exceeds email.max_total_bytes ({cfg.Email.MaxTotalBytes:N0})";
                Skip(item, reason, detail, counts);
            }

            foreach (var batch in pack.Batches)
            {
                ct.ThrowIfCancellationRequested();
                var message = MessageBuilder.Build(batch, recipient, cfg.Email.From, cfg.Source.Mode, cfg.Source.AlbumName, now());
                if (!dryRun)
                {
                    await mailer.SendAsync(message, ct);
                    store.MarkSent(batch.Items, recipient, batch.Id, now());
                }

                counts.Batches++;
                counts.Sent += batch.Items.Count;
            }
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

    private bool AlreadySent(Item item, string recipient) =>
        store.IsSent(item.Id, recipient) || (item.Md5 is not null && store.IsMd5Sent(item.Md5, recipient));

    private void Skip(Item item, SkipReason reason, string detail, Counts counts)
    {
        store.MarkSkipped(item.Id, reason, detail);
        if (counts.SkippedIds.Add(item.Id))
        {
            counts.Skipped++;
        }
    }

    private async Task<RunResult> FinishAsync(long runId, bool dryRun, Counts counts, string? error, CancellationToken ct)
    {
        RecordRun(runId, dryRun, counts, error);

        // An expired login already raised its own alert and pauses every run until the user logs in again,
        // so counting it towards "repeated failures" would only produce a second alert for the same cause.
        if (error is not null && error != AuthExpiredError)
        {
            var failures = store.ConsecutiveFailures();
            if (failures >= _repeatedFailureThreshold)
            {
                await alerter.AlertAsync(
                    AlertKind.RepeatedFailure,
                    RepeatedFailureTitle,
                    $"{failures} scheduled sync runs in a row have failed. Last error: {error}",
                    ct);
            }
        }

        return new RunResult(counts.Found, counts.Sent, counts.Skipped, counts.Batches, dryRun, error);
    }

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

        public HashSet<string> SkippedIds { get; } = new(StringComparer.Ordinal);
    }
}
