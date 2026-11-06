using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkylightPhotoSync.Sources.Amazon;

/// <summary>One website cookie minted by Amazon for the registered device.</summary>
public sealed record AmazonCookie(string Name, string Value, DateTimeOffset? Expires);

/// <summary>
/// What <c>POST /auth/register</c> hands back: the long-lived refresh token (the only credential
/// worth keeping), a one-hour access token (used to deregister again if the login fails), the
/// initial website cookies, and the account holder's name for the "connected as" line.
/// </summary>
public sealed record DeviceRegistration(
    string RefreshToken,
    string AccessToken,
    TimeSpan AccessTokenLifetime,
    IReadOnlyList<AmazonCookie> Cookies,
    string CustomerName);

/// <summary>A device-login call failed for a reason other than an expired or revoked login.</summary>
public sealed class DeviceAuthException : Exception
{
    public DeviceAuthException(string message)
        : base(message)
    {
    }

    public DeviceAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The one-time browser device login and per-run cookie refresh described in issue #30, after the
/// Audible iOS "external login" flow (mkb79/Audible) also used by Apollon77/alexa-cookie and
/// airbone42/immich-amazon-sync. The user signs in once in their own browser (with 2FA and any
/// CAPTCHA) at the PKCE URL from <see cref="BuildSignInUrl"/>, lands on a "page not found" at
/// <c>/ap/maplanding</c> whose address carries the authorization code, and pastes that address
/// back. <see cref="RegisterAsync"/> turns the code into a refresh token that stays valid until the
/// device is removed from the account; <see cref="ExchangeCookiesAsync"/> mints fresh website
/// cookies from it before every run. Registration happens exactly once: re-registering is what
/// broke alexa-cookie (issue #190). No password, TOTP secret or token is ever printed or logged.
/// Shapes are documented, not yet verified live; see <c>docs/research-notes.md</c>.
/// </summary>
public sealed class AmazonDeviceAuth : IDisposable
{
    public const string DeviceType = "A2CZJZGLK2JJVM";
    public const string AppName = "Audible";
    public const string AppVersion = "3.56.2";
    public const string SoftwareVersion = "35602678";
    public const string OsVersion = "15.0.0";
    public const string DeviceModel = "iPhone";

    /// <summary>How the device is listed under "Manage Your Content and Devices".</summary>
    public const string DeviceDisplayName = "Skylight Photo Sync";

    /// <summary>
    /// The name sent at registration: Amazon expands the placeholders into "Matt's Skylight Photo
    /// Sync" (with a dupe suffix when needed), exactly as the Audible app does.
    /// </summary>
    public const string DeviceName = "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%" + DeviceDisplayName;

    public const string UserAgent = "AmazonWebView/Audible/3.56.2/iOS/15.0.0/iPhone";

    public const string AuthorizationCodeParameter = "openid.oa2.authorization_code";

    private const string _openIdIdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";

    /// <summary>
    /// Audible's country suffix for <c>openid.assoc_handle</c> and the retail marketplace id per
    /// storefront (the tlds immich-amazon-sync supports). The marketplace ids are Amazon's retail
    /// ones (US <c>ATVPDKIKX0DER</c>); the Audible app sends its own (US <c>AF2M0KC94RCEA</c>), so
    /// the live pass should try the Audible value if sign-in refuses.
    /// </summary>
    private static readonly Dictionary<string, (string Country, string MarketplaceId)> _storefronts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com"] = ("us", "ATVPDKIKX0DER"),
        ["co.uk"] = ("uk", "A1F83G8C2ARO7P"),
        ["de"] = ("de", "A1PA6795UKMFR9"),
        ["fr"] = ("fr", "A13V1IB3VIYZZH"),
        ["it"] = ("it", "APJ6JRA9NG5V4"),
        ["es"] = ("es", "A1RKKUPIHCS9HS"),
        ["ca"] = ("ca", "A2EUQ1WTGCTBG2"),
    };

    private const string _serialAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private readonly string _tld;
    private readonly HttpClient _http;

