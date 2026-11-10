using System.CommandLine;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Recording;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Sources.Amazon;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>record-fixtures [--out DIR]</c>: records redacted Amazon Photos responses for the saved login
/// and the configured <c>[source] album_name</c> into <c>DIR</c> (Task 12 Step 2).
/// <c>record-fixtures --auth [--out DIR]</c> records only the device login's cookie exchange
/// (issue #30) as <c>exchange_cookies.json</c>, every cookie value replaced, into
/// <see cref="DefaultAuthOutDir"/> unless <c>--out</c> is given.
/// </summary>
public static class RecordFixturesCommand
{
    public const string DefaultOutDir = "tests/SkylightPhotoSync.Tests/Fixtures/Amazon";

    public const string DefaultAuthOutDir = "tests/SkylightPhotoSync.Tests/Fixtures/AmazonAuth";

    public const string AuthNeedsDeviceLoginMessage =
        "record-fixtures --auth failed: no device login is saved; run 'skylight-sync login' first.";

    public static Command Create()
    {
        var outDir = new Option<string?>("--out")
        {
            Description = $"Directory for the recorded files. Default: {DefaultOutDir} under the current directory ({DefaultAuthOutDir} with --auth).",
        };

        var auth = new Option<bool>("--auth")
        {
            Description = $"Record only the device login's cookie exchange as {FixtureRecorder.ExchangeFixtureName}, every cookie value replaced, into {DefaultAuthOutDir} unless --out is given. Needs 'skylight-sync login' (device login).",
            Arity = ArgumentArity.Zero,
        };

        var command = new Command(
            "record-fixtures",
            "Record redacted Amazon Photos API responses as test fixtures, using the saved login and the configured album.");
        command.Options.Add(outDir);
        command.Options.Add(auth);

        command.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(outDir) ?? Path.Combine(Directory.GetCurrentDirectory(), parseResult.GetValue(auth) ? DefaultAuthOutDir : DefaultOutDir),
            parseResult.GetValue(auth),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));

        return command;
    }

    private static async Task<int> ExecuteAsync(string outDir, bool authOnly, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            var paths = AppPaths.Resolve();
            var secrets = CommandSupport.LoadSecretsIfPresent(paths);
            if (authOnly)
            {
                if (!CommandSupport.HasDeviceLogin(secrets))
                {
                    stderr.WriteLine(AuthNeedsDeviceLoginMessage);
                    return 1;
                }

                using var deviceAuth = CommandSupport.CreateDeviceAuth();
                var exchange = await FixtureRecorder.RecordExchangeAsync(deviceAuth, secrets.AmazonRefreshToken!, outDir, ct);
                stdout.WriteLine(exchange);
                stdout.WriteLine($"Wrote 1 redacted file to {outDir}. Review it for leaks before committing.");
                return 0;
            }

            var cfg = ConfigLoader.LoadConfig(paths.ConfigFile);
            if (string.IsNullOrWhiteSpace(cfg.Source.AlbumName))
            {
                stderr.WriteLine("record-fixtures failed: set [source] album_name in config.toml first.");
                return 1;
            }

            if (!CommandSupport.HasAmazonAuth(secrets))
            {
                stderr.WriteLine($"record-fixtures failed: {CommandSupport.NotLoggedInMessage}");
                return 1;
            }

            using var source = CommandSupport.CreateAmazonSource(secrets);
            var written = await FixtureRecorder.RecordAsync(source, outDir, cfg.Source.AlbumName, ct);
            foreach (var path in written)
            {
                stdout.WriteLine(path);
            }

            stdout.WriteLine($"Wrote {written.Count} redacted files to {outDir}. Review every file for leaks before committing.");
            return 0;
        }
        catch (Exception ex) when (ex is AuthExpiredException or DeviceAuthException)
        {
            stderr.WriteLine($"record-fixtures failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is ConfigException or HttpRequestException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"record-fixtures failed: {ex.Message}");
            return 1;
        }
    }
}
