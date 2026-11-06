using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Sources.Amazon;

/// <summary>
/// <see cref="IPhotoSource"/> over the private Amazon Photos web endpoints, authenticated with the
/// browser cookies saved by <c>skylight-sync login</c> (spec §6.6). Requests are sequential with a
/// 0.5 s pause between pages; HTTP 401/403 raise <see cref="AuthExpiredException"/> at once, 429
/// waits 60 s and retries up to three times. Cookie values never appear in exceptions or output.
/// </summary>
public sealed class AmazonPhotosSource : IPhotoSource, IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    internal static readonly TimeSpan PagePause = TimeSpan.FromSeconds(0.5);
    internal static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(60);
    internal const int MaxRateLimitRetries = 3;

    private static readonly Dictionary<string, string> _extensionsByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/heic"] = ".heic",
        ["image/heif"] = ".heif",
        ["video/mp4"] = ".mp4",
        ["video/quicktime"] = ".mov",
    };

    private readonly string _tld;
    private readonly string _sessionId;
    private readonly string _ubid;
    private readonly string _at;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private (string MetadataUrl, string ContentUrl)? _endpoints;

    public AmazonPhotosSource(
        Secrets secrets,
        HttpMessageHandler? handler = null,
        string tld = "com",
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(tld);
        if (string.IsNullOrWhiteSpace(secrets.AmazonSessionId)
            || string.IsNullOrWhiteSpace(secrets.AmazonUbid)
            || string.IsNullOrWhiteSpace(secrets.AmazonAt))
        {
            throw new ArgumentException("No Amazon cookies are saved; run `skylight-sync login` first.", nameof(secrets));
        }

        _tld = tld;
        _sessionId = secrets.AmazonSessionId;
        _ubid = secrets.AmazonUbid;
        _at = secrets.AmazonAt;
        _delay = delay ?? Task.Delay;

        // Cookies are attached by hand from the container (so an injected handler sees them too);
        // the real handler must therefore not keep its own jar.
        _http = new HttpClient(
            handler ?? new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All },
            disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("x-amzn-sessionid", _sessionId);

        AddCookies($".amazon.{tld}");
    }

    /// <summary>
    /// Called with every successful response (after the retry loop), so the fixture recorder can
    /// write the raw bodies. Not called for failures.
    /// </summary>
    public Action<AmazonResponse>? ResponseObserver { get; set; }

    /// <summary>
    /// The account behind the cookies: the root node's name (or owner id) and the number of photos
    /// plus videos from the usage endpoint. Proves the cookies work after <c>login</c>.
    /// </summary>
    public async Task<(string Name, long Count)> WhoAmIAsync(CancellationToken ct)
    {
        var (metadataUrl, _) = await ResolveAsync(ct);

        string name = "unknown";
        using (var root = await GetJsonAsync(AmazonEndpoints.RootNode(metadataUrl), AmazonEndpoints.KindRootNode, ct))
        {
            if (root.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                var node = data[0];
                name = GetString(node, "name") is { Length: > 0 } rootName
                    ? rootName
                    : GetString(node, "ownerId") ?? name;
            }
        }

        long count = 0;
        using (var usage = await GetJsonAsync(AmazonEndpoints.Usage(metadataUrl), AmazonEndpoints.KindUsage, ct))
        {
            foreach (var type in new[] { "photo", "video" })
            {
                if (usage.RootElement.TryGetProperty(type, out var byType)
                    && byType.TryGetProperty("total", out var total)
                    && total.TryGetProperty("count", out var value)
                    && value.TryGetInt64(out var n))
                {
                    count += n;
                }
            }
        }

        return (name, count);
    }

    public async Task<IReadOnlyList<Item>> ListCandidatesAsync(
        Window window, SourceMode mode, string? albumName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);
        var (metadataUrl, _) = await ResolveAsync(ct);
        var items = new List<Item>();

        if (mode == SourceMode.Album)
        {
            if (string.IsNullOrWhiteSpace(albumName))
            {
                throw new ArgumentException("Album mode needs an album name.", nameof(albumName));
            }

            var albumId = await FindAlbumIdAsync(metadataUrl, albumName, ct);
            await foreach (var node in PagesAsync(
                token => AmazonEndpoints.AlbumChildren(metadataUrl, albumId, token), AmazonEndpoints.KindAlbumChildren, ct))
            {
                AddIfCandidate(node, window, items);
            }
        }
        else
        {
            if (window.Since is null)
            {
                throw new ArgumentException("All mode needs a bounded window (lookback_days > 0).", nameof(window));
            }

            var until = DateTimeOffset.UtcNow;
            await foreach (var node in PagesAsync(
                token => AmazonEndpoints.Search(metadataUrl, window, until, token), AmazonEndpoints.KindSearch, ct))
            {
                AddIfCandidate(node, window, items);
            }
        }

        return items.OrderBy(i => i.CapturedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<string> DownloadAsync(Item item, string destDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (_, contentUrl) = await ResolveAsync(ct);
        Directory.CreateDirectory(destDir);

        var extension = Path.GetExtension(item.Name);
        if (extension.Length == 0)
        {
            _extensionsByMime.TryGetValue(item.Mime, out extension);
        }

        var dest = Path.Combine(destDir, item.Id + (extension ?? "").ToLowerInvariant());
        using var response = await SendAsync(
            AmazonEndpoints.NodeContent(contentUrl, item.Id), HttpCompletionOption.ResponseHeadersRead, ct);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(dest))
        {
            await input.CopyToAsync(output, ct);
        }

        ResponseObserver?.Invoke(new AmazonResponse(
            AmazonEndpoints.KindContent,
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength,
            Body: ""));
        return dest;
    }

    public void Dispose() => _http.Dispose();

    private async Task<(string MetadataUrl, string ContentUrl)> ResolveAsync(CancellationToken ct)
    {
        if (_endpoints is { } resolved)
        {
            return resolved;
        }

        using var doc = await GetJsonAsync(AmazonEndpoints.AccountEndpoint(_tld), AmazonEndpoints.KindAccountEndpoint, ct);
        var metadataUrl = GetString(doc.RootElement, "metadataUrl");
        var contentUrl = GetString(doc.RootElement, "contentUrl");
        if (string.IsNullOrWhiteSpace(metadataUrl) || string.IsNullOrWhiteSpace(contentUrl))
        {
            throw new InvalidOperationException(
                "The Amazon account endpoint returned no metadataUrl/contentUrl; the endpoint shape may have changed.");
        }

        // The resolved hosts are not under amazon.<tld>; they need the same cookies.
        foreach (var host in new[] { new Uri(metadataUrl).Host, new Uri(contentUrl).Host })
        {
            if (!host.EndsWith($".amazon.{_tld}", StringComparison.OrdinalIgnoreCase))
            {
                AddCookies(host);
            }
        }

        _endpoints = (metadataUrl, contentUrl);
        return _endpoints.Value;
    }

    private async Task<string> FindAlbumIdAsync(string metadataUrl, string albumName, CancellationToken ct)
    {
        var seen = new List<string>();
        await foreach (var album in PagesAsync(token => AmazonEndpoints.Albums(metadataUrl, token), AmazonEndpoints.KindAlbums, ct))
        {
            var name = GetString(album, "name");
            var id = GetString(album, "id");
            if (name is null || id is null)
            {
                continue;
            }

            seen.Add(name);
            if (name.Equals(albumName, StringComparison.Ordinal))
            {
                return id;
            }
        }

        throw new InvalidOperationException(
            $"Album '{albumName}' was not found in Amazon Photos. Albums seen: {(seen.Count == 0 ? "(none)" : string.Join(", ", seen))}.");
    }

    /// <summary>
    /// Yields every element of <c>data</c> across pages, following <c>nextToken</c>. When the last
    /// page's <c>count</c> promises more elements than were received, it throws rather than
    /// silently stopping short.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(
        Func<string?, Uri> uriFor, string kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? startToken = null;
        long received = 0;
        while (true)
        {
            using var page = await GetJsonAsync(uriFor(startToken), kind, ct);
            if (page.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in data.EnumerateArray())
                {
                    received++;
                    yield return node.Clone();
                }
            }

            var nextToken = GetString(page.RootElement, "nextToken");
            if (string.IsNullOrEmpty(nextToken) || nextToken == startToken)
            {
                if (page.RootElement.TryGetProperty("count", out var count)
                    && count.ValueKind == JsonValueKind.Number
                    && count.TryGetInt64(out var total)
                    && total > received)
                {
                    throw new InvalidOperationException(
                        $"Amazon {kind} reported count {total} but paging stopped after {received} items; "
                        + "the pagination shape must be verified against real fixtures in Task 12 Step 3.");
                }

                yield break;
            }

            startToken = nextToken;
            await _delay(PagePause, ct);
        }
    }

    private static void AddIfCandidate(JsonElement node, Window window, List<Item> items)
    {
        if (MapNode(node) is { } item && window.Contains(item.CapturedAt))
        {
            items.Add(item);
        }
    }

    /// <summary>
    /// A FILE node as an <see cref="Item"/>: photo vs video from <c>contentProperties.contentType</c>
    /// or the presence of <c>video</c>; capture date from <c>contentDate</c>, else <c>createdDate</c>,
    /// else <c>modifiedDate</c>. Folders and nodes without id or date are skipped.
    /// </summary>
    internal static Item? MapNode(JsonElement node)
    {
        var id = GetString(node, "id");
        if (id is null || GetString(node, "kind") is { } kind && kind != "FILE")
        {
            return null;
        }

        node.TryGetProperty("contentProperties", out var props);
        var hasProps = props.ValueKind == JsonValueKind.Object;
        var mime = hasProps ? GetString(props, "contentType") : null;
        var hasVideo = hasProps && props.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object;
        var name = GetString(node, "name") is { Length: > 0 } n ? n : id;
        mime ??= GuessMime(name, hasVideo);

        var capturedAt = (hasProps ? GetDate(props, "contentDate") : null)
            ?? GetDate(node, "createdDate")
            ?? GetDate(node, "modifiedDate");
        if (capturedAt is null)
        {
            return null;
        }

        long size = 0;
        if (hasProps && props.TryGetProperty("size", out var sizeValue) && sizeValue.ValueKind == JsonValueKind.Number)
        {
            sizeValue.TryGetInt64(out size);
        }

        var isVideo = hasVideo || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        return new Item(
            Id: id,
            Name: name,
            Kind: isVideo ? ItemKind.Video : ItemKind.Photo,
            CapturedAt: capturedAt.Value,
            SizeBytes: size,
            Md5: hasProps ? GetString(props, "md5")?.ToLowerInvariant() : null,
            Mime: mime);
    }

    private static string GuessMime(string name, bool hasVideo)
    {
        var extension = Path.GetExtension(name);
        foreach (var (mime, ext) in _extensionsByMime)
        {
            if (ext.Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                return mime;
            }
        }

        return hasVideo ? "video/mp4" : "application/octet-stream";
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string kind, CancellationToken ct)
    {
        using var response = await SendAsync(uri, HttpCompletionOption.ResponseContentRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        ResponseObserver?.Invoke(new AmazonResponse(
            kind,
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength,
            body));
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Amazon Photos returned a non-JSON response for {uri} (HTTP {(int)response.StatusCode}); the cookies may be incomplete.", ex);
        }
    }

    /// <summary>
    /// GET with the cookies for the request's host. 401/403 → <see cref="AuthExpiredException"/>;
    /// 429 → <see cref="RateLimitPause"/> then retry up to <see cref="MaxRateLimitRetries"/> times;
    /// any other non-success → <see cref="HttpRequestException"/> naming status and URL only.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var cookieHeader = _cookies.GetCookieHeader(uri);
            if (cookieHeader.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            var response = await _http.SendAsync(request, completion, ct);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            response.Dispose();
            switch (status)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    throw new AuthExpiredException(
                        $"Amazon Photos rejected the saved cookies (HTTP {(int)status} for {uri}); run `skylight-sync login`.");
                case HttpStatusCode.TooManyRequests when attempt < MaxRateLimitRetries:
                    await _delay(RateLimitPause, ct);
                    continue;
                case HttpStatusCode.TooManyRequests:
                    throw new HttpRequestException(
                        $"Amazon Photos kept returning HTTP 429 for {uri} after {MaxRateLimitRetries} retries.", null, status);
                default:
                    throw new HttpRequestException($"Amazon Photos returned HTTP {(int)status} for {uri}.", null, status);
            }
        }
    }

    private void AddCookies(string domain)
    {
        foreach (var (name, value) in new[] { ("session-id", _sessionId), ("ubid-main", _ubid), ("at-main", _at) })
        {
            try
            {
                _cookies.Add(new Cookie(name, value, "/", domain) { Secure = true });
            }
            catch (CookieException ex)
            {
                throw new ArgumentException($"The saved {name} cookie has characters a cookie value cannot carry; run `skylight-sync login` again.", ex);
            }
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string property) =>
        GetString(element, property) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
}
