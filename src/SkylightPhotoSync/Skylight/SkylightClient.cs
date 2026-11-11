using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkylightPhotoSync.Skylight;

/// <summary>
/// <see cref="ISkylightClient"/> over the unofficial, reverse-engineered Skylight app API at
/// <c>app.ourskylight.com</c> (see docs/superpowers/specs/2026-11-10-skylight-rotation-spike.md).
/// Signs in with the account email and password through the same OAuth 2 + PKCE flow the apps use,
/// caches the tokens in <c>tokenFile</c> (mode 0600) so most runs never sign in again, refreshes an
/// expired access token, and retries a request once after a 401/403. Token and password values never
/// appear in exceptions or output.
/// </summary>
public sealed partial class SkylightClient : ISkylightClient, IDisposable
{
    public const string BaseUrl = "https://app.ourskylight.com";
    public const string ClientId = "skylight-mobile";
    public const string RedirectUri = "https://ourskylight.com/welcome";
    public const string UserAgent = "SkylightMobile (web)";

    /// <summary>Stop paging after this many pages (30 photos each) in case the server never says "last page".</summary>
    internal const int MaxPages = 1000;

    internal const string CaptchaMessage =
        "Skylight asked for a CAPTCHA, which this tool cannot answer. Sign in once in the Skylight app or at app.ourskylight.com, then try again.";

    internal const string RateLimitedMessage =
        "Skylight is rate-limiting sign-in attempts (HTTP 429). Wait a while before trying again.";

    private const int _maxRedirects = 10;
    private static readonly TimeSpan _expiryMargin = TimeSpan.FromMinutes(5);

    private static readonly UnixFileMode _tokenFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _email;
    private readonly string _password;
    private readonly string _tokenFile;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private Tokens? _tokens;
    private bool _tokensLoaded;

    public SkylightClient(
        string email,
        string password,
        string tokenFile,
        HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenFile);
        _email = email;
        _password = password;
        _tokenFile = tokenFile;
        _now = now ?? (() => DateTimeOffset.UtcNow);

