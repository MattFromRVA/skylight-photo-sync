using System.CommandLine;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>prune [--dry-run]</c>: rotation's clean-up on its own, without sending anything. Works whether or
/// not <c>[rotation] enabled</c> is set, so <c>prune --dry-run</c> can check what would be deleted before
/// rotation is turned on. Uses <c>[rotation]</c> keep, max_deletes_per_run and frame_name.
/// Run by hand it always tries to sign in, even while scheduled runs have pruning paused after a
/// refused sign-in: a sign-in that works here is what lifts the pause.
/// </summary>
public static class PruneCommand
{
    public static Command Create()
    {
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be deleted from the frame without deleting anything.",
        };

        var command = new Command("prune", "Delete this tool's older photos from the Skylight frame, keeping the newest [rotation] keep.");
        command.Options.Add(dryRun);
        command.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(dryRun),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }

    private static async Task<int> ExecuteAsync(bool dryRun, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            var paths = AppPaths.Resolve();
            var cfg = ConfigLoader.LoadConfig(paths.ConfigFile);
            var secrets = CommandSupport.LoadSecretsIfPresent(paths);
            if (!CommandSupport.HasSkylightLogin(secrets))
            {
                stderr.WriteLine("prune failed: secrets.toml has no [skylight] email and password.");
                return RunCommand.ExitError;
            }

            FileStream lockFile;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.LockFile))!);
                lockFile = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                stderr.WriteLine($"Another sync run holds the lock file {paths.LockFile}.");
                return RunCommand.ExitLockHeld;
            }

            using (lockFile)
            {
                using var store = new StateStore(paths.StateDb);
                using var client = CommandSupport.CreateSkylightClient(secrets, paths);
                var gate = CommandSupport.CreateSkylightSignInGate(secrets, store);
                var rotator = new FrameRotator(cfg.Rotation, cfg.Email.From, client, store, () => DateTimeOffset.UtcNow);
                PruneResult result;
                try
                {
                    result = await rotator.PruneAsync(dryRun, expectedNew: 0, DateTimeOffset.UtcNow, ct);
                }
                catch (SkylightSignInException ex)
                {
                    gate.RecordFailure(ex.Message);
                    stderr.WriteLine($"prune failed: {ex.Message}");
                    stderr.WriteLine(CommandSupport.SkylightPausedHint);
                    return RunCommand.ExitError;
                }

                if (gate.RecordSuccess())
                {
                    stdout.WriteLine("Skylight sign-in works again; scheduled runs will prune from now on.");
                }

                foreach (var frame in result.Frames)
                {
                    var action = dryRun ? $"would delete {frame.WouldDelete}" : $"deleted {frame.Deleted}";
                    stdout.WriteLine(
                        $"frame \"{frame.FrameName}\": {frame.Ours} photos from {cfg.Email.From}, {action} (keep {cfg.Rotation.Keep})");
                    foreach (var (sender, count) in frame.OtherSenders.OrderByDescending(pair => pair.Value))
                    {
                        stdout.WriteLine($"  left alone: {count} from {sender}");
                    }
                }

                if (result.Frames.Count == 0)
                {
                    stdout.WriteLine("no frames on the Skylight account.");
                }

                return RunCommand.ExitOk;
            }
        }
        catch (ConfigException ex)
        {
            stderr.WriteLine($"prune failed: {ex.Message}");
            return RunCommand.ExitError;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stderr.WriteLine("prune cancelled.");
            return RunCommand.ExitError;
        }
        catch (Exception ex) when (ex is SkylightApiException or HttpRequestException)
        {
            stderr.WriteLine($"prune failed: {ex.Message}");
            return RunCommand.ExitError;
        }
    }
}
