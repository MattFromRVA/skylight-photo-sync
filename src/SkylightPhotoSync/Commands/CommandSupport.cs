using System.Net;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;

namespace SkylightPhotoSync.Commands;

/// <summary>Pieces shared by the commands that load secrets and send mail.</summary>
internal static class CommandSupport
{
    /// <summary>
    /// The secrets file when it exists, otherwise all-empty secrets: a dry run against a local folder
    /// must work before the user has logged in. A present but unreadable file still fails loudly.
    /// </summary>
    public static Secrets LoadSecretsIfPresent(AppPaths paths) =>
        File.Exists(paths.SecretsFile)
            ? ConfigLoader.LoadSecrets(paths.SecretsFile)
            : new Secrets(null, null, null, null);

    /// <summary>
    /// An <see cref="SmtpMailer"/> for the configured server. STARTTLS is required for every host except
    /// loopback: a relay on this machine (and the in-process server the tests use) speaks plain SMTP.
    /// </summary>
    public static SmtpMailer CreateMailer(SmtpConfig smtp, Secrets secrets) =>
        new(smtp, secrets.SmtpPassword ?? "", useStartTls: !IsLoopback(smtp.Host));

    internal static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
