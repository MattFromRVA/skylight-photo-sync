using System.Net;
using System.Net.Http.Headers;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers requests from scripted routes instead of the
/// network. Routes added later win over earlier ones, so a test can start from
/// <see cref="WithAmazonDefaults"/> and override single endpoints. Every request is kept in
/// <see cref="Requests"/> together with the <c>Cookie</c> header it carried.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    /// <summary>The configured album in the recorded account (<c>albums.json</c>, 595 real items, 9 kept).</summary>
    public const string AlbumId = "node_0095";
    public const string AlbumName = "album_0026";

    /// <summary>An album whose recorded name carries a trailing space, as Amazon stores it.</summary>
    public const string TrailingSpaceAlbumId = "node_0009";
    public const string TrailingSpaceAlbumName = "album_0003 ";

    public const string NextToken = "next_token_0001";
    public const string NextToken2 = "next_token_0002";

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> CookieHeaders { get; } = [];

    /// <summary>Each request's body as text (null for requests without one), read at send time.</summary>
    public List<string?> Bodies { get; } = [];

    public ScriptedHandler On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    /// <summary>Routes every request whose path contains <paramref name="pathPart"/>.</summary>
    public ScriptedHandler On(string pathPart, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        On(request => request.RequestUri!.AbsolutePath.Contains(pathPart, StringComparison.Ordinal), respond);

    public IEnumerable<HttpRequestMessage> RequestsTo(string pathPart) =>
        Requests.Where(request => request.RequestUri!.AbsolutePath.Contains(pathPart, StringComparison.Ordinal));

    /// <summary>
    /// The recorded, redacted Amazon account (<c>Fixtures/Amazon/*.json</c>, recorded 2026-11-06):
    /// endpoint discovery, root node, usage, 25 albums, the three-page <see cref="AlbumName"/>
    /// album (three nodes kept per page), a three-node search and <c>tiny.jpg</c> as every node's
    /// content.
    /// </summary>
    public ScriptedHandler WithAmazonDefaults() =>
        On("/drive/v1/account/endpoint", _ => Json("account_endpoint.json"))
        .On("/drive/v1/account/usage", _ => Json("usage.json"))
        .On(request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
            && Query(request).Contains("isRoot:true", StringComparison.Ordinal), _ => Json("root_node.json"))
        .On(request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
            && Query(request).Contains("kind:VISUAL_COLLECTION", StringComparison.Ordinal), _ => Json("albums.json"))
        .On($"/drive/v1/nodes/{AlbumId}/children", request =>
            Query(request).Contains($"startToken={NextToken2}", StringComparison.Ordinal) ? Json("album_children_page3.json")
            : Query(request).Contains($"startToken={NextToken}", StringComparison.Ordinal) ? Json("album_children_page2.json")
            : Json("album_children.json"))
        .On("/drive/v1/search", _ => Json("search.json"))
        .On("/content", _ => Bytes(File.ReadAllBytes(Fixtures.Image("tiny.jpg")), "image/jpeg"));

    /// <summary>
    /// The device-login endpoints answered from <c>Fixtures/AmazonAuth</c>: the cookie exchange
    /// from the redacted recording <c>exchange_cookies.json</c>; registration, access-token refresh
    /// and deregistration from the hand-written placeholders (shapes documented in issue #30).
    /// </summary>
    public ScriptedHandler WithDeviceAuthDefaults() =>
        On("/auth/register", _ => AuthJson("placeholder_auth_register.json"))
        .On("/ap/exchangetoken/cookies", _ => AuthJson("exchange_cookies.json"))
        .On("/auth/token", _ => AuthJson("placeholder_auth_token.json"))
        .On("/auth/deregister", _ => AuthJson("placeholder_auth_deregister.json"));

    public static string Query(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query);

    /// <summary>The body of the request in <see cref="Requests"/> at <paramref name="index"/>.</summary>
    public string? BodyOf(HttpRequestMessage request) => Bodies[Requests.IndexOf(request)];

    public static HttpResponseMessage AuthJson(string fixtureName, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(File.ReadAllText(Fixtures.AmazonAuth(fixtureName)), System.Text.Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Json(string fixtureName) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(Fixtures.Amazon(fixtureName)), System.Text.Encoding.UTF8, "application/json"),
        };

    /// <summary>A 200 response carrying <paramref name="json"/> verbatim.</summary>
    public static HttpResponseMessage JsonBody(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{\"message\":\"scripted error\"}", System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        CookieHeaders.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            if (_routes[i].Match(request))
            {
                return _routes[i].Respond(request);
            }
        }

        throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
    }
}
