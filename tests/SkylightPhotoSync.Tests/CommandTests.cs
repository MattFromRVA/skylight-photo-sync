using MimeKit;
using SkiaSharp;
using SkylightPhotoSync.Commands;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// Drives the run, status, test-email, reset and login commands through <see cref="App.RunAsync"/>
/// against a temp <c>SKYLIGHT_SYNC_HOME</c>, a written config.toml and redirected stdin.
/// </summary>
public sealed class CommandTests : IDisposable
{
    private const string _frame = "frame@example.com";
    private const string _album = "Skylight";
    private const string _sessionId = "133-0000000-0000000";
    private const string _ubid = "130-0000000-1111111";
    private const string _at = "Atza|TESTVALUEONLYxxxx";

    private readonly TestHome _home = new();
    private readonly TempDir _root = new();
    private readonly TextReader _originalStdin = Console.In;

    public void Dispose()
    {
        Console.SetIn(_originalStdin);
        _root.Dispose();
        _home.Dispose();
    }

    [Fact]
    public async Task Run_local_source_dry_run_exit_0()
    {
        WriteConfig();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DateTime.UtcNow.AddDays(-1));

        var (code, out_, _) = await Run("run", "--dry-run", "--source", $"local:{_root.Path}");

        Assert.Equal(0, code);
        Assert.Contains("found 1, sent 1, skipped 0, batches 1 (dry run)", out_);
        using var store = OpenStore();
        Assert.False(store.IsSent($"{_album}/a.png", _frame));
        Assert.True(File.Exists(Path.Combine(_home.Dir, "sync.log")));
    }

    [Fact]
    public async Task Run_flags_override_config()
    {
        WriteConfig();   // album mode, lookback 0
        _root.AddFixture("tiny.jpg", "exif-2020.jpg", DateTime.UtcNow);   // EXIF date 2020: outside a 1-day window
        _root.AddFixture("tiny.png", "recent.png", DateTime.UtcNow);

        var (code, out_, _) = await Run(
            "run", "--dry-run", "--mode", "all", "--lookback-days", "1", "--source", $"local:{_root.Path}");

        Assert.Equal(0, code);
        Assert.Contains("found 1, sent 1, skipped 0, batches 1 (dry run)", out_);
    }

    [Fact]
    public async Task Run_without_source_exits_2()
    {
        WriteConfig();

        var (code, _, err) = await Run("run", "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("Amazon source not yet available", err);
    }

    [Fact]
    public async Task Run_exit_3_when_needs_login()
    {
        WriteConfig();
        _root.AddFixture("tiny.png", $"{_album}/a.png", DateTime.UtcNow.AddDays(-1));
        using (var store = OpenStore())
        {
            store.SetNeedsLogin(true, DateTimeOffset.UtcNow);
        }

        var (code, _, err) = await Run("run", "--source", $"local:{_root.Path}");

        Assert.Equal(3, code);
        Assert.Contains("needs_login", err);
    }

    [Fact]
    public async Task Run_exit_1_on_bad_config()
    {
        File.WriteAllText(Path.Combine(_home.Dir, "config.toml"), "[source]\nmode = \"sideways\"\n");

        var (code, _, err) = await Run("run", "--source", $"local:{_root.Path}");

        Assert.Equal(1, code);
        Assert.Contains("mode", err);
    }

    [Fact]
    public async Task Reset_requires_yes()
    {
        using (var store = OpenStore())
        {
            store.MarkSent([Item("a")], _frame, "batch-1", DateTimeOffset.UtcNow);
        }

        var refused = await Run("reset");
        var done = await Run("reset", "--yes");

        Assert.Equal(1, refused.Code);
        Assert.Contains("--yes", refused.Err);
        Assert.Equal(0, done.Code);
        using var reopened = OpenStore();
        Assert.False(reopened.IsSent("a", _frame));
    }

    [Fact]
    public async Task Login_parses_curl_paste()
    {
        using (var store = OpenStore())
        {
            store.SetNeedsLogin(true, DateTimeOffset.UtcNow);
        }

        Console.SetIn(new StringReader(
            "curl 'https://www.amazon.com/drive/v1/nodes' \\\n" +
            "  -H 'accept: application/json' \\\n" +
            $"  -H 'cookie: session-id={_sessionId}; x-main=\"zzz\"; ubid-main={_ubid}; at-main={_at}; sess-at-main=other' \\\n" +
            "  --compressed\n"));

        var (code, out_, err) = await Run("login");

        Assert.Equal(0, code);
        Assert.Contains("Saved. Run 'skylight-sync run --dry-run' to verify.", out_);
        AssertSecretsSaved(expectedPassword: null);
        Assert.DoesNotContain(_sessionId, out_ + err);
        Assert.DoesNotContain(_at, out_ + err);
        using var store2 = OpenStore();
        Assert.False(store2.NeedsLogin());
    }

    [Fact]
    public async Task Login_parses_curl_cookie_flag()
    {
        Console.SetIn(new StringReader(
            $"curl -b \"session-id={_sessionId}; ubid_main={_ubid}; at_main={_at}\" https://www.amazon.com/photos\n"));

        var (code, _, _) = await Run("login");

        Assert.Equal(0, code);
        AssertSecretsSaved(expectedPassword: null);
    }

    [Fact]
    public async Task Login_accepts_three_values()
    {
        var secretsPath = Path.Combine(_home.Dir, "secrets.toml");
        ConfigLoader.WriteSecrets(secretsPath, new Secrets(null, null, null, SmtpPassword: "app-password"));
        Console.SetIn(new StringReader($"{_sessionId}\n{_ubid}\n{_at}\n"));

        var (code, out_, err) = await Run("login");

        Assert.Equal(0, code);
        Assert.Contains("Saved.", out_);
        AssertSecretsSaved(expectedPassword: "app-password");
        Assert.DoesNotContain(_ubid, out_ + err);
    }

    [Fact]
    public async Task Login_rejects_unrecognised_input()
    {
        Console.SetIn(new StringReader("curl https://www.amazon.com/photos -H 'accept: */*'\n"));

        var (code, _, err) = await Run("login");

        Assert.Equal(1, code);
        Assert.Contains("session-id", err);
        Assert.False(File.Exists(Path.Combine(_home.Dir, "secrets.toml")));
    }

    [Fact]
    public async Task Status_prints_last_run()
    {
        using (var store = OpenStore())
        {
            var runId = store.StartRun(SourceMode.Album);
            store.MarkSent([Item("a"), Item("b")], _frame, "batch-1", DateTimeOffset.UtcNow);
            store.FinishRun(runId, found: 3, sent: 2, skipped: 1, error: null);
        }

        var (code, out_, _) = await Run("status");

        Assert.Equal(0, code);
        Assert.Contains("last run:", out_);
        Assert.Contains("ok (found 3, sent 2, skipped 1)", out_);
        Assert.Contains("total sent: 2", out_);
        Assert.Contains("needs_login: no", out_);
        Assert.Contains("launchd:", out_);
    }

    [Fact]
    public async Task Status_without_runs_says_so()
    {
        using (var store = OpenStore())
        {
            store.SetNeedsLogin(true, DateTimeOffset.UtcNow);
        }

        var (code, out_, _) = await Run("status");

        Assert.Equal(0, code);
        Assert.Contains("last run: no runs yet", out_);
        Assert.Contains("total sent: 0", out_);
        Assert.Contains("needs_login: yes", out_);
    }

    [Fact]
    public async Task TestEmail_sends_one_message()
    {
        await using var stub = await SmtpStub.StartAsync();
        WriteConfig(smtpPort: stub.Port);

        var (code, out_, _) = await Run("test-email");

        Assert.Equal(0, code);
        Assert.Contains(_frame, out_);
        var message = Assert.Single(stub.Received);
        Assert.Equal(_frame, Assert.Single(message.To.Mailboxes).Address);
        var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("image/jpeg", attachment.ContentType.MimeType);
        Assert.NotNull(attachment.Content);
        using var bytes = new MemoryStream();
        await attachment.Content.DecodeToAsync(bytes);
        using var image = SKBitmap.Decode(bytes.ToArray());
        Assert.NotNull(image);
        Assert.Equal((800, 600), (image.Width, image.Height));
    }

    [Fact]
    public void Size_probe_jpeg_is_close_to_its_target()
    {
        const int target = 300_000;

        var jpeg = TestEmailCommand.NoiseJpeg(target);

        Assert.InRange(jpeg.Length, target * 0.75, target * 1.25);
        using var image = SKBitmap.Decode(jpeg);
        Assert.NotNull(image);
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var out_ = new StringWriter();
        var err = new StringWriter();
        var code = await App.RunAsync(args, out_, err);
        return (code, out_.ToString(), err.ToString());
    }

    private static Item Item(string id) =>
        new(id, $"{id}.jpg", ItemKind.Photo, DateTimeOffset.UtcNow.AddDays(-1), 100, null, "image/jpeg");

    private StateStore OpenStore() => new(Path.Combine(_home.Dir, "state.db"));

    private void WriteConfig(int smtpPort = 25) =>
        File.WriteAllText(
            Path.Combine(_home.Dir, "config.toml"),
            $"""
            [source]
            mode = "album"
            album_name = "{_album}"

            [email]
            to = ["{_frame}"]
            from = "sync@example.com"

            [smtp]
            host = "127.0.0.1"
            port = {smtpPort}
            username = "user"
            """);

    private void AssertSecretsSaved(string? expectedPassword)
    {
        var path = Path.Combine(_home.Dir, "secrets.toml");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        var secrets = ConfigLoader.LoadSecrets(path);
        Assert.Equal(new Secrets(_sessionId, _ubid, _at, expectedPassword), secrets);
    }
}
