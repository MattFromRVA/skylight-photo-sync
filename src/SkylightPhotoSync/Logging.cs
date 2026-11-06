using Serilog;
using Serilog.Core;
using Serilog.Events;
using SkylightPhotoSync.Config;

namespace SkylightPhotoSync;

/// <summary>
/// The Serilog logger the commands that do real work write to: a rolling file at
/// <see cref="AppPaths.LogFile"/> (5 MB × 5) plus stderr at Information when stderr is a terminal.
/// </summary>
public static class Logging
{
    public const long FileSizeLimitBytes = 5_000_000;
    public const int RetainedFileCountLimit = 5;

    /// <summary>
    /// Builds the logger. Dispose it when the command ends so the file is flushed. The stderr sink is
    /// skipped when stderr is redirected: under launchd stderr already goes to the same log file, so
    /// writing to both would record every line twice. <see cref="Secrets"/> values are redacted if one is
    /// ever logged as a structured property; nothing else should put a cookie or password in a message.
    /// </summary>
    public static Logger Configure(AppPaths paths)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(paths.LogFile));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Destructure.ByTransforming<Secrets>(_ => "<redacted>")
            .WriteTo.File(
                paths.LogFile,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCountLimit);

        if (!Console.IsErrorRedirected)
        {
            configuration.WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                standardErrorFromLevel: LogEventLevel.Verbose);
        }

        return configuration.CreateLogger();
    }
}
