using MimeKit;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class MessageBuilderTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 5, 14, 30, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Subject_and_body()
    {
        var message = MessageBuilder.Build(
            TwoPhotoBatch(), "frame@example.com", "sender@example.com", SourceMode.Album, "Family", _now);

        Assert.Equal("Skylight Photo Sync — 2 photos (2026-11-05)", message.Subject);
        Assert.Equal("Source: album \"Family\"", message.TextBody);
        Assert.Equal("frame@example.com", Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("sender@example.com", Assert.Single(message.From.Mailboxes).Address);
    }

    [Fact]
    public void Body_in_all_mode_says_all()
    {
        var message = MessageBuilder.Build(
            TwoPhotoBatch(), "frame@example.com", "sender@example.com", SourceMode.All, null, _now);

        Assert.Equal("Source: all", message.TextBody);
    }

    [Fact]
    public void Attachments_present_with_mime()
    {
        var message = MessageBuilder.Build(
            TwoPhotoBatch(), "frame@example.com", "sender@example.com", SourceMode.All, null, _now);

        var attachments = message.Attachments.OfType<MimePart>().ToList();
        Assert.Equal(["tiny.jpg", "tiny.png"], attachments.Select(a => a.FileName));
        Assert.Equal(["image/jpeg", "image/png"], attachments.Select(a => a.ContentType.MimeType));
        Assert.All(attachments, a => Assert.True(a.IsAttachment));
        Assert.Equal(
            new FileInfo(Fixtures.Image("tiny.jpg")).Length,
            DecodedLength(attachments[0]));
    }

    private static long DecodedLength(MimePart part)
    {
        Assert.NotNull(part.Content);
        using var stream = new MemoryStream();
        part.Content.DecodeTo(stream);
        return stream.Length;
    }

    private static Batch TwoPhotoBatch()
    {
        var files = new[] { Fixtures.Image("tiny.jpg"), Fixtures.Image("tiny.png") };
        var items = new[]
        {
            new Item("a", "tiny.jpg", ItemKind.Photo, _now.AddDays(-2), 100, null, "image/jpeg"),
            new Item("b", "tiny.png", ItemKind.Photo, _now.AddDays(-1), 100, null, "image/png"),
        };
        return new Batch("batch-1", items, files, files.Sum(f => new FileInfo(f).Length));
    }
}
