using System.Text.Json;
using SkiaSharp;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Recording;
using SkylightPhotoSync.Sources.Amazon;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class FixtureRecorderTests
{
    private static readonly Secrets _secrets = new("133-0000000-0000000", "130-0000000-1111111", "Atza|TESTVALUEONLYxxxx", null);

    // The raw fixtures below are dated 2026-10-31 .. 2026-11-04; the recorder's 7-day "recent" search
    // window is taken from this clock so the test does not drift out of range as real time passes.
    private static readonly DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An unredacted account with recognisable fake values (names, an owner, a cover id), so the
    /// tests can prove they do not survive recording. Two album pages, a three-node search.
    /// </summary>
    private static ScriptedHandler RawAmazonHandler() =>
        new ScriptedHandler()
            .On("/drive/v1/account/endpoint", _ => ScriptedHandler.JsonBody(
                """{"customerExists":true,"metadataUrl":"https://metadata.example.test/drive/v1/","contentUrl":"https://content.example.test/cdproxy/","region":"us-east-1"}"""))
            .On("/drive/v1/account/usage", _ => ScriptedHandler.JsonBody(
                """{"photo":{"total":{"bytes":5000000000,"count":1200}},"video":{"total":{"bytes":2000000000,"count":34}}}"""))
            .On(request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
                && ScriptedHandler.Query(request).Contains("isRoot:true", StringComparison.Ordinal), _ => ScriptedHandler.JsonBody(
                """{"count":1,"data":[{"id":"ROOTRAW0001","name":"Placeholder Owner","kind":"FOLDER","isRoot":true,"ownerId":"OWNERRAW0001"}]}"""))
            .On(request => request.RequestUri!.AbsolutePath.EndsWith("/drive/v1/nodes", StringComparison.Ordinal)
                && ScriptedHandler.Query(request).Contains("kind:VISUAL_COLLECTION", StringComparison.Ordinal), _ => ScriptedHandler.JsonBody(
                """
                {"count":2,"data":[
                  {"id":"ALBUMRAW0001","name":"Vacation 2025","kind":"VISUAL_COLLECTION","description":"Lake house week","ownerId":"OWNERRAW0001",
                   "collectionProperties":{"covers":[{"id":"nodeRawCover0099","isDefault":true,"ownerId":"OWNERRAW0001"}]},"createdDate":"2025-07-01T10:00:00.000Z"},
                  {"id":"ALBUMRAW0002","name":"Skylight","kind":"VISUAL_COLLECTION","ownerId":"OWNERRAW0001","createdDate":"2026-10-01T10:00:00.000Z"}]}
                """))
            .On("/drive/v1/nodes/ALBUMRAW0002/children", request =>
                ScriptedHandler.Query(request).Contains("startToken=RAWTOKEN0001", StringComparison.Ordinal)
                    ? ScriptedHandler.JsonBody(
                        """
                        {"count":3,"data":[
                          {"id":"NODERAW0003","name":"IMG_0003.JPG","kind":"FILE","ownerId":"OWNERRAW0001","parents":["ROOTRAW0001"],"createdDate":"2026-11-04T08:00:00.000Z",
                           "contentProperties":{"size":345678,"md5":"33333333333333333333333333333333","contentType":"image/jpeg","extension":"jpg","contentDate":"2026-11-03T08:00:00.000Z"}}]}
                        """)
                    : ScriptedHandler.JsonBody(
                        """
                        {"count":3,"nextToken":"RAWTOKEN0001","data":[
                          {"id":"NODERAW0001","name":"IMG_0001.JPG","kind":"FILE","ownerId":"OWNERRAW0001","parents":["ROOTRAW0001"],"createdDate":"2026-11-01T12:00:00.000Z",
                           "contentProperties":{"size":1234567,"md5":"11111111111111111111111111111111","contentType":"image/jpeg","extension":"jpg","contentDate":"2026-10-31T09:15:00.000Z",
                             "image":{"width":4032,"height":3024,"make":"Raw Maker","model":"Raw Model"}}},
                          {"id":"NODERAW0002","name":"IMG_0002.HEIC","kind":"FILE","ownerId":"OWNERRAW0001","parents":["ROOTRAW0001"],"createdDate":"2026-11-03T15:00:00.000Z",
                           "contentProperties":{"size":2345678,"md5":"22222222222222222222222222222222","contentType":"image/heic","extension":"heic","contentDate":"2026-11-02T18:30:00.000Z"}}]}
                        """))
            .On("/drive/v1/search", _ => ScriptedHandler.JsonBody(
                """
                {"count":2,"data":[
                  {"id":"NODERAW0011","name":"recent.jpg","kind":"FILE","ownerId":"OWNERRAW0001","createdDate":"2026-11-02T12:00:00.000Z",
                   "contentProperties":{"size":200000,"md5":"bbbb0000000000000000000000000000","contentType":"image/jpeg","contentDate":"2026-11-02T12:00:00.000Z"}},
                  {"id":"NODERAW0012","name":"clip.mp4","kind":"FILE","ownerId":"OWNERRAW0001","createdDate":"2026-11-03T12:00:00.000Z",
                   "contentProperties":{"size":9000000,"md5":"cccc0000000000000000000000000000","contentType":"video/mp4","contentDate":"2026-11-03T12:00:00.000Z","video":{"duration":12.5}}}]}
                """))
            .On("/content", _ => ScriptedHandler.Bytes(File.ReadAllBytes(Fixtures.Image("tiny.jpg")), "image/jpeg"));

    [Fact]
    public async Task Record_writes_redacted_fixtures()
    {
        using var outDir = new TempDir();
        var handler = RawAmazonHandler();
        var source = new AmazonPhotosSource(_secrets, handler, "com", (_, _) => Task.CompletedTask);

        var written = await FixtureRecorder.RecordAsync(source, outDir.Path, "Skylight", CancellationToken.None, _now);

        Assert.All(written, path => Assert.True(File.Exists(path), path));
        var names = written.Select(Path.GetFileName).ToList();
        foreach (var expected in new[] { "account_endpoint.json", "albums.json", "album_children.json", "album_children_page2.json", "search.json", "root_node.json", "usage.json", "content.json" })
        {
            Assert.Contains(expected, names);
        }

        var jpeg = Assert.Single(written, path => path.EndsWith(".jpg", StringComparison.Ordinal));
        using var image = SKBitmap.Decode(File.ReadAllBytes(jpeg));
        Assert.NotNull(image);
        Assert.NotEqual(File.ReadAllBytes(Fixtures.Image("tiny.jpg")), File.ReadAllBytes(jpeg));

        var text = string.Concat(written.Where(p => p.EndsWith(".json", StringComparison.Ordinal)).Select(File.ReadAllText));
        foreach (var json in written.Where(p => p.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(json));
            Assert.True(parsed.RootElement.TryGetProperty("_recorded", out _), json);
        }

        Assert.Contains("node_0001", text);
        Assert.DoesNotContain("RAW", text);
        Assert.DoesNotContain("IMG_0001.JPG", text);
        Assert.DoesNotContain("Placeholder Owner", text);
        Assert.DoesNotContain("Skylight", text);
        Assert.DoesNotContain("Vacation", text);
        Assert.DoesNotContain("Lake house", text);
        Assert.DoesNotContain("Raw Maker", text);
        Assert.DoesNotContain("nodeRawCover0099", text);
        Assert.Contains("VISUAL_COLLECTION", text);
        Assert.Contains("image/jpeg", text);
        Assert.Contains("2026-10-01T10:00:00.000Z", text);
        Assert.DoesNotContain(_secrets.AmazonAt!, text);
        Assert.DoesNotContain(_secrets.AmazonSessionId!, text);

        // The content download really happened (the smallest photo across album and search is
        // recent.jpg from the search page), and the written JPEG is not the downloaded bytes.
        var content = Assert.Single(handler.RequestsTo("/content"));
        Assert.EndsWith("/nodes/NODERAW0011/content", content.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Redaction_keeps_structure_and_extensions()
    {
        const string body = """
            {"count":2,"nextToken":"opaque","data":[
              {"id":"ABC123","name":"IMG_9.HEIC","kind":"FILE","ownerId":"OWN1","parents":["ROOT1"],"parentMap":{"FOLDER":["ROOT1"]},
               "createdDate":"2026-10-01T10:00:00.000Z",
               "contentProperties":{"md5":"ff","contentType":"image/heic","image":{"location":"37.0,-77.0","make":"Pixel Maker"}}},
              {"id":"DEF456","name":"Beach Trip","kind":"VISUAL_COLLECTION","createdBy":"someone@example.com",
               "description":"Grandma's beach house","coverIds":["ABC123","cOv3rId9"],"collectionProperties":{"covers":["ABC123"]},
               "status":"AVAILABLE","size":"1024",
               "directedId":"A2EUQ1WTGCTBG2","phone":"5551234567","takenOn":"2026-10-01T10:00:00.000Z"},
              {"id":"GHI789","name":"Beach Trip ","kind":"VISUAL_COLLECTION"}]}
            """;

        var redacted = FixtureRecorder.Redact(body, new FixtureRecorder.Redactor(["Beach Trip"]));

        using var doc = JsonDocument.Parse(redacted);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("node_0001", data[0].GetProperty("id").GetString());
        Assert.Equal("file_0001.heic", data[0].GetProperty("name").GetString());
        Assert.Equal("owner_0001", data[0].GetProperty("ownerId").GetString());
        Assert.Equal("node_0002", data[0].GetProperty("parents")[0].GetString());
        Assert.Equal("node_0002", data[0].GetProperty("parentMap").GetProperty("FOLDER")[0].GetString());
        Assert.False(data[0].GetProperty("contentProperties").GetProperty("image").TryGetProperty("location", out _));
        Assert.Equal("node_0003", data[1].GetProperty("id").GetString());
        Assert.Equal("album_0001", data[1].GetProperty("name").GetString());
        Assert.Equal("redacted@example.com", data[1].GetProperty("createdBy").GetString());
        Assert.Equal("next_token_0001", doc.RootElement.GetProperty("nextToken").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());

        Assert.Equal("FILE", data[0].GetProperty("kind").GetString());
        Assert.Equal("VISUAL_COLLECTION", data[1].GetProperty("kind").GetString());
        Assert.Equal("2026-10-01T10:00:00.000Z", data[0].GetProperty("createdDate").GetString());
        Assert.Equal("image/heic", data[0].GetProperty("contentProperties").GetProperty("contentType").GetString());
        Assert.Equal("ff", data[0].GetProperty("contentProperties").GetProperty("md5").GetString());
        Assert.Equal("text_0001", data[0].GetProperty("contentProperties").GetProperty("image").GetProperty("make").GetString());
        Assert.Equal("text_0002", data[1].GetProperty("description").GetString());
        Assert.Equal("node_0001", data[1].GetProperty("coverIds")[0].GetString());
        Assert.Equal("node_0004", data[1].GetProperty("coverIds")[1].GetString());
        Assert.Equal("node_0001", data[1].GetProperty("collectionProperties").GetProperty("covers")[0].GetString());
        Assert.Equal("AVAILABLE", data[1].GetProperty("status").GetString());
        Assert.Equal("text_0003", data[1].GetProperty("size").GetString());
        Assert.Equal("text_0004", data[1].GetProperty("directedId").GetString());
        Assert.Equal("text_0005", data[1].GetProperty("phone").GetString());
        Assert.Equal("2026-10-01T10:00:00.000Z", data[1].GetProperty("takenOn").GetString());

        // Amazon album names can carry trailing spaces; the placeholder keeps them visible.
        Assert.Equal("album_0001 ", data[2].GetProperty("name").GetString());
    }

    [Fact]
    public void Redaction_keeps_the_account_service_urls()
    {
        const string body = """
            {"customerExists":true,"metadataUrl":"https://cdws.us-east-1.amazonaws.com/drive/v1/","contentUrl":"https://content-na.drive.amazonaws.com/",
             "downloadServiceUrl":"https://content-na.drive.amazonaws.com/v2/download","thumbnailServiceUrl":"https://thumbnails-photos.amazon.com/v1/thumbnail/",
             "uploadServiceUrl":"https://content-na.drive.amazonaws.com/v2/upload","region":"us-east-1","customerId":"A1CUSTOMER"}
            """;

        var redacted = FixtureRecorder.Redact(body, new FixtureRecorder.Redactor([]));

        using var doc = JsonDocument.Parse(redacted);
        Assert.Equal("https://content-na.drive.amazonaws.com/v2/download", doc.RootElement.GetProperty("downloadServiceUrl").GetString());
        Assert.Equal("https://thumbnails-photos.amazon.com/v1/thumbnail/", doc.RootElement.GetProperty("thumbnailServiceUrl").GetString());
        Assert.Equal("https://content-na.drive.amazonaws.com/v2/upload", doc.RootElement.GetProperty("uploadServiceUrl").GetString());
        Assert.Equal("us-east-1", doc.RootElement.GetProperty("region").GetString());
        Assert.Equal("owner_0001", doc.RootElement.GetProperty("customerId").GetString());
    }

    [Fact]
    public async Task Record_writes_every_page_of_the_album()
    {
        using var outDir = new TempDir();
        var handler = RawAmazonHandler();
        var source = new AmazonPhotosSource(_secrets, handler, "com", (_, _) => Task.CompletedTask);

        var written = await FixtureRecorder.RecordAsync(source, outDir.Path, "Skylight", CancellationToken.None, _now);

        var names = written.Select(Path.GetFileName).ToList();
        Assert.Contains("album_children.json", names);
        Assert.Contains("album_children_page2.json", names);
        Assert.DoesNotContain("album_children_page3.json", names);
        Assert.DoesNotContain("search_page2.json", names);

        using var page1 = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir.Path, "album_children.json")));
        using var page2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir.Path, "album_children_page2.json")));
        Assert.Equal("album_children", page1.RootElement.GetProperty("_endpoint").GetString());
        Assert.Equal(1, page1.RootElement.GetProperty("_page").GetInt32());
        Assert.Equal("next_token_0001", page1.RootElement.GetProperty("nextToken").GetString());
        Assert.Equal("album_children", page2.RootElement.GetProperty("_endpoint").GetString());
        Assert.Equal(2, page2.RootElement.GetProperty("_page").GetInt32());
        Assert.False(page2.RootElement.TryGetProperty("nextToken", out _));
        Assert.False(page1.RootElement.TryGetProperty("_trimmed", out _));
        Assert.Equal(3, page1.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Record_trims_long_pages_and_downloads_the_smallest_kept_photo()
    {
        using var outDir = new TempDir();
        var handler = new ScriptedHandler().WithAmazonDefaults()
            .On($"/drive/v1/nodes/{ScriptedHandler.AlbumId}/children", _ => ScriptedHandler.JsonBody(
                """
                {"count":5,"data":[
                  {"id":"K1","name":"a.jpg","kind":"FILE","createdDate":"2026-10-01T10:00:00.000Z","contentProperties":{"size":500,"contentType":"image/jpeg","contentDate":"2026-10-01T10:00:00.000Z"}},
                  {"id":"K2","name":"b.jpg","kind":"FILE","createdDate":"2026-10-02T10:00:00.000Z","contentProperties":{"size":100,"contentType":"image/jpeg","contentDate":"2026-10-02T10:00:00.000Z"}},
                  {"id":"K3","name":"c.jpg","kind":"FILE","createdDate":"2026-10-03T10:00:00.000Z","contentProperties":{"size":300,"contentType":"image/jpeg","contentDate":"2026-10-03T10:00:00.000Z"}},
                  {"id":"T4","name":"d.jpg","kind":"FILE","createdDate":"2026-10-04T10:00:00.000Z","contentProperties":{"size":10,"contentType":"image/jpeg","contentDate":"2026-10-04T10:00:00.000Z"}},
                  {"id":"T5","name":"e.jpg","kind":"FILE","createdDate":"2026-10-05T10:00:00.000Z","contentProperties":{"size":20,"contentType":"image/jpeg","contentDate":"2026-10-05T10:00:00.000Z"}}]}
                """))
            .On("/drive/v1/search", _ => ScriptedHandler.JsonBody("""{"count":0,"data":[]}"""));
        var source = new AmazonPhotosSource(_secrets, handler, "com", (_, _) => Task.CompletedTask);

        var written = await FixtureRecorder.RecordAsync(source, outDir.Path, ScriptedHandler.AlbumName, CancellationToken.None, _now);

        using var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir.Path, "album_children.json")));
        var data = page.RootElement.GetProperty("data");
        Assert.Equal(FixtureRecorder.NodesKeptPerPage, data.GetArrayLength());
        Assert.Equal(FixtureRecorder.NodesKeptPerPage, page.RootElement.GetProperty("count").GetInt32());
        var trimmed = page.RootElement.GetProperty("_trimmed");
        Assert.Equal(FixtureRecorder.NodesKeptPerPage, trimmed.GetProperty("keptPerPage").GetInt32());
        Assert.Equal(5, trimmed.GetProperty("originalDataLength").GetInt32());
        Assert.Equal(5, trimmed.GetProperty("originalCount").GetInt32());

        // T4 was the smallest photo but was trimmed away, so the download is K2, the smallest kept node.
        var content = Assert.Single(handler.RequestsTo("/content"));
        Assert.EndsWith("/nodes/K2/content", content.RequestUri!.AbsolutePath);
        using var contentDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir.Path, "content.json")));
        Assert.Equal(data[1].GetProperty("id").GetString(), contentDoc.RootElement.GetProperty("nodeId").GetString());
        Assert.Contains(written, p => Path.GetFileName(p) == $"content_{data[1].GetProperty("id").GetString()}.jpg");

        using var search = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir.Path, "search.json")));
        Assert.Equal(0, search.RootElement.GetProperty("count").GetInt32());
        Assert.Empty(search.RootElement.GetProperty("data").EnumerateArray());
    }
}
