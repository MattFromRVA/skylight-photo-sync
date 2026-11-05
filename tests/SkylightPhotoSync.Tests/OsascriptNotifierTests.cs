using SkylightPhotoSync.Alerts;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class OsascriptNotifierTests
{
    [Fact]
    public async Task Runs_osascript_display_notification()
    {
        var runner = new FakeProcessRunner().Enqueue(0);
        var notifier = new OsascriptNotifier(runner);

        await notifier.NotifyAsync("Amazon login expired", "Run skylight-sync login", CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("osascript", call.FileName);
        Assert.Equal(
            ["-e", "display notification \"Run skylight-sync login\" with title \"Amazon login expired\""],
            call.Args);
    }

    [Fact]
    public async Task Escapes_quotes_and_backslashes()
    {
        var runner = new FakeProcessRunner().Enqueue(0);
        var notifier = new OsascriptNotifier(runner);

        await notifier.NotifyAsync("Say \"hi\"", "C:\\path \"x\"", CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            ["-e", "display notification \"C:\\\\path \\\"x\\\"\" with title \"Say \\\"hi\\\"\""],
            call.Args);
    }

    [Fact]
    public async Task Non_zero_exit_does_not_throw()
    {
        var runner = new FakeProcessRunner().Enqueue(1, stdErr: "execution error");
        var notifier = new OsascriptNotifier(runner);

        await notifier.NotifyAsync("t", "b", CancellationToken.None);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Runner_exception_is_swallowed()
    {
        var runner = new FakeProcessRunner();
        var notifier = new OsascriptNotifier(runner);

        await notifier.NotifyAsync("t", "b", CancellationToken.None);

        Assert.Single(runner.Calls);
    }
}
