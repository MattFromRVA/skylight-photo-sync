using System.Net;
using System.Text.Json;
using SkylightPhotoSync.Commands;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Sources.Amazon;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// Drives <see cref="AmazonPhotosSource"/> through a <see cref="ScriptedHandler"/> serving the
/// recorded, redacted fixtures (<c>Fixtures/Amazon/*.json</c>, recorded from a live account on
/// 2026-11-06). No test touches the network.
/// </summary>
public sealed class AmazonPhotosSourceTests
{
    private const string _sessionId = "133-0000000-0000000";
    private const string _ubid = "130-0000000-1111111";
    private const string _at = "Atza|TESTVALUEONLYxxxx";
    private const string _album = ScriptedHandler.AlbumName;
    private static readonly Secrets _secrets = new(_sessionId, _ubid, _at, null);
    private static readonly DateTimeOffset _since = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The nine kept nodes of the recorded album, in capture-date order.</summary>
    private static readonly string[] _albumIds =
        ["node_0096", "node_0098", "node_0099", "node_0100", "node_0102", "node_0103", "node_0104", "node_0105", "node_0106"];

    private readonly ScriptedHandler _handler = new ScriptedHandler().WithAmazonDefaults().WithDeviceAuthDefaults();
    private readonly List<TimeSpan> _delays = [];

    private AmazonPhotosSource Source() =>
        new(_secrets, _handler, "com", (pause, _) =>
        {
            _delays.Add(pause);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Album_mode_lists_album_children_oldest_first()
    {
        var items = await Source().ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        Assert.Equal(_albumIds, items.Select(i => i.Id).ToArray());
        var first = items[0];
        Assert.Equal("file_0001.jpeg", first.Name);
        Assert.Equal(ItemKind.Photo, first.Kind);
        Assert.Equal(new DateTimeOffset(2020, 7, 9, 12, 42, 11, 80, TimeSpan.Zero), first.CapturedAt);
        Assert.Equal(1684228, first.SizeBytes);
        Assert.Equal("2867b70f508e2ae935ab7decbb0d66a8", first.Md5);
        Assert.Equal("image/jpeg", first.Mime);
        Assert.Equal("image/heic", items[3].Mime);
        Assert.Equal("file_0004.heic", items[3].Name);
        Assert.All(items, item => Assert.Equal(ItemKind.Photo, item.Kind));
    }

    [Fact]
    public async Task Album_mode_applies_the_window()
    {
        var items = await Source().ListCandidatesAsync(
            new Window(new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero)), SourceMode.Album, _album, CancellationToken.None);

        Assert.Equal(["node_0104", "node_0105", "node_0106"], items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Album_mode_names_a_missing_album_and_quotes_every_name()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.Album, "Nope", CancellationToken.None));

        Assert.Contains("'Nope'", ex.Message);
        Assert.Contains($"\"{_album}\"", ex.Message);

        // Names are quoted so the trailing space Amazon keeps on an album name is visible.
        Assert.Contains($"\"{ScriptedHandler.TrailingSpaceAlbumName}\"", ex.Message);
    }

