using Tomlyn;
using Tomlyn.Model;

namespace SkylightPhotoSync.Config;

/// <summary>Raised for any problem with config.toml or secrets.toml; the message is user-facing.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads and writes the TOML files described in spec §6.8.</summary>
public static class ConfigLoader
{
    private const UnixFileMode _secretsMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode _groupOrOtherBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static AppConfig LoadConfig(string path)
    {
        var root = ParseFile(path);

        var source = ReadSource(Table(root, "source"));
        var limits = Table(root, "limits");
        var image = Table(root, "image");
        var email = Table(root, "email");
        var smtp = Table(root, "smtp");
        var alerts = Table(root, "alerts");
        var schedule = Table(root, "schedule");

        // Spec 6.7: alerts go to the SMTP username unless [alerts] email says otherwise.
        var smtpUsername = RequireString(smtp, "smtp", "username");
        var alertEmail = GetString(alerts, "alerts", "email");

        return new AppConfig(
            Source: source,
            Limits: new LimitsConfig(
                MaxItemsPerRun: GetInt(limits, "limits", "max_items_per_run") ?? 25),
            Image: new ImageConfig(
                MaxLongEdge: GetInt(image, "image", "max_long_edge") ?? 2048,
                JpegQuality: GetInt(image, "image", "jpeg_quality") ?? 88),
            Email: new EmailConfig(
                To: RequireStringList(email, "email", "to"),
                From: RequireString(email, "email", "from"),
                MaxAttachments: GetInt(email, "email", "max_attachments") ?? 12,
                MaxTotalBytes: GetLong(email, "email", "max_total_bytes") ?? 17_000_000L),
            Smtp: new SmtpConfig(
                Host: RequireString(smtp, "smtp", "host"),
                Port: GetInt(smtp, "smtp", "port") ?? 587,
                Username: smtpUsername),
            Alerts: new AlertsConfig(
                Email: string.IsNullOrWhiteSpace(alertEmail) ? smtpUsername : alertEmail),
            Schedule: new ScheduleConfig(
                IntervalMinutes: GetInt(schedule, "schedule", "interval_minutes") ?? 60));
    }

