using System.CommandLine;
using SkylightPhotoSync.Config;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>import-secrets</c>: moves every value in secrets.toml into the macOS Keychain, replacing the
/// Keychain's value for the same key, then deletes the file (<see cref="SecretStore.ImportFileAsync"/>).
/// This is the migration path for an existing secrets.toml, and how a hand-edited password (SMTP,
/// Skylight) reaches the Keychain, which otherwise wins over the file. Prints counts, never values.
/// </summary>
public static class ImportSecretsCommand
{
    public static Command Create()
    {
        var command = new Command(
            "import-secrets",
            "Move the secrets in secrets.toml into the macOS Keychain (replacing the values there) and delete the file.");

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            try
            {
                var paths = AppPaths.Resolve();
                var moved = await CommandSupport.CreateSecretStore(paths).ImportFileAsync(ct);
                stdout.WriteLine(moved == 0
                    ? $"{paths.SecretsFile} has no secrets; nothing to import."
                    : $"Moved {moved} secret{(moved == 1 ? "" : "s")} into the macOS Keychain and deleted {paths.SecretsFile}.");
                return 0;
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"import-secrets failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
