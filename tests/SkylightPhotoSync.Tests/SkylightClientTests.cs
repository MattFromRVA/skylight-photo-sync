using System.Net;
using System.Text.Json;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// <see cref="SkylightClient"/> against <see cref="ScriptedHandler"/>. Every Skylight response here is a
/// HAND-WRITTEN PLACEHOLDER shaped after the community docs (dknowles2/pyskylight, the dknowles2 gist);
/// none was recorded from a live account yet. Replace with redacted recordings once one is made.
/// </summary>
public sealed class SkylightClientTests : IDisposable
{
    private const string _email = "me@example.com";
    private const string _password = "placeholder-password-not-real";
    private const string _csrf = "placeholder-csrf-token";
    private const string _code = "placeholder-auth-code";
    private const string _access = "placeholder-access-token";
    private const string _refresh = "placeholder-refresh-token";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly string _tokenFile;
    private readonly DateTimeOffset _now = _t0;
    private bool _signedIn;

    public SkylightClientTests()
    {
        _tokenFile = Path.Combine(_dir.Path, "skylight-token.json");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Signs_in_with_pkce_and_lists_frames()
    {
        var handler = SignInRoutes().On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        var frames = await client.ListFramesAsync(CancellationToken.None);

        Assert.Equal([new SkylightFrame("5455113", "Kitchen"), new SkylightFrame("77", "Calendar")], frames);
        var post = Assert.Single(handler.RequestsTo("/auth/session"), r => r.Method == HttpMethod.Post);
        var form = handler.BodyOf(post)!;
        Assert.Contains($"authenticity_token={_csrf}", form);
        Assert.Contains("email=me%40example.com", form);
        Assert.Contains("skylightcloud_session=s1", handler.CookieHeaders[handler.Requests.IndexOf(post)]);
        var tokenBody = handler.BodyOf(Assert.Single(handler.RequestsTo("/oauth/token")))!;
        Assert.Contains("grant_type=authorization_code", tokenBody);
        Assert.Contains($"code={_code}", tokenBody);
        Assert.Contains("code_verifier=", tokenBody);
        var authorize = handler.RequestsTo("/oauth/authorize").First();
        Assert.Contains("code_challenge_method=S256", authorize.RequestUri!.Query);
        Assert.Contains("client_id=skylight-mobile", authorize.RequestUri.Query);
        var api = Assert.Single(handler.RequestsTo("/api/frames"));
        Assert.Equal($"Bearer {_access}", api.Headers.Authorization!.ToString());
        Assert.Contains(SkylightClient.UserAgent, api.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Caches_tokens_in_a_private_file_and_reuses_them()
    {
        using (var first = Client(SignInRoutes().On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson))))
        {
            await first.ListFramesAsync(CancellationToken.None);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_tokenFile));
        }

        Assert.DoesNotContain(_password, File.ReadAllText(_tokenFile));
        var second = new ScriptedHandler().On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(second);
        await client.ListFramesAsync(CancellationToken.None);

