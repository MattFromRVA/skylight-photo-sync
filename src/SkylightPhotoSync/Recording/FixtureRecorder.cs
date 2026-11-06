using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Sources.Amazon;

namespace SkylightPhotoSync.Recording;

/// <summary>
/// Records redacted Amazon Photos responses from a live session so the unit tests can run against
/// real shapes (Task 12 Step 2). Node ids become <c>node_0001…</c>, owner ids <c>owner_0001…</c>,
/// album names <c>album_0001…</c>, file names <c>file_0001.ext</c>, emails
/// <c>redacted@example.com</c>, GPS <c>location</c> fields are dropped, and the one downloaded
/// binary is replaced by a generated tiny JPEG. Every other string is redacted by default to
/// <c>text_0001…</c> unless its key is on a short allowlist (kind, content type, dates, …) or it is
/// an ISO-8601 date or an uppercase letters-only enum-like token. Cookie values never reach
/// the output because the client never puts them in a response body.
/// Every page of the album and search listings is written (<c>album_children.json</c>,
/// <c>album_children_page2.json</c>, …) with only the first <see cref="NodesKeptPerPage"/> nodes of
/// each page kept, so a 600-photo album stays a few kilobytes of fixtures; a trimmed page records
/// the original <c>count</c> and data length under <c>_trimmed</c> and gets a <c>count</c> that
/// matches the nodes kept, so the fixtures still page consistently.
/// </summary>
public static partial class FixtureRecorder
{
    /// <summary>Nodes kept per recorded item page; the rest are dropped before redaction.</summary>
    public const int NodesKeptPerPage = 3;

    private const int _recentLookbackDays = 7;

    private static readonly string[] _kindsInOrder =
    [
        AmazonEndpoints.KindAccountEndpoint,
        AmazonEndpoints.KindRootNode,
        AmazonEndpoints.KindUsage,
        AmazonEndpoints.KindAlbums,
        AmazonEndpoints.KindAlbumChildren,
        AmazonEndpoints.KindSearch,
    ];

    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    private static readonly string[] _trimmedKinds = [AmazonEndpoints.KindAlbumChildren, AmazonEndpoints.KindSearch];

    private static readonly HashSet<string> _passThroughKeys = new(StringComparer.Ordinal)
    {
        "kind", "contentType", "mimeType", "extension", "status", "region", "version",
        "metadataUrl", "contentUrl", "downloadServiceUrl", "thumbnailServiceUrl", "uploadServiceUrl",
        "md5", "isRoot",
    };

    /// <summary>
    /// Calls the account endpoint (through <see cref="AmazonPhotosSource.WhoAmIAsync"/>), the album
    /// list, every page of the album, an all-mode search over the last seven days and downloads the
    /// smallest kept photo, then writes one redacted JSON file per response (every page of the item
    /// listings, trimmed to <see cref="NodesKeptPerPage"/> nodes) plus the stand-in JPEG into
    /// <paramref name="outDir"/>. Returns the written paths.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RecordAsync(
        AmazonPhotosSource source, string outDir, string albumName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(albumName);
        Directory.CreateDirectory(outDir);

        var captured = new Dictionary<string, List<AmazonResponse>>(StringComparer.Ordinal);
        var previous = source.ResponseObserver;
        source.ResponseObserver = response =>
        {
            if (!captured.TryGetValue(response.Kind, out var list))
            {
                captured[response.Kind] = list = [];
            }

            list.Add(response);
            previous?.Invoke(response);
        };

        try
        {
            var (accountName, _) = await source.WhoAmIAsync(ct);
            var albumItems = await source.ListCandidatesAsync(new Window(null), SourceMode.Album, albumName, ct);
            var recent = await source.ListCandidatesAsync(
                Window.FromLookback(_recentLookbackDays, DateTimeOffset.UtcNow), SourceMode.All, null, ct);

            var pages = new Dictionary<string, List<Page>>(StringComparer.Ordinal);
            var keptIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in _kindsInOrder)
            {
                if (!captured.TryGetValue(kind, out var responses))
                {
                    continue;
                }

                pages[kind] = responses.Select(response => Trim(Parse(response.Body), kind, keptIds)).ToList();
                RewriteCounts(pages[kind]);
            }

            var smallest = albumItems.Concat(recent)
                .Where(item => item.Kind == ItemKind.Photo && keptIds.Contains(item.Id))
                .OrderBy(item => item.SizeBytes)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (smallest is not null)
            {
                var scratch = Path.Combine(Path.GetTempPath(), "skylight-photo-sync", $"record-{Guid.NewGuid():N}");
                try
                {
                    await source.DownloadAsync(smallest, scratch, ct);
                }
                finally
                {
                    TryDelete(scratch);
                }
            }

