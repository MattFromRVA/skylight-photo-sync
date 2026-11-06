using System.Net;
using SkylightPhotoSync.Commands;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Sources.Amazon;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// Drives <see cref="AmazonPhotosSource"/> through a <see cref="ScriptedHandler"/> serving the
/// hand-written <c>placeholder_*.json</c> fixtures. No test touches the network.
/// </summary>
public sealed class AmazonPhotosSourceTests
{
    private const string _sessionId = "133-0000000-0000000";
    private const string _ubid = "130-0000000-1111111";
    private const string _at = "Atza|TESTVALUEONLYxxxx";
    private static readonly Secrets _secrets = new(_sessionId, _ubid, _at, null);
    private static readonly DateTimeOffset _since = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly ScriptedHandler _handler = new ScriptedHandler().WithAmazonDefaults();
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
        var items = await Source().ListCandidatesAsync(new Window(null), SourceMode.Album, "Skylight", CancellationToken.None);

        Assert.Equal(
            ["NODEPLACEHOLDER0001", "NODEPLACEHOLDER0002", "NODEPLACEHOLDER0003"],
            items.Select(i => i.Id).ToArray());
        var first = items[0];
        Assert.Equal("IMG_0001.JPG", first.Name);
        Assert.Equal(ItemKind.Photo, first.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 9, 15, 0, TimeSpan.Zero), first.CapturedAt);
        Assert.Equal(1234567, first.SizeBytes);
        Assert.Equal("11111111111111111111111111111111", first.Md5);
        Assert.Equal("image/jpeg", first.Mime);
        Assert.Equal("image/heic", items[1].Mime);

        // No contentDate: falls back to createdDate.
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero), items[2].CapturedAt);
    }

    [Fact]
    public async Task Album_mode_applies_the_window()
    {
        var items = await Source().ListCandidatesAsync(
            new Window(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)), SourceMode.Album, "Skylight", CancellationToken.None);

        Assert.Equal(["NODEPLACEHOLDER0002", "NODEPLACEHOLDER0003"], items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Album_mode_names_a_missing_album()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.Album, "Nope", CancellationToken.None));

        Assert.Contains("'Nope'", ex.Message);
        Assert.Contains("Skylight", ex.Message);
    }

    [Fact]
    public async Task All_mode_uses_search_with_date_range()
    {
        var items = await Source().ListCandidatesAsync(new Window(_since), SourceMode.All, null, CancellationToken.None);

        var search = Assert.Single(_handler.RequestsTo("/drive/v1/search"));
        var query = ScriptedHandler.Query(search);
        Assert.Contains("filters=type:(PHOTOS OR VIDEOS) AND contentDate:[2026-10-30T00:00:00.000Z TO ", query);
        Assert.Contains("sort=[\"contentDate ASC\"]", query);
        Assert.Empty(_handler.RequestsTo("/children"));

        // too-old.jpg (September) is outside the window; the video is kept and typed.
        Assert.Equal(["NODEPLACEHOLDER0011", "NODEPLACEHOLDER0012"], items.Select(i => i.Id).ToArray());
        Assert.Equal(ItemKind.Video, items[1].Kind);
        Assert.Equal("video/mp4", items[1].Mime);
    }

    [Fact]
    public async Task All_mode_refuses_an_unbounded_window()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.All, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Http401_throws_AuthExpired(HttpStatusCode status)
    {
        _handler.On("/drive/v1/account/endpoint", _ => ScriptedHandler.Status(status));

        await Assert.ThrowsAsync<AuthExpiredException>(() =>
            Source().ListCandidatesAsync(new Window(null), SourceMode.Album, "Skylight", CancellationToken.None));

        Assert.Single(_handler.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Http429_backs_off_and_retries()
    {
        var attempts = 0;
        _handler.On("/drive/v1/account/usage", _ =>
            ++attempts <= 2 ? ScriptedHandler.Status(HttpStatusCode.TooManyRequests) : ScriptedHandler.Json("placeholder_usage.json"));

        var (name, count) = await Source().WhoAmIAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], _delays);
        Assert.Equal(("Placeholder Owner", 1234L), (name, count));
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
    public async Task Pagination_follows_startToken()
    {
        await Source().ListCandidatesAsync(new Window(null), SourceMode.Album, "Skylight", CancellationToken.None);

        var pages = _handler.RequestsTo("/children").Select(ScriptedHandler.Query).ToList();
        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain("startToken", pages[0]);
        Assert.Contains($"startToken={ScriptedHandler.NextToken}", pages[1]);
        Assert.Contains("limit=200", pages[0]);
        Assert.Contains("sort=[\"contentDate ASC\"]", pages[0]);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], _delays);
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
        Assert.Equal("metadata.example.test", usage.RequestUri!.Host);
        Assert.Contains($"at-main={_at}", _handler.CookieHeaders[_handler.Requests.IndexOf(usage)]);
    }

    [Fact]
    public async Task WhoAmI_resolves_endpoints_once()
    {
        var source = Source();

        await source.WhoAmIAsync(CancellationToken.None);
        await source.ListCandidatesAsync(new Window(null), SourceMode.Album, "Skylight", CancellationToken.None);

        Assert.Single(_handler.RequestsTo("/drive/v1/account/endpoint"));
    }

    [Fact]
    public async Task Download_streams_content_to_an_id_named_file()
    {
        using var dest = new TempDir();
        var source = Source();
        var items = await source.ListCandidatesAsync(new Window(null), SourceMode.Album, "Skylight", CancellationToken.None);

        var path = await source.DownloadAsync(items[0], dest.Path, CancellationToken.None);

        Assert.Equal(Path.Combine(dest.Path, "NODEPLACEHOLDER0001.jpg"), path);
        Assert.Equal(File.ReadAllBytes(Fixtures.Image("tiny.jpg")), File.ReadAllBytes(path));
        var content = Assert.Single(_handler.RequestsTo("/content"));
        Assert.Equal("content.example.test", content.RequestUri!.Host);
        Assert.Equal("/cdproxy/nodes/NODEPLACEHOLDER0001/content", content.RequestUri.AbsolutePath);
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

        await source.ListCandidatesAsync(new Window(null), SourceMode.Album, "Skylight", CancellationToken.None);

        Assert.Equal(
            [AmazonEndpoints.KindAccountEndpoint, AmazonEndpoints.KindAlbums, AmazonEndpoints.KindAlbumChildren, AmazonEndpoints.KindAlbumChildren],
            seen.Select(r => r.Kind).ToArray());
        Assert.Contains("\"metadataUrl\"", seen[0].Body);
    }
}
