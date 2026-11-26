using System.CommandLine;
using System.Globalization;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Launchd;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary><c>status</c>: last run, totals, how photos are delivered, rotation, the Skylight and Amazon <c>needs_login</c> flags, where secrets are kept, how Amazon is authenticated, and the launchd schedule.</summary>
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
                var (cfg, configError) = LoadConfig(paths);
                stdout.WriteLine($"delivery: {DescribeDelivery(cfg, configError)}");
                stdout.WriteLine($"rotation: {DescribeRotation(cfg, configError, store)}");
                stdout.WriteLine($"skylight_needs_login: {DescribeSkylightSignIn(cfg, store)}");
                stdout.WriteLine($"needs_login: {(store.NeedsLogin() ? "yes" : "no")}");
                var (auth, secretsLocation) = await DescribeSecretsAsync(paths, ct);
                stdout.WriteLine($"secrets: {secretsLocation}");
                stdout.WriteLine($"auth: {auth}");
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
    /// How Amazon is authenticated (<c>device login</c>, <c>pasted cookies</c> or <c>not logged in</c>)
    /// and where secrets are kept: <c>macOS Keychain</c>, or <c>secrets.toml</c> (with why, when the
    /// Keychain could not be read). Secrets that cannot be read (wrong file mode, bad TOML, a locked
    /// Keychain and no file) are reported rather than failing the whole command.
    /// </summary>
    private static async Task<(string Auth, string Location)> DescribeSecretsAsync(AppPaths paths, CancellationToken ct)
    {
        var store = CommandSupport.CreateSecretStore(paths);
        var location = store.UsesKeychain ? "macOS Keychain" : "secrets.toml";
        try
        {
            var auth = CommandSupport.DescribeAmazonAuth(await store.LoadAsync(ct));
            if (store.KeychainProblem is not null)
            {
                location = $"secrets.toml (Keychain unavailable: {store.KeychainProblem})";
            }

            return (auth, location);
        }
        catch (ConfigException ex)
        {
            return ($"unknown ({ex.Message})", location);
        }
    }

    /// <summary>The config, or why it cannot be read: the lines that need it then say so instead of failing the command.</summary>
    private static (AppConfig? Config, string? Error) LoadConfig(AppPaths paths)
    {
        try
        {
            return (ConfigLoader.LoadConfig(paths.ConfigFile), null);
        }
        catch (ConfigException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// <c>email</c>, or <c>api</c> with the frames <c>[delivery] frames</c> names (<c>every frame</c> when
    /// it names none). Straight from config.toml: status never signs in to Skylight, so the names are
    /// the configured ones, not checked against the account.
    /// </summary>
    private static string DescribeDelivery(AppConfig? cfg, string? configError)
    {
        if (cfg is null)
        {
            return $"unknown ({configError})";
        }

        if (cfg.Delivery.Method != DeliveryMethod.Api)
        {
            return "email";
        }

        return cfg.Delivery.Frames.Count == 0 ? "api (every frame)" : $"api ({string.Join(", ", cfg.Delivery.Frames)})";
    }

    /// <summary><c>off</c>, or the keep count and how many photos rotation has deleted so far.</summary>
    private static string DescribeRotation(AppConfig? cfg, string? configError, StateStore store)
    {
        if (cfg is null)
        {
            return $"unknown ({configError})";
        }

        return cfg.Rotation.Enabled
            ? $"on, keep {cfg.Rotation.Keep} per frame, {store.TotalPruned()} pruned so far"
            : "off";
    }

    /// <summary>
    /// <c>no</c>, or what a refused Skylight sign-in has paused, since when and why: delivery under
    /// <c>[delivery] method = "api"</c> (and pruning too when rotation is on), otherwise rotation's pruning.
    /// </summary>
    private static string DescribeSkylightSignIn(AppConfig? cfg, StateStore store)
    {
        if (store.SkylightSignInFailure() is not { } failure)
        {
            return "no";
        }

        var paused = cfg switch
        {
            { Delivery.Method: DeliveryMethod.Api, Rotation.Enabled: true } => "delivery and pruning paused",
            { Delivery.Method: DeliveryMethod.Api } => "delivery paused",
            _ => "pruning paused",
        };
        return $"yes, {paused} since {Format(failure.FailedAt)} ({failure.Reason})";
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
