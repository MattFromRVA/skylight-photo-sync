using System.Xml.Linq;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Launchd;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class LaunchdInstallerTests
{
    private const int _uid = 501;
    private const string _program = "/Users/matt/.dotnet/tools/skylight-sync";

    [Fact]
    public void RenderPlist_contains_interval_and_label()
    {
        var plist = LaunchdInstaller.RenderPlist(_program, 90, "/Users/matt/Library/Logs/sync.log");

        var dict = ParsePlistDict(plist);
        Assert.Equal("com.mattfromrva.skylight-photo-sync", LaunchdInstaller.Label);
        Assert.Equal(LaunchdInstaller.Label, dict["Label"].Value);
        Assert.Equal("5400", dict["StartInterval"].Value);
        Assert.Equal("true", dict["RunAtLoad"].Name.LocalName);
        Assert.Equal([_program, "run"], dict["ProgramArguments"].Elements("string").Select(e => e.Value));
        Assert.Equal("/Users/matt/Library/Logs/sync.log", dict["StandardOutPath"].Value);
        Assert.Equal("/Users/matt/Library/Logs/sync.log", dict["StandardErrorPath"].Value);
    }

    [Fact]
    public void RenderPlist_escapes_xml_in_paths()
    {
        var plist = LaunchdInstaller.RenderPlist("/Apps/Tools & Co/skylight-sync", 60, "/logs/<sync>.log");

        var dict = ParsePlistDict(plist);
        Assert.Equal("/Apps/Tools & Co/skylight-sync", dict["ProgramArguments"].Elements("string").First().Value);
        Assert.Equal("/logs/<sync>.log", dict["StandardOutPath"].Value);
    }

    [Fact]
    public async Task Install_writes_plist_and_calls_launchctl()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var plistDir = Path.Combine(dir.Path, "LaunchAgents");
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, plistDir, _uid);

        var path = await installer.InstallAsync(Config(intervalMinutes: 30), _program, CancellationToken.None);

        Assert.Equal(Path.Combine(plistDir, LaunchdInstaller.Label + ".plist"), path);
        Assert.True(File.Exists(path));
        var dict = ParsePlistDict(await File.ReadAllTextAsync(path));
        Assert.Equal("1800", dict["StartInterval"].Value);
        Assert.Equal(Path.Combine(home.Dir, "sync.log"), dict["StandardOutPath"].Value);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("launchctl", call.FileName);
        Assert.Equal(["bootstrap", $"gui/{_uid}", path], call.Args);
        Assert.True(installer.IsInstalled());
    }

    [Fact]
    public async Task Install_falls_back_to_load_when_bootstrap_fails()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner()
            .Enqueue(5, stdErr: "Bootstrap failed: 5: Input/output error\n")
            .Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);

        var path = await installer.InstallAsync(Config(60), _program, CancellationToken.None);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["bootstrap", $"gui/{_uid}", path], runner.Calls[0].Args);
        Assert.Equal(["load", path], runner.Calls[1].Args);
    }

    [Fact]
    public async Task Install_throws_with_launchctl_stderr_when_both_fail()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner()
            .Enqueue(5, stdErr: "Bootstrap failed: 5: Input/output error\n")
            .Enqueue(1, stdErr: "Load failed: 5: Input/output error\n");
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);

        var ex = await Assert.ThrowsAsync<LaunchdException>(() =>
            installer.InstallAsync(Config(60), _program, CancellationToken.None));

        Assert.Contains("Load failed", ex.Message);
    }

    [Fact]
    public async Task Reinstall_boots_out_the_existing_agent_first()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);
        var path = await installer.InstallAsync(Config(60), _program, CancellationToken.None);
        runner.Enqueue(0).Enqueue(0);

        await installer.InstallAsync(Config(15), _program, CancellationToken.None);

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(["bootout", $"gui/{_uid}/{LaunchdInstaller.Label}"], runner.Calls[1].Args);
        Assert.Equal(["bootstrap", $"gui/{_uid}", path], runner.Calls[2].Args);
        Assert.Equal("900", ParsePlistDict(await File.ReadAllTextAsync(path))["StartInterval"].Value);
    }

    [Fact]
    public async Task Uninstall_removes_plist()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);
        var path = await installer.InstallAsync(Config(60), _program, CancellationToken.None);
        runner.Enqueue(0);

        await installer.UninstallAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.False(installer.IsInstalled());
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal("launchctl", runner.Calls[1].FileName);
        Assert.Equal(["bootout", $"gui/{_uid}/{LaunchdInstaller.Label}"], runner.Calls[1].Args);
    }

    [Fact]
    public async Task Uninstall_falls_back_to_unload_when_bootout_fails()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);
        var path = await installer.InstallAsync(Config(60), _program, CancellationToken.None);
        runner.Enqueue(3, stdErr: "Boot-out failed: 3: No such process\n").Enqueue(0);

        await installer.UninstallAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal(["unload", path], runner.Calls[2].Args);
    }

    [Fact]
    public async Task Uninstall_keeps_plist_and_throws_when_both_fail()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);
        var path = await installer.InstallAsync(Config(60), _program, CancellationToken.None);
        runner.Enqueue(3, stdErr: "Boot-out failed: 3: No such process\n")
              .Enqueue(1, stdErr: "Unload failed: 113: Could not find specified service\n");

        var ex = await Assert.ThrowsAsync<LaunchdException>(() => installer.UninstallAsync(CancellationToken.None));

        Assert.Contains("Could not find specified service", ex.Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Uninstall_is_a_no_op_when_not_installed()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner();
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);

        await installer.UninstallAsync(CancellationToken.None);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task NextRun_is_null_when_agent_is_not_loaded()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner().Enqueue(113, stdErr: "Could not find service\n");
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);

        var next = await installer.NextRunAsync(CancellationToken.None);

        Assert.Null(next);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["print", $"gui/{_uid}/{LaunchdInstaller.Label}"], call.Args);
    }

    [Fact]
    public async Task NextRun_adds_run_interval_to_last_log_write()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var log = AppPaths.Resolve().LogFile;
        await File.WriteAllTextAsync(log, "ran\n");
        var lastRun = new DateTime(2026, 11, 5, 14, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(log, lastRun);
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);
        await installer.InstallAsync(Config(60), _program, CancellationToken.None);
        runner.Enqueue(0, stdOut: $"{LaunchdInstaller.Label} = {{\n\tstate = not running\n\trun interval = 3600 seconds\n\truns = 2\n}}\n");

        var next = await installer.NextRunAsync(CancellationToken.None);

        Assert.Equal(new DateTimeOffset(lastRun).AddHours(1), next);
    }

    [Fact]
    public async Task NextRun_is_null_when_log_has_never_been_written()
    {
        using var dir = new TempDir();
        using var home = new TestHome();
        var runner = new FakeProcessRunner().Enqueue(0);
        var installer = new LaunchdInstaller(runner, dir.Path, _uid);
        await installer.InstallAsync(Config(60), _program, CancellationToken.None);
        runner.Enqueue(0, stdOut: "\trun interval = 3600 seconds\n");

        Assert.Null(await installer.NextRunAsync(CancellationToken.None));
    }

    [MacOnlyFact]
    public async Task RenderPlist_passes_plutil_lint()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, LaunchdInstaller.Label + ".plist");
        await File.WriteAllTextAsync(path, LaunchdInstaller.RenderPlist(_program, 60, "/tmp/sync.log"));

        var result = await new SkylightPhotoSync.Infrastructure.ProcessRunner()
            .RunAsync("plutil", ["-lint", path], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [MacOnlyFact]
    public void Current_uid_is_read_from_libc()
    {
        var uid = NativeUid.Current();

        Assert.NotEqual(0u, uid);
    }

    private static AppConfig Config(int intervalMinutes) => new(
        Source: new SourceConfig(SourceMode.Album, "Skylight", 0, false),
        Limits: new LimitsConfig(25),
        Image: new ImageConfig(2048, 88),
        Email: new EmailConfig(["frame@example.com"], "me@example.com", 12, 17_000_000),
        Smtp: new SmtpConfig("smtp.example.com", 587, "me@example.com"),
        Alerts: new AlertsConfig("me@example.com"),
        Schedule: new ScheduleConfig(intervalMinutes));

    /// <summary>Parses a plist and returns the top-level dict as key → value element.</summary>
    private static Dictionary<string, XElement> ParsePlistDict(string plist)
    {
        var doc = XDocument.Parse(plist);
        Assert.Equal("plist", doc.Root!.Name.LocalName);
        var dict = doc.Root.Element("dict")!;
        var result = new Dictionary<string, XElement>();
        var elements = dict.Elements().ToList();
        for (var i = 0; i + 1 < elements.Count; i += 2)
        {
            Assert.Equal("key", elements[i].Name.LocalName);
            result[elements[i].Value] = elements[i + 1];
        }

        return result;
    }
}

public class LaunchdCommandTests
{
    [Fact]
    public async Task Help_lists_install_and_uninstall_launchd()
    {
        var out_ = new StringWriter();
        var err = new StringWriter();

        var code = await App.RunAsync(["--help"], out_, err);

        Assert.Equal(0, code);
        Assert.Contains("install-launchd", out_.ToString());
        Assert.Contains("uninstall-launchd", out_.ToString());
    }

    [Fact]
    public async Task InstallLaunchd_help_describes_the_command()
    {
        var out_ = new StringWriter();
        var err = new StringWriter();

        var code = await App.RunAsync(["install-launchd", "--help"], out_, err);

        Assert.Equal(0, code);
        Assert.Contains("install-launchd", out_.ToString());
        Assert.Contains("LaunchAgent", out_.ToString());
    }

    [Fact]
    public async Task InstallLaunchd_exits_1_with_one_line_error_when_config_is_missing()
    {
        using var home = new TestHome();
        var out_ = new StringWriter();
        var err = new StringWriter();

        var code = await App.RunAsync(["install-launchd"], out_, err);

        Assert.Equal(1, code);
        var lines = err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.Contains("config.toml", line);
    }
}
