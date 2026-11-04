using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.Tests;

public class WindowTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromLookback_zero_is_unbounded()
    {
        var window = Window.FromLookback(0, _now);

        Assert.Null(window.Since);
    }

    [Fact]
    public void FromLookback_positive_days_starts_that_many_days_before_now()
    {
        var window = Window.FromLookback(30, _now);

        Assert.Equal(_now.AddDays(-30), window.Since);
    }

    [Fact]
    public void FromLookback_rejects_negative_days()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Window.FromLookback(-1, _now));
    }
}
