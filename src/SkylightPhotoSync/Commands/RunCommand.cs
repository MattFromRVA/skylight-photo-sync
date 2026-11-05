using System.CommandLine;
using Serilog.Core;
using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Pipeline;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary><c>run</c>: one sync run (spec §6.9). CLI flags override config.toml for this run only.</summary>
public static class RunCommand
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitAmazonUnavailable = 2;
    public const int ExitNeedsLogin = 3;
    public const int ExitLockHeld = 4;

    private const string _localSourcePrefix = "local:";

    public static Command Create()
    {
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "List what would be sent without sending or marking anything.",
        };
        var mode = new Option<string?>("--mode")
        {
            Description = "Override [source] mode for this run.",
        };
        mode.AcceptOnlyFromAmong("album", "all");
        var lookbackDays = new Option<int?>("--lookback-days")
        {
            Description = "Override [source] lookback_days for this run (0 = no date bound, album mode only).",
        };
        var limit = new Option<int?>("--limit")
        {
            Description = "Send at most N items this run instead of [limits] max_items_per_run.",
        };
        var source = new Option<string?>("--source")
        {
            Description = "Where photos come from: local:PATH reads a folder. Default: Amazon Photos.",
        };

        var command = new Command("run", "Email new photos to the frame.");
        command.Options.Add(dryRun);
        command.Options.Add(mode);
        command.Options.Add(lookbackDays);
        command.Options.Add(limit);
        command.Options.Add(source);

        command.SetAction((parseResult, ct) => ExecuteAsync(
            new Options(
                parseResult.GetValue(dryRun),
                parseResult.GetValue(mode),
                parseResult.GetValue(lookbackDays),
                parseResult.GetValue(limit),
                parseResult.GetValue(source)),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));

        return command;
    }

    /// <summary>Applies <c>--mode</c> and <c>--lookback-days</c> to the loaded config, with the loader's rules.</summary>
    internal static AppConfig ApplyOverrides(AppConfig cfg, string? modeText, int? lookbackDays)
    {
        var source = cfg.Source;
        if (modeText is not null)
        {
            var mode = modeText.Equals("all", StringComparison.OrdinalIgnoreCase) ? SourceMode.All : SourceMode.Album;
            if (mode == SourceMode.Album && string.IsNullOrWhiteSpace(source.AlbumName))
            {
                throw new ConfigException("--mode album needs [source] album_name in config.toml.");
            }

            // Album mode defaults to "no date bound", which all mode must never inherit.
            var lookback = mode == SourceMode.All && source.Mode == SourceMode.Album && source.LookbackDays == 0
                ? 30
                : source.LookbackDays;
            source = source with { Mode = mode, LookbackDays = lookback };
        }

        if (lookbackDays is { } days)
        {
            if (days < 0)
            {
                throw new ConfigException("--lookback-days must not be negative.");
            }

            source = source with { LookbackDays = days };
        }

        if (source.Mode == SourceMode.All && source.LookbackDays == 0)
        {
            throw new ConfigException("lookback_days = 0 is not allowed in all mode; it would select every photo in the library.");
        }

        return cfg with { Source = source };
    }

    private static async Task<int> ExecuteAsync(Options options, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (options.Limit is < 0)
        {
            stderr.WriteLine("run failed: --limit must not be negative.");
            return ExitError;
        }

        var paths = AppPaths.Resolve();
        Logger log;
        try
        {
            log = Logging.Configure(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"run failed: cannot open the log file {paths.LogFile}: {ex.Message}");
            return ExitError;
        }

        using (log)
        {
            try
            {
                return await RunAsync(paths, options, log, stdout, stderr, ct);
            }
            catch (LockHeldException ex)
            {
                log.Warning("run skipped: {Message}", ex.Message);
                stderr.WriteLine(ex.Message);
                return ExitLockHeld;
            }
            catch (ConfigException ex)
            {
                log.Error("run failed: {Message}", ex.Message);
                stderr.WriteLine($"run failed: {ex.Message}");
                return ExitError;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                log.Warning("run cancelled");
                stderr.WriteLine("run cancelled.");
                return ExitError;
            }
            catch (Exception ex)
            {
                log.Error(ex, "run failed");
                stderr.WriteLine($"run failed: {ex.GetType().Name}: {ex.Message}");
                return ExitError;
            }
        }
    }

    private static async Task<int> RunAsync(
        AppPaths paths, Options options, Logger log, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var cfg = ApplyOverrides(ConfigLoader.LoadConfig(paths.ConfigFile), options.Mode, options.LookbackDays);

        if (options.Source is null)
        {
            stderr.WriteLine("Amazon source not yet available; use --source local:PATH.");
            return ExitAmazonUnavailable;
        }

        if (!options.Source.StartsWith(_localSourcePrefix, StringComparison.Ordinal)
            || options.Source.Length == _localSourcePrefix.Length)
        {
            stderr.WriteLine($"run failed: unknown source '{options.Source}'; use local:PATH.");
            return ExitError;
        }

        var source = new LocalFolderSource(options.Source[_localSourcePrefix.Length..]);
        var secrets = CommandSupport.LoadSecretsIfPresent(paths);
        var runner = new ProcessRunner();
        var now = () => DateTimeOffset.UtcNow;

        using var store = new StateStore(paths.StateDb);
        var normalizer = new ImageNormalizer(new SipsHeicConverter(runner), cfg.Image);
        var mailer = CommandSupport.CreateMailer(cfg.Smtp, secrets);
        var alerter = new Alerter(store, mailer, cfg.Alerts.Email, cfg.Email.From, new OsascriptNotifier(runner), now);
        var pipeline = new SyncPipeline(cfg, source, store, normalizer, mailer, alerter, now, paths.LockFile);

        log.Information(
            "run started: mode={Mode} album={Album} lookbackDays={LookbackDays} limit={Limit} dryRun={DryRun} source={Source}",
            cfg.Source.Mode, cfg.Source.AlbumName, cfg.Source.LookbackDays, options.Limit, options.DryRun, options.Source);

        var result = await pipeline.RunAsync(options.DryRun, options.Limit, ct);

        var summary = $"found {result.Found}, sent {result.Sent}, skipped {result.Skipped}, batches {result.Batches}"
            + (result.DryRun ? " (dry run)" : "");
        stdout.WriteLine(summary);

        if (result.Error is null)
        {
            log.Information("run finished: {Summary}", summary);
            return ExitOk;
        }

        log.Error("run failed: {Error} ({Summary})", result.Error, summary);
        stderr.WriteLine($"run failed: {result.Error}");
        return result.Error is SyncPipeline.NeedsLoginError or SyncPipeline.AuthExpiredError
            ? ExitNeedsLogin
            : ExitError;
    }

    private sealed record Options(bool DryRun, string? Mode, int? LookbackDays, int? Limit, string? Source);
}
