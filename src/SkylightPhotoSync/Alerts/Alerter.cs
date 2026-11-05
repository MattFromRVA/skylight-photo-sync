using MimeKit;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Alerts;

/// <summary>
/// Raises an alert as a macOS notification plus an email, at most once per 24 hours per
/// <see cref="AlertKind"/> (spec §6.7). The email is skipped for <see cref="AlertKind.SmtpDown"/>
/// (the mail server is the problem) and when no mailer is configured.
/// </summary>
public sealed class Alerter(
    StateStore store,
    IMailer? mailer,
    string alertTo,
    string from,
    INotifier notifier,
    Func<DateTimeOffset> now)
{
    /// <summary>Minimum gap between two alerts of the same kind.</summary>
    public static readonly TimeSpan RateLimit = TimeSpan.FromHours(24);

    /// <summary>Prefix put in front of every alert email subject.</summary>
    public const string SubjectPrefix = "Skylight Photo Sync: ";

    /// <summary>
    /// Sends the alert unless one of the same kind went out within the last 24 hours.
    /// Returns <c>true</c> when it was sent (notification, email where applicable, and recorded),
    /// <c>false</c> when it was suppressed. A failing mailer never prevents the notification or the record.
    /// </summary>
    public async Task<bool> AlertAsync(AlertKind kind, string title, string body, CancellationToken ct)
    {
        var at = now();
        var key = kind.ToString();
        var last = store.LastAlertAt(key);
        if (last is not null && at - last.Value < RateLimit)
        {
            return false;
        }

        await notifier.NotifyAsync(title, body, ct);

        if (mailer is not null && kind != AlertKind.SmtpDown)
        {
            await TryEmailAsync(title, body, ct);
        }

        store.RecordAlert(key, at);
        return true;
    }

    private async Task TryEmailAsync(string title, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(alertTo));
        message.Subject = SubjectPrefix + title;
        message.Body = new TextPart("plain") { Text = body };

        try
        {
            await mailer!.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The alert email is best effort: when mail is broken the notification still
            // reaches the user, and recording the alert keeps us from retrying every run.
        }
    }
}
