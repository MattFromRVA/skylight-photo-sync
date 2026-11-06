using SkylightPhotoSync.Models;
using SkylightPhotoSync.Sources.Amazon;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// Pins the exact URL of every Drive v1 request. The query strings were verified against the live
/// service on 2026-11-06 (see <c>docs/research-notes.md</c>): without <c>resourceVersion=V2</c> and
/// <c>ContentType=JSON</c> the children endpoint answers 404 and search answers 400.
/// </summary>
public sealed class AmazonEndpointsTests
{
    private const string _metadata = "https://cdws.us-east-1.amazonaws.com/drive/v1/";
    private const string _content = "https://content-na.drive.amazonaws.com/";
    private const string _v2 = "resourceVersion=V2&ContentType=JSON";
    private const string _sort = "sort=['contentProperties.contentDate ASC']";

    private static readonly DateTimeOffset _since = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _until = new(2026, 11, 6, 12, 30, 15, 250, TimeSpan.Zero);

    [Fact]
    public void Account_endpoint_is_on_the_retail_site()
    {
        Assert.Equal("https://www.amazon.com/drive/v1/account/endpoint", AmazonEndpoints.AccountEndpoint("com").AbsoluteUri);
        Assert.Equal("https://www.amazon.co.uk/drive/v1/account/endpoint", AmazonEndpoints.AccountEndpoint("co.uk").AbsoluteUri);
    }

    [Fact]
    public void Root_node_and_usage_carry_the_v2_params()
    {
        Assert.Equal((_metadata + "nodes", $"?filters=isRoot:true&{_v2}"), Split(AmazonEndpoints.RootNode(_metadata)));
        Assert.Equal((_metadata + "account/usage", $"?{_v2}"), Split(AmazonEndpoints.Usage(_metadata)));
    }

    [Fact]
    public void Albums_filter_by_kind_and_page()
    {
        Assert.Equal(
            (_metadata + "nodes", $"?filters=kind:VISUAL_COLLECTION&limit=200&{_v2}"),
            Split(AmazonEndpoints.Albums(_metadata)));
        Assert.Equal(
            (_metadata + "nodes", $"?filters=kind:VISUAL_COLLECTION&limit=200&startToken=tok&{_v2}"),
            Split(AmazonEndpoints.Albums(_metadata, "tok")));
    }

    [Fact]
    public void Album_children_sort_by_capture_date_and_page()
    {
        Assert.Equal(
            (_metadata + "nodes/node_0001/children", $"?{_sort}&limit=200&{_v2}"),
            Split(AmazonEndpoints.AlbumChildren(_metadata, "node_0001")));
        Assert.Equal(
            (_metadata + "nodes/node_0001/children", $"?{_sort}&limit=200&startToken=tok&{_v2}"),
            Split(AmazonEndpoints.AlbumChildren(_metadata, "node_0001", "tok")));
    }

    [Fact]
    public void Search_filters_by_created_date_in_the_customer_context()
    {
        var (path, query) = Split(AmazonEndpoints.Search(_metadata, new Window(_since), _until));

        Assert.Equal(_metadata + "search", path);
        Assert.Equal(
            "?filters=type:(PHOTOS OR VIDEOS) AND createdDate:[2026-10-30T00:00:00.000Z TO 2026-11-06T12:30:15.250Z]"
            + $"&{_sort}&searchContext=customer&limit=200&{_v2}",
            query);
        Assert.EndsWith($"&startToken=tok&{_v2}", Split(AmazonEndpoints.Search(_metadata, new Window(_since), _until, "tok")).Query);
    }

    [Fact]
    public void Search_formats_dates_in_utc()
    {
        var local = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.FromHours(5));

        var (_, query) = Split(AmazonEndpoints.Search(_metadata, new Window(local), _until));

        Assert.Contains("createdDate:[2026-10-30T00:00:00.000Z TO ", query);
    }

    [Fact]
    public void Search_needs_a_bounded_window()
    {
        Assert.Throws<ArgumentException>(() => AmazonEndpoints.Search(_metadata, new Window(null), _until));
    }

    [Fact]
    public void Node_content_has_no_query()
    {
        Assert.Equal((_content + "nodes/node_0001/content", ""), Split(AmazonEndpoints.NodeContent(_content, "node_0001")));
    }

    [Fact]
    public void Base_urls_work_with_or_without_a_trailing_slash()
    {
        Assert.Equal(AmazonEndpoints.Usage(_metadata).AbsoluteUri, AmazonEndpoints.Usage(_metadata.TrimEnd('/')).AbsoluteUri);
        Assert.Equal(
            AmazonEndpoints.NodeContent(_content, "x").AbsoluteUri,
            AmazonEndpoints.NodeContent(_content.TrimEnd('/'), "x").AbsoluteUri);
    }

    [Fact]
    public void Ids_are_escaped()
    {
        var (path, _) = Split(AmazonEndpoints.AlbumChildren(_metadata, "a/b c"));

        Assert.Equal(_metadata + "nodes/a%2Fb%20c/children", path);
    }

    /// <summary>The path and the decoded query, so assertions read like the live URLs.</summary>
    private static (string Path, string Query) Split(Uri uri) =>
        (uri.GetLeftPart(UriPartial.Path), Uri.UnescapeDataString(uri.Query));
}