    public AmazonDeviceAuth(HttpMessageHandler? handler = null, string tld = "com")
    {
        _tld = RequireStorefront(tld).Tld;
        _http = new HttpClient(
            handler ?? new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All },
            disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
    }

    public string Tld => _tld;

    /// <summary>A PKCE verifier: base64url of 32 random bytes, no padding.</summary>
    public static string NewCodeVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A device serial: 40 random uppercase alphanumerics, fixed for the device's lifetime.</summary>
    public static string NewSerial() => RandomNumberGenerator.GetString(_serialAlphabet, 40);

    /// <summary>The OAuth client id for a serial: hex of <c>serial#A2CZJZGLK2JJVM</c>.</summary>
    public static string ClientId(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        return Convert.ToHexStringLower(Encoding.ASCII.GetBytes(serial + "#" + DeviceType));
    }

    /// <summary>The S256 challenge for a verifier: base64url of its SHA-256, no padding.</summary>
    public static string CodeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>
    /// The <c>https://www.amazon.{tld}/ap/signin</c> URL the user opens in a browser. It asks for
    /// an authorization code bound to the PKCE challenge and to a device client id, with the
    /// Audible iOS association handle and page id so Amazon shows the device-registration consent.
    /// </summary>
    public static Uri BuildSignInUrl(string tld, string codeVerifier, string serial)
    {
        var (storefront, country, marketplaceId) = RequireStorefront(tld);
        var query = new (string Name, string Value)[]
        {
            ("openid.oa2.response_type", "code"),
            ("openid.oa2.code_challenge_method", "S256"),
            ("openid.oa2.code_challenge", CodeChallenge(codeVerifier)),
            ("openid.return_to", $"https://www.amazon.{storefront}/ap/maplanding"),
            ("openid.assoc_handle", $"amzn_audible_ios_{country}"),
            ("openid.identity", _openIdIdentifierSelect),
            ("pageId", "amzn_audible_ios"),
            ("accountStatusPolicy", "P1"),
            ("openid.claimed_id", _openIdIdentifierSelect),
            ("openid.mode", "checkid_setup"),
            ("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            ("openid.oa2.client_id", $"device:{ClientId(serial)}"),
            ("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            ("marketPlaceId", marketplaceId),
            ("openid.oa2.scope", "device_auth_access"),
            ("forceMobileLayout", "true"),
            ("openid.ns", "http://specs.openid.net/auth/2.0"),
            ("openid.pape.max_auth_age", "0"),
        };

        var builder = new StringBuilder($"https://www.amazon.{storefront}/ap/signin");
        var separator = '?';
        foreach (var (name, value) in query)
        {
            builder.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    /// <summary>
    /// The authorization code from the pasted <c>/ap/maplanding?...</c> address. The error names
    /// the missing parameter and never echoes the paste.
    /// </summary>
    public static string ParseAuthorizationCode(string pastedUrl)
    {
        var text = (pastedUrl ?? "").Trim();
        var queryStart = text.IndexOf('?');
        if (text.Length == 0 || queryStart < 0)
        {
            throw new ArgumentException(
                $"Expected the full address of the page Amazon lands on after sign-in (it contains {AuthorizationCodeParameter}=...); paste the whole address bar.",
                nameof(pastedUrl));
        }

        var query = text[(queryStart + 1)..];
        var fragment = query.IndexOf('#');
        if (fragment >= 0)
        {
            query = query[..fragment];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (!name.Equals(AuthorizationCodeParameter, StringComparison.Ordinal))
            {
                continue;
            }

            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..]).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        throw new ArgumentException(
            $"The pasted address has no {AuthorizationCodeParameter}; after signing in, copy the whole address of the \"page not found\" page at /ap/maplanding and paste it.",
            nameof(pastedUrl));
    }

    /// <summary>
    /// <c>POST https://api.amazon.{tld}/auth/register</c>: registers this tool as a device and
    /// returns its tokens. Call it once per login; store the refresh token; never call it again
    /// for the same device.
    /// </summary>
    public async Task<DeviceRegistration> RegisterAsync(string authorizationCode, string codeVerifier, string serial, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);

        var body = new
        {
            requested_token_type = new[] { "bearer", "mac_dms", "website_cookies" },
            cookies = new { website_cookies = Array.Empty<object>(), domain = CookieDomain },
            registration_data = new
            {
                domain = "Device",
                app_version = AppVersion,
                device_serial = serial,
                device_type = DeviceType,
                device_name = DeviceName,
                os_version = OsVersion,
                software_version = SoftwareVersion,
                device_model = DeviceModel,
                app_name = AppName,
            },
            auth_data = new
            {
                client_id = ClientId(serial),
                authorization_code = authorizationCode,
                code_verifier = codeVerifier,
                code_algorithm = "SHA-256",
                client_domain = "DeviceLegacy",
            },
            requested_extensions = new[] { "device_info", "customer_info" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.amazon.{_tld}/auth/register")
        {
            Content = JsonContent.Create(body),
        };
        using var document = await SendJsonAsync(request, "register the device", ct);
        var root = document.RootElement;

        if (!TryGet(root, out var success, "response", "success") || !TryGet(success, out var tokens, "tokens"))
        {
            throw new DeviceAuthException("Amazon's registration response has no response.success.tokens; the endpoint shape may have changed.");
        }

        var refreshToken = TryGet(tokens, out var bearer, "bearer") ? GetString(bearer, "refresh_token") : null;
        var accessToken = TryGet(tokens, out bearer, "bearer") ? GetString(bearer, "access_token") : null;
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            throw new DeviceAuthException("Amazon's registration response carries no bearer refresh_token/access_token; the endpoint shape may have changed.");
        }

        var lifetime = TimeSpan.FromHours(1);
        if (TryGet(tokens, out bearer, "bearer") && bearer.TryGetProperty("expires_in", out var expiresIn))
        {
            var seconds = expiresIn.ValueKind switch
            {
                JsonValueKind.Number when expiresIn.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(expiresIn.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => 3600L,
            };
            lifetime = TimeSpan.FromSeconds(seconds);
        }

        var cookies = new List<AmazonCookie>();
        if (tokens.TryGetProperty("website_cookies", out var websiteCookies) && websiteCookies.ValueKind == JsonValueKind.Array)
        {
            foreach (var cookie in websiteCookies.EnumerateArray())
            {
                if (ParseCookie(cookie) is { } parsed)
                {
                    cookies.Add(parsed);
                }
            }
        }

        var customerName = "";
        if (TryGet(success, out var customerInfo, "extensions", "customer_info"))
        {
            customerName = GetString(customerInfo, "name")?.Trim() ?? "";
        }

        return new DeviceRegistration(refreshToken, accessToken, lifetime, cookies, customerName);
    }

    /// <summary>
    /// <c>POST https://www.amazon.{tld}/ap/exchangetoken/cookies</c>: trades the refresh token for
    /// a fresh set of website cookies (<c>session-id</c>, <c>ubid-main</c>, <c>at-main</c>,
    /// <c>sess-at-main</c>, <c>x-main</c>, <c>session-token</c>). A 401/403, or a reply without
    /// cookies, means the device was removed or the token revoked: <see cref="AuthExpiredException"/>.
    /// </summary>
    public async Task<IReadOnlyList<AmazonCookie>> ExchangeCookiesAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://www.amazon.{_tld}/ap/exchangetoken/cookies")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["app_name"] = AppName,
                ["app_version"] = AppVersion,
                ["source_token"] = refreshToken,
                ["source_token_type"] = "refresh_token",
                ["requested_token_type"] = "auth_cookies",
                ["domain"] = CookieDomain,
            }),
        };
        request.Headers.Add("x-amzn-identity-auth-domain", $"api.amazon.{_tld}");

        using var document = await SendJsonAsync(request, "refresh the Amazon cookies", ct, authFailureIsExpiry: true);

        var cookies = new List<AmazonCookie>();
        if (TryGet(document.RootElement, out var byDomain, "response", "tokens", "cookies") && byDomain.ValueKind == JsonValueKind.Object)
        {
            foreach (var domain in byDomain.EnumerateObject())
            {
                if (domain.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var cookie in domain.Value.EnumerateArray())
                {
                    if (ParseCookie(cookie) is { } parsed)
                    {
                        cookies.Add(parsed);
                    }
                }
            }
        }

        if (cookies.Count == 0)
        {
            throw new AuthExpiredException(
                "Amazon returned no cookies for the saved device login; the device may have been removed from the account. Run `skylight-sync login`.");
        }

        return cookies;
    }

