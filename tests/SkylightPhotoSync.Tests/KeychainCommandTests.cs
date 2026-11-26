using SkylightPhotoSync.Commands;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// login, logout, run, status and import-secrets with secrets in a <see cref="FakeKeychain"/>
/// (issue #15). Never the real Keychain: <see cref="CommandSupport.KeychainFactory"/> supplies the fake.
/// </summary>
public sealed class KeychainCommandTests : IDisposable
{
    private const string _sessionId = "133-0000000-0000000";
    private const string _ubid = "130-0000000-1111111";
    private const string _at = "Atza|TESTVALUEONLYxxxx";
    private const string _refresh = "Atnr|PLACEHOLDER_REFRESH_TOKEN_NOT_REAL";
    private const string _serial = "ABCDEFGHIJ0123456789KLMNOPQRST0123456789";
    private const string _code = "ANPlaceholderAuthorizationCode";
    private const string _landing = "https://www.amazon.com/ap/maplanding?openid.assoc_handle=amzn_audible_ios_us&openid.oa2.authorization_code=" + _code + "&openid.mode=id_res";

    private readonly TestHome _home = new();
    private readonly TextReader _originalStdin = Console.In;
    private readonly ScriptedHandler _amazon = new ScriptedHandler().WithAmazonDefaults().WithDeviceAuthDefaults();
    private readonly FakeKeychain _keychain = new();

    public KeychainCommandTests()
    {
        CommandSupport.AmazonHandlerFactory = () => _amazon;
        CommandSupport.KeychainFactory = () => _keychain;
    }

    private string SecretsFile => Path.Combine(_home.Dir, "secrets.toml");

    public void Dispose()
    {
        CommandSupport.AmazonHandlerFactory = null;
        CommandSupport.KeychainFactory = null;
        Console.SetIn(_originalStdin);
        _home.Dispose();
    }

    [Fact]
    public void The_override_home_never_uses_the_real_keychain()
    {
        CommandSupport.KeychainFactory = null;

        Assert.False(CommandSupport.CreateSecretStore(AppPaths.Resolve()).UsesKeychain);
    }