    public static Secrets LoadSecrets(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Secrets file not found: {path}");
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & _groupOrOtherBits) != 0)
            {
                throw new ConfigException(
                    $"{path} is accessible by other users; it must be mode 0600. Fix with: chmod 600 \"{path}\"");
            }
        }

        var root = ParseFile(path);
        var amazon = Table(root, "amazon");
        var smtp = Table(root, "smtp");

        return new Secrets(
            AmazonSessionId: GetStringEither(amazon, "amazon", "session_id", "session-id"),
            AmazonUbid: GetStringEither(amazon, "amazon", "ubid_main", "ubid-main"),
            AmazonAt: GetStringEither(amazon, "amazon", "at_main", "at-main"),
            SmtpPassword: GetString(smtp, "smtp", "password"),
            AmazonRefreshToken: GetString(amazon, "amazon", "refresh_token"),
            AmazonDeviceSerial: GetString(amazon, "amazon", "device_serial"));
    }

    public static void WriteSecrets(string path, Secrets s)
    {
        var root = new TomlTable();

        var amazon = new TomlTable();
        AddIfPresent(amazon, "refresh_token", s.AmazonRefreshToken);
        AddIfPresent(amazon, "device_serial", s.AmazonDeviceSerial);
        AddIfPresent(amazon, "session_id", s.AmazonSessionId);
        AddIfPresent(amazon, "ubid_main", s.AmazonUbid);
        AddIfPresent(amazon, "at_main", s.AmazonAt);
        if (amazon.Count > 0)
        {
            root["amazon"] = amazon;
        }

        var smtp = new TomlTable();
        AddIfPresent(smtp, "password", s.SmtpPassword);
        if (smtp.Count > 0)
        {
            root["smtp"] = smtp;
        }

        var text = TomlSerializer.Serialize(root);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create the file already locked down, and tighten an existing looser file before any
        // secret is written, so the contents are never readable by other users.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = _secretsMode;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, _secretsMode);
            }

            writer.Write(text);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, _secretsMode);
        }
    }

    private static TomlTable ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Config file not found: {path}");
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"Could not read {path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ConfigException($"Could not read {path}: {ex.Message}", ex);
        }

        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text)
                ?? throw new ConfigException($"Invalid TOML in {path}: the document is empty.");
        }
        catch (TomlException ex)
        {
            throw new ConfigException($"Invalid TOML in {path}: {ex.Message}", ex);
        }
    }

    private static SourceConfig ReadSource(TomlTable? source)
    {
        var modeText = GetString(source, "source", "mode") ?? "album";
        SourceMode mode;
        if (modeText.Equals("album", StringComparison.OrdinalIgnoreCase))
        {
            mode = SourceMode.Album;
        }
        else if (modeText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            mode = SourceMode.All;
        }
        else
        {
            throw new ConfigException($"[source] mode must be \"album\" or \"all\", got \"{modeText}\".");
        }

        var albumName = GetString(source, "source", "album_name");
        if (mode == SourceMode.Album && string.IsNullOrWhiteSpace(albumName))
        {
            throw new ConfigException("[source] album_name is required when mode = \"album\".");
        }

        // The default depends on the mode and applies only when the key is absent.
        var lookbackDays = GetInt(source, "source", "lookback_days") ?? (mode == SourceMode.Album ? 0 : 30);
        if (lookbackDays < 0)
        {
            throw new ConfigException("[source] lookback_days must not be negative.");
        }

        if (mode == SourceMode.All && lookbackDays == 0)
        {
            throw new ConfigException(
                "[source] lookback_days = 0 is not allowed when mode = \"all\"; it would select every photo in the library.");
        }

        var includeVideos = GetBool(source, "source", "include_videos") ?? false;

        return new SourceConfig(mode, albumName, lookbackDays, includeVideos);
    }

    private static TomlTable? Table(TomlTable root, string name)
    {
        if (!root.TryGetValue(name, out var value))
        {
            return null;
        }

        return value as TomlTable ?? throw new ConfigException($"[{name}] must be a table.");
    }

    private static string? GetString(TomlTable? table, string section, string key)
    {
        if (table is null || !table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value as string ?? throw new ConfigException($"[{section}] {key} must be a string.");
    }

    private static string? GetStringEither(TomlTable? table, string section, string underscoreKey, string dashKey)
    {
        return GetString(table, section, underscoreKey) ?? GetString(table, section, dashKey);
    }

    private static string RequireString(TomlTable? table, string section, string key)
    {
        var value = GetString(table, section, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ConfigException($"[{section}] {key} is required.");
        }

        return value;
    }

    private static long? GetLong(TomlTable? table, string section, string key)
    {
        if (table is null || !table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value as long? ?? throw new ConfigException($"[{section}] {key} must be an integer.");
    }

    private static int? GetInt(TomlTable? table, string section, string key)
    {
        var value = GetLong(table, section, key);
        if (value is null)
        {
            return null;
        }

        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new ConfigException($"[{section}] {key} is out of range.");
        }

        return (int)value;
    }

    private static bool? GetBool(TomlTable? table, string section, string key)
    {
        if (table is null || !table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value as bool? ?? throw new ConfigException($"[{section}] {key} must be true or false.");
    }

    private static IReadOnlyList<string> RequireStringList(TomlTable? table, string section, string key)
    {
        if (table is null || !table.TryGetValue(key, out var value))
        {
            throw new ConfigException($"[{section}] {key} is required.");
        }

        if (value is not TomlArray array)
        {
            throw new ConfigException($"[{section}] {key} must be an array of strings.");
        }

        var items = new List<string>(array.Count);
        foreach (var element in array)
        {
            if (element is not string text || string.IsNullOrWhiteSpace(text))
            {
                throw new ConfigException($"[{section}] {key} must be an array of non-empty strings.");
            }

            items.Add(text);
        }

        if (items.Count == 0)
        {
            throw new ConfigException($"[{section}] {key} must list at least one address.");
        }

        return items;
    }

    private static void AddIfPresent(TomlTable table, string key, string? value)
    {
        if (value is not null)
        {
            table[key] = value;
        }
    }
}
