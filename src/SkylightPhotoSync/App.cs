using System.CommandLine;
using SkylightPhotoSync.Commands;

namespace SkylightPhotoSync;

/// <summary>
/// Builds the command-line root and runs it against the supplied writers so
/// that tests can capture output without touching the real console.
/// </summary>
public static class App
{
    public const string Name = "skylight-sync";
    public const string Version = "0.1.0";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        var root = BuildRootCommand();
        var parseResult = root.Parse(args);
        var configuration = new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        };
        return await parseResult.InvokeAsync(configuration, ct);
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Emails new Amazon Photos to a Skylight Calendar on a Mac schedule.");

        // Replace the built-in version option so the output goes to the supplied writer
        // and uses the tool's own name and version rather than the assembly's.
        foreach (var builtIn in root.Options.OfType<VersionOption>().ToList())
        {
            root.Options.Remove(builtIn);
        }

        var versionOption = new Option<bool>("--version")
        {
            Description = "Show version information",
            Arity = ArgumentArity.Zero,
        };
        root.Options.Add(versionOption);
        root.Subcommands.Add(InstallLaunchdCommand.Create());
        root.Subcommands.Add(UninstallLaunchdCommand.Create());

        root.SetAction(parseResult =>
        {
            if (parseResult.GetValue(versionOption))
            {
                parseResult.InvocationConfiguration.Output.WriteLine($"{Name} {Version}");
                return 0;
            }

            parseResult.InvocationConfiguration.Error.WriteLine("No command given. Run with --help for usage.");
            return 1;
        });

        return root;
    }
}
