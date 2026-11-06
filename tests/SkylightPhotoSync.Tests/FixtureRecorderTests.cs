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

    [Fact]
    public async Task Record_writes_redacted_fixtures()
    {
        using var outDir = new TempDir();
        var handler = new ScriptedHandler().WithAmazonDefaults();
        var source = new AmazonPhotosSource(_secrets, handler, "com", (_, _) => Task.CompletedTask);

        var written = await FixtureRecorder.RecordAsync(source, outDir.Path, "Skylight", CancellationToken.None);

        Assert.All(written, path => Assert.True(File.Exists(path), path));
        var names = written.Select(Path.GetFileName).ToList();
        foreach (var expected in new[] { "account_endpoint.json", "albums.json", "album_children.json", "search.json", "root_node.json", "usage.json", "content.json" })
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
        Assert.DoesNotContain("PLACEHOLDER", text);
        Assert.DoesNotContain("IMG_0001.JPG", text);
        Assert.DoesNotContain("Placeholder Owner", text);
        Assert.DoesNotContain("Skylight", text);
        Assert.DoesNotContain("Vacation", text);
        Assert.DoesNotContain("Lake house", text);
        Assert.DoesNotContain("nodePlaceholderCover0099", text);
        Assert.Contains("VISUAL_COLLECTION", text);
        Assert.Contains("image/jpeg", text);
        Assert.Contains("2026-10-01T10:00:00.000Z", text);
        Assert.DoesNotContain(_secrets.AmazonAt!, text);
        Assert.DoesNotContain(_secrets.AmazonSessionId!, text);

        // The content download really happened, and the written JPEG is not the downloaded bytes.
        Assert.Single(handler.RequestsTo("/content"));
    }

    [Fact]
    public void Redaction_keeps_structure_and_extensions()
    {
        const string body = """
            {"count":2,"nextToken":"opaque","data":[
              {"id":"ABC123","name":"IMG_9.HEIC","kind":"FILE","ownerId":"OWN1","parents":["ROOT1"],"createdDate":"2026-10-01T10:00:00.000Z",
               "contentProperties":{"md5":"ff","contentType":"image/heic","image":{"location":"37.0,-77.0","make":"Pixel Maker"}}},
              {"id":"DEF456","name":"Beach Trip","kind":"VISUAL_COLLECTION","createdBy":"someone@example.com",
               "description":"Grandma's beach house","coverIds":["ABC123","cOv3rId9"],"status":"AVAILABLE","size":"1024",
               "directedId":"A2EUQ1WTGCTBG2","phone":"5551234567","takenOn":"2026-10-01T10:00:00.000Z"}]}
            """;

        var redacted = FixtureRecorder.Redact(body, new FixtureRecorder.Redactor(["Beach Trip"]));

        using var doc = JsonDocument.Parse(redacted);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("node_0001", data[0].GetProperty("id").GetString());
        Assert.Equal("file_0001.heic", data[0].GetProperty("name").GetString());
        Assert.Equal("owner_0001", data[0].GetProperty("ownerId").GetString());
        Assert.Equal("node_0002", data[0].GetProperty("parents")[0].GetString());
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
        Assert.Equal("text_0003", data[1].GetProperty("coverIds")[1].GetString());
        Assert.Equal("AVAILABLE", data[1].GetProperty("status").GetString());
        Assert.Equal("text_0004", data[1].GetProperty("size").GetString());
        Assert.Equal("text_0005", data[1].GetProperty("directedId").GetString());
        Assert.Equal("text_0006", data[1].GetProperty("phone").GetString());
        Assert.Equal("2026-10-01T10:00:00.000Z", data[1].GetProperty("takenOn").GetString());
    }
}