    /// <summary>
    /// <c>POST https://api.amazon.{tld}/auth/token</c>: a one-hour access token from the refresh
    /// token, needed only to deregister.
    /// </summary>
    public async Task<string> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.amazon.{_tld}/auth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["app_name"] = AppName,
                ["app_version"] = AppVersion,
                ["source_token"] = refreshToken,
                ["source_token_type"] = "refresh_token",
                ["requested_token_type"] = "access_token",
            }),
        };

        using var document = await SendJsonAsync(request, "get an access token", ct, authFailureIsExpiry: true);
        var accessToken = GetString(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new DeviceAuthException("Amazon's token response carries no access_token; the endpoint shape may have changed.");
        }

        return accessToken;
    }

    /// <summary>
    /// <c>POST https://api.amazon.{tld}/auth/deregister</c> with the bearer token: removes this
    /// device from the account (and only this device). Used by <c>logout</c> and when a login
    /// fails after registration, so no orphan device is left behind.
    /// </summary>
    public async Task DeregisterAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.amazon.{_tld}/auth/deregister")
        {
            Content = JsonContent.Create(new { deregister_all_existing_accounts = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var document = await SendJsonAsync(request, "remove the device", ct);
    }

    public void Dispose() => _http.Dispose();

    private string CookieDomain => $".amazon.{_tld}";

    /// <summary>
    /// Sends and parses JSON. Errors name the operation, the status and Amazon's
    /// <c>response.error.code</c>/<c>message</c> when present; never the request or the body's
    /// tokens. With <paramref name="authFailureIsExpiry"/>, 401/403 become
    /// <see cref="AuthExpiredException"/>.
    /// </summary>
    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, string what, CancellationToken ct, bool authFailureIsExpiry = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new DeviceAuthException($"Could not {what}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                if (authFailureIsExpiry && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new AuthExpiredException(
                        $"Amazon rejected the saved device login (HTTP {status}); the device may have been removed from the account. Run `skylight-sync login`.");
                }

                throw new DeviceAuthException($"Could not {what}: Amazon answered HTTP {status}{DescribeError(body)}.");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new DeviceAuthException($"Could not {what}: Amazon answered HTTP {status} with a non-JSON body.", ex);
            }
        }
    }

    /// <summary>Amazon's <c>response.error.code</c> and <c>message</c>, if the body carries them.</summary>
    private static string DescribeError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryGet(document.RootElement, out var error, "response", "error"))
            {
                var code = GetString(error, "code");
                var message = GetString(error, "message");
                if (code is not null || message is not null)
                {
                    return $" ({code}{(code is not null && message is not null ? ": " : "")}{message})";
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: nothing worth quoting.
        }

        return "";
    }

    private static AmazonCookie? ParseCookie(JsonElement cookie)
    {
        var name = GetString(cookie, "Name");
        var value = GetString(cookie, "Value");
        if (string.IsNullOrEmpty(name) || value is null)
        {
            return null;
        }

        DateTimeOffset? expires = null;
        if (GetString(cookie, "Expires") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            expires = parsed;
        }

        // Amazon wraps some values (x-main) in quotes that are not part of the cookie.
        return new AmazonCookie(name, value.Trim('"'), expires);
    }

    private static (string Tld, string Country, string MarketplaceId) RequireStorefront(string tld)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tld);
        if (!_storefronts.TryGetValue(tld, out var storefront))
        {
            throw new ArgumentException(
                $"Amazon storefront '{tld}' is not supported for device login; use one of {string.Join(", ", _storefronts.Keys)}.", nameof(tld));
        }

        return (tld.ToLowerInvariant(), storefront.Country, storefront.MarketplaceId);
    }

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var name in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
