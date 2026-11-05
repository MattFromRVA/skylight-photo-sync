using MimeKit;

namespace SkylightPhotoSync.Mail;

/// <summary>Delivers one message; throws <see cref="SendFailedException"/> when the server does not accept it.</summary>
public interface IMailer
{
    Task SendAsync(MimeMessage message, CancellationToken ct);
}
