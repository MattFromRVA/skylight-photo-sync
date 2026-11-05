using MimeKit;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class SmtpMailerTests
{
    [Fact]
    public async Task Send_delivers_to_stub()
    {
        await using var stub = await SmtpStub.StartAsync();
        var mailer = new SmtpMailer(new SmtpConfig("127.0.0.1", stub.Port, "user"), password: "", useStartTls: false);

        await mailer.SendAsync(Message("Hello frame"), CancellationToken.None);

        var received = Assert.Single(stub.Received);
        Assert.Equal("Hello frame", received.Subject);
        Assert.Equal("frame@example.com", Assert.Single(received.To.Mailboxes).Address);
    }

    [Fact]
    public async Task Send_failure_throws_SendFailed()
    {
        var closedPort = SmtpStub.FreePort();
        var mailer = new SmtpMailer(new SmtpConfig("127.0.0.1", closedPort, "user"), password: "", useStartTls: false);

        var ex = await Assert.ThrowsAsync<SendFailedException>(
            () => mailer.SendAsync(Message("never sent"), CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    private static MimeMessage Message(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MailboxAddress.Parse("frame@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }
}
