using System.CommandLine;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Launchd;

namespace SkylightPhotoSync.Commands;

/// <summary><c>uninstall-launchd</c>: unload the LaunchAgent and delete its plist.</summary>
public static class UninstallLaunchdCommand
{
    public static Command Create()
    {
        var command = new Command("uninstall-launchd", "Unload the LaunchAgent and delete its plist.");

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            try
            {
                var installer = new LaunchdInstaller(new ProcessRunner(), LaunchdInstaller.DefaultPlistDir);
                if (!installer.IsInstalled())
                {
                    stdout.WriteLine("Not installed; nothing to do.");
                    return 0;
                }

                await installer.UninstallAsync(ct);
                stdout.WriteLine($"Removed {installer.PlistPath}");
                return 0;
            }
            catch (Exception ex) when (ex is LaunchdException or IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"uninstall-launchd failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
