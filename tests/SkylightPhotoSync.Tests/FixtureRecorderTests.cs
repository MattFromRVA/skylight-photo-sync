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
              {"id":"ABC123","name":"IMG_9.HEIC","ownerId":"OWN1","parents":["ROOT1"],"contentProperties":{"md5":"ff","image":{"location":"37.0,-77.0"}}},
              {"id":"DEF456","name":"Beach Trip","kind":"VISUAL_COLLECTION","createdBy":"someone@example.com"}]}
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
    }
}
