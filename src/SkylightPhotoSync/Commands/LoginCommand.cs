using System.CommandLine;
using System.Text.RegularExpressions;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Commands;

/// <summary>
/// <c>login</c>: reads the Amazon session cookies from stdin, either from a pasted "Copy as cURL"
/// command or as three lines (session-id, ubid-main, at-main), saves them to secrets.toml and clears
/// <c>needs_login</c>. Cookie values are never printed or logged.
/// </summary>
public static partial class LoginCommand
{
    public const string SavedMessage = "Saved. Run 'skylight-sync run --dry-run' to verify.";

    public const string PastePrompt =
        "Paste the 'Copy as cURL' command (or session-id, ubid-main and at-main on three lines), then press Ctrl-D.";

    private const string _sessionIdKey = "session-id";
    private static readonly string[] _ubidKeys = ["ubid-main", "ubid_main"];
    private static readonly string[] _atKeys = ["at-main", "at_main"];

    public static Command Create()
    {
        var command = new Command(
            "login",
            "Save the Amazon session cookies read from stdin: paste a 'Copy as cURL' command, or session-id, ubid-main and at-main on three lines.");

        command.SetAction(async (parseResult, ct) =>
        {
            var stdout = parseResult.InvocationConfiguration.Output;
            var stderr = parseResult.InvocationConfiguration.Error;

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
                ConfigLoader.WriteSecrets(
                    paths.SecretsFile,
                    new Secrets(cookies.SessionId, cookies.Ubid, cookies.At, existing.SmtpPassword));

                using var store = new StateStore(paths.StateDb);
                store.SetNeedsLogin(false, null);

                stdout.WriteLine(SavedMessage);
                return 0;
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                stderr.WriteLine($"login failed: {ex.Message}");
                return 1;
            }
        });

        return command;
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