    [Fact]
    public async Task Album_mode_matches_the_album_name_case_sensitively()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.Album, _album.ToUpperInvariant(), CancellationToken.None));

        Assert.Contains($"'{_album.ToUpperInvariant()}'", ex.Message);
    }

    [Fact]
    public async Task Album_mode_accepts_a_name_that_differs_only_by_surrounding_spaces()
    {
        _handler.On($"/nodes/{ScriptedHandler.TrailingSpaceAlbumId}/children", _ => ScriptedHandler.JsonBody("""{"count":0,"data":[]}"""));

        var items = await Source().ListCandidatesAsync(
            new Window(null), SourceMode.Album, ScriptedHandler.TrailingSpaceAlbumName.Trim(), CancellationToken.None);

        Assert.Empty(items);
        Assert.Single(_handler.RequestsTo($"/nodes/{ScriptedHandler.TrailingSpaceAlbumId}/children"));
    }

    [Fact]
    public async Task Album_mode_prefers_the_exact_name_over_a_trimmed_match()
    {
        _handler.On(
            request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
                && ScriptedHandler.Query(request).Contains("kind:VISUAL_COLLECTION", StringComparison.Ordinal),
            _ => ScriptedHandler.JsonBody(
                $$"""{"count":2,"data":[{"id":"TRIMMED","name":"{{_album}} ","kind":"VISUAL_COLLECTION"},{"id":"{{ScriptedHandler.AlbumId}}","name":"{{_album}}","kind":"VISUAL_COLLECTION"}]}"""));

        await Source().ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        Assert.Empty(_handler.RequestsTo("/nodes/TRIMMED/children"));
        Assert.NotEmpty(_handler.RequestsTo($"/nodes/{ScriptedHandler.AlbumId}/children"));
    }

    [Fact]
    public async Task All_mode_uses_search_with_date_range()
    {
        var items = await Source().ListCandidatesAsync(new Window(_since), SourceMode.All, null, CancellationToken.None);

        var search = Assert.Single(_handler.RequestsTo("/drive/v1/search"));
        var query = ScriptedHandler.Query(search);
        Assert.Contains("filters=type:(PHOTOS OR VIDEOS) AND createdDate:[2026-10-30T00:00:00.000Z TO ", query);
        Assert.Contains("sort=['contentProperties.contentDate ASC']", query);
        Assert.Contains("searchContext=customer", query);
        Assert.EndsWith("resourceVersion=V2&ContentType=JSON", query);
        Assert.Empty(_handler.RequestsTo("/children"));

        // The recorded search page: three HEIC photos captured on 2026-10-30, uploaded that evening.
        Assert.Equal(["node_0107", "node_0108", "node_0109"], items.Select(i => i.Id).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 46, 51, 883, TimeSpan.Zero), items[0].CapturedAt);
        Assert.All(items, item => Assert.Equal("image/heic", item.Mime));
    }

    [Fact]
    public async Task All_mode_drops_items_captured_before_the_window_and_types_videos()
    {
        _handler.On("/drive/v1/search", _ => ScriptedHandler.JsonBody(
            """
            {"count":3,"data":[
              {"id":"OLD","name":"too-old.jpg","kind":"FILE","createdDate":"2026-11-02T12:00:00.000Z","contentProperties":{"contentType":"image/jpeg","contentDate":"2026-09-01T12:00:00.000Z"}},
              {"id":"RECENT","name":"recent.jpg","kind":"FILE","createdDate":"2026-11-02T12:00:00.000Z","contentProperties":{"contentType":"image/jpeg","contentDate":"2026-11-02T12:00:00.000Z"}},
              {"id":"CLIP","name":"clip.mp4","kind":"FILE","createdDate":"2026-11-03T12:00:00.000Z","contentProperties":{"contentType":"video/mp4","contentDate":"2026-11-03T12:00:00.000Z","video":{"duration":12.5}}}]}
            """));

        var items = await Source().ListCandidatesAsync(new Window(_since), SourceMode.All, null, CancellationToken.None);

        // Uploaded inside the window but captured before it: the client-side capture-date filter drops it.
        Assert.Equal(["RECENT", "CLIP"], items.Select(i => i.Id).ToArray());
        Assert.Equal(ItemKind.Video, items[1].Kind);
        Assert.Equal("video/mp4", items[1].Mime);
    }

    [Fact]
    public async Task All_mode_refuses_an_unbounded_window()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.All, null, CancellationToken.None));
    }

    [Fact]
    public void Node_without_contentDate_falls_back_to_createdDate_then_modifiedDate()
    {
        using var created = JsonDocument.Parse(
            """{"id":"A","kind":"FILE","name":"a.jpg","createdDate":"2026-11-04T08:00:00.000Z","modifiedDate":"2026-11-05T08:00:00.000Z","contentProperties":{"contentType":"image/jpeg"}}""");
        using var modified = JsonDocument.Parse("""{"id":"B","kind":"FILE","modifiedDate":"2026-11-05T08:00:00.000Z"}""");
        using var folder = JsonDocument.Parse("""{"id":"C","kind":"FOLDER","createdDate":"2026-11-05T08:00:00.000Z"}""");
        using var undated = JsonDocument.Parse("""{"id":"D","kind":"FILE","name":"d.jpg"}""");

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero), AmazonPhotosSource.MapNode(created.RootElement)!.CapturedAt);
        var b = AmazonPhotosSource.MapNode(modified.RootElement)!;
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero), b.CapturedAt);
        Assert.Equal("B", b.Name);
        Assert.Equal("application/octet-stream", b.Mime);
        Assert.Null(AmazonPhotosSource.MapNode(folder.RootElement));
        Assert.Null(AmazonPhotosSource.MapNode(undated.RootElement));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Http401_throws_AuthExpired(HttpStatusCode status)
    {
        _handler.On("/drive/v1/account/endpoint", _ => ScriptedHandler.Status(status));

        await Assert.ThrowsAsync<AuthExpiredException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None));

        Assert.Single(_handler.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Http429_backs_off_and_retries()
    {
        var attempts = 0;
        _handler.On("/drive/v1/account/usage", _ =>
            ++attempts <= 2 ? ScriptedHandler.Status(HttpStatusCode.TooManyRequests) : ScriptedHandler.Json("usage.json"));

        var (name, count) = await Source().WhoAmIAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], _delays);

        // The recorded root node has no name; the usage endpoint counts 50183 photos + 1832 videos.
        Assert.Equal(("", 52015L), (name, count));
    }

    [Fact]
    public async Task Http429_gives_up_after_three_retries()
    {
        _handler.On("/drive/v1/account/usage", _ => ScriptedHandler.Status(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Source().WhoAmIAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, _handler.RequestsTo("/drive/v1/account/usage").Count());
        Assert.Equal(3, _delays.Count(d => d == TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task WhoAmI_uses_the_root_node_name_when_there_is_one()
    {
        _handler.On(
            request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
                && ScriptedHandler.Query(request).Contains("isRoot:true", StringComparison.Ordinal),
            _ => ScriptedHandler.JsonBody("""{"count":1,"data":[{"id":"ROOT","kind":"FOLDER","isRoot":true,"name":"Holder ","ownerId":"A1OPAQUE"}]}"""));

        var (name, _) = await Source().WhoAmIAsync(CancellationToken.None);

        Assert.Equal("Holder", name);
    }

    [Fact]
    public async Task Pagination_follows_startToken_across_the_recorded_pages()
    {
        await Source().ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        var pages = _handler.RequestsTo("/children").Select(ScriptedHandler.Query).ToList();
        Assert.Equal(3, pages.Count);
        Assert.DoesNotContain("startToken", pages[0]);
        Assert.Contains($"startToken={ScriptedHandler.NextToken}&", pages[1]);
        Assert.Contains($"startToken={ScriptedHandler.NextToken2}&", pages[2]);
        Assert.All(pages, page => Assert.Contains("limit=200", page));
        Assert.All(pages, page => Assert.Contains("sort=['contentProperties.contentDate ASC']", page));
        Assert.All(pages, page => Assert.EndsWith("resourceVersion=V2&ContentType=JSON", page));
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5)], _delays);
    }

    [Fact]
    public async Task Search_without_nextToken_but_more_results_fails_loudly()
    {
        _handler.On("/drive/v1/search", _ => ScriptedHandler.JsonBody(
            """{"count":500,"data":[{"id":"ONLY","kind":"FILE","name":"a.jpg","contentProperties":{"contentType":"image/jpeg","contentDate":"2026-11-02T12:00:00.000Z"}}]}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Source().ListCandidatesAsync(new Window(_since), SourceMode.All, null, CancellationToken.None));

        Assert.Contains(AmazonEndpoints.KindSearch, ex.Message);
        Assert.Contains("500", ex.Message);
        Assert.Contains("nextToken", ex.Message);
    }

    [Fact]
    public async Task Accepts_underscore_cookie_names()
    {
        Assert.True(LoginCommand.TryParse(
            $"curl -b \"session-id={_sessionId}; ubid_main={_ubid}; at_main={_at}\" https://www.amazon.com/photos\n",
            out var cookies,
            out _));
        var secrets = new Secrets(cookies.SessionId, cookies.Ubid, cookies.At, null);
        var source = new AmazonPhotosSource(secrets, _handler);

        await source.WhoAmIAsync(CancellationToken.None);

        var account = Assert.Single(_handler.RequestsTo("/drive/v1/account/endpoint"));
        var cookieHeader = _handler.CookieHeaders[_handler.Requests.IndexOf(account)];
        Assert.NotNull(cookieHeader);
        Assert.Contains($"session-id={_sessionId}", cookieHeader);
        Assert.Contains($"ubid-main={_ubid}", cookieHeader);
        Assert.Contains($"at-main={_at}", cookieHeader);
        Assert.DoesNotContain("ubid_main", cookieHeader);
        Assert.Equal(_sessionId, Assert.Single(account.Headers.GetValues("x-amzn-sessionid")));
        Assert.Contains("Mozilla/5.0", account.Headers.UserAgent.ToString());

        // The resolved metadata host is a different domain and still gets the cookies.
        var usage = Assert.Single(_handler.RequestsTo("/drive/v1/account/usage"));
        Assert.Equal("cdws.us-east-1.amazonaws.com", usage.RequestUri!.Host);
        Assert.Contains($"at-main={_at}", _handler.CookieHeaders[_handler.Requests.IndexOf(usage)]);
    }

    [Fact]
    public async Task WhoAmI_resolves_endpoints_once()
    {
        var source = Source();

        await source.WhoAmIAsync(CancellationToken.None);
        await source.ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        Assert.Single(_handler.RequestsTo("/drive/v1/account/endpoint"));
    }

    [Fact]
    public async Task Download_streams_content_to_an_id_named_file()
    {
        using var dest = new TempDir();
        var source = Source();
        var items = await source.ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        var path = await source.DownloadAsync(items[0], dest.Path, CancellationToken.None);

        Assert.Equal(Path.Combine(dest.Path, "node_0096.jpeg"), path);
        Assert.Equal(File.ReadAllBytes(Fixtures.Image("tiny.jpg")), File.ReadAllBytes(path));
        var content = Assert.Single(_handler.RequestsTo("/content"));
        Assert.Equal("content-na.drive.amazonaws.com", content.RequestUri!.Host);
        Assert.Equal("/nodes/node_0096/content", content.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task Other_errors_report_status_and_url_but_never_cookies()
    {
        _handler.On("/drive/v1/search", _ => ScriptedHandler.Status(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Source().ListCandidatesAsync(new Window(_since), SourceMode.All, null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("500", ex.Message);
        Assert.Contains("/drive/v1/search", ex.Message);
        Assert.DoesNotContain(_at, ex.Message);
        Assert.DoesNotContain(_sessionId, ex.Message);
    }

    [Fact]
    public void Missing_cookies_are_rejected_up_front()
    {
        Assert.Throws<ArgumentException>(() => new AmazonPhotosSource(new Secrets(null, null, null, null), _handler));
    }

    [Fact]
    public async Task Response_observer_sees_every_response()
    {
        var seen = new List<AmazonResponse>();
        var source = Source();
        source.ResponseObserver = seen.Add;

        await source.ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        Assert.Equal(
            [
                AmazonEndpoints.KindAccountEndpoint,
                AmazonEndpoints.KindAlbums,
                AmazonEndpoints.KindAlbumChildren,
                AmazonEndpoints.KindAlbumChildren,
                AmazonEndpoints.KindAlbumChildren,
            ],
            seen.Select(r => r.Kind).ToArray());
        Assert.Contains("\"metadataUrl\"", seen[0].Body);
    }

    private const string _refresh = "Atnr|PLACEHOLDER_REFRESH_TOKEN_NOT_REAL";
    private const string _serial = "ABCDEFGHIJ0123456789KLMNOPQRST0123456789";
    private static readonly Secrets _deviceSecrets = new(null, null, null, null, AmazonRefreshToken: _refresh, AmazonDeviceSerial: _serial);

    /// <summary>The session-id and at-main of the recorded exchange response (<c>Fixtures/Amazon/exchange_cookies.json</c>).</summary>
    private const string _exchangedSessionId = "000-0000000-0000003";
    private const string _exchangedAt = "Atza|redacted_0007";

    private AmazonPhotosSource DeviceSource() =>
        new(_deviceSecrets, _handler, "com", (pause, _) =>
        {
            _delays.Add(pause);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Source_refreshes_cookies_before_first_request()
    {
        var source = DeviceSource();

        var (_, count) = await source.WhoAmIAsync(CancellationToken.None);

        Assert.Equal(52015L, count);
        var exchange = _handler.Requests[0];
        Assert.Equal("/ap/exchangetoken/cookies", exchange.RequestUri!.AbsolutePath);
        Assert.Null(_handler.CookieHeaders[0]);
        Assert.Single(_handler.RequestsTo("/ap/exchangetoken/cookies"));
        Assert.Empty(_handler.RequestsTo("/auth/register"));

        // Every cookie Amazon minted goes to amazon.com and to the resolved metadata host.
        var account = Assert.Single(_handler.RequestsTo("/drive/v1/account/endpoint"));
        var cookieHeader = _handler.CookieHeaders[_handler.Requests.IndexOf(account)]!;
        foreach (var expected in new[]
        {
            $"session-id={_exchangedSessionId}", "ubid-main=000-0000000-0000004", $"at-main={_exchangedAt}",
            "sess-at-main=redacted_0008", "x-main=redacted_0006", "session-token=redacted_0005",
        })
        {
            Assert.Contains(expected, cookieHeader);
        }

        Assert.Equal(_exchangedSessionId, Assert.Single(account.Headers.GetValues("x-amzn-sessionid")));
        var usage = Assert.Single(_handler.RequestsTo("/drive/v1/account/usage"));
        Assert.Equal("cdws.us-east-1.amazonaws.com", usage.RequestUri!.Host);
        Assert.Contains($"at-main={_exchangedAt}", _handler.CookieHeaders[_handler.Requests.IndexOf(usage)]);
        Assert.Contains("x-main=redacted_0006", _handler.CookieHeaders[_handler.Requests.IndexOf(usage)]);
        // The quotes Amazon wraps x-main, at-main and sess-at-main in never reach the Cookie header.
        Assert.DoesNotContain('"', _handler.CookieHeaders[_handler.Requests.IndexOf(usage)]!);
    }

    [Fact]
    public async Task Source_exchanges_once_per_instance()
    {
        var source = DeviceSource();

        await source.WhoAmIAsync(CancellationToken.None);
        await source.ListCandidatesAsync(new Window(null), SourceMode.Album, _album, CancellationToken.None);

        Assert.Single(_handler.RequestsTo("/ap/exchangetoken/cookies"));
    }

    [Fact]
    public async Task Source_retries_once_after_401_with_fresh_cookies()
    {
        var exchanges = 0;
        _handler.On("/ap/exchangetoken/cookies", _ => ++exchanges == 1
            ? ScriptedHandler.Json("exchange_cookies.json")
            : ScriptedHandler.JsonBody("""{"response":{"tokens":{"cookies":{".amazon.com":[{"Name":"session-id","Value":"133-0000000-6666666"},{"Name":"ubid-main","Value":"130-0000000-7777777"},{"Name":"at-main","Value":"placeholder-at-main-second"}]}}}}"""));
        var usageCalls = 0;
        _handler.On("/drive/v1/account/usage", _ => ++usageCalls == 1 ? ScriptedHandler.Status(HttpStatusCode.Unauthorized) : ScriptedHandler.Json("usage.json"));

        var (_, count) = await DeviceSource().WhoAmIAsync(CancellationToken.None);

        Assert.Equal(52015L, count);
        Assert.Equal(2, exchanges);
        Assert.Equal(2, usageCalls);
        var retried = _handler.RequestsTo("/drive/v1/account/usage").Last();
        Assert.Equal("133-0000000-6666666", Assert.Single(retried.Headers.GetValues("x-amzn-sessionid")));
        var cookieHeader = _handler.CookieHeaders[_handler.Requests.IndexOf(retried)]!;
        Assert.Contains("at-main=placeholder-at-main-second", cookieHeader);
        Assert.DoesNotContain(_exchangedAt, cookieHeader);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Source_retries_once_after_401_then_throws()
    {
        _handler.On("/drive/v1/account/endpoint", _ => ScriptedHandler.Status(HttpStatusCode.Unauthorized));
        var source = DeviceSource();

        await Assert.ThrowsAsync<AuthExpiredException>(() => source.WhoAmIAsync(CancellationToken.None));

        Assert.Equal(
            ["/ap/exchangetoken/cookies", "/drive/v1/account/endpoint", "/ap/exchangetoken/cookies", "/drive/v1/account/endpoint"],
            _handler.Requests.Select(r => r.RequestUri!.AbsolutePath).ToArray());

        // One re-exchange per run: a later 401 on the same instance is final.
        await Assert.ThrowsAsync<AuthExpiredException>(() => source.WhoAmIAsync(CancellationToken.None));
        Assert.Equal(5, _handler.Requests.Count);
    }

    [Fact]
    public async Task Source_with_failed_exchange_throws_AuthExpired_before_touching_drive()
    {
        _handler.On("/ap/exchangetoken/cookies", _ => ScriptedHandler.Status(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<AuthExpiredException>(() => DeviceSource().WhoAmIAsync(CancellationToken.None));

        Assert.DoesNotContain(_refresh, ex.Message);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Source_without_refresh_token_uses_cookies_unchanged()
    {
        await Source().WhoAmIAsync(CancellationToken.None);

        Assert.Empty(_handler.RequestsTo("/ap/exchangetoken/cookies"));
        var account = Assert.Single(_handler.RequestsTo("/drive/v1/account/endpoint"));
        Assert.Equal($"session-id={_sessionId}; ubid-main={_ubid}; at-main={_at}", _handler.CookieHeaders[_handler.Requests.IndexOf(account)]);
        Assert.Equal(_sessionId, Assert.Single(account.Headers.GetValues("x-amzn-sessionid")));
    }

    [Fact]
    public async Task Cookie_mode_401_is_final_without_an_exchange()
    {
        _handler.On("/drive/v1/account/endpoint", _ => ScriptedHandler.Status(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<AuthExpiredException>(() => Source().WhoAmIAsync(CancellationToken.None));

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public void Refresh_token_alone_is_enough_to_construct()
    {
        using var source = new AmazonPhotosSource(new Secrets(null, null, null, null, AmazonRefreshToken: _refresh), _handler);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void Recorded_fixtures_hold_no_placeholders_and_consistent_counts()
    {
        var pages = new[] { "album_children.json", "album_children_page2.json", "album_children_page3.json" }
            .Select(name => JsonDocument.Parse(File.ReadAllText(Fixtures.Amazon(name))))
            .ToList();
        try
        {
            var kept = pages.Sum(page => page.RootElement.GetProperty("data").GetArrayLength());
            Assert.Equal(_albumIds.Length, kept);
            Assert.All(pages, page => Assert.Equal(kept, page.RootElement.GetProperty("count").GetInt32()));
            Assert.All(pages, page => Assert.Equal(595, page.RootElement.GetProperty("_trimmed").GetProperty("originalCount").GetInt32()));
            Assert.False(pages[2].RootElement.TryGetProperty("nextToken", out _));
        }
        finally
        {
            pages.ForEach(page => page.Dispose());
        }

        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Fixtures.Amazon("albums.json"))!, "*.json"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("_placeholder", text);
            Assert.Contains("\"_recorded\"", text);
            Assert.DoesNotContain("@", text.Replace("redacted@example.com", ""));
        }
    }
}