        // Redirects are walked by hand (the authorization code arrives in a redirect that must not be
        // followed) and cookies are attached by hand so an injected test handler sees them too.
        var ownHandler = handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(ownHandler, disposeHandler: handler is null) { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<IReadOnlyList<SkylightFrame>> ListFramesAsync(CancellationToken ct)
    {
        using var document = await GetJsonAsync("/api/frames", ct);
        var frames = new List<SkylightFrame>();
        foreach (var entry in Data(document.RootElement))
        {
            var id = ReadId(entry);
            if (id is null)
            {
                continue;
            }

            var name = Attributes(entry) is { } attributes && attributes.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? ""
                    : "";
            frames.Add(new SkylightFrame(id, name));
        }

        return frames;
    }

    public async Task<IReadOnlyList<SkylightMessage>> ListMessagesAsync(string frameId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        var messages = new List<SkylightMessage>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var document = await GetJsonAsync(
                $"/api/frames/{Uri.EscapeDataString(frameId)}/messages?page={page.ToString(CultureInfo.InvariantCulture)}", ct);
            var count = 0;
            foreach (var entry in Data(document.RootElement))
            {
                count++;
                if (ParseMessage(entry) is { } message)
                {
                    messages.Add(message);
                }
            }

            var numPages = document.RootElement.TryGetProperty("meta", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("num_pages", out var numPagesElement)
                && numPagesElement.TryGetInt32(out var parsed)
                    ? parsed
                    : (int?)null;
            if (count == 0 || (numPages is { } last && page >= last))
            {
                break;
            }
        }

        return messages;
    }

    public async Task DeleteMessageAsync(string frameId, string messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        using var response = await SendApiAsync(
            HttpMethod.Delete, $"/api/frames/{Uri.EscapeDataString(frameId)}/messages/{Uri.EscapeDataString(messageId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;   // already gone
        }

        EnsureSuccess(response, "delete a photo");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Base64url without padding, as PKCE wants.</summary>
    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The <c>csrf-token</c> meta tag, or a hidden <c>authenticity_token</c> input, of the sign-in page.</summary>
    internal static string? FindCsrfToken(string html)
    {
        var match = CsrfMetaRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }

        match = AuthenticityInputRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static SkylightMessage? ParseMessage(JsonElement entry)
    {
        var id = ReadId(entry);
        if (id is null || Attributes(entry) is not { } attributes)
        {
            return null;
        }

        if (!attributes.TryGetProperty("created_at", out var createdElement)
            || createdElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(createdElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
        {
            return null;   // without a date it cannot be ordered, so it is never pruned
        }

        var fromEmail = attributes.TryGetProperty("from_email", out var fromElement) && fromElement.ValueKind == JsonValueKind.String
            ? fromElement.GetString()
            : null;
        var destroyed = attributes.TryGetProperty("destroyed_at", out var destroyedElement)
            && destroyedElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        return new SkylightMessage(id, createdAt, fromEmail, destroyed);
    }

    private static IEnumerable<JsonElement> Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : throw new SkylightApiException("Skylight answered without a data list; the unofficial API may have changed.");

    private static JsonElement? Attributes(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("attributes", out var attributes)
            && attributes.ValueKind == JsonValueKind.Object
                ? attributes
                : null;

    private static string? ReadId(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        using var response = await SendApiAsync(HttpMethod.Get, pathAndQuery, ct);
        EnsureSuccess(response, "list frames or photos");
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new SkylightApiException("Skylight answered with something that is not JSON; the unofficial API may have changed.", ex);
        }
    }

    /// <summary>An authenticated API call; one retry with fresh tokens after a 401/403.</summary>
    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string pathAndQuery, CancellationToken ct)
    {
        var accessToken = await AccessTokenAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _http.SendAsync(request, ct);
            if (attempt == 0 && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                accessToken = await RenewAsync(ct);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new SkylightApiException(
                    $"Skylight refused the request (HTTP {(int)response.StatusCode}) even after signing in again.");
            }

            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SkylightApiException($"Skylight answered HTTP {(int)response.StatusCode} when trying to {what}.");
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (!_tokensLoaded)
        {
            _tokens = LoadTokens();
            _tokensLoaded = true;
        }

        if (_tokens is { } tokens && tokens.ExpiresAt - _expiryMargin > _now())
        {
            return tokens.AccessToken;
        }

        return await RenewAsync(ct);
    }

    /// <summary>The refresh grant when a refresh token is cached, else (or when it fails) a full sign-in.</summary>
    private async Task<string> RenewAsync(CancellationToken ct)
    {
        if (_tokens?.RefreshToken is { Length: > 0 } refreshToken)
        {
            var refreshed = await TryTokenRequestAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = ClientId,
                },
                ct);
            if (refreshed is not null)
            {
                SaveTokens(refreshed);
                return refreshed.AccessToken;
            }
        }

        var signedIn = await SignInAsync(ct);
        SaveTokens(signedIn);
        return signedIn.AccessToken;
    }

    /// <summary>
    /// OAuth 2 authorization code + PKCE: open the authorize URL (which lands on the sign-in page and
    /// sets the session cookie), post the email and password with the page's CSRF token, open the
    /// authorize URL again and read the code from the redirect to <see cref="RedirectUri"/>, then
    /// exchange the code for tokens.
    /// </summary>
    private async Task<Tokens> SignInAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authorizeUrl = BaseUrl + "/oauth/authorize?" + FormQuery(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["scope"] = "everything",
            ["redirect_uri"] = RedirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        var (page, code) = await WalkAsync(new HttpRequestMessage(HttpMethod.Get, authorizeUrl), state, ct);
        if (code is null)
        {
            var csrf = page is null ? null : FindCsrfToken(page);
            if (csrf is null)
            {
                throw new SkylightSignInException(MentionsCaptcha(page)
                    ? CaptchaMessage
                    : "Could not find the Skylight sign-in form; the unofficial API may have changed.");
            }

            var post = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/auth/session")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["authenticity_token"] = csrf,
                    ["email"] = _email,
                    ["password"] = _password,
                }),
            };
            (var answer, code) = await WalkAsync(post, state, ct);
            if (code is null)
            {
                (_, code) = await WalkAsync(new HttpRequestMessage(HttpMethod.Get, authorizeUrl), state, ct);
            }