    [Fact]
    public async Task Login_writes_to_the_keychain_and_moves_the_file_secrets()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "app-password", SkylightEmail: "me@example.com", SkylightPassword: "sky-pw"));
        Console.SetIn(new StringReader(_landing + "\n"));

        var (code, out_, err) = await Run("login");

        Assert.Equal(0, code);
        Assert.False(File.Exists(SecretsFile));
        Assert.Equal(_refresh, Item(SecretKey.AmazonRefreshToken));
        Assert.Equal(_serial.Length, Item(SecretKey.AmazonDeviceSerial)!.Length);
        Assert.Equal("app-password", Item(SecretKey.SmtpPassword));
        Assert.Equal("me@example.com", Item(SecretKey.SkylightEmail));
        Assert.Equal("sky-pw", Item(SecretKey.SkylightPassword));
        Assert.Null(Item(SecretKey.AmazonSessionId));
        Assert.DoesNotContain(_refresh, out_ + err);
    }

    [Fact]
    public async Task Login_with_cookies_writes_to_the_keychain()
    {
        Console.SetIn(new StringReader($"{_sessionId}\n{_ubid}\n{_at}\n"));

        var (code, _, _) = await Run("login", "--cookies");

        Assert.Equal(0, code);
        Assert.Equal(_sessionId, Item(SecretKey.AmazonSessionId));
        Assert.Equal(_ubid, Item(SecretKey.AmazonUbid));
        Assert.Equal(_at, Item(SecretKey.AmazonAt));
        Assert.False(File.Exists(SecretsFile));
    }

    [Fact]
    public async Task Login_reports_a_keychain_that_refuses_the_write_and_keeps_the_file()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "app-password"));
        var before = File.ReadAllBytes(SecretsFile);
        Console.SetIn(new StringReader($"{_sessionId}\n{_ubid}\n{_at}\n"));
        CommandSupport.KeychainFactory = () => new RefusingWritesKeychain();

        var (code, out_, err) = await Run("login", "--cookies");

        Assert.Equal(1, code);
        Assert.Contains("login failed", err);
        Assert.Contains("Keychain", err);
        Assert.Equal(before, File.ReadAllBytes(SecretsFile));
        Assert.DoesNotContain(_at, out_ + err);
    }

    [Fact]
    public async Task Device_login_warns_and_keeps_the_file_when_the_keychain_refuses_every_write()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "app-password"));
        var before = File.ReadAllBytes(SecretsFile);
        Console.SetIn(new StringReader(_landing + "\n"));
        CommandSupport.KeychainFactory = () => new RefusingWritesKeychain();

        var (code, out_, err) = await Run("login");

        Assert.Equal(1, code);
        Assert.Contains("may not have been put back", err);
        Assert.Contains("removed the device again", err);
        Assert.Equal(before, File.ReadAllBytes(SecretsFile));
        Assert.DoesNotContain(_refresh, out_ + err);
    }

    [Fact]
    public async Task Logout_stops_before_deregistering_when_the_keychain_cannot_be_read()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, null, AmazonRefreshToken: _refresh, AmazonDeviceSerial: _serial));
        var before = File.ReadAllBytes(SecretsFile);
        Seed(SecretKey.SmtpPassword, "app-password");
        _keychain.FailWith = "The user interaction is not allowed.";

        var (code, out_, err) = await Run("logout");

        Assert.Equal(1, code);
        Assert.Contains("The user interaction is not allowed.", err);
        Assert.Empty(_amazon.RequestsTo("/auth/deregister"));
        Assert.Equal(before, File.ReadAllBytes(SecretsFile));
        Assert.Equal("app-password", Item(SecretKey.SmtpPassword));
        Assert.DoesNotContain(_refresh, out_ + err);
    }

    [Fact]
    public async Task Logout_removes_the_amazon_items_from_the_keychain()
    {
        Seed(SecretKey.AmazonRefreshToken, _refresh);
        Seed(SecretKey.AmazonDeviceSerial, _serial);
        Seed(SecretKey.SmtpPassword, "app-password");

        var (code, out_, _) = await Run("logout");

        Assert.Equal(0, code);
        Assert.Contains("Logged out of Amazon Photos", out_);
        Assert.Single(_amazon.RequestsTo("/auth/deregister"));
        Assert.Equal(["skylight-photo-sync.smtp.password"], _keychain.Items.Keys);
        using var store = new StateStore(Path.Combine(_home.Dir, "state.db"));
        Assert.True(store.NeedsLogin());
    }

    [Fact]
    public async Task Run_reads_the_login_from_the_keychain()
    {
        WriteConfig();
        Seed(SecretKey.AmazonRefreshToken, _refresh);
        Seed(SecretKey.AmazonDeviceSerial, _serial);

        var (code, out_, err) = await Run("run", "--dry-run", "--limit", "1");

        Assert.Equal(0, code);
        Assert.Contains("would send 1", out_);
        Assert.Single(_amazon.RequestsTo("/ap/exchangetoken/cookies"));
        Assert.DoesNotContain(_refresh, out_ + err);
    }

    [Fact]
    public async Task Status_says_where_secrets_are_kept()
    {
        Seed(SecretKey.AmazonRefreshToken, _refresh);

        var (code, out_, _) = await Run("status");

        Assert.Equal(0, code);
        Assert.Contains("secrets: macOS Keychain", out_);
        Assert.Contains("auth: device login", out_);
    }

    [Fact]
    public async Task Status_shows_the_file_fallback_when_the_keychain_cannot_be_read()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(_sessionId, _ubid, _at, null));
        _keychain.FailWith = "The user interaction is not allowed.";

        var (code, out_, _) = await Run("status");

        Assert.Equal(0, code);
        Assert.Contains("secrets: secrets.toml (Keychain unavailable: The user interaction is not allowed.)", out_);
        Assert.Contains("auth: pasted cookies", out_);
    }

    [Fact]
    public async Task Status_without_a_keychain_says_secrets_toml()
    {
        CommandSupport.KeychainFactory = null;

        var (_, out_, _) = await Run("status");

        Assert.Contains("secrets: secrets.toml", out_);
    }

    [Fact]
    public async Task Import_secrets_moves_the_file_into_the_keychain()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "new-password"));
        Seed(SecretKey.SmtpPassword, "old-password");

        var (code, out_, err) = await Run("import-secrets");

        Assert.Equal(0, code);
        Assert.Contains("Moved 1 secret", out_);
        Assert.False(File.Exists(SecretsFile));
        Assert.Equal("new-password", Item(SecretKey.SmtpPassword));
        Assert.DoesNotContain("new-password", out_ + err);
    }

    [Fact]
    public async Task Import_secrets_without_a_file_is_a_no_op()
    {
        var (code, out_, _) = await Run("import-secrets");

        Assert.Equal(0, code);
        Assert.Contains("nothing to import", out_);
    }

    [Fact]
    public async Task Import_secrets_without_a_keychain_fails()
    {
        CommandSupport.KeychainFactory = null;
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "pw"));

        var (code, _, err) = await Run("import-secrets");

        Assert.Equal(1, code);
        Assert.Contains("import-secrets failed", err);
        Assert.True(File.Exists(SecretsFile));
    }

    private string? Item(SecretKey key) =>
        _keychain.Items.TryGetValue(SecretStore.ServiceFor(key), out var value) ? value : null;

    private void Seed(SecretKey key, string value) => _keychain.Items[SecretStore.ServiceFor(key)] = value;

    private void WriteConfig() =>
        File.WriteAllText(
            Path.Combine(_home.Dir, "config.toml"),
            $"""
            [source]
            mode = "album"
            album_name = "{ScriptedHandler.AlbumName}"

            [email]
            to = ["frame@example.com"]
            from = "sync@example.com"

            [smtp]
            host = "127.0.0.1"
            port = 25
            username = "user"
            """);

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var out_ = new StringWriter();
        var err = new StringWriter();
        var code = await App.RunAsync(args, out_, err);
        return (code, out_.ToString(), err.ToString());
    }

    /// <summary>Reads fine (empty), refuses every write.</summary>
    private sealed class RefusingWritesKeychain : IKeychain
    {
        public Task<string?> FindAsync(string service, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SetAsync(string service, string value, CancellationToken ct) => throw new KeychainException("write refused");

        public Task DeleteAsync(string service, CancellationToken ct) => throw new KeychainException("write refused");
    }
}
