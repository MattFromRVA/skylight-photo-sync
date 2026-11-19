using System.CommandLine;
using Serilog.Core;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Delivery;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>test-upload</c>: uploads one generated 800×600 JPEG through Skylight's app API to every frame
/// <c>[delivery] frames</c> names (every frame on the account when it is empty) and prints the frame
/// names; the API counterpart of <c>test-email</c>. It needs only <c>[skylight]</c> email and password
/// in secrets.toml and works whatever <c>[delivery] method</c> is, so the API can be tried before
/// switching to it. The photo id Skylight returns is recorded for each frame, so rotation removes the
/// test image later (before any real photo), but nothing is marked sent.
/// Like a manual <c>prune</c> it always tries to sign in, even while scheduled runs are paused after a
/// refused sign-in: a sign-in that works here lifts the pause, a refused one starts it.
/// </summary>
public static class TestUploadCommand
{
    internal const string TestFileName = "skylight-sync-test.jpg";

    public static Command Create()
    {
        var command = new Command("test-upload", "Upload a generated test image to every configured Skylight frame through the API.");
        command.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }

    private static async Task<int> ExecuteAsync(TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var paths = AppPaths.Resolve();
        Logger log;
        try
        {
            log = Logging.Configure(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"test-upload failed: cannot open the log file {paths.LogFile}: {ex.Message}");
            return RunCommand.ExitError;
        }

        using (log)
        {
            try
            {
                var cfg = ConfigLoader.LoadConfig(paths.ConfigFile);
                var secrets = CommandSupport.LoadSecretsIfPresent(paths);
                if (!CommandSupport.HasSkylightLogin(secrets))
                {
                    throw new ConfigException("secrets.toml has no [skylight] email and password; test-upload needs both.");
                }

                // The run lock: a scheduled run must not sign in or record uploads at the same moment.
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
                    return await UploadAsync(cfg, secrets, paths, log, stdout, stderr, ct);
                }
            }
            catch (Exception ex) when (ex is ConfigException or SkylightApiException or HttpRequestException)
            {
                log.Error("test-upload failed: {Message}", ex.Message);
                stderr.WriteLine($"test-upload failed: {ex.Message}");
                return RunCommand.ExitError;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stderr.WriteLine("test-upload cancelled.");
                return RunCommand.ExitError;
            }
            catch (Exception ex)
            {
                log.Error(ex, "test-upload failed");
                stderr.WriteLine($"test-upload failed: {ex.GetType().Name}: {ex.Message}");
                return RunCommand.ExitError;
            }
        }
    }

    private static async Task<int> UploadAsync(
        AppConfig cfg, Secrets secrets, AppPaths paths, Logger log, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        using var store = new StateStore(paths.StateDb);
        var client = CommandSupport.CreateSkylightApi(secrets, paths);
        var folder = Path.Combine(Path.GetTempPath(), "skylight-sync-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gate = CommandSupport.CreateSkylightSignInGate(secrets, store);
            var delivery = new SkylightApiDelivery(cfg.Delivery, cfg.Rotation, client, store, () => DateTimeOffset.UtcNow);
            try
            {
                // Signs in and lists the frames, so a refused sign-in or a misspelt frame name stops here, before anything is uploaded.
                await delivery.DestinationsAsync(ct);
            }
            catch (SkylightSignInException ex)
            {
                gate.RecordFailure(ex.Message);
                log.Error("test-upload failed: {Message}", ex.Message);
                stderr.WriteLine($"test-upload failed: {ex.Message}");
                stderr.WriteLine(CommandSupport.SkylightPausedHintFor(cfg));
                return RunCommand.ExitError;
            }

            if (gate.RecordSuccess())
            {
                stdout.WriteLine(CommandSupport.SkylightSignInRestoredMessage(cfg));
            }

            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, TestFileName);
            var jpeg = TestEmailCommand.TestJpeg();
            await File.WriteAllBytesAsync(file, jpeg, ct);

            var (uploaded, unconfirmed) = await delivery.UploadTestPhotoAsync(file, ct);
            foreach (var frame in uploaded)
            {
                log.Information("test image uploaded to frame {Frame} ({Bytes} bytes)", frame, jpeg.Length);
                stdout.WriteLine($"Uploaded test image to frame \"{frame}\"");
            }

            if (unconfirmed.Count > 0)
            {
                // Without the photo's id rotation cannot remove the image, and nothing proves it is on that frame.
                throw new SkylightApiException(
                    $"Skylight returned no photo id for the test image on {string.Join(", ", unconfirmed.Select(frame => $"\"{frame}\""))}. "
                    + "If it shows on that frame, delete it in the Skylight app; rotation cannot remove it.");
            }

            stdout.WriteLine("The test image is recorded as this tool's, so rotation removes it before any real photo; you can also delete it in the Skylight app.");
            return RunCommand.ExitOk;
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