        Assert.Single(second.Requests);
    }

    [Fact]
    public async Task Cached_tokens_for_a_different_account_are_discarded()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1), account: "previous@example.com");
        var handler = SignInRoutes().On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        await client.ListFramesAsync(CancellationToken.None);

        Assert.Contains("grant_type=authorization_code", handler.BodyOf(Assert.Single(handler.RequestsTo("/oauth/token"))));
        Assert.All(handler.RequestsTo("/api/frames"), r => Assert.Equal($"Bearer {_access}", r.Headers.Authorization!.ToString()));
        using var saved = JsonDocument.Parse(File.ReadAllText(_tokenFile));
        Assert.Equal(_email, saved.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Cached_tokens_without_an_account_are_discarded()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1), account: null);
        var handler = SignInRoutes().On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        await client.ListFramesAsync(CancellationToken.None);

        Assert.Contains("grant_type=authorization_code", handler.BodyOf(Assert.Single(handler.RequestsTo("/oauth/token"))));
    }

    [Fact]
    public async Task Cached_tokens_match_the_account_ignoring_case_and_spaces()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1), account: " ME@Example.com ");
        var handler = new ScriptedHandler().On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        await client.ListFramesAsync(CancellationToken.None);

        Assert.Equal("Bearer cached-access", Assert.Single(handler.Requests).Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed()
    {
        WriteTokenFile(expiresAt: _t0.AddMinutes(-1));
        var handler = new ScriptedHandler()
            .On("/oauth/token", _ => ScriptedHandler.JsonBody(TokenJson("refreshed-access")))
            .On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        await client.ListFramesAsync(CancellationToken.None);

        Assert.Contains("grant_type=refresh_token", handler.BodyOf(Assert.Single(handler.RequestsTo("/oauth/token"))));
        Assert.Equal("Bearer refreshed-access", Assert.Single(handler.RequestsTo("/api/frames")).Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Unauthorized_renews_once_then_retries()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var calls = 0;
        var handler = new ScriptedHandler()
            .On("/oauth/token", _ => ScriptedHandler.JsonBody(TokenJson("renewed-access")))
            .On("/api/frames", _ => ++calls == 1 ? ScriptedHandler.Status(HttpStatusCode.Unauthorized) : ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        var frames = await client.ListFramesAsync(CancellationToken.None);

        Assert.Equal(2, frames.Count);
        Assert.Equal("Bearer renewed-access", handler.RequestsTo("/api/frames").Last().Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Refused_refresh_falls_back_to_signing_in()
    {
        WriteTokenFile(expiresAt: _t0.AddMinutes(-1));
        var handler = SignInRoutes(refreshWorks: false).On("/api/frames", _ => ScriptedHandler.JsonBody(_framesJson));
        using var client = Client(handler);

        await client.ListFramesAsync(CancellationToken.None);

        Assert.Single(handler.RequestsTo("/auth/session"), r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Wrong_password_says_to_check_secrets_without_echoing_them()
    {
        var handler = SignInRoutes(acceptPassword: false);
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightSignInException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("[skylight] email and password", ex.Message);
        Assert.DoesNotContain(_password, ex.Message);
        Assert.False(File.Exists(_tokenFile));
    }

    [Fact]
    public async Task Captcha_after_the_password_is_a_sign_in_failure_that_names_it()
    {
        // HAND-WRITTEN PLACEHOLDER: what a CAPTCHA challenge page really looks like has not been recorded.
        var handler = SignInRoutes().On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/auth/session", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><div class=\"g-recaptcha\"></div></html>") });
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightSignInException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("CAPTCHA", ex.Message);
        Assert.DoesNotContain(_password, ex.Message);
    }

    [Fact]
    public async Task Missing_sign_in_form_is_a_sign_in_failure()
    {
        var handler = SignInRoutes().On("/auth/session/new", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>redesigned</html>") });
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightSignInException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("sign-in form", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Rate_limited_sign_in_is_a_sign_in_failure()
    {
        var handler = SignInRoutes().On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/auth/session", _ =>
            ScriptedHandler.Status(HttpStatusCode.TooManyRequests));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightSignInException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task Outage_during_sign_in_is_not_a_sign_in_failure()
    {
        var handler = SignInRoutes().On("/oauth/authorize", _ => ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable));
        using var client = Client(handler);

        // ThrowsAsync wants the exact type: an outage must not be the kind of failure that pauses pruning.
        var ex = await Assert.ThrowsAsync<SkylightApiException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task Outage_during_refresh_does_not_fall_back_to_the_password()
    {
        WriteTokenFile(expiresAt: _t0.AddMinutes(-1));
        var handler = SignInRoutes().On("/oauth/token", _ => ScriptedHandler.Status(HttpStatusCode.BadGateway));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightApiException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("502", ex.Message);
        Assert.Empty(handler.RequestsTo("/auth/session"));
        Assert.True(File.Exists(_tokenFile));
    }

    [Fact]
    public async Task Lists_every_page_of_messages()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = new ScriptedHandler().On("/api/frames/5455113/messages", request =>
            ScriptedHandler.JsonBody(request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal) ? _messagesPage2Json : _messagesPage1Json));
        using var client = Client(handler);

        var messages = await client.ListMessagesAsync("5455113", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            [
                new SkylightMessage("901", new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero), "sync@example.com", false),
                new SkylightMessage("900", new DateTimeOffset(2026, 11, 11, 13, 0, 0, TimeSpan.Zero), "grandma@example.com", false),
                new SkylightMessage("899", new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero), null, true),
            ],
            messages);
    }

    [Fact]
    public async Task Delete_tolerates_an_already_deleted_photo_but_not_errors()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = new ScriptedHandler()
            .On("/messages/gone", _ => ScriptedHandler.Status(HttpStatusCode.NotFound))
            .On("/messages/broken", _ => ScriptedHandler.Status(HttpStatusCode.InternalServerError))
            .On("/messages/ok", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = Client(handler);

        await client.DeleteMessageAsync("1", "ok", CancellationToken.None);
        await client.DeleteMessageAsync("1", "gone", CancellationToken.None);
        var ex = await Assert.ThrowsAsync<SkylightApiException>(() => client.DeleteMessageAsync("1", "broken", CancellationToken.None));

        Assert.Contains("500", ex.Message);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Delete, r.Method));
    }

    [Theory]
    [InlineData("<meta name=\"csrf-token\" content=\"abc+/=\" />", "abc+/=")]
    [InlineData("<input type=\"hidden\" name=\"authenticity_token\" value=\"xyz&amp;1\">", "xyz&1")]
    [InlineData("<html></html>", null)]
    public void FindCsrfToken(string html, string? expected) => Assert.Equal(expected, SkylightClient.FindCsrfToken(html));

    private const string _framesJson = """
        {"data":[
          {"id":"5455113","type":"frame","attributes":{"name":"Kitchen","plus":false}},
          {"id":77,"type":"frame","attributes":{"name":"Calendar"}}
        ]}
        """;

    private const string _messagesPage1Json = """
        {"data":[
          {"id":"901","type":"message","attributes":{"created_at":"2026-11-11T14:00:00.000Z","from_email":"sync@example.com","asset_type":"photo","destroyed_at":null}},
          {"id":"900","type":"message","attributes":{"created_at":"2026-11-11T13:00:00Z","from_email":"grandma@example.com"}}
        ],"meta":{"current_page":1,"num_pages":2}}
        """;

    private const string _messagesPage2Json = """
        {"data":[
          {"id":"899","type":"message","attributes":{"created_at":"2026-11-10T09:30:00Z","from_email":null,"destroyed_at":"2026-11-10T10:00:00Z"}},
          {"id":"898","type":"message","attributes":{"from_email":"no-date@example.com"}}
        ],"meta":{"current_page":2,"num_pages":2}}
        """;

    private static string TokenJson(string access) =>
        JsonSerializer.Serialize(new { access_token = access, token_type = "Bearer", expires_in = 7200, refresh_token = _refresh, scope = "everything" });

    private SkylightClient Client(ScriptedHandler handler) => new(_email, _password, _tokenFile, handler, () => _now);

    private void WriteTokenFile(DateTimeOffset expiresAt, string? account = _email) =>
        File.WriteAllText(_tokenFile, JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["email"] = account,
            ["access_token"] = "cached-access",
            ["refresh_token"] = _refresh,
            ["expires_at"] = expiresAt.UtcDateTime.ToString("O"),
        }.Where(pair => pair.Value is not null).ToDictionary()));

    /// <summary>
    /// The sign-in dance: authorize → sign-in page (sets the session cookie) → POST credentials →
    /// authorize again → redirect to the welcome URL with the code and the caller's state.
    /// </summary>
    private ScriptedHandler SignInRoutes(bool acceptPassword = true, bool refreshWorks = true) =>
        new ScriptedHandler()
            .On("/oauth/authorize", request =>
            {
                if (!_signedIn)
                {
                    return Redirect("/auth/session/new");
                }

                var state = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["state"];
                return Redirect($"{SkylightClient.RedirectUri}?code={_code}&state={state}");
            })
            .On("/auth/session/new", _ =>
            {
                var page = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"<html><head><meta name=\"csrf-token\" content=\"{_csrf}\" /></head></html>"),
                };
                page.Headers.Add("Set-Cookie", "skylightcloud_session=s1; path=/; secure; HttpOnly");
                return page;
            })
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/auth/session", _ =>
            {
                if (!acceptPassword)
                {
                    return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("<html>Invalid</html>") };
                }

                _signedIn = true;
                return Redirect("/auth/session/success");
            })
            .On("/auth/session/success", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ok</html>") })
            .On("/oauth/token", request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("grant_type=refresh_token", StringComparison.Ordinal) && !refreshWorks
                    ? ScriptedHandler.Status(HttpStatusCode.BadRequest)
                    : ScriptedHandler.JsonBody(TokenJson(_access));
            });

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}
