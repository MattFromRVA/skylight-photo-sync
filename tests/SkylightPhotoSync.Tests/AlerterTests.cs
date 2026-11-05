using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class AlerterTests : IDisposable
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly StateStore _store;
    private readonly FakeNotifier _notifier = new();
    private DateTimeOffset _now = _t0;

    public AlerterTests()
    {
        _store = new StateStore(Path.Combine(_dir.Path, "state.db"));
    }

    [Fact]
    public async Task First_alert_sends_email_and_notification()
    {
        await using var stub = await SmtpStub.StartAsync();
        var alerter = Alerter(Mailer(stub));

        var sent = await alerter.AlertAsync(AlertKind.AuthExpired, "Amazon login expired", AlertTexts.AuthExpiredBody, CancellationToken.None);

        Assert.True(sent);
        var call = Assert.Single(_notifier.Calls);
        Assert.Equal(("Amazon login expired", AlertTexts.AuthExpiredBody), call);
        var mail = Assert.Single(stub.Received);
        Assert.Equal("Skylight Photo Sync: Amazon login expired", mail.Subject);
        Assert.Equal("matt@example.com", Assert.Single(mail.To.Mailboxes).Address);
        Assert.Equal("sync@example.com", Assert.Single(mail.From.Mailboxes).Address);
        Assert.Equal(AlertTexts.AuthExpiredBody, mail.TextBody);
        Assert.Equal(_t0, _store.LastAlertAt("AuthExpired"));
    }

    [Fact]
    public async Task Second_alert_within_24h_is_suppressed()
    {
        await using var stub = await SmtpStub.StartAsync();
        var alerter = Alerter(Mailer(stub));
        await alerter.AlertAsync(AlertKind.RepeatedFailure, "Sync failing", "3 runs failed", CancellationToken.None);
        _now = _t0.AddHours(23).AddMinutes(59);

        var sent = await alerter.AlertAsync(AlertKind.RepeatedFailure, "Sync failing", "4 runs failed", CancellationToken.None);

        Assert.False(sent);
        Assert.Single(_notifier.Calls);
        Assert.Single(stub.Received);
        Assert.Equal(_t0, _store.LastAlertAt("RepeatedFailure"));
    }

    [Fact]
    public async Task Alert_after_24h_sends_again()
    {
        await using var stub = await SmtpStub.StartAsync();
        var alerter = Alerter(Mailer(stub));
        await alerter.AlertAsync(AlertKind.RepeatedFailure, "Sync failing", "3 runs failed", CancellationToken.None);
        _now = _t0.AddHours(24).AddSeconds(1);

        var sent = await alerter.AlertAsync(AlertKind.RepeatedFailure, "Sync failing", "27 runs failed", CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(2, _notifier.Calls.Count);
        Assert.Equal(2, stub.Received.Count);
        Assert.Equal(_now, _store.LastAlertAt("RepeatedFailure"));
    }

    [Fact]
    public async Task SmtpDown_kind_skips_email()
    {
        await using var stub = await SmtpStub.StartAsync();
        var alerter = Alerter(Mailer(stub));

        var sent = await alerter.AlertAsync(AlertKind.SmtpDown, "Mail server unreachable", "SMTP failed", CancellationToken.None);

        Assert.True(sent);
        Assert.Single(_notifier.Calls);
        Assert.Empty(stub.Received);
        Assert.Equal(_t0, _store.LastAlertAt("SmtpDown"));
    }

    [Fact]
    public async Task Null_mailer_still_notifies_and_records()
    {
        var alerter = Alerter(mailer: null);

        var sent = await alerter.AlertAsync(AlertKind.AuthExpired, "Amazon login expired", "body", CancellationToken.None);

        Assert.True(sent);
        Assert.Single(_notifier.Calls);
        Assert.Equal(_t0, _store.LastAlertAt("AuthExpired"));
    }

    [Fact]
    public async Task Mailer_failure_does_not_block_notification_or_record()
    {
        var deadMailer = new SmtpMailer(new SmtpConfig("127.0.0.1", SmtpStub.FreePort(), "user"), password: "", useStartTls: false);
        var alerter = Alerter(deadMailer);

        var sent = await alerter.AlertAsync(AlertKind.AuthExpired, "Amazon login expired", "body", CancellationToken.None);

        Assert.True(sent);
        Assert.Single(_notifier.Calls);
        Assert.Equal(_t0, _store.LastAlertAt("AuthExpired"));
    }

    [Fact]
    public async Task Bad_alert_address_still_notifies_and_records()
    {
        await using var stub = await SmtpStub.StartAsync();
        var alerter = new Alerter(_store, Mailer(stub), alertTo: "matt@@example.com", from: "sync@example.com", _notifier, () => _now);

        var sent = await alerter.AlertAsync(AlertKind.AuthExpired, "Amazon login expired", "body", CancellationToken.None);

        Assert.True(sent);
        Assert.Single(_notifier.Calls);
        Assert.Empty(stub.Received);
        Assert.Equal(_t0, _store.LastAlertAt("AuthExpired"));
    }

    [Fact]
    public async Task Kinds_are_rate_limited_independently()
    {
        var alerter = Alerter(mailer: null);
        await alerter.AlertAsync(AlertKind.AuthExpired, "a", "b", CancellationToken.None);

        var sent = await alerter.AlertAsync(AlertKind.SmtpDown, "c", "d", CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(2, _notifier.Calls.Count);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private Alerter Alerter(IMailer? mailer) =>
        new(_store, mailer, alertTo: "matt@example.com", from: "sync@example.com", _notifier, () => _now);

    private static SmtpMailer Mailer(SmtpStub stub) =>
        new(new SmtpConfig("127.0.0.1", stub.Port, "user"), password: "", useStartTls: false);

    private sealed class FakeNotifier : INotifier
    {
        public List<(string Title, string Body)> Calls { get; } = [];

        public Task NotifyAsync(string title, string body, CancellationToken ct)
        {
            Calls.Add((title, body));
            return Task.CompletedTask;
        }
    }
}
