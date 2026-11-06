using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Sources.Amazon;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// Pins the Audible-style device login (issue #30): the PKCE sign-in URL, the pasted
/// <c>/ap/maplanding</c> URL, <c>/auth/register</c>, <c>/ap/exchangetoken/cookies</c>,
/// <c>/auth/token</c> and <c>/auth/deregister</c>. Responses are the hand-written placeholders
/// under <c>Fixtures/AmazonAuth</c> (documented shapes, awaiting live verification); every token
/// value in them is fake and must never reach an exception message.
/// </summary>
public sealed class AmazonDeviceAuthTests
{
    private const string _serial = "ABCDEFGHIJ0123456789KLMNOPQRST0123456789";
    private const string _verifier = "placeholder-code-verifier-not-random-0123456789abcdef";
    private const string _code = "ANPlaceholderAuthorizationCode";
    private const string _refresh = "Atnr|PLACEHOLDER_REFRESH_TOKEN_NOT_REAL";
    private const string _access = "Atna|PLACEHOLDER_ACCESS_TOKEN_NOT_REAL";

    private readonly ScriptedHandler _handler = new ScriptedHandler().WithDeviceAuthDefaults();

    private AmazonDeviceAuth Auth(string tld = "com") => new(_handler, tld);

    [Fact]
    public void SignInUrl_has_pkce_and_device_client_id()
    {
        var url = AmazonDeviceAuth.BuildSignInUrl("com", _verifier, _serial);

        Assert.Equal("https", url.Scheme);
        Assert.Equal("www.amazon.com", url.Host);
        Assert.Equal("/ap/signin", url.AbsolutePath);
        var query = ParseQuery(url);

        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        var expectedClientId = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(_serial + "#A2CZJZGLK2JJVM"));
        Assert.Equal("code", query["openid.oa2.response_type"]);
        Assert.Equal("S256", query["openid.oa2.code_challenge_method"]);
        Assert.Equal(expectedChallenge, query["openid.oa2.code_challenge"]);
        Assert.Equal($"device:{expectedClientId}", query["openid.oa2.client_id"]);
        Assert.Equal("device_auth_access", query["openid.oa2.scope"]);
        Assert.Equal("https://www.amazon.com/ap/maplanding", query["openid.return_to"]);
        Assert.Equal("amzn_audible_ios_us", query["openid.assoc_handle"]);
        Assert.Equal("amzn_audible_ios", query["pageId"]);
        Assert.Equal("http://specs.openid.net/auth/2.0/identifier_select", query["openid.identity"]);
        Assert.Equal("http://specs.openid.net/auth/2.0/identifier_select", query["openid.claimed_id"]);
        Assert.Equal("checkid_setup", query["openid.mode"]);
        Assert.Equal("http://specs.openid.net/auth/2.0", query["openid.ns"]);
        Assert.Equal("http://www.amazon.com/ap/ext/oauth/2", query["openid.ns.oa2"]);
        Assert.Equal("http://specs.openid.net/extensions/pape/1.0", query["openid.ns.pape"]);
        Assert.Equal("0", query["openid.pape.max_auth_age"]);
        Assert.Equal("P1", query["accountStatusPolicy"]);
        Assert.Equal("true", query["forceMobileLayout"]);
        Assert.Equal("ATVPDKIKX0DER", query["marketPlaceId"]);
        Assert.Equal(18, query.Count);

