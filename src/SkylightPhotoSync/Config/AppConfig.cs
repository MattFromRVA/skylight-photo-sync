namespace SkylightPhotoSync.Config;

/// <summary>Which Amazon Photos items are candidates: one named album, or everything.</summary>
public enum SourceMode
{
    Album,
    All,
}

/// <summary><c>[source]</c> in config.toml.</summary>
public sealed record SourceConfig(SourceMode Mode, string? AlbumName, int LookbackDays, bool IncludeVideos);

/// <summary><c>[limits]</c> in config.toml.</summary>
public sealed record LimitsConfig(int MaxItemsPerRun);

/// <summary><c>[image]</c> in config.toml.</summary>
public sealed record ImageConfig(int MaxLongEdge, int JpegQuality);

/// <summary><c>[email]</c> in config.toml.</summary>
public sealed record EmailConfig(IReadOnlyList<string> To, string From, int MaxAttachments, long MaxTotalBytes);

/// <summary><c>[smtp]</c> in config.toml (the password lives in secrets.toml).</summary>
public sealed record SmtpConfig(string Host, int Port, string Username);

/// <summary><c>[alerts]</c> in config.toml.</summary>
public sealed record AlertsConfig(string Email);

/// <summary><c>[schedule]</c> in config.toml.</summary>
public sealed record ScheduleConfig(int IntervalMinutes);

/// <summary>
/// <c>[rotation]</c> in config.toml: keep only the newest <see cref="Keep"/> photos this tool sent on
/// the Skylight frame, deleting older ones through Skylight's unofficial app API after each run, and
/// resend photos that were rotated out longest ago once nothing new is left to send.
/// </summary>
/// <param name="Enabled">Off by default; needs <c>[skylight]</c> email and password in secrets.toml.</param>
/// <param name="Keep">How many of this tool's photos stay on each frame.</param>
/// <param name="MaxDeletesPerRun">Safety brake: most photos one run may delete per frame.</param>
/// <param name="FrameName">Only rotate the frame with this name; <see langword="null"/> means every frame on the account.</param>
/// <param name="ArrivalWaitMinutes">How long a run waits for the photos it just emailed to show up on the frame before pruning.</param>
public sealed record RotationConfig(bool Enabled, int Keep, int MaxDeletesPerRun, string? FrameName, int ArrivalWaitMinutes)
{
    /// <summary>The defaults, with rotation turned off.</summary>
    public static RotationConfig Disabled { get; } = new(Enabled: false, Keep: 30, MaxDeletesPerRun: 100, FrameName: null, ArrivalWaitMinutes: 10);
}

/// <summary>The whole non-secret configuration, one record per TOML table.</summary>
public sealed record AppConfig(
    SourceConfig Source,
    LimitsConfig Limits,
    ImageConfig Image,
    EmailConfig Email,
    SmtpConfig Smtp,
    AlertsConfig Alerts,
    ScheduleConfig Schedule)
{
    /// <summary><c>[rotation]</c>; outside the positional list so existing callers keep compiling.</summary>
    public RotationConfig Rotation { get; init; } = RotationConfig.Disabled;
}
