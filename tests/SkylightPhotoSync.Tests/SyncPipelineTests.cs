using MimeKit;
using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Pipeline;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class SyncPipelineTests : IDisposable
{
    private const string _frame = "frame@example.com";
    private const string _calendar = "calendar@example.com";
    private const string _album = "Skylight";

    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly AppConfig _baseConfig = new(
        Source: new SourceConfig(SourceMode.Album, _album, LookbackDays: 0, IncludeVideos: false),
        Limits: new LimitsConfig(MaxItemsPerRun: 25),
        Image: new ImageConfig(MaxLongEdge: 2048, JpegQuality: 88),
        Email: new EmailConfig([_frame], "sync@example.com", MaxAttachments: 12, MaxTotalBytes: 17_000_000),
        Smtp: new SmtpConfig("127.0.0.1", 25, "user"),
        Alerts: new AlertsConfig("matt@example.com"),
        Schedule: new ScheduleConfig(60));

    private readonly TempDir _home = new();
    private readonly TempDir _root = new();
    private readonly StateStore _store;
    private readonly FakeNotifier _notifier = new();
    private readonly string _lockPath;
    private readonly DateTimeOffset _now = _t0;

    public SyncPipelineTests()
    {
        _store = new StateStore(Path.Combine(_home.Path, "state.db"));
        _lockPath = Path.Combine(_home.Path, "run.lock");
    }

    [Fact]
    public async Task Sends_new_items_and_marks_sent()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny_rot6.heic", $"{_album}/b.heic", DaysAgo(2));
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 2, Sent: 2, Skipped: 0, Batches: 1, DryRun: false, Error: null), result);
        var message = Assert.Single(stub.Received);
        Assert.Equal(_frame, Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("Skylight Photo Sync — 2 photos (2026-11-05)", message.Subject);
        Assert.Equal($"Source: album \"{_album}\"", message.TextBody);
        Assert.Equal(["2026-11-02_120000_Skylight.jpg", "2026-11-03_120000_Skylight.jpg"], AttachmentNames(message));
        Assert.True(_store.IsSent($"{_album}/a.png", _frame));
        Assert.True(_store.IsSent($"{_album}/b.heic", _frame));
        var run = Assert.IsType<RunRecord>(_store.LastRun());
        Assert.NotNull(run.FinishedAt);
        Assert.Null(run.Error);
        Assert.Equal((2, 2, 0), (run.Found, run.Sent, run.Skipped));
        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task Second_run_sends_nothing()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var source = Source();
        var pipeline = Pipeline(_baseConfig, source, Mailer(stub));
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _root.AddFixture("tiny.png", $"{_album}/same-bytes-again.png", DaysAgo(1));   // same md5 as a.png

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 0, Sent: 0, Skipped: 0, Batches: 0, DryRun: false, Error: null), result);
        Assert.Single(stub.Received);
        Assert.Equal(1, source.Downloads);
        Assert.False(_store.IsSent($"{_album}/same-bytes-again.png", _frame));
    }

    [Fact]
    public async Task Respects_max_items_per_run_oldest_first()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny_rot6.heic", $"{_album}/c.heic", DaysAgo(1));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.jpg", $"{_album}/exif-2020.jpg", DaysAgo(0));   // EXIF date makes it the oldest
        var cfg = _baseConfig with { Limits = new LimitsConfig(MaxItemsPerRun: 2) };
        var pipeline = Pipeline(cfg, Source(), Mailer(stub));

        var first = await pipeline.RunAsync(dryRun: false, limit: 1, CancellationToken.None);
        var second = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((4, 1, 1), (first.Found, first.Sent, first.Batches));
        Assert.Equal((3, 2, 1), (second.Found, second.Sent, second.Batches));
        Assert.Equal(2, stub.Received.Count);
        Assert.Equal(["2020-01-02_030405_Skylight.jpg"], AttachmentNames(stub.Received[0]));
        Assert.Equal(["2026-11-02_120000_Skylight.jpg", "2026-11-03_120000_Skylight.gif"], AttachmentNames(stub.Received[1]));
        Assert.False(_store.IsSent($"{_album}/c.heic", _frame));
    }

    [Fact]
    public async Task Dry_run_sends_nothing_and_marks_nothing()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub));

        var result = await pipeline.RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 2, Sent: 2, Skipped: 0, Batches: 1, DryRun: true, Error: null), result);
        Assert.Empty(stub.Received);
        Assert.False(_store.IsSent($"{_album}/a.png", _frame));
        Assert.False(_store.IsSent($"{_album}/b.gif", _frame));
        var run = Assert.IsType<RunRecord>(_store.LastRun());
        Assert.Null(run.Error);
        Assert.Equal(0, run.Sent);
    }

    [Fact]
    public async Task Needs_login_short_circuits()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _store.SetNeedsLogin(true, _t0.AddHours(-1));
        var source = Source();
        var pipeline = Pipeline(_baseConfig, source, Mailer(stub));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("needs_login", result.Error);
        Assert.Equal((0, 0, 0, 0), (result.Found, result.Sent, result.Skipped, result.Batches));
        Assert.Equal(0, source.Listings);
        Assert.Equal(0, source.Downloads);
        Assert.Empty(stub.Received);
        Assert.Null(_store.LastRun());
        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task Auth_expired_midrun_keeps_sent_sets_flag_alerts_once()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(5));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(4));
        var mailer = Mailer(stub);
        await Pipeline(_baseConfig, Source(), mailer).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _root.AddFixture("tiny.jpg", $"{_album}/c.jpg", DaysAgo(2));          // EXIF 2020: downloaded first
        _root.AddFixture("tiny_rot6.heic", $"{_album}/d.heic", DaysAgo(1));   // the download that hits the expiry
        var expiring = Source(failAfterDownloads: 1);
        var pipeline = Pipeline(_baseConfig, expiring, mailer, alertMailer: mailer);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var again = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("auth_expired", result.Error);
        Assert.Equal((2, 0, 0), (result.Found, result.Sent, result.Batches));
        Assert.Equal("needs_login", again.Error);
        Assert.Equal(1, expiring.Downloads);
        Assert.True(_store.NeedsLogin());
        Assert.True(_store.IsSent($"{_album}/a.png", _frame));
        Assert.True(_store.IsSent($"{_album}/b.gif", _frame));
        Assert.False(_store.IsSent($"{_album}/c.jpg", _frame));
        Assert.False(_store.IsSent($"{_album}/d.heic", _frame));
        var call = Assert.Single(_notifier.Calls);
        Assert.Equal(("Amazon login expired", AlertTexts.AuthExpiredBody), call);
        Assert.Equal(2, stub.Received.Count);   // the first run's batch plus one alert email
        var alert = stub.Received[1];
        Assert.Equal("matt@example.com", Assert.Single(alert.To.Mailboxes).Address);
        Assert.Equal("Skylight Photo Sync: Amazon login expired", alert.Subject);
        var run = Assert.IsType<RunRecord>(_store.LastRun());
        Assert.Equal("auth_expired", run.Error);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task Lock_prevents_overlap()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var source = Source();
        var pipeline = Pipeline(_baseConfig, source, Mailer(stub));
        using var held = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<LockHeldException>(() => pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None));

        Assert.Equal(0, source.Listings);
        Assert.Empty(stub.Received);
        Assert.Null(_store.LastRun());
    }

    [Fact]
    public async Task Lock_is_released_after_run()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub));

        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        using var reopened = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.CanWrite);
    }

    [Fact]
    public async Task Oversize_item_skipped_and_queue_advances()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.mp4", $"{_album}/huge.mp4", DaysAgo(3));
        _root.AddFixture("tiny.png", $"{_album}/after.png", DaysAgo(2));
        var cfg = _baseConfig with { Source = _baseConfig.Source with { IncludeVideos = true } };
        var source = Source();
        var pipeline = Pipeline(cfg, source, Mailer(stub), videoCapBytes: 100);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var again = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 2, Sent: 1, Skipped: 1, Batches: 1, DryRun: false, Error: null), result);
        Assert.Equal(["2026-11-03_120000_Skylight.jpg"], AttachmentNames(Assert.Single(stub.Received)));
        Assert.True(_store.IsSkipped($"{_album}/huge.mp4"));
        Assert.False(_store.IsSent($"{_album}/huge.mp4", _frame));
        Assert.Equal(0, again.Found);           // the oversize item is not listed as new again
        Assert.Equal(2, source.Downloads);      // ... and not downloaded again either
    }

    [Fact]
    public async Task Attachment_over_max_total_bytes_is_skipped_as_oversize()
    {
        await using var stub = await SmtpStub.StartAsync();
        // GIFs and videos are attached byte-for-byte, so their attachment sizes are the fixture sizes.
        _root.AddFixture("tiny.gif", $"{_album}/big.gif", DaysAgo(3));
        _root.AddFixture("tiny.mp4", $"{_album}/small.mp4", DaysAgo(2));
        var gifBytes = new FileInfo(Fixtures.Image("tiny.gif")).Length;
        Assert.True(new FileInfo(Fixtures.Image("tiny.mp4")).Length < gifBytes);
        var cfg = _baseConfig with
        {
            Source = _baseConfig.Source with { IncludeVideos = true },
            Email = _baseConfig.Email with { MaxTotalBytes = gifBytes - 1 },
        };
        var pipeline = Pipeline(cfg, Source(), Mailer(stub));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((2, 1, 1, 1), (result.Found, result.Sent, result.Skipped, result.Batches));
        Assert.Equal(["2026-11-03_120000_Skylight.mp4"], AttachmentNames(Assert.Single(stub.Received)));
        Assert.True(_store.IsSkipped($"{_album}/big.gif"));
        Assert.True(_store.IsSent($"{_album}/small.mp4", _frame));
    }

    [Fact]
    public async Task Undecodable_file_is_recorded_unsupported_and_queue_advances()
    {
        await using var stub = await SmtpStub.StartAsync();
        var broken = Path.Combine(_root.Path, _album, "broken.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        await File.WriteAllBytesAsync(broken, new byte[64]);
        File.SetLastWriteTimeUtc(broken, DaysAgo(3));
        _root.AddFixture("tiny.png", $"{_album}/fine.png", DaysAgo(2));
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((2, 1, 1, 1), (result.Found, result.Sent, result.Skipped, result.Batches));
        Assert.True(_store.IsSkipped($"{_album}/broken.jpg"));
        Assert.True(_store.IsSent($"{_album}/fine.png", _frame));
        Assert.Null(Assert.IsType<RunRecord>(_store.LastRun()).Error);
    }

    [Fact]
    public async Task Videos_are_skipped_while_disabled_and_sent_once_enabled()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.mp4", $"{_album}/clip.mp4", DaysAgo(3));
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(2));
        var source = Source();

        var disabled = await Pipeline(_baseConfig, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var enabledCfg = _baseConfig with { Source = _baseConfig.Source with { IncludeVideos = true } };
        var enabled = await Pipeline(enabledCfg, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((2, 1, 1), (disabled.Found, disabled.Sent, disabled.Skipped));
        Assert.Equal(["2026-11-03_120000_Skylight.jpg"], AttachmentNames(stub.Received[0]));
        Assert.Equal((1, 1, 0), (enabled.Found, enabled.Sent, enabled.Skipped));
        Assert.Equal(["2026-11-02_120000_Skylight.mp4"], AttachmentNames(stub.Received[1]));
        Assert.Equal(2, source.Downloads);   // the disabled video was never downloaded
    }

    [Fact]
    public async Task Mode_switch_does_not_resend()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", "other/b.gif", DaysAgo(2));
        _root.AddFixture("tiny.png", "other/a-again.png", DaysAgo(1));   // same bytes as a.png
        var source = Source();
        await Pipeline(_baseConfig, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var allCfg = _baseConfig with { Source = new SourceConfig(SourceMode.All, _album, LookbackDays: 30, IncludeVideos: false) };

        var result = await Pipeline(allCfg, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 1, Sent: 1, Skipped: 0, Batches: 1, DryRun: false, Error: null), result);
        Assert.Equal(2, stub.Received.Count);
        Assert.Equal("Source: all", stub.Received[1].TextBody);
        Assert.Equal(["2026-11-03_120000_other-b..gif"], AttachmentNames(stub.Received[1]));
        Assert.True(_store.IsSent("other/b.gif", _frame));
        Assert.False(_store.IsSent("other/a-again.png", _frame));
        Assert.Equal(2, source.Downloads);
    }

    [Fact]
    public async Task Sends_to_every_recipient_and_tracks_state_per_recipient()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var two = _baseConfig with { Email = _baseConfig.Email with { To = [_frame, _calendar] } };
        var source = Source();

        var result = await Pipeline(two, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var one = _baseConfig with { Email = _baseConfig.Email with { To = [_frame] } };
        var again = await Pipeline(one, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var third = _baseConfig with { Email = _baseConfig.Email with { To = [_frame, "third@example.com"] } };
        var added = await Pipeline(third, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, 2, 2), (result.Found, result.Sent, result.Batches));
        Assert.Equal([_frame, _calendar], stub.Received.Take(2).Select(m => m.To.Mailboxes.Single().Address));
        Assert.True(_store.IsSent($"{_album}/a.png", _frame));
        Assert.True(_store.IsSent($"{_album}/a.png", _calendar));
        Assert.Equal((0, 0, 0), (again.Found, again.Sent, again.Batches));
        Assert.Equal((1, 1, 1), (added.Found, added.Sent, added.Batches));
        Assert.Equal("third@example.com", stub.Received[2].To.Mailboxes.Single().Address);
        Assert.Equal(3, stub.Received.Count);
    }

    [Fact]
    public async Task Send_failure_stops_run_marks_nothing_and_finishes_with_error()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var pipeline = Pipeline(_baseConfig, Source(), DeadMailer());

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.StartsWith("send_failed", result.Error);
        Assert.Equal((1, 0, 0), (result.Found, result.Sent, result.Batches));
        Assert.False(_store.IsSent($"{_album}/a.png", _frame));
        var run = Assert.IsType<RunRecord>(_store.LastRun());
        Assert.NotNull(run.FinishedAt);
        Assert.StartsWith("send_failed", run.Error);
        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task Three_failures_trigger_repeated_failure_alert()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var pipeline = Pipeline(_baseConfig, Source(), DeadMailer());

        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        Assert.Empty(_notifier.Calls);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(3, _store.ConsecutiveFailures());
        var call = Assert.Single(_notifier.Calls);
        Assert.Equal("Sync failing", call.Title);
        Assert.Contains("3", call.Body);
        Assert.Equal(_t0, _store.LastAlertAt("RepeatedFailure"));
    }

    [Fact]
    public async Task Temp_dir_is_removed_after_run()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        Directory.CreateDirectory(SyncPipeline.TempRoot);
        var before = Directory.GetDirectories(SyncPipeline.TempRoot).Length;
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub));

        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(before, Directory.GetDirectories(SyncPipeline.TempRoot).Length);
    }

    public void Dispose()
    {
        _store.Dispose();
        _root.Dispose();
        _home.Dispose();
    }

    private static DateTime DaysAgo(int days) => _t0.AddDays(-days).UtcDateTime;

    private static IReadOnlyList<string> AttachmentNames(MimeMessage message) =>
        message.Attachments.Select(a => ((MimePart)a).FileName ?? "").ToList();

    private static SmtpMailer Mailer(SmtpStub stub) =>
        new(new SmtpConfig("127.0.0.1", stub.Port, "user"), password: "", useStartTls: false);

    private static SmtpMailer DeadMailer() =>
        new(new SmtpConfig("127.0.0.1", SmtpStub.FreePort(), "user"), password: "", useStartTls: false);

    private FailingSource Source(int failAfterDownloads = int.MaxValue) =>
        new(new LocalFolderSource(_root.Path), failAfterDownloads);

    private SyncPipeline Pipeline(
        AppConfig cfg, IPhotoSource source, IMailer mailer, IMailer? alertMailer = null, long videoCapBytes = 22_000_000)
    {
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), cfg.Image, videoCapBytes);
        var alerter = new Alerter(_store, alertMailer, cfg.Alerts.Email, cfg.Email.From, _notifier, () => _now);
        return new SyncPipeline(cfg, source, _store, normalizer, mailer, alerter, () => _now, _lockPath);
    }

    /// <summary>
    /// Wraps a source, counting calls, and throws <see cref="AuthExpiredException"/> on the download after
    /// <paramref name="failAfterDownloads"/> successful ones (never, by default).
    /// </summary>
    private sealed class FailingSource(IPhotoSource inner, int failAfterDownloads = int.MaxValue) : IPhotoSource
    {
        public int Listings { get; private set; }

        public int Downloads { get; private set; }

        public Task<IReadOnlyList<Item>> ListCandidatesAsync(Window window, SourceMode mode, string? albumName, CancellationToken ct)
        {
            Listings++;
            return inner.ListCandidatesAsync(window, mode, albumName, ct);
        }

        public Task<string> DownloadAsync(Item item, string destDir, CancellationToken ct)
        {
            if (Downloads >= failAfterDownloads)
            {
                throw new AuthExpiredException();
            }

            Downloads++;
            return inner.DownloadAsync(item, destDir, ct);
        }
    }

    private sealed class FakeHeicConverter : IHeicConverter
    {
        public Task<string> ToJpegAsync(string heicPath, string destDir, CancellationToken ct)
        {
            Directory.CreateDirectory(destDir);
            var output = Path.Combine(destDir, Path.GetFileNameWithoutExtension(heicPath) + ".jpg");
            File.Copy(Fixtures.Image("tiny.jpg"), output, overwrite: true);
            return Task.FromResult(output);
        }
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<(string Title, string Body)> Calls { get; } = [];

        public Task NotifyAsync(string title, string body, CancellationToken ct)
        {
            Calls.Add((title, body));
            return Task.CompletedTask;
        }
    }
}
