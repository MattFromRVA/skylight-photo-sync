using System.Globalization;
using System.Text;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Sources.Amazon;

/// <summary>
/// URL builders for the private Amazon Photos (Drive v1) endpoints the client uses. The shapes come
/// from the unofficial <c>amazon_photos</c> Python library and are verified against Matt's recorded
/// fixtures in Task 12 Step 3. <c>metadataUrl</c> and <c>contentUrl</c> are the values the account
/// endpoint returns, with or without a trailing slash.
/// </summary>
public static class AmazonEndpoints
{
    /// <summary>Nodes per page; the largest page size the service is known to accept.</summary>
    public const int PageSize = 200;

    public const string KindAccountEndpoint = "account_endpoint";
    public const string KindRootNode = "root_node";
    public const string KindUsage = "usage";
    public const string KindAlbums = "albums";
    public const string KindAlbumChildren = "album_children";
    public const string KindSearch = "search";
    public const string KindContent = "content";

    private const string _sortOldestFirst = "[\"contentDate ASC\"]";
    private const string _dateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Endpoint discovery: returns <c>metadataUrl</c> and <c>contentUrl</c> for the account.</summary>
    public static Uri AccountEndpoint(string tld) => new($"https://www.amazon.{tld}/drive/v1/account/endpoint");

    /// <summary>The account's root folder node; its <c>name</c>/<c>ownerId</c> identify the account.</summary>
    public static Uri RootNode(string metadataUrl) => Build(metadataUrl, "nodes", ("filters", "isRoot:true"));

    /// <summary>Per-type item counts and bytes.</summary>
    public static Uri Usage(string metadataUrl) => Build(metadataUrl, "account/usage");

    /// <summary>One page of the account's albums.</summary>
    public static Uri Albums(string metadataUrl, string? startToken = null) =>
        Build(
            metadataUrl,
            "nodes",
            ("filters", "kind:VISUAL_COLLECTION"),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("startToken", startToken));

    /// <summary>One page of an album's items, oldest capture date first.</summary>
    public static Uri AlbumChildren(string metadataUrl, string albumId, string? startToken = null) =>
        Build(
            metadataUrl,
            $"nodes/{Uri.EscapeDataString(albumId)}/children",
            ("sort", _sortOldestFirst),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("startToken", startToken));

    /// <summary>
    /// One page of every photo and video captured inside <paramref name="window"/> (which must be
    /// bounded) up to <paramref name="until"/>, oldest first.
    /// </summary>
    public static Uri Search(string metadataUrl, Window window, DateTimeOffset until, string? startToken = null)
    {
        if (window.Since is not { } since)
        {
            throw new ArgumentException("All mode needs a bounded window (lookback_days > 0).", nameof(window));
        }

        var range = $"{FormatDate(since)} TO {FormatDate(until)}";
        return Build(
            metadataUrl,
            "search",
            ("filters", $"type:(PHOTOS OR VIDEOS) AND contentDate:[{range}]"),
            ("sort", _sortOldestFirst),
            ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("startToken", startToken));
    }

    /// <summary>The original bytes of a node.</summary>
    public static Uri NodeContent(string contentUrl, string nodeId) =>
        Build(contentUrl, $"nodes/{Uri.EscapeDataString(nodeId)}/content");

    internal static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(_dateFormat, CultureInfo.InvariantCulture);

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