            var redactor = new Redactor([albumName, accountName]);
            var recordedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var written = new List<string>();
            foreach (var kind in _kindsInOrder)
            {
                if (!pages.TryGetValue(kind, out var kindPages))
                {
                    continue;
                }

                for (var i = 0; i < kindPages.Count; i++)
                {
                    var page = kindPages[i];
                    Walk(page.Body, key: null, parentKind: null, idsOnly: false, redactor);
                    Stamp(page.Body, kind, recordedAt);
                    page.Body.Insert(2, "_page", i + 1);
                    if (page.Trimmed is { } trimmed)
                    {
                        page.Body.Insert(3, "_trimmed", trimmed);
                    }

                    var fileName = i == 0 ? $"{kind}.json" : $"{kind}_page{i + 1}.json";
                    written.Add(await WriteAsync(Path.Combine(outDir, fileName), page.Body, ct));
                }
            }

            if (smallest is not null && captured.TryGetValue(AmazonEndpoints.KindContent, out var contents))
            {
                var content = contents[0];
                var nodeId = redactor.NodeId(smallest.Id);
                var binaryName = $"content_{nodeId}.jpg";
                var node = new JsonObject
                {
                    ["statusCode"] = content.StatusCode,
                    ["contentType"] = content.ContentType,
                    ["contentLength"] = content.ContentLength,
                    ["nodeId"] = nodeId,
                    ["binary"] = $"{binaryName} is a generated stand-in; the real bytes were downloaded and discarded",
                };
                Stamp(node, AmazonEndpoints.KindContent, recordedAt);
                written.Add(await WriteAsync(Path.Combine(outDir, "content.json"), node, ct));

                var binaryPath = Path.Combine(outDir, binaryName);
                await File.WriteAllBytesAsync(binaryPath, TinyJpeg(), ct);
                written.Add(binaryPath);
            }

