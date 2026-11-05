using System.Security;
using System.Text.RegularExpressions;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Infrastructure;

namespace SkylightPhotoSync.Launchd;

/// <summary>
/// Writes, loads and unloads the LaunchAgent that runs <c>skylight-sync run</c> on the configured
/// interval. Every <c>launchctl</c> call goes through <see cref="IProcessRunner"/> so tests can fake it;
/// <paramref name="uid"/> is injectable for the same reason and defaults to the current user.
/// </summary>
public sealed partial class LaunchdInstaller(IProcessRunner runner, string plistDir, uint? uid = null)
{
    public const string Label = "com.mattfromrva.skylight-photo-sync";

    private const string _launchctl = "launchctl";

    /// <summary><c>~/Library/LaunchAgents</c>, where per-user agents live.</summary>
    public static string DefaultPlistDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public string PlistPath => Path.Combine(plistDir, Label + ".plist");

    /// <summary>
    /// Renders the LaunchAgent plist: runs <paramref name="programPath"/> with the <c>run</c> argument
    /// every <paramref name="intervalMinutes"/> minutes and once at login, appending stdout and stderr
    /// to <paramref name="logFile"/>.
    /// </summary>
    public static string RenderPlist(string programPath, int intervalMinutes, string logFile)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMinutes);
        var startInterval = checked(intervalMinutes * 60);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{Escape(programPath)}</string>
                    <string>run</string>
                </array>
                <key>StartInterval</key>
                <integer>{startInterval}</integer>
                <key>RunAtLoad</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{Escape(logFile)}</string>
                <key>StandardErrorPath</key>
                <string>{Escape(logFile)}</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>
    /// Writes the plist and loads it (<c>launchctl bootstrap</c>, falling back to the legacy
    /// <c>launchctl load</c>). An already-installed agent is booted out first so a changed
    /// interval takes effect. Returns the plist path.
    /// </summary>
    public async Task<string> InstallAsync(AppConfig cfg, string programPath, CancellationToken ct)
    {
        var path = PlistPath;
        var logFile = AppPaths.Resolve().LogFile;
        Directory.CreateDirectory(plistDir);
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

        if (File.Exists(path))
        {
            // Best effort: a stale agent that is not loaded makes bootout fail, which is fine.
            await runner.RunAsync(_launchctl, ["bootout", ServiceTarget], ct);
        }

        await File.WriteAllTextAsync(path, RenderPlist(programPath, cfg.Schedule.IntervalMinutes, logFile), ct);

        var bootstrap = await runner.RunAsync(_launchctl, ["bootstrap", DomainTarget, path], ct);
        if (bootstrap.ExitCode != 0)
        {
            var load = await runner.RunAsync(_launchctl, ["load", path], ct);
            if (load.ExitCode != 0)
            {
                throw new LaunchdException($"launchctl could not load {path}: {Describe(bootstrap, load)}");
            }
        }

        return path;
    }

    /// <summary>
    /// Unloads the agent (<c>launchctl bootout</c>, falling back to <c>launchctl unload</c>) and
    /// deletes the plist. Does nothing when the plist is not present.
    /// </summary>
    public async Task UninstallAsync(CancellationToken ct)
    {
        var path = PlistPath;
        if (!File.Exists(path))
        {
            return;
        }

        var bootout = await runner.RunAsync(_launchctl, ["bootout", ServiceTarget], ct);
        if (bootout.ExitCode != 0)
        {
            var unload = await runner.RunAsync(_launchctl, ["unload", path], ct);
            if (unload.ExitCode != 0)
            {
                throw new LaunchdException($"launchctl could not unload {Label}: {Describe(bootout, unload)}");
            }
        }

        File.Delete(path);
    }

    public bool IsInstalled() => File.Exists(PlistPath);

    /// <summary>
    /// Best-effort estimate of the next scheduled run: <c>launchctl print</c> reports the run
    /// interval but not the next fire time, so this adds the interval to the log file's last write
    /// (every run appends to it). Null when the agent is not loaded or nothing has run yet.
    /// </summary>
    public async Task<DateTimeOffset?> NextRunAsync(CancellationToken ct)
    {
        var print = await runner.RunAsync(_launchctl, ["print", ServiceTarget], ct);
        if (print.ExitCode != 0)
        {
            return null;
        }

        var match = RunIntervalPattern().Match(print.StdOut);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var seconds))
        {
            return null;
        }

        var logFile = AppPaths.Resolve().LogFile;
        if (!File.Exists(logFile))
        {
            return null;
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(logFile)).AddSeconds(seconds);
    }

    private string DomainTarget => $"gui/{uid ?? NativeUid.Current()}";

    private string ServiceTarget => $"{DomainTarget}/{Label}";

    private static string Escape(string value) => SecurityElement.Escape(value);

    private static string Describe(ProcessResult first, ProcessResult fallback)
    {
        var text = string.IsNullOrWhiteSpace(fallback.StdErr) ? first.StdErr : fallback.StdErr;
        text = text.Trim().ReplaceLineEndings(" ");
        return string.IsNullOrEmpty(text) ? $"exit code {fallback.ExitCode}" : text;
    }

    [GeneratedRegex(@"run interval = (\d+) seconds")]
    private static partial Regex RunIntervalPattern();
}
