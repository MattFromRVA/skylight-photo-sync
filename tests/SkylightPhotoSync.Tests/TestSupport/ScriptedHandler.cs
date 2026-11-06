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
    public const string AlbumId = "ALBUMPLACEHOLDER0002";
    public const string NextToken = "PLACEHOLDERNEXTTOKEN0001";

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> CookieHeaders { get; } = [];

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
    /// The placeholder Amazon account: endpoint discovery, root node, usage, two albums, a
    /// two-page "Skylight" album, a three-node search and <c>tiny.jpg</c> as every node's content.
    /// </summary>
    public ScriptedHandler WithAmazonDefaults() =>
        On("/drive/v1/account/endpoint", _ => Json("placeholder_account_endpoint.json"))
        .On("/drive/v1/account/usage", _ => Json("placeholder_usage.json"))
        .On(request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
            && Query(request).Contains("isRoot:true", StringComparison.Ordinal), _ => Json("placeholder_root_node.json"))
        .On(request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
            && Query(request).Contains("kind:VISUAL_COLLECTION", StringComparison.Ordinal), _ => Json("placeholder_albums.json"))
        .On($"/drive/v1/nodes/{AlbumId}/children", request =>
            Query(request).Contains($"startToken={NextToken}", StringComparison.Ordinal)
                ? Json("placeholder_album_children_page2.json")
                : Json("placeholder_album_children_page1.json"))
        .On("/drive/v1/search", _ => Json("placeholder_search.json"))
        .On("/content", _ => Bytes(File.ReadAllBytes(Fixtures.Image("tiny.jpg")), "image/jpeg"));

    public static string Query(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query);

    public static HttpResponseMessage Json(string fixtureName) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(File.ReadAllText(Fixtures.Amazon(fixtureName)), System.Text.Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{\"message\":\"scripted error\"}", System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        CookieHeaders.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null);
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            if (_routes[i].Match(request))
            {
                return Task.FromResult(_routes[i].Respond(request));
            }
        }

        throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
    }
}
