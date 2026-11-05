namespace SkylightPhotoSync.Models;

/// <summary>
/// The capture-date window a run considers. <see cref="Since"/> is <see langword="null"/> when
/// the window is unbounded (album mode with <c>lookback_days = 0</c>).
/// </summary>
public sealed record Window(DateTimeOffset? Since)
{
    /// <summary>
    /// Builds a window reaching <paramref name="days"/> days back from <paramref name="now"/>.
    /// Zero days means no date bound.
    /// </summary>
    public static Window FromLookback(int days, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        return days == 0 ? new Window(Since: null) : new Window(now.AddDays(-days));
    }

    /// <summary>True when an item captured at <paramref name="capturedAt"/> falls inside the window.</summary>
    public bool Contains(DateTimeOffset capturedAt) => Since is null || capturedAt >= Since.Value;
}
