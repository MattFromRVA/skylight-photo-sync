using System.CommandLine;
using System.Globalization;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Launchd;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary><c>status</c>: last run, totals, the <c>needs_login</c> flag, how Amazon is authenticated, and the launchd schedule.</summary>
public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show the last run, how much has been sent, and the launchd schedule.");

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            try
            {
                var paths = AppPaths.Resolve();
                using var store = new StateStore(paths.StateDb);
                stdout.WriteLine($"last run: {DescribeLastRun(store.LastRun())}");
                stdout.WriteLine($"total sent: {store.TotalSent()}");
                stdout.WriteLine($"needs_login: {(store.NeedsLogin() ? "yes" : "no")}");
                stdout.WriteLine($"auth: {DescribeAuth(paths)}");
                stdout.WriteLine($"launchd: {await DescribeLaunchdAsync(ct)}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                stderr.WriteLine($"status failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    /// <summary>
    /// <c>device login</c>, <c>pasted cookies</c> or <c>not logged in</c>; a secrets file that
    /// cannot be read (wrong mode, bad TOML) is reported rather than failing the whole command.
    /// </summary>
    private static string DescribeAuth(AppPaths paths)
    {
        try
        {
            return CommandSupport.DescribeAmazonAuth(CommandSupport.LoadSecretsIfPresent(paths));
        }
        catch (ConfigException ex)
        {
            return $"unknown ({ex.Message})";
        }
    }

    internal static string DescribeLastRun(RunRecord? run)
    {
        if (run is null)
        {
            return "no runs yet";
        }

        var counts = $"(found {run.Found}, sent {run.Sent}, skipped {run.Skipped})";
        if (run.FinishedAt is null)
        {
            return $"{Format(run.StartedAt)} in progress {counts}";
        }

        var outcome = run.Error is null ? "ok" : $"error: {run.Error}";
        return $"{Format(run.FinishedAt.Value)} {outcome} {counts}";
    }

    private static async Task<string> DescribeLaunchdAsync(CancellationToken ct)
    {
        var installer = new LaunchdInstaller(new ProcessRunner(), LaunchdInstaller.DefaultPlistDir);
        if (!installer.IsInstalled())
        {
            return "not installed";
        }

        var next = await installer.NextRunAsync(ct);
        return next is null ? "installed, next run unknown" : $"installed, next run {Format(next.Value)}";
    }

    private static string Format(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
