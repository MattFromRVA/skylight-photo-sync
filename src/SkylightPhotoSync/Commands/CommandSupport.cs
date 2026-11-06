using System.Net;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Sources.Amazon;

namespace SkylightPhotoSync.Commands;

/// <summary>Pieces shared by the commands that load secrets, send mail or talk to Amazon.</summary>
internal static class CommandSupport
{
    /// <summary>
    /// Test hook: when set, every <see cref="AmazonPhotosSource"/> a command builds uses this handler
    /// instead of the network. Tests run serially, so a static is safe.
    /// </summary>
    internal static Func<HttpMessageHandler>? AmazonHandlerFactory { get; set; }

    /// <summary>
    /// The secrets file when it exists, otherwise all-empty secrets: a dry run against a local folder
    /// must work before the user has logged in. A present but unreadable file still fails loudly.
    /// </summary>
    public static Secrets LoadSecretsIfPresent(AppPaths paths) =>
        File.Exists(paths.SecretsFile)
            ? ConfigLoader.LoadSecrets(paths.SecretsFile)
            : new Secrets(null, null, null, null);

    /// <summary>The Amazon storefront the tool signs in to and reads from.</summary>
    public const string AmazonTld = "com";

    public const string NotLoggedInMessage = "not logged in to Amazon Photos; run 'skylight-sync login' first.";

    /// <summary>True when all three Amazon cookies are saved (the fallback login).</summary>
    public static bool HasAmazonCookies(Secrets secrets) =>
        !string.IsNullOrWhiteSpace(secrets.AmazonSessionId)
        && !string.IsNullOrWhiteSpace(secrets.AmazonUbid)
        && !string.IsNullOrWhiteSpace(secrets.AmazonAt);

    /// <summary>True when a device login's refresh token is saved (the default login).</summary>
    public static bool HasDeviceLogin(Secrets secrets) => !string.IsNullOrWhiteSpace(secrets.AmazonRefreshToken);

    /// <summary>True when Amazon can be reached at all: a device login or the three cookies.</summary>
    public static bool HasAmazonAuth(Secrets secrets) => HasDeviceLogin(secrets) || HasAmazonCookies(secrets);

    /// <summary>How Amazon is authenticated, for <c>status</c>: the device login wins when both are saved.</summary>
    public static string DescribeAmazonAuth(Secrets secrets) =>
        HasDeviceLogin(secrets) ? "device login" : HasAmazonCookies(secrets) ? "pasted cookies" : "not logged in";

    /// <summary>An <see cref="AmazonPhotosSource"/> for the saved login (see <see cref="HasAmazonAuth"/>).</summary>
    public static AmazonPhotosSource CreateAmazonSource(Secrets secrets) =>
        new(secrets, AmazonHandlerFactory?.Invoke(), AmazonTld);

    /// <summary>The device-login client for <c>login</c> and <c>logout</c>.</summary>
    public static AmazonDeviceAuth CreateDeviceAuth() => new(AmazonHandlerFactory?.Invoke(), AmazonTld);

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
