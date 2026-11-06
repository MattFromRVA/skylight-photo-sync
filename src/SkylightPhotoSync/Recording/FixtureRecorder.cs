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
/// </summary>
public static partial class FixtureRecorder
{
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

    private static readonly HashSet<string> _passThroughKeys = new(StringComparer.Ordinal)
    {
        "kind", "contentType", "mimeType", "extension", "status", "region", "version",
        "metadataUrl", "contentUrl", "md5", "isRoot",
    };

    /// <summary>
    /// Calls the account endpoint (through <see cref="AmazonPhotosSource.WhoAmIAsync"/>), the album
    /// list, the album's pages, an all-mode search over the last seven days and downloads the smallest
    /// photo seen, then writes one redacted JSON file per endpoint kind (first page only) plus the
    /// stand-in JPEG into <paramref name="outDir"/>. Returns the written paths.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RecordAsync(
        AmazonPhotosSource source, string outDir, string albumName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(albumName);
        Directory.CreateDirectory(outDir);

        var captured = new Dictionary<string, AmazonResponse>(StringComparer.Ordinal);
        var previous = source.ResponseObserver;
        source.ResponseObserver = response =>
        {
            captured.TryAdd(response.Kind, response);
            previous?.Invoke(response);
        };

        try
        {
            var (accountName, _) = await source.WhoAmIAsync(ct);
            var albumItems = await source.ListCandidatesAsync(new Window(null), SourceMode.Album, albumName, ct);
            var recent = await source.ListCandidatesAsync(
                Window.FromLookback(_recentLookbackDays, DateTimeOffset.UtcNow), SourceMode.All, null, ct);

            var smallest = albumItems.Concat(recent)
                .Where(item => item.Kind == ItemKind.Photo)
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
                if (!captured.TryGetValue(kind, out var response))
                {
                    continue;
                }

                var node = RedactNode(response.Body, redactor);
                Stamp(node, kind, recordedAt);
                written.Add(await WriteAsync(Path.Combine(outDir, $"{kind}.json"), node, ct));
            }

            if (smallest is not null && captured.TryGetValue(AmazonEndpoints.KindContent, out var content))
            {
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
        var parsed = JsonNode.Parse(body) ?? throw new InvalidOperationException("The recorded body is not JSON.");
        if (parsed is not JsonObject root)
        {
            root = new JsonObject { ["data"] = parsed };
        }

        foreach (var key in root.Select(p => p.Key).Where(k => k.StartsWith('_')).ToList())
        {
            root.Remove(key);
        }

        Walk(root, key: null, parentKind: null, redactor);
        return root;
    }

    private static void Stamp(JsonObject node, string kind, string recordedAt)
    {
        node.Remove("_recorded");
        node.Remove("_endpoint");
        node.Insert(0, "_endpoint", kind);
        node.Insert(0, "_recorded", $"{recordedAt} by skylight-sync record-fixtures; ids, names, owners and emails redacted");
    }

    private static void Walk(JsonNode? node, string? key, string? parentKind, Redactor redactor)
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

                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[property] = RedactString(property, text, kind, redactor);
                    }
                    else
                    {
                        Walk(child, property, kind, redactor);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue item && item.TryGetValue<string>(out var text))
                    {
                        array[i] = RedactString(key ?? "", text, parentKind, redactor);
                    }
                    else
                    {
                        Walk(array[i], key, parentKind, redactor);
                    }
                }

                break;
        }
    }

    private static string RedactString(string key, string text, string? kind, Redactor redactor)
    {
        if (text.Contains('@'))
        {
            return "redacted@example.com";
        }

        switch (key)
        {
            case "id":
            case "parentId":
            case "nodeId":
            case "parents":
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
        private readonly HashSet<string> _knownNames = knownNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _albums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

        public string NodeId(string real) => Map(_nodes, real, "node_{0:D4}");

        public string OwnerId(string real) => Map(_owners, real, "owner_{0:D4}");

        public string AlbumName(string real) => Map(_albums, real, "album_{0:D4}");

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
            : _knownNames.Contains(real) ? AlbumName(real)
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
