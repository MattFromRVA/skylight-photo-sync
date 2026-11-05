using System.CommandLine;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Launchd;

namespace SkylightPhotoSync.Commands;

/// <summary><c>install-launchd</c>: write the LaunchAgent plist and load it.</summary>
public static class InstallLaunchdCommand
{
    public static Command Create()
    {
        var command = new Command(
            "install-launchd",
            "Write and load the LaunchAgent that runs 'skylight-sync run' on the configured interval.");

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            try
            {
                var cfg = ConfigLoader.LoadConfig(AppPaths.Resolve().ConfigFile);
                var programPath = Environment.ProcessPath
                    ?? throw new LaunchdException("Could not determine the path of the running program.");
                var installer = new LaunchdInstaller(new ProcessRunner(), LaunchdInstaller.DefaultPlistDir);
                var plist = await installer.InstallAsync(cfg, programPath, ct);
                stdout.WriteLine(plist);
                return 0;
            }
            catch (Exception ex) when (ex is ConfigException or LaunchdException or IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"install-launchd failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
