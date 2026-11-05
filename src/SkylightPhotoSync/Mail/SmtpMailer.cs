using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SkylightPhotoSync.Config;

namespace SkylightPhotoSync.Mail;

/// <summary>
/// Sends through MailKit's <see cref="SmtpClient"/>: STARTTLS unless <paramref name="useStartTls"/> is off,
/// authenticates only when a password is given, and turns every SMTP or socket error into
/// <see cref="SendFailedException"/>.
/// </summary>
public sealed class SmtpMailer(SmtpConfig cfg, string password, bool useStartTls = true) : IMailer
{
    public async Task SendAsync(MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();
        try
        {
            var security = useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(cfg.Host, cfg.Port, security, ct);
            if (password.Length > 0)
            {
                await client.AuthenticateAsync(cfg.Username, password, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or SocketException or IOException)
        {
            throw new SendFailedException($"Sending via {cfg.Host}:{cfg.Port} failed: {ex.Message}", ex);
        }
    }
}
