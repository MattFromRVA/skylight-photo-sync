using System.CommandLine;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Recording;
using SkylightPhotoSync.Sources;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>record-fixtures [--out DIR]</c>: records redacted Amazon Photos responses for the saved cookies
/// and the configured <c>[source] album_name</c> into <paramref name="DIR"/> (Task 12 Step 2).
/// </summary>
public static class RecordFixturesCommand
{
    public const string DefaultOutDir = "tests/SkylightPhotoSync.Tests/Fixtures/Amazon";

    public static Command Create()
    {
        var outDir = new Option<string?>("--out")
        {
            Description = $"Directory for the recorded files. Default: {DefaultOutDir} under the current directory.",
        };

        var command = new Command(
            "record-fixtures",
            "Record redacted Amazon Photos API responses as test fixtures, using the saved cookies and the configured album.");
        command.Options.Add(outDir);

        command.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(outDir) ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultOutDir),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));

        return command;
    }

    private static async Task<int> ExecuteAsync(string outDir, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            var paths = AppPaths.Resolve();
            var cfg = ConfigLoader.LoadConfig(paths.ConfigFile);
            if (string.IsNullOrWhiteSpace(cfg.Source.AlbumName))
            {
                stderr.WriteLine("record-fixtures failed: set [source] album_name in config.toml first.");
                return 1;
            }

            var secrets = CommandSupport.LoadSecretsIfPresent(paths);
            if (!CommandSupport.HasAmazonCookies(secrets))
            {
                stderr.WriteLine("record-fixtures failed: no Amazon cookies saved; run 'skylight-sync login' first.");
                return 1;
            }

            using var source = CommandSupport.CreateAmazonSource(secrets);
            var written = await FixtureRecorder.RecordAsync(source, outDir, cfg.Source.AlbumName, ct);
            foreach (var path in written)
            {
                stdout.WriteLine(path);
            }

            stdout.WriteLine($"Wrote {written.Count} redacted files to {outDir}. Review them, then commit (placeholder_*.json can go).");
            return 0;
        }
        catch (AuthExpiredException ex)
        {
            stderr.WriteLine($"record-fixtures failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is ConfigException or HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"record-fixtures failed: {ex.Message}");
            return 1;
        }
    }
}