            return written;
        }
        finally
        {
            source.ResponseObserver = previous;
        }
    }

    /// <summary>
    /// Keeps the first <see cref="NodesKeptPerPage"/> elements of <c>data</c> on an item page and
    /// remembers their ids. Other kinds (albums, root node, usage) are kept whole.
    /// </summary>
    private static Page Trim(JsonObject body, string kind, HashSet<string> keptIds)
    {
        if (body["data"] is not JsonArray data)
        {
            return new Page(body, Trimmed: null);
        }

        JsonObject? trimmed = null;
        if (_trimmedKinds.Contains(kind) && data.Count > NodesKeptPerPage)
        {
            trimmed = new JsonObject
            {
                ["keptPerPage"] = NodesKeptPerPage,
                ["originalDataLength"] = data.Count,
                ["originalCount"] = body["count"]?.DeepClone(),
            };
            while (data.Count > NodesKeptPerPage)
            {
                data.RemoveAt(data.Count - 1);
            }
        }

        foreach (var node in data)
        {
            if (node is JsonObject obj && obj["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id))
            {
                keptIds.Add(id);
            }
        }

        return new Page(body, trimmed);
    }

    /// <summary>
    /// When any page of a kind was trimmed, <c>count</c> on every page becomes the number of nodes
    /// kept across the pages, so the client's "count promised more than paging delivered" check
    /// still holds against the fixtures.
    /// </summary>
    private static void RewriteCounts(List<Page> pages)
    {
        if (pages.All(page => page.Trimmed is null))
        {
            return;
        }

        var kept = pages.Sum(page => page.Body["data"] is JsonArray data ? data.Count : 0);
        foreach (var page in pages)
        {
            page.Body["count"] = kept;
        }
    }

    private static JsonObject Parse(string body)
    {
        var parsed = JsonNode.Parse(body) ?? throw new InvalidOperationException("The recorded body is not JSON.");
        if (parsed is not JsonObject root)
        {
            root = new JsonObject { ["data"] = parsed };
        }

        foreach (var key in root.Select(p => p.Key).Where(k => k.StartsWith('_')).ToList())
        {
            root.Remove(key);
        }

        return root;
    }

    private sealed record Page(JsonObject Body, JsonObject? Trimmed);

    /// <summary>Redacts one JSON body; see the class summary for the rules.</summary>
    internal static string Redact(string body, Redactor redactor) =>
        RedactNode(body, redactor).ToJsonString(_writeOptions);

    /// <summary>A small valid JPEG that stands in for every downloaded binary.</summary>
    internal static byte[] TinyJpeg()
    {
        var info = new SKImageInfo(16, 12, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot allocate the stand-in image surface.");
        surface.Canvas.Clear(new SKColor(0x4A, 0x90, 0xD9));
        using var paint = new SKPaint { Color = new SKColor(0xF4, 0xA2, 0x61) };
        surface.Canvas.DrawRect(new SKRect(4, 3, 12, 9), paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 75)
            ?? throw new InvalidOperationException("Cannot encode the stand-in JPEG.");
        return data.ToArray();
    }

    private static JsonObject RedactNode(string body, Redactor redactor)
    {
        var root = Parse(body);
        Walk(root, key: null, parentKind: null, idsOnly: false, redactor);
        return root;
    }

    private static void Stamp(JsonObject node, string kind, string recordedAt)
    {
        node.Remove("_recorded");
        node.Remove("_endpoint");
        node.Insert(0, "_endpoint", kind);
        node.Insert(0, "_recorded", $"{recordedAt} by skylight-sync record-fixtures; ids, names, owners and emails redacted");
    }

    /// <summary>
    /// Redacts every string in place. Inside <c>parentMap</c> (<c>{"FOLDER": [ids]}</c>) every string
    /// is a node id whatever its key, hence <paramref name="idsOnly"/>.
    /// </summary>
    private static void Walk(JsonNode? node, string? key, string? parentKind, bool idsOnly, Redactor redactor)
    {
        switch (node)
        {
            case JsonObject obj:
                var kind = obj["kind"] is JsonValue kindValue && kindValue.TryGetValue<string>(out var k) ? k : parentKind;
                foreach (var property in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[property];
                    if (property.Equals("location", StringComparison.OrdinalIgnoreCase))
                    {
                        obj.Remove(property);
                        continue;
                    }

                    var childIdsOnly = idsOnly || property.Equals("parentMap", StringComparison.Ordinal);
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[property] = RedactString(property, text, kind, childIdsOnly, redactor);
                    }
                    else
                    {
                        Walk(child, property, kind, childIdsOnly, redactor);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue item && item.TryGetValue<string>(out var text))
                    {
                        array[i] = RedactString(key ?? "", text, parentKind, idsOnly, redactor);
                    }
                    else
                    {
                        Walk(array[i], key, parentKind, idsOnly, redactor);
                    }
                }

                break;
        }
    }

    private static string RedactString(string key, string text, string? kind, bool idsOnly, Redactor redactor)
    {
        if (text.Contains('@'))
        {
            return "redacted@example.com";
        }

        if (idsOnly)
        {
            return redactor.NodeId(text);
        }

        switch (key)
        {
            case "id":
            case "parentId":
            case "nodeId":
            case "parents":
            case "coverIds":
            case "covers":
                return redactor.NodeId(text);
            case "ownerId":
            case "customerId":
            case "owner":
            case "createdBy":
            case "modifiedBy":
                return redactor.OwnerId(text);
            case "nextToken":
            case "startToken":
                return redactor.Token(text);
            case "name":
                return kind == "VISUAL_COLLECTION" ? redactor.AlbumName(text) : redactor.FileName(text);
        }

        if (_passThroughKeys.Contains(key) || key.EndsWith("Date", StringComparison.Ordinal) || key.EndsWith("Time", StringComparison.Ordinal))
        {
            return text;
        }

        return redactor.Text(text);
    }

    private static bool IsStructuralValue(string text) =>
        IsoDateTimePattern().IsMatch(text)
        || EnumTokenPattern().IsMatch(text);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?)?$")]
    private static partial Regex IsoDateTimePattern();

    [GeneratedRegex(@"^[A-Z][A-Z_]{0,39}$")]
    private static partial Regex EnumTokenPattern();

    private static async Task<string> WriteAsync(string path, JsonObject node, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, node.ToJsonString(_writeOptions) + "\n", ct);
        return path;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover scratch download must not fail the recording.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>
    /// Stable placeholder assignment across every file of one recording: the same real value always
    /// maps to the same placeholder, numbered by first appearance.
    /// </summary>
    internal sealed class Redactor(IEnumerable<string> knownNames)
    {
        private readonly HashSet<string> _knownNames =
            knownNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _albums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

        public string NodeId(string real) => Map(_nodes, real, "node_{0:D4}");

        public string OwnerId(string real) => Map(_owners, real, "owner_{0:D4}");

        /// <summary>
        /// Album names keep their surrounding whitespace (Amazon preserves a trailing space typed
        /// into a name), so the fixtures show the quirk the client's trimmed match exists for.
        /// </summary>
        public string AlbumName(string real)
        {
            var core = real.Trim();
            if (core.Length == 0)
            {
                return Map(_albums, real, "album_{0:D4}");
            }

            var start = real.IndexOf(core, StringComparison.Ordinal);
            return real[..start] + Map(_albums, core, "album_{0:D4}") + real[(start + core.Length)..];
        }

        public string Token(string real) => Map(_tokens, real, "next_token_{0:D4}");

        public string FileName(string real)
        {
            var extension = Path.GetExtension(real);
            var hasExtension = extension.Length is > 1 and <= 6 && extension.Skip(1).All(char.IsAsciiLetterOrDigit);
            return hasExtension
                ? Map(_files, real, "file_{0:D4}") + extension.ToLowerInvariant()
                : Map(_files, real, "name_{0:D4}");
        }

        /// <summary>
        /// Any other string: an already-seen node id stays that node's placeholder, a known album or
        /// account name becomes an album placeholder, dates and enum-like tokens stay, and
        /// everything else becomes <c>text_0001…</c>.
        /// </summary>
        public string Text(string real) =>
            _nodes.TryGetValue(real, out var node) ? node
            : _knownNames.Contains(real.Trim()) ? AlbumName(real)
            : IsStructuralValue(real) ? real
            : Map(_texts, real, "text_{0:D4}");

        private static string Map(Dictionary<string, string> map, string real, string format)
        {
            if (!map.TryGetValue(real, out var placeholder))
            {
                placeholder = string.Format(CultureInfo.InvariantCulture, format, map.Count + 1);
                map[real] = placeholder;
            }

            return placeholder;
        }
    }
}