            if (code is null)
            {
                throw new SkylightSignInException(MentionsCaptcha(answer)
                    ? CaptchaMessage
                    : "Skylight did not accept the sign-in. Check [skylight] email and password in secrets.toml.");
            }
        }

        return await TryTokenRequestAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = ClientId,
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["code_verifier"] = verifier,
                },
                ct)
            ?? throw new SkylightSignInException("Skylight did not hand out a token after signing in.");
    }

    /// <summary>
    /// Sends <paramref name="first"/> and follows redirects on app.ourskylight.com by hand. Returns the
    /// authorization code when a redirect to <see cref="RedirectUri"/> carries one (it is never fetched),
    /// otherwise the final page's HTML.
    /// </summary>
    private async Task<(string? Page, string? Code)> WalkAsync(HttpRequestMessage first, string state, CancellationToken ct)
    {
        var next = first;
        for (var hop = 0; hop <= _maxRedirects; hop++)
        {
            using var request = next;
            AttachCookies(request);
            using var response = await _http.SendAsync(request, ct);
            StoreCookies(request.RequestUri!, response);
            var status = (int)response.StatusCode;
            if (status >= 500)
            {
                // An outage, not a verdict on the password: the next run may simply try again.
                throw new SkylightApiException($"Skylight answered HTTP {status} during sign-in.");
            }

            if (status == (int)HttpStatusCode.TooManyRequests)
            {
                throw new SkylightSignInException(
                    RateLimitedMessage);
            }

            if (status is not (>= 300 and < 400) || response.Headers.Location is not { } location)
            {
                return (await response.Content.ReadAsStringAsync(ct), null);
            }

            var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            if (target.GetLeftPart(UriPartial.Path).TrimEnd('/') == RedirectUri)
            {
                var query = ParseQuery(target.Query);
                if (query.TryGetValue("state", out var returnedState) && returnedState != state)
                {
                    throw new SkylightSignInException("Skylight's sign-in answered with a mismatched state.");
                }

                return (null, query.TryGetValue("code", out var code) && code.Length > 0 ? code : null);
            }

            if (!string.Equals(target.Host, new Uri(BaseUrl).Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new SkylightSignInException($"Skylight's sign-in redirected somewhere unexpected ({target.Host}).");
            }

            next = new HttpRequestMessage(HttpMethod.Get, target);
        }

        throw new SkylightSignInException("Skylight's sign-in redirected too many times.");
    }

    private async Task<Tokens?> TryTokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/oauth/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct);
        if ((int)response.StatusCode >= 500)
        {
            // An outage says nothing about the tokens or the password, so neither is given up on.
            throw new SkylightApiException($"Skylight answered HTTP {(int)response.StatusCode} when asked for a token.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Returning null here would send a refused refresh on to the password, into the same throttle.
            throw new SkylightSignInException(
                RateLimitedMessage);
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (string.IsNullOrEmpty(access))
            {
                return null;
            }

            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 7200;
            return new Tokens(access, refresh ?? _tokens?.RefreshToken, _now().AddSeconds(expiresIn));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void AttachCookies(HttpRequestMessage request)
    {
        var header = _cookies.GetCookieHeader(request.RequestUri!);
        if (header.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    private void StoreCookies(Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            try
            {
                _cookies.SetCookies(uri, value);
            }
            catch (CookieException)
            {
                // A cookie the container cannot parse is not one the sign-in needs.
            }
        }
    }

    /// <summary>True when the page Skylight answered with looks like a CAPTCHA or similar challenge.</summary>
    private static bool MentionsCaptcha(string? page) =>
        page is not null && page.Contains("captcha", StringComparison.OrdinalIgnoreCase);

    private static string FormQuery(Dictionary<string, string> values) =>
        string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            result[name] = value;
        }

        return result;
    }

    private Tokens? LoadTokens()
    {
        try
        {
            if (!File.Exists(_tokenFile))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_tokenFile));
            var root = document.RootElement;

            // Tokens belong to one account: after [skylight] email changes they must not keep
            // working (and pruning) the previous account's frames. A cache without an account
            // predates this check, so it costs one fresh sign-in too.
            var account = root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (!SameAccount(account, _email))
            {
                return null;
            }

            var access = root.GetProperty("access_token").GetString();
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var expiresAt = DateTimeOffset.Parse(root.GetProperty("expires_at").GetString()!, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(access) ? null : new Tokens(access, refresh, expiresAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            return null;   // a damaged cache only costs a fresh sign-in
        }
    }

    private static bool SameAccount(string? cached, string configured) =>
        cached is not null && string.Equals(cached.Trim(), configured.Trim(), StringComparison.OrdinalIgnoreCase);

    private void SaveTokens(Tokens tokens)
    {
        _tokens = tokens;
        var json = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["email"] = _email.Trim(),
            ["access_token"] = tokens.AccessToken,
            ["refresh_token"] = tokens.RefreshToken,
            ["expires_at"] = tokens.ExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        });

        var directory = Path.GetDirectoryName(Path.GetFullPath(_tokenFile)) ?? ".";
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(_tokenFile)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = _tokenFileMode;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
            }

            File.Move(temp, _tokenFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is an optimisation: the next run signs in again.
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    [GeneratedRegex("""<meta\s+name=["']csrf-token["']\s+content=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex CsrfMetaRegex();

    [GeneratedRegex("""name=["']authenticity_token["'][^>]*value=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex AuthenticityInputRegex();

    private sealed record Tokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}
