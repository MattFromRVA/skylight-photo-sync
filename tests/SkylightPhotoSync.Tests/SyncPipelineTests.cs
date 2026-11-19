using MimeKit;
using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Delivery;
using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Pipeline;
using SkylightPhotoSync.Skylight;
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

    private const string _kitchen = "skylight:frame:100";

    /// <summary><c>[delivery] method = "api"</c> with no <c>[email]</c> and no <c>[smtp]</c>.</summary>
    private static readonly AppConfig _apiConfig = _baseConfig with
    {
        Email = null,
        Smtp = null,
        Alerts = new AlertsConfig(null),
        Delivery = new DeliveryConfig(DeliveryMethod.Api, []),
    };

    private readonly TempDir _home = new();
    private readonly TempDir _root = new();
    private readonly StateStore _store;
    private readonly FakeNotifier _notifier = new();
    private readonly string _lockPath;
    private DateTimeOffset _now = _t0;

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
    public async Task Duplicate_bytes_in_one_run_are_sent_once()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        _root.AddFixture("tiny.png", $"{_album}/a-reupload.png", DaysAgo(1));   // same md5 as a.png
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub));

        var first = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var second = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((2, 2), (first.Found, first.Sent));
        var message = Assert.Single(stub.Received);
        Assert.Equal(2, AttachmentNames(message).Count);
        Assert.True(_store.IsSent($"{_album}/a.png", _frame));
        Assert.False(_store.IsSent($"{_album}/a-reupload.png", _frame));
        Assert.Equal((0, 0), (second.Sent, second.Batches));
        Assert.Single(stub.Received);
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
    public async Task Dry_run_reports_each_email_and_each_skip()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.mp4", $"{_album}/clip.mp4", DaysAgo(2));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(1));
        var reporter = new RecordingReporter();
        var pipeline = Pipeline(_baseConfig, Source(), Mailer(stub), reporter: reporter);

        await pipeline.RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Empty(stub.Received);
        var email = Assert.Single(reporter.Emails);
        Assert.Equal((1, _frame), (email.Number, email.Destination));
        Assert.Equal(["a.png", "b.gif"], email.Items.Select(i => i.Item.Name));
        Assert.All(email.Items, i => Assert.True(i.Bytes > 0));
        Assert.All(email.Items, i => Assert.False(i.Recycled));
        Assert.Equal(email.Items.Sum(i => i.Bytes), email.TotalBytes);
        var skip = Assert.Single(reporter.Skips);
        Assert.Equal(("clip.mp4", SkipReason.VideoDisabled, "source.include_videos is false"), (skip.Item.Name, skip.Reason, skip.Detail));
    }

    [Fact]
    public async Task Reports_one_numbered_email_per_recipient_after_sending()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var two = _baseConfig with { Email = _baseConfig.Email! with { To = [_frame, _calendar] } };
        var reporter = new RecordingReporter();
        var pipeline = Pipeline(two, Source(), Mailer(stub), reporter: reporter);

        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(2, stub.Received.Count);
        Assert.Equal([(1, _frame), (2, _calendar)], reporter.Emails.Select(e => (e.Number, e.Destination)));
        Assert.Empty(reporter.Skips);
    }

    [Fact]
    public async Task Reports_nothing_for_an_email_the_server_refused()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var reporter = new RecordingReporter();
        var pipeline = Pipeline(_baseConfig, Source(), DeadMailer(), reporter: reporter);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.StartsWith("send_failed: ", result.Error);
        Assert.Empty(reporter.Emails);
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
            Email = _baseConfig.Email! with { MaxTotalBytes = gifBytes - 1 },
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
        var two = _baseConfig with { Email = _baseConfig.Email! with { To = [_frame, _calendar] } };
        var source = Source();

        var result = await Pipeline(two, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var one = _baseConfig with { Email = _baseConfig.Email! with { To = [_frame] } };
        var again = await Pipeline(one, source, Mailer(stub)).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var third = _baseConfig with { Email = _baseConfig.Email! with { To = [_frame, "third@example.com"] } };
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

    [Fact]
    public async Task Rotation_recycles_the_longest_ago_sent_once_nothing_is_new()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        _root.AddFixture("tiny_rot6.heic", $"{_album}/c.heic", DaysAgo(1));
        var pipeline = Pipeline(Rotating(keep: 1, maxPerRun: 1), Source(), Mailer(stub));

        var sent = new List<(int Sent, int Recycled)>();
        for (var hour = 0; hour < 5; hour++)
        {
            _now = _t0.AddHours(hour);
            var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
            Assert.Null(result.Error);
            sent.Add((result.Sent, result.Recycled));
        }

        // a, b, c are new; then a (sent longest ago), then b. c, the newest send, is never resent while "on the frame".
        Assert.Equal([(1, 0), (1, 0), (1, 0), (1, 1), (1, 1)], sent);
        Assert.Equal(5, stub.Received.Count);
        Assert.Equal(AttachmentNames(stub.Received[0]), AttachmentNames(stub.Received[3]));
        Assert.Equal(AttachmentNames(stub.Received[1]), AttachmentNames(stub.Received[4]));
        Assert.Equal(1, _store.LastSentTimes().Count(pair => pair.Value == _t0.AddHours(3)));
    }

    [Fact]
    public async Task Rotation_tops_up_a_short_run_with_recycled_items()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var cfg = Rotating(keep: 1, maxPerRun: 2);
        var pipeline = Pipeline(cfg, Source(), Mailer(stub));
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);   // a, b
        _now = _t0.AddHours(1);
        _root.AddFixture("tiny_rot6.heic", $"{_album}/c.heic", DaysAgo(1));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, 2, 1), (result.Found, result.Sent, result.Recycled));   // c is new, a is recycled
    }

    [Fact]
    public async Task Without_rotation_nothing_is_recycled()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var cfg = Rotating(keep: 1, maxPerRun: 25) with { Rotation = RotationConfig.Disabled };
        var pipeline = Pipeline(cfg, Source(), Mailer(stub));
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((0, 0), (result.Sent, result.Recycled));
    }

    [Fact]
    public async Task Rotation_prunes_the_frame_after_sending()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 2, maxPerRun: 25);
        var client = new FakeSkylightClient().Add(cfg.Email!.From, 5, _t0.AddHours(-1)).Add("someone@example.com", 3, _t0.AddDays(-3));
        var pipeline = Pipeline(cfg, Source(), Mailer(stub), rotator: Rotator(cfg, client));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, 3, (string?)null), (result.Sent, result.Pruned, result.Error));
        Assert.Equal(["m1", "m2", "m3"], client.Deleted.Select(d => d.MessageId));
    }

    [Fact]
    public async Task Rotation_dry_run_counts_but_deletes_nothing()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 2, maxPerRun: 25);
        var client = new FakeSkylightClient().Add(cfg.Email!.From, 5, _t0);
        var pipeline = Pipeline(cfg, Source(), Mailer(stub), rotator: Rotator(cfg, client));

        var result = await pipeline.RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Equal(3, result.Pruned);
        Assert.Empty(client.Deleted);
    }

    [Fact]
    public async Task Prune_failure_is_reported_but_sent_items_stay_sent()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 1, maxPerRun: 25);
        var client = new FakeSkylightClient { FailDeletesWith = new SkylightApiException("Skylight answered HTTP 500") }
            .Add(cfg.Email!.From, 3, _t0);
        var pipeline = Pipeline(cfg, Source(), Mailer(stub), rotator: Rotator(cfg, client));

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("prune_failed: Skylight answered HTTP 500", result.Error);
        Assert.True(_store.IsSent($"{_album}/a.png", _frame));
        Assert.Equal(result.Error, _store.LastRun()!.Error);
    }

    [Fact]
    public async Task Refused_skylight_sign_in_pauses_pruning_and_alerts_once()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 1, maxPerRun: 1);
        var client = new FakeSkylightClient { FailEverythingWith = Refused() }.Add(cfg.Email!.From, 3, _t0);
        var pipeline = Pipeline(cfg, Source(), Mailer(stub), rotator: Rotator(cfg, client), gate: Gate());

        var first = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.StartsWith("skylight_sign_in_failed: Skylight did not accept the sign-in", first.Error);
        Assert.False(first.PrunePaused);
        Assert.True(_store.IsSent($"{_album}/a.png", _frame));
        var failure = Assert.IsType<SkylightSignInFailure>(_store.SkylightSignInFailure());
        Assert.Equal((_t0, Refused().Message), (failure.FailedAt, failure.Reason));
        var call = Assert.Single(_notifier.Calls);
        Assert.Equal(("Skylight sign-in failed", AlertTexts.SkylightSignInBody(Refused().Message)), call);
        Assert.Contains("skylight-sync prune --dry-run", call.Body);
        Assert.Contains("secrets.toml", call.Body);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task While_paused_runs_keep_sending_without_touching_skylight_or_alerting_again()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        _root.AddFixture("tiny_rot6.heic", $"{_album}/c.heic", DaysAgo(1));
        var cfg = Rotating(keep: 1, maxPerRun: 1);
        var client = new FakeSkylightClient { FailEverythingWith = Refused() }.Add(cfg.Email!.From, 3, _t0);
        var pipeline = Pipeline(cfg, Source(), Mailer(stub), rotator: Rotator(cfg, client), gate: Gate());
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        var later = new List<RunResult>();
        for (var hour = 1; hour <= 30; hour++)   // more than a day of hourly runs: past the alert rate limit
        {
            _now = _t0.AddHours(hour);
            later.Add(await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None));
        }

        Assert.All(later, run => Assert.Equal((null, true, 1, 0), (run.Error, run.PrunePaused, run.Sent, run.Pruned)));
        Assert.Equal(1, client.Calls);
        Assert.Single(_notifier.Calls);
        Assert.Equal(0, _store.ConsecutiveFailures());
        Assert.Empty(client.Deleted);
    }

    [Fact]
    public async Task Changed_credentials_are_tried_again_and_success_lifts_the_pause()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 1, maxPerRun: 1);
        var client = new FakeSkylightClient { FailEverythingWith = Refused() }.Add(cfg.Email!.From, 3, _t0.AddHours(-1));
        var rotator = Rotator(cfg, client);
        await Pipeline(cfg, Source(), Mailer(stub), rotator: rotator, gate: Gate())
            .RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);
        client.FailEverythingWith = null;

        var result = await Pipeline(cfg, Source(), Mailer(stub), rotator: rotator, gate: Gate("placeholder-corrected-password"))
            .RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((null, false, 2), (result.Error, result.PrunePaused, result.Pruned));
        Assert.Null(_store.SkylightSignInFailure());
        Assert.Single(_notifier.Calls);
    }

    [Fact]
    public async Task Changed_credentials_that_are_refused_too_alert_again_right_away()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 1, maxPerRun: 1);
        var client = new FakeSkylightClient { FailEverythingWith = Refused() };
        var rotator = Rotator(cfg, client);
        await Pipeline(cfg, Source(), Mailer(stub), rotator: rotator, gate: Gate())
            .RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);   // well inside the alert rate limit

        var second = Pipeline(cfg, Source(), Mailer(stub), rotator: rotator, gate: Gate("placeholder-another-wrong-password"));
        var result = await second.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var paused = await second.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.StartsWith("skylight_sign_in_failed: ", result.Error);
        Assert.True(paused.PrunePaused);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, _notifier.Calls.Count);
        Assert.Equal(_t0.AddHours(1), _store.SkylightSignInFailure()!.FailedAt);
    }

    [Fact]
    public async Task A_skylight_outage_does_not_pause_pruning()
    {
        await using var stub = await SmtpStub.StartAsync();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var cfg = Rotating(keep: 1, maxPerRun: 1);
        var client = new FakeSkylightClient { FailEverythingWith = new SkylightApiException("Skylight answered HTTP 503 during sign-in.") };
        var pipeline = Pipeline(cfg, Source(), Mailer(stub), rotator: Rotator(cfg, client), gate: Gate());

        var first = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var second = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("prune_failed: Skylight answered HTTP 503 during sign-in.", first.Error);
        Assert.False(second.PrunePaused);
        Assert.Equal(2, client.Calls);
        Assert.Null(_store.SkylightSignInFailure());
        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task Api_delivery_uploads_each_photo_oldest_first_and_records_its_message_id()
    {
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        var reporter = new RecordingReporter();
        var pipeline = ApiPipeline(_apiConfig, Source(), client, reporter: reporter);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 2, Sent: 2, Skipped: 0, Batches: 2, DryRun: false, Error: null), result);
        Assert.Equal(
            [("100", "2026-11-02_120000_Skylight.jpg", "image/jpeg"), ("100", "2026-11-03_120000_Skylight.gif", "image/gif")],
            client.Uploads.Select(u => (Assert.Single(u.FrameIds), u.FileName, u.Mime)));
        Assert.Equal("up1", _store.SentMessageId($"{_album}/a.png", _kitchen));
        Assert.Equal("up2", _store.SentMessageId($"{_album}/b.gif", _kitchen));
        Assert.Equal([(1, "Kitchen", "a.png"), (2, "Kitchen", "b.gif")], reporter.Emails.Select(d => (d.Number, d.Destination, Assert.Single(d.Items).Item.Name)));
        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task Api_delivery_sends_nothing_twice()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        var source = Source();
        var pipeline = ApiPipeline(_apiConfig, source, client);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        var second = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((0, 0), (second.Found, second.Sent));
        Assert.Single(client.Uploads);
        Assert.Equal(1, source.Downloads);
    }

    [Fact]
    public async Task Api_delivery_marks_nothing_sent_for_an_upload_that_failed()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient
        {
            FailUploadsWith = new SkylightApiException("Skylight answered HTTP 503 to upload a photo."),
            FailUploadsAfter = 1,
        };
        var reporter = new RecordingReporter();
        var pipeline = ApiPipeline(_apiConfig, Source(), client, reporter: reporter);

        var failed = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("upload_failed: Skylight answered HTTP 503 to upload a photo.", failed.Error);
        Assert.Equal((2, 1, 1), (failed.Found, failed.Sent, failed.Batches));
        Assert.True(_store.IsSent($"{_album}/a.png", _kitchen));
        Assert.False(_store.IsSent($"{_album}/b.gif", _kitchen));
        Assert.Equal(1, _store.TotalSent());
        Assert.Single(reporter.Emails);
        Assert.Null(_store.SkylightSignInFailure());

        client.FailUploadsWith = null;
        var retried = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, 1, null), (retried.Found, retried.Sent, retried.Error));
        Assert.Equal(["2026-11-02_120000_Skylight.jpg", "2026-11-03_120000_Skylight.gif"], client.Uploads.Select(u => u.FileName));
        Assert.Equal("up2", _store.SentMessageId($"{_album}/b.gif", _kitchen));
    }

    [Fact]
    public async Task Api_delivery_marks_nothing_sent_when_skylight_names_no_photo_for_a_frame()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        client.Frames.Add(new SkylightFrame("200", "Calendar"));
        var delivery = new SkylightApiDelivery(_apiConfig.Delivery, _apiConfig.Rotation, new ForgetfulUploads(client), _store, () => _now);
        var pipeline = PipelineFor(_apiConfig, Source(), delivery);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.StartsWith("upload_failed: ", result.Error);
        Assert.Contains("Calendar", result.Error);
        Assert.True(_store.IsSent($"{_album}/a.png", _kitchen));
        Assert.False(_store.IsSent($"{_album}/a.png", "skylight:frame:200"));
    }

    [Fact]
    public async Task Api_dry_run_uploads_nothing_and_marks_nothing()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        var reporter = new RecordingReporter();
        var pipeline = ApiPipeline(_apiConfig, Source(), client, reporter: reporter);

        var result = await pipeline.RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Equal(new RunResult(Found: 1, Sent: 1, Skipped: 0, Batches: 1, DryRun: true, Error: null), result);
        Assert.Empty(client.Uploads);
        Assert.Equal(0, _store.TotalSent());
        Assert.Equal("Kitchen", Assert.Single(reporter.Emails).Destination);
    }

    [Fact]
    public async Task Api_delivery_uploads_to_every_frame_in_one_call_by_default()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        client.Frames.Add(new SkylightFrame("200", "Calendar"));
        var pipeline = ApiPipeline(_apiConfig, Source(), client);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, 2, 2), (result.Found, result.Sent, result.Batches));
        Assert.Equal(["100", "200"], Assert.Single(client.Uploads).FrameIds);
        Assert.Equal("up1", _store.SentMessageId($"{_album}/a.png", _kitchen));
        Assert.Equal("up2", _store.SentMessageId($"{_album}/a.png", "skylight:frame:200"));
    }

    [Fact]
    public async Task Api_delivery_uploads_only_to_the_frames_named_in_config()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        client.Frames.Add(new SkylightFrame("200", "Calendar"));
        var cfg = _apiConfig with { Delivery = new DeliveryConfig(DeliveryMethod.Api, ["calendar"]) };
        var pipeline = ApiPipeline(cfg, Source(), client);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(["200"], Assert.Single(client.Uploads).FrameIds);
        Assert.False(_store.IsSent($"{_album}/a.png", _kitchen));
    }

    [Fact]
    public async Task Api_delivery_fails_clearly_for_a_frame_name_the_account_does_not_have()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient();
        var cfg = _apiConfig with { Delivery = new DeliveryConfig(DeliveryMethod.Api, ["Bedroom"]) };
        var source = Source();
        var pipeline = ApiPipeline(cfg, source, client);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("upload_failed: [delivery] frames names \"Bedroom\", but the Skylight account has no frame called that. Its frames: \"Kitchen\".", result.Error);
        Assert.Empty(client.Uploads);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task Api_delivery_does_not_recycle_photos_it_cannot_prune()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var cfg = _apiConfig with
        {
            Limits = new LimitsConfig(1),
            Rotation = new RotationConfig(Enabled: true, Keep: 1, MaxDeletesPerRun: 100, FrameName: null, ArrivalWaitMinutes: 0),
        };
        var client = new FakeSkylightClient();
        var source = Source();
        var pipeline = ApiPipeline(cfg, source, client);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(2);

        var third = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((0, 0, 0, null), (third.Found, third.Sent, third.Recycled, third.Error));
        Assert.Equal(2, client.Uploads.Count);
        Assert.Equal(2, source.Downloads);
        Assert.Equal("up1", _store.SentMessageId($"{_album}/a.png", _kitchen));
    }

    [Fact]
    public async Task Api_rotation_recycles_and_prunes_by_recorded_id_leaving_other_photos_alone()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        _root.AddFixture("tiny_rot6.heic", $"{_album}/c.heic", DaysAgo(1));
        var client = new FakeSkylightClient().Add("grandma@example.com", 2, _t0.AddDays(-9), idPrefix: "gma");
        client.Messages["100"].Add(new SkylightMessage("app1", _t0.AddDays(-30), null, Destroyed: false));
        var pipeline = ApiPipeline(ApiRotating(keep: 1, maxPerRun: 1), Source(), client, rotate: true);

        var runs = new List<(int Sent, int Recycled, int Pruned)>();
        for (var hour = 0; hour < 5; hour++)
        {
            _now = _t0.AddHours(hour);
            var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
            Assert.Null(result.Error);
            runs.Add((result.Sent, result.Recycled, result.Pruned));

            // Never more than keep of this tool's photos once a run is over.
            Assert.Single(client.Messages["100"], m => m.Id.StartsWith("up", StringComparison.Ordinal));
        }

        // a, b, c are new; then a (sent longest ago) and b come round again, each as a new upload.
        Assert.Equal([(1, 0, 0), (1, 0, 1), (1, 0, 1), (1, 1, 1), (1, 1, 1)], runs);
        Assert.Equal(client.Uploads[0].FileName, client.Uploads[3].FileName);
        Assert.Equal(client.Uploads[1].FileName, client.Uploads[4].FileName);
        Assert.Equal(["up1", "up2", "up3", "up4"], client.Deleted.Select(d => d.MessageId));
        Assert.Equal(["gma1", "gma2", "app1", "up5"], client.Messages["100"].Select(m => m.Id));
        Assert.Equal(["up5"], _store.RecordedUploads(_kitchen).Select(u => u.MessageId));
    }

    [Fact]
    public async Task Api_recycled_reupload_keeps_the_earlier_copy_tracked_until_it_is_pruned()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient { FailDeletesWith = new SkylightApiException("Skylight returned 500 for the delete.") };
        var pipeline = ApiPipeline(ApiRotating(keep: 1, maxPerRun: 1), Source(), client, rotate: true);
        for (var hour = 0; hour < 3; hour++)
        {
            _now = _t0.AddHours(hour);
            var failed = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
            Assert.Equal(hour == 0 ? null : "prune_failed: Skylight returned 500 for the delete.", failed.Error);
        }

        // a was uploaded twice (up1, up3) while nothing could be deleted; sent_items only knows up3.
        Assert.Equal("up3", _store.SentMessageId($"{_album}/a.png", _kitchen));
        Assert.Equal(["up1", "up2", "up3"], client.Messages["100"].Select(m => m.Id));
        client.FailDeletesWith = null;
        _now = _t0.AddHours(3);

        var result = await pipeline.RunAsync(dryRun: false, limit: 0, CancellationToken.None);

        Assert.Equal((0, 2, (string?)null), (result.Sent, result.Pruned, result.Error));
        Assert.Equal(["up1", "up2"], client.Deleted.Select(d => d.MessageId));
        Assert.Equal(["up3"], client.Messages["100"].Select(m => m.Id));
    }

    [Fact]
    public async Task Api_photos_uploaded_before_a_reset_are_still_pruned_when_they_are_sent_again()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient();
        var pipeline = ApiPipeline(ApiRotating(keep: 2, maxPerRun: 5), Source(), client, rotate: true);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _store.ResetSent();
        _now = _t0.AddHours(1);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((2, 2, (string?)null), (result.Sent, result.Pruned, result.Error));
        Assert.Equal(["up1", "up2"], client.Deleted.Select(d => d.MessageId));
        Assert.Equal(["up3", "up4"], client.Messages["100"].Select(m => m.Id));
    }

    [Fact]
    public async Task Api_rotation_dry_run_uploads_and_deletes_nothing()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient();
        var cfg = ApiRotating(keep: 1, maxPerRun: 5);
        await ApiPipeline(cfg with { Rotation = RotationConfig.Disabled }, Source(), client).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);

        var result = await ApiPipeline(cfg, Source(), client, rotate: true).RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Equal((1, 1, 1, null), (result.Sent, result.Recycled, result.Pruned, result.Error));
        Assert.Equal(2, client.Uploads.Count);
        Assert.Empty(client.Deleted);
    }

    [Fact]
    public async Task Api_recycled_photo_goes_to_every_frame_again()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient();
        client.Frames.Add(new SkylightFrame("200", "Calendar"));
        var pipeline = ApiPipeline(ApiRotating(keep: 1, maxPerRun: 2), Source(), client, rotate: true);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, null), (result.Recycled, result.Error));
        Assert.Equal(["100", "200"], client.Uploads[^1].FrameIds);
        Assert.Single(client.Messages["100"]);
        Assert.Single(client.Messages["200"]);
    }

    [Fact]
    public async Task Api_recycled_photo_goes_only_to_the_frames_rotation_prunes()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient();
        client.Frames.Add(new SkylightFrame("200", "Den"));
        var cfg = ApiRotating(keep: 1, maxPerRun: 2);
        cfg = cfg with { Rotation = cfg.Rotation with { FrameName = "Kitchen" } };
        var pipeline = ApiPipeline(cfg, Source(), client, rotate: true);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((0, 1, 1, null), (result.Found, result.Sent, result.Recycled, result.Error));
        Assert.Equal([["100", "200"], ["100", "200"], ["100"]], client.Uploads.Select(u => u.FrameIds));
        Assert.Single(client.Messages["100"]);
        Assert.Equal(2, client.Messages["200"].Count);
    }

    [Fact]
    public async Task Api_delivery_does_not_recycle_when_rotation_prunes_none_of_its_frames()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        _root.AddFixture("tiny.gif", $"{_album}/b.gif", DaysAgo(2));
        var client = new FakeSkylightClient();
        client.Frames.Add(new SkylightFrame("200", "Den"));
        var cfg = ApiRotating(keep: 1, maxPerRun: 2) with { Delivery = new DeliveryConfig(DeliveryMethod.Api, ["Den"]) };
        cfg = cfg with { Rotation = cfg.Rotation with { FrameName = "Kitchen" } };
        var source = Source();
        var pipeline = ApiPipeline(cfg, source, client, rotate: true);
        await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddHours(1);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((0, 0, 0, null), (result.Found, result.Sent, result.Recycled, result.Error));
        Assert.Equal([["200"], ["200"]], client.Uploads.Select(u => u.FrameIds));
        Assert.Equal(2, source.Downloads);
        Assert.Equal(2, client.Messages["200"].Count);
    }

    [Fact]
    public async Task Api_delivery_reports_skylight_unreachable_as_an_upload_failure()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient { FailEverythingWith = new HttpRequestException("Name or service not known") };
        var source = Source();
        var pipeline = ApiPipeline(_apiConfig, source, client);

        var result = await pipeline.RunAsync(dryRun: true, limit: null, CancellationToken.None);

        Assert.Equal("upload_failed: Could not reach Skylight: Name or service not known", result.Error);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task Api_delivery_reports_an_upload_timeout_as_an_upload_failure_and_marks_nothing_sent()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient { FailUploadsWith = new TaskCanceledException("The request timed out.") };
        var pipeline = ApiPipeline(_apiConfig, Source(), client);

        var result = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("upload_failed: Could not reach Skylight: The request timed out.", result.Error);
        Assert.Equal(0, _store.TotalSent());
    }

    [Fact]
    public async Task Api_delivery_refused_sign_in_pauses_delivery_and_alerts_once()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient { FailEverythingWith = Refused() };
        var source = Source();
        var pipeline = ApiPipeline(_apiConfig, source, client, gate: Gate());

        var first = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        _now = _t0.AddDays(2);   // past the alert rate limit: the pause, not the limit, keeps it to one alert
        var second = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var third = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("skylight_sign_in_failed: " + Refused().Message, first.Error);
        Assert.Equal("skylight_sign_in_needed", second.Error);
        Assert.Equal("skylight_sign_in_needed", third.Error);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, source.Downloads);
        Assert.Equal(0, _store.TotalSent());
        var call = Assert.Single(_notifier.Calls);
        Assert.Equal(("Skylight sign-in failed", AlertTexts.SkylightSignInDeliveryBody(Refused().Message)), call);
        Assert.Equal((_t0, Refused().Message), (_store.SkylightSignInFailure()!.FailedAt, _store.SkylightSignInFailure()!.Reason));
    }

    [Fact]
    public async Task Api_delivery_resumes_when_the_skylight_credentials_change()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient { FailEverythingWith = Refused() };
        await ApiPipeline(_apiConfig, Source(), client, gate: Gate()).RunAsync(dryRun: false, limit: null, CancellationToken.None);
        client.FailEverythingWith = null;

        var result = await ApiPipeline(_apiConfig, Source(), client, gate: Gate("placeholder-corrected-password"))
            .RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal((1, null), (result.Sent, result.Error));
        Assert.Null(_store.SkylightSignInFailure());
        Assert.True(_store.IsSent($"{_album}/a.png", _kitchen));
    }

    [Fact]
    public async Task Api_delivery_skylight_outage_does_not_pause_delivery()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient { FailEverythingWith = new SkylightApiException("Skylight answered HTTP 503 during sign-in.") };
        var pipeline = ApiPipeline(_apiConfig, Source(), client, gate: Gate());

        var first = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        var second = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);

        Assert.Equal("upload_failed: Skylight answered HTTP 503 during sign-in.", first.Error);
        Assert.Equal(first.Error, second.Error);
        Assert.Equal(2, client.Calls);
        Assert.Null(_store.SkylightSignInFailure());
        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task Api_delivery_without_a_mail_server_alerts_by_notification_only()
    {
        _root.AddFixture("tiny.png", $"{_album}/a.png", DaysAgo(3));
        var client = new FakeSkylightClient { FailUploadsWith = new SkylightApiException("Skylight answered HTTP 503 to upload a photo.") };
        var pipeline = ApiPipeline(_apiConfig, Source(), client);

        RunResult? last = null;
        for (var i = 0; i < SyncPipeline.RepeatedFailureThreshold; i++)
        {
            last = await pipeline.RunAsync(dryRun: false, limit: null, CancellationToken.None);
        }

        Assert.StartsWith("upload_failed: ", last!.Error);
        Assert.Equal("Sync failing", Assert.Single(_notifier.Calls).Title);
        Assert.NotNull(_store.LastAlertAt(AlertKind.RepeatedFailure.ToString()));
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
        AppConfig cfg, IPhotoSource source, IMailer mailer, IMailer? alertMailer = null, long videoCapBytes = 22_000_000,
        FrameRotator? rotator = null, SkylightSignInGate? gate = null, IRunReporter? reporter = null)
    {
        var delivery = new EmailDelivery(cfg.Email!, cfg.Source, mailer, _store, () => _now);
        return PipelineFor(cfg, source, delivery, alertMailer, videoCapBytes, rotator, gate, reporter);
    }

    /// <summary>A pipeline that uploads through <paramref name="client"/>; the config has no <c>[email]</c> or <c>[smtp]</c>.</summary>
    private SyncPipeline ApiPipeline(
        AppConfig cfg, IPhotoSource source, ISkylightClient client, SkylightSignInGate? gate = null, IRunReporter? reporter = null,
        bool rotate = false) =>
        PipelineFor(
            cfg, source, new SkylightApiDelivery(cfg.Delivery, cfg.Rotation, client, _store, () => _now),
            rotator: rotate ? Rotator(cfg, client) : null, gate: gate, reporter: reporter);

    private static AppConfig ApiRotating(int keep, int maxPerRun) =>
        _apiConfig with
        {
            Limits = new LimitsConfig(maxPerRun),
            Rotation = new RotationConfig(Enabled: true, keep, MaxDeletesPerRun: 100, FrameName: null, ArrivalWaitMinutes: 0),
        };

    private SyncPipeline PipelineFor(
        AppConfig cfg, IPhotoSource source, IPhotoDelivery delivery, IMailer? alertMailer = null, long videoCapBytes = 22_000_000,
        FrameRotator? rotator = null, SkylightSignInGate? gate = null, IRunReporter? reporter = null)
    {
        var normalizer = new ImageNormalizer(new FakeHeicConverter(), cfg.Image, videoCapBytes);
        var alerter = new Alerter(_store, alertMailer, cfg.Alerts.Email ?? "", cfg.Email?.From ?? "", _notifier, () => _now);
        return new SyncPipeline(cfg, source, _store, normalizer, delivery, alerter, () => _now, _lockPath, rotator, gate, reporter);
    }

    private SkylightSignInGate Gate(string password = "placeholder-password-not-real") =>
        new(_store, "me@example.com", password, () => _now);

    private static SkylightSignInException Refused() =>
        new("Skylight did not accept the sign-in. Check [skylight] email and password in secrets.toml.");

    private static AppConfig Rotating(int keep, int maxPerRun) =>
        _baseConfig with
        {
            Limits = new LimitsConfig(maxPerRun),
            Rotation = new RotationConfig(Enabled: true, keep, MaxDeletesPerRun: 100, FrameName: null, ArrivalWaitMinutes: 0),
        };

    private FrameRotator Rotator(AppConfig cfg, ISkylightClient client) =>
        new(cfg.Rotation, cfg.Email?.From, client, _store, () => _now, (_, _) => Task.CompletedTask);

    private sealed class RecordingReporter : IRunReporter
    {
        /// <summary>Every delivery reported: emails, or uploads under <c>[delivery] method = "api"</c>.</summary>
        public List<DeliveryReport> Emails { get; } = [];

        public List<(Item Item, SkipReason Reason, string Detail)> Skips { get; } = [];

        public void Delivered(DeliveryReport delivery) => Emails.Add(delivery);

        public void Skipped(Item item, SkipReason reason, string detail) => Skips.Add((item, reason, detail));
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

    /// <summary>A Skylight that accepts an upload but reports a message for the first frame only.</summary>
    private sealed class ForgetfulUploads(ISkylightClient inner) : ISkylightClient
    {
        public Task<IReadOnlyList<SkylightFrame>> ListFramesAsync(CancellationToken ct) => inner.ListFramesAsync(ct);

        public Task<IReadOnlyList<SkylightMessage>> ListMessagesAsync(string frameId, CancellationToken ct) => inner.ListMessagesAsync(frameId, ct);

        public Task DeleteMessageAsync(string frameId, string messageId, CancellationToken ct) => inner.DeleteMessageAsync(frameId, messageId, ct);

        public async Task<IReadOnlyList<SkylightUpload>> UploadPhotoAsync(IReadOnlyList<string> frameIds, string path, string mime, CancellationToken ct) =>
            (await inner.UploadPhotoAsync(frameIds, path, mime, ct)).Take(1).ToList();
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
