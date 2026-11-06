using System.CommandLine;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Sources.Amazon;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>logout</c>: removes the registered device from the Amazon account (<c>/auth/token</c> then
/// <c>/auth/deregister</c>), drops every Amazon key from secrets.toml (the <c>[smtp]</c> password
/// stays) and sets <c>needs_login</c> so scheduled runs stop without contacting Amazon. Pasted
/// cookies are simply forgotten. If Amazon cannot be reached the local state is cleared anyway and
/// the command exits 1, telling the user to remove the device by hand. Nothing secret is printed.
/// </summary>
public static class LogoutCommand
{
    public const string NotLoggedInMessage = "Amazon Photos: not logged in; nothing to do.";

    public static Command Create()
    {
        var command = new Command(
            "logout",
            "Remove the registered device from your Amazon account (or forget the pasted cookies) and stop scheduled runs until the next login.");

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            try
            {
                var paths = AppPaths.Resolve();
                var secrets = CommandSupport.LoadSecretsIfPresent(paths);
                if (!CommandSupport.HasAmazonAuth(secrets))
                {
                    stdout.WriteLine(NotLoggedInMessage);
                    return 0;
                }

                var wasDevice = CommandSupport.HasDeviceLogin(secrets);
                string? problem = null;
                var alreadyRemoved = false;
                if (wasDevice)
                {
                    try
                    {
                        alreadyRemoved = !await CommandSupport.DeregisterDeviceAsync(secrets.AmazonRefreshToken!, ct);
                    }
                    catch (DeviceAuthException ex)
                    {
                        problem = ex.Message;
                    }
                }

                ConfigLoader.WriteSecrets(paths.SecretsFile, secrets with
                {
                    AmazonRefreshToken = null,
                    AmazonDeviceSerial = null,
                    AmazonSessionId = null,
                    AmazonUbid = null,
                    AmazonAt = null,
                });
                using (var store = new StateStore(paths.StateDb))
                {
                    store.SetNeedsLogin(true, null);
                }

                if (problem is not null)
                {
                    stderr.WriteLine(
                        $"Logged out of Amazon Photos locally, but the device could not be removed from the account: {problem} "
                        + $"Remove \"{AmazonDeviceAuth.DeviceDisplayName}\" under Manage Your Content and Devices on amazon.{CommandSupport.AmazonTld}.");
                    return 1;
                }

                stdout.WriteLine(!wasDevice
                    ? "Logged out of Amazon Photos; the saved cookies were removed."
                    : alreadyRemoved
                        ? $"Logged out of Amazon Photos; the device \"{AmazonDeviceAuth.DeviceDisplayName}\" had already been removed from the account."
                        : $"Logged out of Amazon Photos; the device \"{AmazonDeviceAuth.DeviceDisplayName}\" was removed from the account.");
                stdout.WriteLine("Scheduled runs will exit with needs_login until you run 'skylight-sync login' again.");
                return 0;
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                stderr.WriteLine($"logout failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }
}
