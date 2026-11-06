using SkylightPhotoSync.Config;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class ConfigLoaderTests
{
    private const string _restOfConfig = """

        [email]
        to = ["household@ourskylight.com"]
        from = "me@example.com"

        [smtp]
        host = "smtp.example.com"
        username = "me@example.com"

        [alerts]
        email = "me@example.com"
        """;

    [Fact]
    public void Album_mode_defaults_lookback_to_zero()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            album_name = "Skylight"
            """);

        var config = ConfigLoader.LoadConfig(path);

        Assert.Equal(SourceMode.Album, config.Source.Mode);
        Assert.Equal("Skylight", config.Source.AlbumName);
        Assert.Equal(0, config.Source.LookbackDays);
        Assert.False(config.Source.IncludeVideos);
    }

    [Fact]
    public void All_mode_defaults_lookback_to_thirty()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "all"
            """);

        var config = ConfigLoader.LoadConfig(path);

        Assert.Equal(SourceMode.All, config.Source.Mode);
        Assert.Null(config.Source.AlbumName);
        Assert.Equal(30, config.Source.LookbackDays);
    }

    [Fact]
    public void All_mode_rejects_lookback_zero()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "all"
            lookback_days = 0
            """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadConfig(path));

        Assert.Contains("lookback_days", ex.Message);
    }

    [Fact]
    public void Album_mode_requires_album_name()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            """);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadConfig(path));

        Assert.Contains("album_name", ex.Message);
    }

    [Fact]
    public void Explicit_values_override_defaults()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            album_name = "Skylight"
            lookback_days = 7
            include_videos = true

            [limits]
            max_items_per_run = 5

            [image]
            max_long_edge = 1024
            jpeg_quality = 70

            [email]
            to = ["a@ourskylight.com", "b@ourskylight.com"]
            from = "me@example.com"
            max_attachments = 4
            max_total_bytes = 5000000

            [smtp]
            host = "smtp.example.com"
            port = 465
            username = "me@example.com"

            [alerts]
            email = "alerts@example.com"

            [schedule]
            interval_minutes = 15
            """, appendRest: false);

        var config = ConfigLoader.LoadConfig(path);

        Assert.Equal(7, config.Source.LookbackDays);
        Assert.True(config.Source.IncludeVideos);
        Assert.Equal(5, config.Limits.MaxItemsPerRun);
        Assert.Equal(1024, config.Image.MaxLongEdge);
        Assert.Equal(70, config.Image.JpegQuality);
        Assert.Equal(["a@ourskylight.com", "b@ourskylight.com"], config.Email.To);
        Assert.Equal("me@example.com", config.Email.From);
        Assert.Equal(4, config.Email.MaxAttachments);
        Assert.Equal(5_000_000L, config.Email.MaxTotalBytes);
        Assert.Equal("smtp.example.com", config.Smtp.Host);
        Assert.Equal(465, config.Smtp.Port);
        Assert.Equal("me@example.com", config.Smtp.Username);
        Assert.Equal("alerts@example.com", config.Alerts.Email);
        Assert.Equal(15, config.Schedule.IntervalMinutes);
    }

    [Fact]
    public void Omitted_keys_use_spec_defaults()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            album_name = "Skylight"
            """);

        var config = ConfigLoader.LoadConfig(path);

        Assert.Equal(25, config.Limits.MaxItemsPerRun);
        Assert.Equal(2048, config.Image.MaxLongEdge);
        Assert.Equal(88, config.Image.JpegQuality);
        Assert.Equal(12, config.Email.MaxAttachments);
        Assert.Equal(17_000_000L, config.Email.MaxTotalBytes);
        Assert.Equal(587, config.Smtp.Port);
        Assert.Equal(60, config.Schedule.IntervalMinutes);
    }

    [Fact]
    public void Alerts_email_defaults_to_smtp_username_when_section_is_omitted()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            album_name = "Skylight"

            [email]
            to = ["household@ourskylight.com"]
            from = "me@example.com"

            [smtp]
            host = "smtp.example.com"
            username = "smtp-user@example.com"
            """, appendRest: false);

        var config = ConfigLoader.LoadConfig(path);

        Assert.Equal("smtp-user@example.com", config.Alerts.Email);
    }

    [Fact]
    public void Alerts_email_defaults_to_smtp_username_when_key_is_omitted()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            album_name = "Skylight"

            [email]
            to = ["household@ourskylight.com"]
            from = "me@example.com"

            [smtp]
            host = "smtp.example.com"
            username = "smtp-user@example.com"

            [alerts]
            """, appendRest: false);

        var config = ConfigLoader.LoadConfig(path);

        Assert.Equal("smtp-user@example.com", config.Alerts.Email);
    }

    [Fact]
    public void Missing_email_recipients_throw()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source]
            mode = "album"
            album_name = "Skylight"

            [email]
            from = "me@example.com"

            [smtp]
            host = "smtp.example.com"
            username = "me@example.com"

            [alerts]
            email = "me@example.com"
            """, appendRest: false);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadConfig(path));

        Assert.Contains("[email] to", ex.Message);
    }

    [Fact]
    public void Invalid_toml_throws_config_exception()
    {
        using var home = new TestHome();
        var path = WriteConfig(home, """
            [source
            mode = "album"
            """);

        Assert.Throws<ConfigException>(() => ConfigLoader.LoadConfig(path));
    }

    [Fact]
    public void Secrets_file_must_be_0600()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");
        File.WriteAllText(path, """
            [smtp]
            password = "not-a-real-password"
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadSecrets(path));

        Assert.Contains("0600", ex.Message);
    }

    [Fact]
    public void WriteSecrets_sets_0600()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");
        var secrets = new Secrets("sid-placeholder", "ubid-placeholder", "at-placeholder", "smtp-placeholder");

        ConfigLoader.WriteSecrets(path, secrets);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(secrets, ConfigLoader.LoadSecrets(path));
    }

    [Fact]
    public void WriteSecrets_tightens_existing_loose_file()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");
        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var secrets = new Secrets("sid-placeholder", "ubid-placeholder", "at-placeholder", "smtp-placeholder");

        ConfigLoader.WriteSecrets(path, secrets);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(secrets, ConfigLoader.LoadSecrets(path));
    }

    [Fact]
    public void WriteSecrets_failure_leaves_previous_file_intact()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");
        ConfigLoader.WriteSecrets(path, new Secrets("sid-placeholder", "ubid-placeholder", "at-placeholder", "smtp-placeholder"));
        var before = File.ReadAllBytes(path);
        var homeMode = File.GetUnixFileMode(home.Dir);
        File.SetUnixFileMode(home.Dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                ConfigLoader.WriteSecrets(path, new Secrets(null, null, null, null, AmazonRefreshToken: "refresh-placeholder")));

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(["secrets.toml"], Directory.GetFiles(home.Dir).Select(Path.GetFileName));
        }
        finally
        {
            File.SetUnixFileMode(home.Dir, homeMode);
        }
    }

    [Fact]
    public void WriteSecrets_to_a_directory_path_fails_and_leaves_no_temp_file()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<IOException>(() => ConfigLoader.WriteSecrets(path, new Secrets(null, null, null, "smtp-placeholder")));

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(home.Dir));
    }

    [Fact]
    public void WriteSecrets_omits_null_values()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");

        ConfigLoader.WriteSecrets(path, new Secrets(null, null, null, "smtp-placeholder"));

        var loaded = ConfigLoader.LoadSecrets(path);
        Assert.Null(loaded.AmazonSessionId);
        Assert.Null(loaded.AmazonUbid);
        Assert.Null(loaded.AmazonAt);
        Assert.Equal("smtp-placeholder", loaded.SmtpPassword);
    }

    [Fact]
    public void Accepts_dash_and_underscore_cookie_keys()
    {
        using var home = new TestHome();
        var dashed = WriteSecretsFile(home, "dashed.toml", """
            [amazon]
            session-id = "sid-placeholder"
            ubid-main = "ubid-placeholder"
            at-main = "at-placeholder"
            """);
        var underscored = WriteSecretsFile(home, "underscored.toml", """
            [amazon]
            session_id = "sid-placeholder"
            ubid_main = "ubid-placeholder"
            at_main = "at-placeholder"
            """);

        var fromDashed = ConfigLoader.LoadSecrets(dashed);
        var fromUnderscored = ConfigLoader.LoadSecrets(underscored);

        Assert.Equal("at-placeholder", fromDashed.AmazonAt);
        Assert.Equal("sid-placeholder", fromDashed.AmazonSessionId);
        Assert.Equal("ubid-placeholder", fromDashed.AmazonUbid);
        Assert.Equal(fromDashed, fromUnderscored);
    }

    [Fact]
    public void Secrets_roundtrip_refresh_token()
    {
        using var home = new TestHome();
        var path = Path.Combine(home.Dir, "secrets.toml");
        var secrets = new Secrets(
            AmazonSessionId: null,
            AmazonUbid: null,
            AmazonAt: null,
            SmtpPassword: "smtp-placeholder",
            AmazonRefreshToken: "Atnr|PLACEHOLDER_REFRESH_TOKEN_NOT_REAL",
            AmazonDeviceSerial: "ABCDEFGHIJ0123456789KLMNOPQRST0123456789");

        ConfigLoader.WriteSecrets(path, secrets);

        Assert.Equal(secrets, ConfigLoader.LoadSecrets(path));
        var text = File.ReadAllText(path);
        Assert.Contains("[amazon]", text);
        Assert.Contains("refresh_token = ", text);
        Assert.Contains("device_serial = ", text);
        Assert.DoesNotContain("session_id", text);
        Assert.DoesNotContain("at_main", text);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Secrets_without_any_amazon_keys_load_with_nulls()
    {
        using var home = new TestHome();
        var path = WriteSecretsFile(home, "secrets.toml", """
            [smtp]
            password = "smtp-placeholder"
            """);

        var loaded = ConfigLoader.LoadSecrets(path);

        Assert.Null(loaded.AmazonRefreshToken);
        Assert.Null(loaded.AmazonDeviceSerial);
        Assert.Null(loaded.AmazonAt);
        Assert.Equal("smtp-placeholder", loaded.SmtpPassword);
    }

    private static string WriteConfig(TestHome home, string sourceSection, bool appendRest = true)
    {
        var path = Path.Combine(home.Dir, "config.toml");
        File.WriteAllText(path, appendRest ? sourceSection + _restOfConfig : sourceSection);
        return path;
    }

    private static string WriteSecretsFile(TestHome home, string name, string contents)
    {
        var path = Path.Combine(home.Dir, name);
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}
