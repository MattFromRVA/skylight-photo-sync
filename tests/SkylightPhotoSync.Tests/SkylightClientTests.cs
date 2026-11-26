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
    public async Task Rate_limited_refresh_is_a_sign_in_failure_that_never_sends_the_password()
    {
        WriteTokenFile(expiresAt: _t0.AddMinutes(-1));
        var handler = SignInRoutes().On("/oauth/token", _ => ScriptedHandler.Status(HttpStatusCode.TooManyRequests));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightSignInException>(() => client.ListFramesAsync(CancellationToken.None));

        Assert.Contains("429", ex.Message);
        Assert.Empty(handler.RequestsTo("/auth/session"));
        Assert.Single(handler.Requests);
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

    // ---- Upload (issue #16). The flow is jakewilkins/skylight-frame's (research notes, "Skylight upload
    // API"): POST /api/message_upload_urls, PUT the bytes to the presigned URL, then find the new photo's
    // id by listing the frame. Every response below is a HAND-WRITTEN PLACEHOLDER shaped after that
    // client's code; none was recorded from a live account (that is #44).

    [Fact]
    public async Task Upload_requests_a_url_puts_the_bytes_and_finds_the_new_id_by_listing()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var file = Fixtures.Image("tiny.jpg");
        var bytes = File.ReadAllBytes(file);
        var handler = UploadRoutes(Listings("5455113", ["901", "900"], ["902", "901", "900"]));
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["5455113"], file, "image/jpeg", CancellationToken.None);

        Assert.Equal([new SkylightUpload("5455113", "902")], uploads);
        var post = Assert.Single(handler.RequestsTo("/api/message_upload_urls"));
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("Bearer cached-access", post.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(handler.BodyOf(post)!);
        Assert.Equal(["5455113"], body.RootElement.GetProperty("frame_ids").EnumerateArray().Select(e => e.GetString()));
        var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("jpg", message.GetProperty("ext").GetString());
        Assert.Equal("", message.GetProperty("caption").GetString());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), message.GetProperty("local_file_id").GetString());

        var put = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal(_presignedUrl, put.RequestUri!.ToString());
        Assert.Null(put.Headers.Authorization);   // the Skylight token never goes to the storage host
        Assert.Equal("application/octet-stream", put.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(bytes.Length, put.Content.Headers.ContentLength);

        // Listed once before the upload (to know what was already there) and once after.
        Assert.Equal(2, handler.RequestsTo("/api/frames/5455113/messages").Count());
        Assert.True(handler.Requests.IndexOf(post) > handler.Requests.IndexOf(handler.RequestsTo("/messages").First()));
    }

    [Fact]
    public async Task Upload_to_two_frames_is_one_upload_with_an_id_found_on_each()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("100", ["1"], ["5", "1"]), Listings("200", [], ["6"]));
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["100", "200"], Fixtures.Image("tiny.png"), "image/png", CancellationToken.None);

        Assert.Equal([new SkylightUpload("100", "5"), new SkylightUpload("200", "6")], uploads);
        Assert.Single(handler.RequestsTo("/api/message_upload_urls"));
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        using var body = JsonDocument.Parse(handler.BodyOf(handler.RequestsTo("/api/message_upload_urls").Single())!);
        Assert.Equal(["100", "200"], body.RootElement.GetProperty("frame_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("png", body.RootElement.GetProperty("messages")[0].GetProperty("ext").GetString());
    }

    [Fact]
    public async Task Upload_keeps_listing_until_the_new_photo_shows_up()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["10"], ["10"], ["11", "10"]));
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None);

        Assert.Equal([new SkylightUpload("1", "11")], uploads);
        Assert.Equal(4, handler.RequestsTo("/api/frames/1/messages").Count());
        Assert.Equal(3, _delays.Count);
        Assert.All(_delays, d => Assert.Equal(SkylightClient.UploadListInterval, d));
    }

    [Fact]
    public async Task Upload_returns_no_id_for_a_frame_the_photo_never_shows_up_on()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["10"]));
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None);

        Assert.Empty(uploads);
        Assert.Equal(SkylightClient.UploadListAttempts, _delays.Count);
        Assert.Equal(1 + SkylightClient.UploadListAttempts, handler.RequestsTo("/api/frames/1/messages").Count());
    }

    [Fact]
    public async Task Upload_never_guesses_when_two_new_photos_appear()
    {
        // Someone else's photo arrived at the same time; recording the wrong id could later delete it.
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["12", "11", "10"]));
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None);

        Assert.Empty(uploads);
        Assert.Equal(2, handler.RequestsTo("/api/frames/1/messages").Count());
    }

    [Fact]
    public async Task Upload_ignores_a_new_photo_that_is_already_deleted()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["12", "11-destroyed", "10"]));
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None);

        Assert.Equal([new SkylightUpload("1", "12")], uploads);
    }

    [Fact]
    public async Task Refused_upload_url_request_throws_and_sends_no_bytes()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["11", "10"]))
            .On("/api/message_upload_urls", _ => ScriptedHandler.Status(HttpStatusCode.UnprocessableEntity));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightApiException>(
            () => client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None));

        Assert.Contains("422", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Upload_url_entry_with_an_error_throws_and_sends_no_bytes()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["11", "10"]))
            .On("/api/message_upload_urls", _ => ScriptedHandler.JsonBody("""{"data":{"upload_urls":[{"error":"unsupported file type"}]}}"""));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightApiException>(
            () => client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None));

        Assert.Contains("unsupported file type", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"upload_urls":[]}}""")]
    [InlineData("""{"data":{"upload_urls":[{"url":"http://insecure.example.com/x"}]}}""")]
    [InlineData("not json")]
    public async Task Unreadable_upload_url_answer_throws_and_sends_no_bytes(string answer)
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["11", "10"]))
            .On("/api/message_upload_urls", _ => ScriptedHandler.JsonBody(answer));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightApiException>(
            () => client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None));

        Assert.Contains("may have changed", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Refused_bytes_throw_without_renewing_the_token_or_naming_the_url()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var handler = UploadRoutes(Listings("1", ["10"], ["11", "10"]))
            .On(r => r.Method == HttpMethod.Put, _ => ScriptedHandler.Status(HttpStatusCode.Forbidden));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<SkylightApiException>(
            () => client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None));

        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("placeholder-signature", ex.Message);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Empty(handler.RequestsTo("/oauth/token"));
        Assert.Single(handler.RequestsTo("/api/frames/1/messages"));   // not listed again: nothing was uploaded
    }

    [Fact]
    public async Task Unauthorized_upload_url_request_renews_the_token_once_and_resends_the_body()
    {
        WriteTokenFile(expiresAt: _t0.AddHours(1));
        var calls = 0;
        var handler = UploadRoutes(Listings("1", ["10"], ["11", "10"]))
            .On("/oauth/token", _ => ScriptedHandler.JsonBody(TokenJson("renewed-access")))
            .On("/api/message_upload_urls", _ => ++calls == 1 ? ScriptedHandler.Status(HttpStatusCode.Unauthorized) : UploadUrlsAnswer());
        using var client = Client(handler);

        var uploads = await client.UploadPhotoAsync(["1"], Fixtures.Image("tiny.jpg"), "image/jpeg", CancellationToken.None);

        Assert.Equal([new SkylightUpload("1", "11")], uploads);
        var posts = handler.RequestsTo("/api/message_upload_urls").ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("Bearer renewed-access", posts[1].Headers.Authorization!.ToString());
        Assert.Equal(handler.BodyOf(posts[0]), handler.BodyOf(posts[1]));
        Assert.Contains("grant_type=refresh_token", handler.BodyOf(Assert.Single(handler.RequestsTo("/oauth/token"))));
    }

    [Theory]
    [InlineData("image/jpeg", "a.jpeg", "jpg")]
    [InlineData("image/png", "a.png", "png")]
    [InlineData("image/heic", "a.heic", "heic")]
    [InlineData("video/mp4", "a.mp4", "mp4")]
    [InlineData("video/quicktime", "a.mov", "mov")]
    [InlineData("application/octet-stream", "a.JPEG", "jpg")]
    [InlineData("application/octet-stream", "a.gif", "gif")]
    public void Upload_extension_follows_the_mime_type_then_the_file_name(string mime, string path, string expected) =>
        Assert.Equal(expected, SkylightClient.UploadExtension(path, mime));

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

    private const string _presignedUrl =
        "https://placeholder-bucket.s3.amazonaws.com/uploads/placeholder-key.jpg?X-Amz-Signature=placeholder-signature";

    private readonly List<TimeSpan> _delays = [];

    private SkylightClient Client(ScriptedHandler handler) =>
        new(_email, _password, _tokenFile, handler, () => _now, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    /// <summary>HAND-WRITTEN PLACEHOLDER of the <c>message_upload_urls</c> answer jakewilkins/skylight-frame reads.</summary>
    private static HttpResponseMessage UploadUrlsAnswer() =>
        ScriptedHandler.JsonBody(JsonSerializer.Serialize(new { data = new { upload_urls = new[] { new { url = _presignedUrl } } } }));

    /// <summary>
    /// HAND-WRITTEN PLACEHOLDER upload routes. Each frame lists its message ids per call: the first list
    /// is the frame before the upload, the next ones what each later listing returns (the last repeats).
    /// An id ending in <c>-destroyed</c> is listed as deleted.
    /// </summary>
    private static (string FrameId, string[][] Listings) Listings(string frameId, params string[][] listings) => (frameId, listings);

    private static ScriptedHandler UploadRoutes(params (string FrameId, string[][] Listings)[] frames)
    {
        var handler = new ScriptedHandler()
            .On("/api/message_upload_urls", _ => UploadUrlsAnswer())
            .On(r => r.Method == HttpMethod.Put && r.RequestUri!.Host == "placeholder-bucket.s3.amazonaws.com",
                _ => new HttpResponseMessage(HttpStatusCode.OK));
        foreach (var (frameId, listings) in frames)
        {
            var calls = 0;
            handler.On($"/api/frames/{frameId}/messages", _ =>
            {
                var ids = listings[Math.Min(calls++, listings.Length - 1)];
                var data = ids.Select(id => new
                {
                    id = id.Replace("-destroyed", "", StringComparison.Ordinal),
                    attributes = new
                    {
                        created_at = "2026-11-11T14:00:00Z",
                        from_email = (string?)null,
                        destroyed_at = id.EndsWith("-destroyed", StringComparison.Ordinal) ? "2026-11-11T14:05:00Z" : null,
                    },
                });
                return ScriptedHandler.JsonBody(JsonSerializer.Serialize(new { data, meta = new { current_page = 1, num_pages = 1 } }));
            });
        }

        return handler;
    }

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
