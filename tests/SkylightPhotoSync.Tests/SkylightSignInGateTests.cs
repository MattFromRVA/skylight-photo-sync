using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class SkylightSignInGateTests : IDisposable
{
    private const string _email = "me@example.com";
    private const string _password = "placeholder-password-not-real";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly StateStore _store;

    public SkylightSignInGateTests()
    {
        _store = new StateStore(Path.Combine(_dir.Path, "state.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Open_until_a_failure_is_recorded_then_closed_for_the_same_credentials()
    {
        var gate = Gate();
        Assert.True(gate.ShouldAttempt());

        gate.RecordFailure("refused");

        Assert.False(gate.ShouldAttempt());
        Assert.False(Gate().ShouldAttempt());   // a later process with the same secrets.toml
        Assert.False(Gate(email: "  ME@Example.com ").ShouldAttempt());   // the same account, typed differently
        Assert.Equal((_t0, "refused"), (_store.SkylightSignInFailure()!.FailedAt, _store.SkylightSignInFailure()!.Reason));
    }

    [Theory]
    [InlineData(_email, "placeholder-corrected-password")]
    [InlineData("other@example.com", _password)]
    public void Changed_credentials_open_the_gate_and_forget_the_failure_and_its_alert(string email, string password)
    {
        Gate().RecordFailure("refused");
        _store.RecordAlert("SkylightSignIn", _t0);
        _store.RecordAlert("SmtpDown", _t0);

        Assert.True(Gate(email, password).ShouldAttempt());

        Assert.Null(_store.SkylightSignInFailure());
        Assert.Null(_store.LastAlertAt("SkylightSignIn"));
        Assert.Equal(_t0, _store.LastAlertAt("SmtpDown"));
    }

    [Fact]
    public void Success_lifts_the_pause_and_says_whether_there_was_one()
    {
        var gate = Gate();
        Assert.False(gate.RecordSuccess());
        gate.RecordFailure("refused");
        _store.RecordAlert("SkylightSignIn", _t0);

        Assert.True(gate.RecordSuccess());

        Assert.True(gate.ShouldAttempt());
        Assert.Null(_store.LastAlertAt("SkylightSignIn"));
    }

    [Fact]
    public void What_is_stored_does_not_contain_the_credentials_and_differs_per_failure()
    {
        Gate().RecordFailure("refused");
        var first = _store.SkylightSignInFailure()!.Fingerprint;
        Gate().RecordFailure("refused");
        var second = _store.SkylightSignInFailure()!.Fingerprint;

        Assert.DoesNotContain(_password, first);
        Assert.DoesNotContain(_email, first);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(_password)), first);
        Assert.NotEqual(first, second);   // salted
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-fingerprint")]
    [InlineData("v1:***:***")]
    [InlineData("v0:AAAA:AAAA")]
    public void An_unreadable_fingerprint_opens_the_gate(string fingerprint)
    {
        _store.SetSkylightSignInFailure("refused", fingerprint, _t0);

        Assert.True(Gate().ShouldAttempt());
    }

    private SkylightSignInGate Gate(string email = _email, string password = _password) =>
        new(_store, email, password, () => _t0);
}
