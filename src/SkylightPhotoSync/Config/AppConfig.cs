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

/// <summary>The whole non-secret configuration, one record per TOML table.</summary>
public sealed record AppConfig(
    SourceConfig Source,
    LimitsConfig Limits,
    ImageConfig Image,
    EmailConfig Email,
    SmtpConfig Smtp,
    AlertsConfig Alerts,
    ScheduleConfig Schedule);
