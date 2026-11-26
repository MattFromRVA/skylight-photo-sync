using System.Net;
using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Infrastructure;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.Sources;
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

    /// <summary>Test hook: the handler every <see cref="SkylightClient"/> a command builds uses instead of the network.</summary>
    internal static Func<HttpMessageHandler>? SkylightHandlerFactory { get; set; }

    /// <summary>
    /// Test hook: the Skylight account <c>test-upload</c> talks to instead of a <see cref="SkylightClient"/>,
    /// whose upload is not built yet (issue #16).
    /// </summary>
    internal static Func<ISkylightClient>? SkylightClientFactory { get; set; }

    /// <summary>Test hook: replaces the pause between "have the new photos arrived?" checks.</summary>
    internal static Func<TimeSpan, CancellationToken, Task>? RotationDelay { get; set; }

    /// <summary>Test hook: replaces the macOS notification that every alert posts.</summary>
    internal static Func<INotifier>? NotifierFactory { get; set; }

    public static INotifier CreateNotifier(IProcessRunner runner) => NotifierFactory?.Invoke() ?? new OsascriptNotifier(runner);

    public const string SkylightLoginMissingMessage =
        "[rotation] is enabled but secrets.toml has no [skylight] email and password; add them or set [rotation] enabled = false.";

    public const string SkylightLoginMissingForDeliveryMessage =
        "[delivery] method = \"api\" needs [skylight] email and password in secrets.toml; add them or set [delivery] method = \"email\".";

    /// <summary>True when the Skylight app sign-in that rotation and API delivery need is saved.</summary>
    public static bool HasSkylightLogin(Secrets secrets) =>
        !string.IsNullOrWhiteSpace(secrets.SkylightEmail) && !string.IsNullOrWhiteSpace(secrets.SkylightPassword);

    /// <summary>A <see cref="SkylightClient"/> for the saved sign-in (see <see cref="HasSkylightLogin"/>).</summary>
    public static SkylightClient CreateSkylightClient(Secrets secrets, AppPaths paths) =>
        new(secrets.SkylightEmail!, secrets.SkylightPassword!, paths.SkylightTokenFile, SkylightHandlerFactory?.Invoke());

    /// <summary>The account <c>test-upload</c> uploads to: <see cref="CreateSkylightClient"/> unless a test replaced it.</summary>
    public static ISkylightClient CreateSkylightApi(Secrets secrets, AppPaths paths) =>
        SkylightClientFactory?.Invoke() ?? CreateSkylightClient(secrets, paths);

    /// <summary>The gate that remembers a refused Skylight sign-in for the saved credentials.</summary>
    public static SkylightSignInGate CreateSkylightSignInGate(Secrets secrets, State.StateStore store) =>
        new(store, secrets.SkylightEmail!, secrets.SkylightPassword!, () => DateTimeOffset.UtcNow);

    public const string SkylightPausedHint =
        "Pruning is paused until a sign-in works: check [skylight] email and password in secrets.toml, "
        + "sign in once in the Skylight app, then run 'skylight-sync prune --dry-run'.";

    public const string SkylightDeliveryPausedHint =
        "Delivery is paused until a sign-in works: check [skylight] email and password in secrets.toml, "
        + "sign in once in the Skylight app, then run 'skylight-sync test-upload' "
        + "(or 'skylight-sync prune --dry-run', which signs in without adding a test photo).";

    /// <summary>The hint that follows a refused sign-in: what is paused under this config and how to lift it.</summary>
    public static string SkylightPausedHintFor(AppConfig cfg) =>
        cfg.Delivery.Method == DeliveryMethod.Api ? SkylightDeliveryPausedHint : SkylightPausedHint;

    /// <summary>What a command prints when its sign-in lifted the pause a refused one had left.</summary>
    public static string SkylightSignInRestoredMessage(AppConfig cfg) =>
        cfg.Delivery.Method == DeliveryMethod.Api
            ? "Skylight sign-in works again; scheduled runs will send photos from now on."
            : "Skylight sign-in works again; scheduled runs will prune from now on.";

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
    /// Removes the device behind <paramref name="refreshToken"/> from the account
    /// (<c>/auth/token</c> then <c>/auth/deregister</c>). False when Amazon no longer accepts the
    /// token, i.e. the device is already gone; <see cref="DeviceAuthException"/> when Amazon cannot
    /// be reached or refuses the removal.
    /// </summary>
    public static async Task<bool> DeregisterDeviceAsync(string refreshToken, CancellationToken ct)
    {
        using var auth = CreateDeviceAuth();
        try
        {
            var accessToken = await auth.RefreshAccessTokenAsync(refreshToken, ct);
            await auth.DeregisterAsync(accessToken, ct);
            return true;
        }
        catch (AuthExpiredException)
        {
            return false;
        }
    }

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
