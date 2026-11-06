using System.CommandLine;
using System.Text.RegularExpressions;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Sources.Amazon;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>login</c>: the one-time browser device login (issue #30). It prints a PKCE sign-in URL,
/// reads the <c>/ap/maplanding</c> address the browser lands on, registers this tool as a device
/// (<see cref="AmazonDeviceAuth.RegisterAsync"/>), proves the resulting refresh token against
/// Amazon Photos (<see cref="AmazonPhotosSource.WhoAmIAsync"/>, which mints the first cookies),
/// and only then writes the refresh token and device serial to secrets.toml (0600) and clears
/// <c>needs_login</c>. If anything fails after registration the device is deregistered again and
/// nothing is written. <c>login --cookies</c> is the fallback: the Amazon session cookies from
/// stdin, either from a pasted "Copy as cURL" command or as three lines (session-id, ubid-main,
/// at-main), saved and proven the same way; rejected cookies stay saved but exit 1. No token,
/// cookie or authorization code is ever printed or logged.
/// </summary>
public static partial class LoginCommand
{
    public const string SavedMessage = "Saved. Run 'skylight-sync run --dry-run' to verify.";

    public const string DeviceRegisteredMessage =
        "Device registered as \"" + AmazonDeviceAuth.DeviceDisplayName + "\"; remove it under Manage Your Content and Devices to revoke.";

    public const string SignInInstructions =
        """
        Sign in to Amazon once in your browser; the tool then registers itself as a device and refreshes its own session from now on.

        1. Open this address in a browser (any device; copy it exactly):

        """;

    public const string PasteInstructions =
        """

        2. Sign in with your Amazon account, including the two-factor prompt (an authenticator app or the Amazon app is the most reliable; email/SMS codes can be flaky here).
        3. You will land on a "page not found" at amazon.com/ap/maplanding. That is expected.
        4. Copy the whole address from the address bar of that page and paste it here, then press Return.
        5. Afterwards, clear this terminal window's scrollback (Terminal: Cmd-K); the address contains a single-use sign-in code.
        """;

    public const string ClearScrollbackMessage =
        "Clear this terminal window's scrollback now (Terminal: Cmd-K); the address you pasted contains a single-use sign-in code.";

    public const string PreviousDeviceNotRemovedMessage =
        "warning: the previous device login could not be removed from your Amazon account; remove \""
        + AmazonDeviceAuth.DeviceDisplayName + "\" under Manage Your Content and Devices.";

    public const string PasteAddressPrompt = "Paste the address of the /ap/maplanding page: ";

    public const string RejectedMessage =
        "login failed: Amazon Photos rejected the cookies (they are saved in secrets.toml anyway). They may be expired or incomplete: "
        + "copy a fresh 'Copy as cURL' from a signed-in Amazon Photos tab and run login again.";

    public const string PastePrompt =
        "Paste the 'Copy as cURL' command (or session-id, ubid-main and at-main on three lines), then press Ctrl-D.";

    private const string _sessionIdKey = "session-id";
    private static readonly string[] _ubidKeys = ["ubid-main", "ubid_main"];
    private static readonly string[] _atKeys = ["at-main", "at_main"];

    public static Command Create()
    {
        var command = new Command(
            "login",
            "Sign in to Amazon once in your browser and register this tool as a device (default), or with --cookies paste browser cookies from stdin: "
            + "a 'Copy as cURL' command, or session-id, ubid-main and at-main on three lines.");
        var cookiesOption = new Option<bool>("--cookies")
        {
            Description = "Fallback: paste the browser cookies instead of the one-time device login.",
            Arity = ArgumentArity.Zero,
        };
        command.Options.Add(cookiesOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;
            return parseResult.GetValue(cookiesOption)
                ? await LoginWithCookiesAsync(stdout, stderr, ct)
                : await LoginWithDeviceAsync(stdout, stderr, ct);
        });

        return command;
    }

    private static async Task<int> LoginWithDeviceAsync(TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var codeVerifier = AmazonDeviceAuth.NewCodeVerifier();
        var serial = AmazonDeviceAuth.NewSerial();
        var signInUrl = AmazonDeviceAuth.BuildSignInUrl(CommandSupport.AmazonTld, codeVerifier, serial);

        stdout.Write(SignInInstructions);
        stdout.WriteLine(signInUrl);
        stdout.WriteLine(PasteInstructions);
        stdout.Write(PasteAddressPrompt);
        stdout.Flush();

        var pasted = await ReadNonEmptyLineAsync(ct);
        stdout.WriteLine();
        if (pasted is null)
        {
            stderr.WriteLine("login failed: no address was pasted.");
            return 1;
        }

        try
        {
            return await CompleteDeviceLoginAsync(pasted, codeVerifier, serial, stdout, stderr, ct);
        }
        finally
        {
            stdout.WriteLine(ClearScrollbackMessage);
        }
    }

    private static async Task<int> CompleteDeviceLoginAsync(
        string pasted, string codeVerifier, string serial, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        string authorizationCode;
        try
        {
            authorizationCode = AmazonDeviceAuth.ParseAuthorizationCode(pasted);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"login failed: {ex.Message}");
            return 1;
        }

        try
        {
            var paths = AppPaths.Resolve();
            var existing = CommandSupport.LoadSecretsIfPresent(paths);
            var previousSecretsFile = File.Exists(paths.SecretsFile) ? File.ReadAllBytes(paths.SecretsFile) : null;
            using var auth = CommandSupport.CreateDeviceAuth();

            DeviceRegistration registration;
            try
            {
                registration = await auth.RegisterAsync(authorizationCode, codeVerifier, serial, ct);
            }
            catch (DeviceAuthException ex)
            {
                stderr.WriteLine($"login failed: {ex.Message}");
                return 1;
            }

            string displayName;
            long count;
            var written = false;
            try
            {
                // The device login replaces any pasted cookies; the SMTP password is kept.
                var secrets = existing with
                {
                    AmazonRefreshToken = registration.RefreshToken,
                    AmazonDeviceSerial = serial,
                    AmazonSessionId = null,
                    AmazonUbid = null,
                    AmazonAt = null,
                };

                string name;
                using (var source = CommandSupport.CreateAmazonSource(secrets))
                {
                    (name, count) = await source.WhoAmIAsync(ct);
                }

                using var store = new StateStore(paths.StateDb);
                ConfigLoader.WriteSecrets(paths.SecretsFile, secrets);
                written = true;
                store.SetNeedsLogin(false, null);
                displayName = registration.CustomerName.Length > 0 ? registration.CustomerName : name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var removed = await TryDeregisterAsync(auth, registration.AccessToken, ct);
                if (written)
                {
                    RestoreSecretsFile(paths.SecretsFile, previousSecretsFile);
                }

                stderr.WriteLine(
                    $"login failed: the new device login could not be completed ({ex.Message}). Nothing was changed; "
                    + (removed
                        ? "the tool removed the device again."
                        : $"the tool could not remove the device, so remove \"{AmazonDeviceAuth.DeviceDisplayName}\" under Manage Your Content and Devices."));
                return 1;
            }

            await DeregisterPreviousDeviceAsync(existing, stderr, ct);

            stdout.WriteLine(displayName.Length > 0
                ? $"Amazon Photos: connected as {displayName}, {count} items"
                : $"Amazon Photos: connected, {count} items");
            stdout.WriteLine(DeviceRegisteredMessage);
            stdout.WriteLine(SavedMessage);
            return 0;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            stderr.WriteLine($"login failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Best-effort removal of the device whose refresh token a login just replaced, so it is not
    /// left on the account with no way for the tool to remove it. A failure only warns.
    /// </summary>
    private static async Task DeregisterPreviousDeviceAsync(Secrets previous, TextWriter stderr, CancellationToken ct)
    {
        if (!CommandSupport.HasDeviceLogin(previous))
        {
            return;
        }

        try
        {
            await CommandSupport.DeregisterDeviceAsync(previous.AmazonRefreshToken!, ct);
        }
        catch (DeviceAuthException)
        {
            stderr.WriteLine(PreviousDeviceNotRemovedMessage);
        }
    }

    /// <summary>Puts secrets.toml back exactly as it was before a failed login (absent when it did not exist).</summary>
    private static void RestoreSecretsFile(string path, byte[]? previous)
    {
        if (previous is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, previous);
        }
    }

    /// <summary>Best-effort deregistration after a failed login; true when Amazon accepted it.</summary>
    private static async Task<bool> TryDeregisterAsync(AmazonDeviceAuth auth, string accessToken, CancellationToken ct)
    {
        try
        {
            await auth.DeregisterAsync(accessToken, ct);
            return true;
        }
        catch (DeviceAuthException)
        {
            return false;
        }
    }

    /// <summary>The first non-blank line of stdin, trimmed, or null at end of input.</summary>
    private static async Task<string?> ReadNonEmptyLineAsync(CancellationToken ct)
    {
        while (await Console.In.ReadLineAsync(ct) is { } line)
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }

        return null;
    }

    private static async Task<int> LoginWithCookiesAsync(TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        string input;
        if (Console.IsInputRedirected)
        {
            input = await Console.In.ReadToEndAsync(ct);
        }
        else
        {
            stderr.WriteLine(PastePrompt);

            // On a terminal, Console.In's line editor needs Ctrl-D twice to end input; the raw
            // stdin stream ends on the first Ctrl-D, as PastePrompt says.
            using var raw = new StreamReader(Console.OpenStandardInput());
            input = await raw.ReadToEndAsync(ct);
        }
        if (!TryParse(input, out var cookies, out var problem))
        {
            stderr.WriteLine($"login failed: {problem}");
            return 1;
        }

        try
        {
            var paths = AppPaths.Resolve();
            var existing = CommandSupport.LoadSecretsIfPresent(paths);

            // Pasted cookies replace a device login, whose device is removed from the account.
            var secrets = new Secrets(cookies.SessionId, cookies.Ubid, cookies.At, existing.SmtpPassword);
            ConfigLoader.WriteSecrets(paths.SecretsFile, secrets);
            await DeregisterPreviousDeviceAsync(existing, stderr, ct);

            string name;
            long count;
            try
            {
                using var source = CommandSupport.CreateAmazonSource(secrets);
                (name, count) = await source.WhoAmIAsync(ct);
            }
            catch (AuthExpiredException)
            {
                stderr.WriteLine(RejectedMessage);
                return 1;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException)
            {
                stderr.WriteLine($"login failed: could not verify the cookies with Amazon Photos (they are saved in secrets.toml anyway): {ex.Message}");
                return 1;
            }

            using var store = new StateStore(paths.StateDb);
            store.SetNeedsLogin(false, null);

            stdout.WriteLine(name.Length > 0
                ? $"Amazon Photos: connected as {name}, {count} items"
                : $"Amazon Photos: connected, {count} items");
            stdout.WriteLine(SavedMessage);
            return 0;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            stderr.WriteLine($"login failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Extracts the three cookies from a pasted cURL command (its <c>Cookie:</c> header or <c>-b</c>
    /// value) or from three non-empty lines. The error names only the missing keys, never any value.
    /// </summary>
    internal static bool TryParse(string input, out Cookies cookies, out string problem)
    {
        cookies = default;
        if (CurlPattern().IsMatch(input))
        {
            var header = CookieHeaderPattern().Match(input);
            if (!header.Success)
            {
                problem = "the pasted curl command has no Cookie header (with session-id, ubid-main and at-main); copy the request as cURL from the browser's network tab while signed in to Amazon Photos.";
                return false;
            }

            var jar = ParseCookieHeader(header.Groups["value"].Value);
            var sessionId = Lookup(jar, _sessionIdKey);
            var ubid = Lookup(jar, _ubidKeys);
            var at = Lookup(jar, _atKeys);
            if (sessionId is null || ubid is null || at is null)
            {
                var missing = new List<string>(3);
                if (sessionId is null)
                {
                    missing.Add(_sessionIdKey);
                }

                if (ubid is null)
                {
                    missing.Add(_ubidKeys[0]);
                }

                if (at is null)
                {
                    missing.Add(_atKeys[0]);
                }

                problem = $"the Cookie header is missing {string.Join(", ", missing)}; copy a request made while signed in to Amazon Photos.";
                return false;
            }

            cookies = new Cookies(sessionId, ubid, at);
            problem = "";
            return true;
        }

        var lines = input.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count != 3)
        {
            problem = $"expected a pasted curl command or three lines (session-id, ubid-main, at-main), got {lines.Count} non-empty lines.";
            return false;
        }

        cookies = new Cookies(lines[0], lines[1], lines[2]);
        problem = "";
        return true;
    }

    private static Dictionary<string, string> ParseCookieHeader(string header)
    {
        var jar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in header.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim().Trim('"');
            if (name.Length > 0 && value.Length > 0)
            {
                jar.TryAdd(name, value);
            }
        }

        return jar;
    }

    private static string? Lookup(Dictionary<string, string> jar, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (jar.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    internal readonly record struct Cookies(string SessionId, string Ubid, string At);

    [GeneratedRegex(@"\bcurl\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurlPattern();

    // -H 'cookie: a=b; c=d' | -H "Cookie: ..." | -b 'a=b; c=d' | --cookie "..." | -b a=b
    [GeneratedRegex(
        """(?:-H\s+(?<q>['"])\s*cookie\s*:\s*(?<value>.*?)\k<q>)|(?:(?:-b|--cookie)\s+(?:(?<q>['"])(?<value>.*?)\k<q>|(?<value>\S+)))""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CookieHeaderPattern();
}
