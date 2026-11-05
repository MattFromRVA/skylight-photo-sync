using System.Globalization;
using MimeKit;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Mail;

/// <summary>Turns one <see cref="Batch"/> into the email a Skylight frame receives.</summary>
public static class MessageBuilder
{
    /// <summary>
    /// Subject <c>Skylight Photo Sync — {n} photos ({yyyy-MM-dd})</c>, a one-line plain-text body naming
    /// the source, and one attachment per file in <see cref="Batch.Files"/> named by the file's basename
    /// with the MIME type taken from its extension.
    /// </summary>
    public static MimeMessage Build(
        Batch batch, string to, string from, SourceMode mode, string? albumName, DateTimeOffset now)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = string.Create(
            CultureInfo.InvariantCulture,
            $"Skylight Photo Sync — {batch.Items.Count} photos ({now:yyyy-MM-dd})");

        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = mode == SourceMode.All ? "Source: all" : $"Source: album \"{albumName}\"" },
        };
        foreach (var file in batch.Files)
        {
            body.Add(Attachment(file));
        }

        message.Body = body;
        return message;
    }

    private static MimePart Attachment(string file)
    {
        var name = Path.GetFileName(file);
        var part = new MimePart(MimeTypes.GetMimeType(name))
        {
            Content = new MimeContent(new MemoryStream(File.ReadAllBytes(file))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = name,
        };
        return part;
    }
}
