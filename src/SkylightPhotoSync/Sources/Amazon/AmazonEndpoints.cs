using System.Globalization;
using System.Text;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Sources.Amazon;

/// <summary>
/// URL builders for the private Amazon Photos (Drive v1) endpoints the client uses. The query
/// strings and response shapes were verified against the live service on 2026-11-06 and are pinned
/// by the recorded fixtures under <c>tests/SkylightPhotoSync.Tests/Fixtures/Amazon</c>; see
/// <c>docs/research-notes.md</c>. Every metadata request carries <c>resourceVersion=V2</c> and
/// <c>ContentType=JSON</c>: without them <c>nodes/{id}/children</c> answers 404 and <c>search</c>
/// answers 400. <c>metadataUrl</c> and <c>contentUrl</c> are the values the account endpoint
/// returns, with or without a trailing slash.
/// </summary>
public static class AmazonEndpoints
{
    /// <summary>Nodes per page; verified accepted by the service.</summary>
    public const int PageSize = 200;

    public const string KindAccountEndpoint = "account_endpoint";
    public const string KindRootNode = "root_node";
    public const string KindUsage = "usage";
    public const string KindAlbums = "albums";
    public const string KindAlbumChildren = "album_children";
    public const string KindSearch = "search";
    public const string KindContent = "content";

    /// <summary>
    /// True capture-date order. <c>contentDate ASC</c> (without the <c>contentProperties.</c> prefix)
    /// is rejected by the service.
    /// </summary>
    private const string _sortOldestFirst = "['contentProperties.contentDate ASC']";

    private const string _dateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly (string Name, string? Value)[] _metadataParams =
    [
        ("resourceVersion", "V2"),
        ("ContentType", "JSON"),
    ];

    /// <summary>Endpoint discovery: returns <c>metadataUrl</c> and <c>contentUrl</c> for the account.</summary>
    public static Uri AccountEndpoint(string tld) => new($"https://www.amazon.{tld}/drive/v1/account/endpoint");

    /// <summary>The account's root folder node; its <c>name</c>/<c>ownerId</c> identify the account.</summary>
    public static Uri RootNode(string metadataUrl) => Metadata(metadataUrl, "nodes", ("filters", "isRoot:true"));

    /// <summary>Per-type item counts and bytes.</summary>
    public static Uri Usage(string metadataUrl) => Metadata(metadataUrl, "account/usage");

    /// <summary>One page of the account's albums.</summary>
    public static Uri Albums(string metadataUrl, string? startToken = null) =>
        Metadata(
            metadataUrl,
            "nodes",
            ("filters", "kind:VISUAL_COLLECTION"),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("startToken", startToken));

    /// <summary>One page of an album's items, oldest capture date first.</summary>
    public static Uri AlbumChildren(string metadataUrl, string albumId, string? startToken = null) =>
        Metadata(
            metadataUrl,
            $"nodes/{Uri.EscapeDataString(albumId)}/children",
            ("sort", _sortOldestFirst),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("startToken", startToken));

    /// <summary>
    /// One page of every photo and video uploaded inside <paramref name="window"/> (which must be
    /// bounded) up to <paramref name="until"/>, oldest capture date first. The service rejects a
    /// <c>contentDate</c> range filter, so the range is on <c>createdDate</c> (the upload date, never
    /// earlier than the capture date); the caller still filters by capture date client-side.
    /// </summary>
    public static Uri Search(string metadataUrl, Window window, DateTimeOffset until, string? startToken = null)
    {
        if (window.Since is not { } since)
        {
            throw new ArgumentException("All mode needs a bounded window (lookback_days > 0).", nameof(window));
        }

        var range = $"{FormatDate(since)} TO {FormatDate(until)}";
        return Metadata(
            metadataUrl,
            "search",
            ("filters", $"type:(PHOTOS OR VIDEOS) AND createdDate:[{range}]"),
            ("sort", _sortOldestFirst),
            ("searchContext", "customer"),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("startToken", startToken));
    }

    /// <summary>The original bytes of a node, with its real <c>Content-Type</c>.</summary>
    public static Uri NodeContent(string contentUrl, string nodeId) =>
        Build(contentUrl, $"nodes/{Uri.EscapeDataString(nodeId)}/content");

    internal static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(_dateFormat, CultureInfo.InvariantCulture);

    /// <summary>A metadata URL: the endpoint's own query followed by the V2 parameters.</summary>
    private static Uri Metadata(string metadataUrl, string path, params (string Name, string? Value)[] query) =>
        Build(metadataUrl, path, [.. query, .. _metadataParams]);

    private static Uri Build(string baseUrl, string path, params (string Name, string? Value)[] query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var builder = new StringBuilder(baseUrl.TrimEnd('/')).Append('/').Append(path);
        var separator = '?';
        foreach (var (name, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }
}