        // The verifier itself must never be in the URL; only its challenge.
        Assert.DoesNotContain(_verifier, url.ToString());
    }

    [Fact]
    public void SignInUrl_derives_assoc_handle_and_marketplace_from_tld()
    {
        var url = AmazonDeviceAuth.BuildSignInUrl("co.uk", _verifier, _serial);
        var query = ParseQuery(url);

        Assert.Equal("www.amazon.co.uk", url.Host);
        Assert.Equal("https://www.amazon.co.uk/ap/maplanding", query["openid.return_to"]);
        Assert.Equal("amzn_audible_ios_uk", query["openid.assoc_handle"]);
        Assert.Equal("A1F83G8C2ARO7P", query["marketPlaceId"]);
        Assert.Equal("amzn_audible_ios_de", ParseQuery(AmazonDeviceAuth.BuildSignInUrl("de", _verifier, _serial))["openid.assoc_handle"]);
        Assert.Throws<ArgumentException>(() => AmazonDeviceAuth.BuildSignInUrl("jp", _verifier, _serial));
    }

    [Fact]
    public void CodeVerifier_and_serial_have_expected_shape()
    {
        var verifier = AmazonDeviceAuth.NewCodeVerifier();
        var serial = AmazonDeviceAuth.NewSerial();

        // base64url of 32 random bytes, unpadded: 43 characters from the URL-safe alphabet.
        Assert.Equal(43, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.Equal(40, serial.Length);
        Assert.Matches("^[A-Z0-9]+$", serial);
        Assert.NotEqual(verifier, AmazonDeviceAuth.NewCodeVerifier());
        Assert.NotEqual(serial, AmazonDeviceAuth.NewSerial());
    }

    [Fact]
    public void ParseAuthorizationCode_extracts_code_and_rejects_missing()
    {
        var pasted =
            "https://www.amazon.com/ap/maplanding?openid.assoc_handle=amzn_audible_ios_us&openid.claimed_id=https%3A%2F%2Fwww.amazon.com%2Fap%2Fid%2Famzn1.account.PLACEHOLDER"
            + $"&openid.oa2.authorization_code={_code}&openid.mode=id_res&openid.ns=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0&openid.signed=assoc_handle\n";

        Assert.Equal(_code, AmazonDeviceAuth.ParseAuthorizationCode(pasted));
        Assert.Equal(_code, AmazonDeviceAuth.ParseAuthorizationCode($"  https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code={_code}  "));

        var missing = Assert.Throws<ArgumentException>(() =>
            AmazonDeviceAuth.ParseAuthorizationCode("https://www.amazon.com/ap/maplanding?openid.mode=id_res"));
        Assert.Contains("openid.oa2.authorization_code", missing.Message);
        Assert.Throws<ArgumentException>(() => AmazonDeviceAuth.ParseAuthorizationCode(""));
        Assert.Throws<ArgumentException>(() => AmazonDeviceAuth.ParseAuthorizationCode("not a url at all"));
    }

    [Fact]
    public async Task Register_posts_expected_body_and_parses_tokens()
    {
        using var auth = Auth();

        var registration = await auth.RegisterAsync(_code, _verifier, _serial, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.amazon.com/auth/register", request.RequestUri!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(_handler.BodyOf(request)!);
        var root = body.RootElement;
        Assert.Equal(["bearer", "mac_dms", "website_cookies"], root.GetProperty("requested_token_type").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(".amazon.com", root.GetProperty("cookies").GetProperty("domain").GetString());
        Assert.Equal(0, root.GetProperty("cookies").GetProperty("website_cookies").GetArrayLength());
        var registrationData = root.GetProperty("registration_data");
        Assert.Equal("Device", registrationData.GetProperty("domain").GetString());
        Assert.Equal("3.56.2", registrationData.GetProperty("app_version").GetString());
        Assert.Equal(_serial, registrationData.GetProperty("device_serial").GetString());
        Assert.Equal("A2CZJZGLK2JJVM", registrationData.GetProperty("device_type").GetString());
        Assert.Equal("%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Skylight Photo Sync", registrationData.GetProperty("device_name").GetString());
        Assert.Equal("15.0.0", registrationData.GetProperty("os_version").GetString());
        Assert.Equal("35602678", registrationData.GetProperty("software_version").GetString());
        Assert.Equal("iPhone", registrationData.GetProperty("device_model").GetString());
        Assert.Equal("Audible", registrationData.GetProperty("app_name").GetString());
        var authData = root.GetProperty("auth_data");
        Assert.Equal(Convert.ToHexStringLower(Encoding.ASCII.GetBytes(_serial + "#A2CZJZGLK2JJVM")), authData.GetProperty("client_id").GetString());
        Assert.Equal(_code, authData.GetProperty("authorization_code").GetString());
        Assert.Equal(_verifier, authData.GetProperty("code_verifier").GetString());
        Assert.Equal("SHA-256", authData.GetProperty("code_algorithm").GetString());
        Assert.Equal("DeviceLegacy", authData.GetProperty("client_domain").GetString());
        Assert.Equal(["device_info", "customer_info"], root.GetProperty("requested_extensions").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.Equal(_refresh, registration.RefreshToken);
        Assert.Equal(_access, registration.AccessToken);
        Assert.Equal(TimeSpan.FromHours(1), registration.AccessTokenLifetime);
        Assert.Equal("Placeholder Person", registration.CustomerName);
        Assert.Equal(
            ["x-main", "at-main", "sess-at-main", "ubid-main", "session-id", "session-token"],
            registration.Cookies.Select(c => c.Name).ToArray());
        // Amazon wraps some values in quotes that are not part of the cookie.
        Assert.Equal("placeholder-x-main-from-register", registration.Cookies[0].Value);
        Assert.Equal("133-0000000-3333333", registration.Cookies.Single(c => c.Name == "session-id").Value);
    }

    [Fact]
    public async Task Register_failure_reports_code_and_message_but_no_secrets()
    {
        _handler.On("/auth/register", _ => ScriptedHandler.AuthJson("placeholder_auth_register_error.json", HttpStatusCode.BadRequest));
        using var auth = Auth();

        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() => auth.RegisterAsync(_code, _verifier, _serial, CancellationToken.None));

        Assert.Contains("400", ex.Message);
        Assert.Contains("InvalidValue", ex.Message);
        Assert.Contains("One or more provided values are invalid", ex.Message);
        Assert.DoesNotContain(_code, ex.Message);
        Assert.DoesNotContain(_verifier, ex.Message);
    }

    [Fact]
    public async Task Register_response_without_tokens_is_an_error()
    {
        _handler.On("/auth/register", _ => ScriptedHandler.JsonBody("""{"response":{"success":{"tokens":{}}}}"""));
        using var auth = Auth();

        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() => auth.RegisterAsync(_code, _verifier, _serial, CancellationToken.None));

        Assert.Contains("refresh_token", ex.Message);
    }

    [Fact]
    public async Task Exchange_posts_form_and_loads_cookies()
    {
        using var auth = Auth();

        var cookies = await auth.ExchangeCookiesAsync(_refresh, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://www.amazon.com/ap/exchangetoken/cookies", request.RequestUri!.ToString());
        Assert.Equal("api.amazon.com", Assert.Single(request.Headers.GetValues("x-amzn-identity-auth-domain")));
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        var form = ParseForm(_handler.BodyOf(request)!);
        Assert.Equal(_refresh, form["source_token"]);
        Assert.Equal("refresh_token", form["source_token_type"]);
        Assert.Equal("auth_cookies", form["requested_token_type"]);
        Assert.Equal(".amazon.com", form["domain"]);
        Assert.Equal("Audible", form["app_name"]);
        Assert.Equal("3.56.2", form["app_version"]);
        Assert.Null(request.Headers.TryGetValues("Cookie", out _) ? "has cookies" : null);

        Assert.Equal(
            ["session-id", "ubid-main", "at-main", "sess-at-main", "x-main", "session-token"],
            cookies.Select(c => c.Name).ToArray());
        Assert.Equal("133-0000000-4444444", cookies[0].Value);
        Assert.Equal("placeholder-x-main-from-exchange", cookies.Single(c => c.Name == "x-main").Value);
        Assert.Equal(new DateTimeOffset(2026, 12, 6, 12, 0, 0, TimeSpan.Zero), cookies[0].Expires);
    }

    [Fact]
    public async Task Exchange_uses_the_tld_everywhere()
    {
        _handler.On("/ap/exchangetoken/cookies", _ => ScriptedHandler.JsonBody(
            """{"response":{"tokens":{"cookies":{".amazon.de":[{"Name":"session-id","Value":"257-0000000-0000000","Path":"/","Expires":"06 Dec 2026 12:00:00 GMT"}]}}}}"""));
        using var auth = Auth("de");

        var cookies = await auth.ExchangeCookiesAsync(_refresh, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("www.amazon.de", request.RequestUri!.Host);
        Assert.Equal("api.amazon.de", Assert.Single(request.Headers.GetValues("x-amzn-identity-auth-domain")));
        Assert.Equal(".amazon.de", ParseForm(_handler.BodyOf(request)!)["domain"]);
        Assert.Equal("257-0000000-0000000", Assert.Single(cookies).Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Exchange_401_throws_AuthExpired(HttpStatusCode status)
    {
        _handler.On("/ap/exchangetoken/cookies", _ => ScriptedHandler.Status(status));
        using var auth = Auth();

        var ex = await Assert.ThrowsAsync<AuthExpiredException>(() => auth.ExchangeCookiesAsync(_refresh, CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.Contains("skylight-sync login", ex.Message);
        Assert.DoesNotContain(_refresh, ex.Message);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Exchange_without_cookies_throws_AuthExpired()
    {
        _handler.On("/ap/exchangetoken/cookies", _ => ScriptedHandler.JsonBody("""{"response":{"tokens":{"cookies":{}}},"request_id":"x"}"""));
        using var auth = Auth();

        var ex = await Assert.ThrowsAsync<AuthExpiredException>(() => auth.ExchangeCookiesAsync(_refresh, CancellationToken.None));

        Assert.DoesNotContain(_refresh, ex.Message);
    }

    [Fact]
    public async Task Exchange_other_errors_report_status_only()
    {
        _handler.On("/ap/exchangetoken/cookies", _ => ScriptedHandler.Status(HttpStatusCode.InternalServerError));
        using var auth = Auth();

        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() => auth.ExchangeCookiesAsync(_refresh, CancellationToken.None));

        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain(_refresh, ex.Message);
    }

    [Fact]
    public async Task RefreshAccessToken_posts_form_and_parses_token()
    {
        using var auth = Auth();

        var accessToken = await auth.RefreshAccessTokenAsync(_refresh, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.amazon.com/auth/token", request.RequestUri!.ToString());
        var form = ParseForm(_handler.BodyOf(request)!);
        Assert.Equal(_refresh, form["source_token"]);
        Assert.Equal("refresh_token", form["source_token_type"]);
        Assert.Equal("access_token", form["requested_token_type"]);
        Assert.Equal("Audible", form["app_name"]);
        Assert.Equal("Atna|PLACEHOLDER_ACCESS_TOKEN_FROM_REFRESH_NOT_REAL", accessToken);
    }

    [Fact]
    public async Task Deregister_posts_bearer_and_keeps_other_devices()
    {
        using var auth = Auth();

        await auth.DeregisterAsync(_access, CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.amazon.com/auth/deregister", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(_access, request.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(_handler.BodyOf(request)!);
        Assert.False(body.RootElement.GetProperty("deregister_all_existing_accounts").GetBoolean());
    }

    [Fact]
    public async Task Deregister_failure_reports_status_but_no_token()
    {
        _handler.On("/auth/deregister", _ => ScriptedHandler.Status(HttpStatusCode.Unauthorized));
        using var auth = Auth();

        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() => auth.DeregisterAsync(_access, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(_access, ex.Message);
    }

    [Fact]
    public void Device_auth_fixtures_are_labelled_placeholders()
    {
        var files = Directory.GetFiles(Path.GetDirectoryName(Fixtures.AmazonAuth("placeholder_auth_register.json"))!, "*.json");

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.StartsWith("placeholder_", Path.GetFileName(file));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(doc.RootElement.TryGetProperty("_placeholder", out _), $"{Path.GetFileName(file)} lacks _placeholder");
            Assert.False(doc.RootElement.TryGetProperty("_recorded", out _), $"{Path.GetFileName(file)} claims to be recorded");
        }
    }

    private static Dictionary<string, string> ParseQuery(Uri url) => ParseForm(url.Query.TrimStart('?'));

    private static Dictionary<string, string> ParseForm(string encoded) =>
        encoded.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "", StringComparer.Ordinal);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
