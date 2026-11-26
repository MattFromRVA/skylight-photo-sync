using SkylightPhotoSync.Config;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeKeychain _keychain = new();

    private string SecretsFile => Path.Combine(_dir.Path, "secrets.toml");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Without_a_keychain_reads_the_file()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "smtp-pw"));

        var secrets = await new SecretStore(SecretsFile, keychain: null).LoadAsync(CancellationToken.None);

        Assert.Equal("smtp-pw", secrets.SmtpPassword);
    }

    [Fact]
    public async Task Nothing_anywhere_reads_as_empty_secrets()
    {
        var secrets = await new SecretStore(SecretsFile, _keychain).LoadAsync(CancellationToken.None);

        Assert.Equal(new Secrets(null, null, null, null), secrets);
    }

    [Fact]
    public async Task Keychain_wins_per_key_and_the_file_fills_the_gaps()
    {
        ConfigLoader.WriteSecrets(
            SecretsFile,
            new Secrets(null, null, null, "file-smtp", SkylightEmail: "file@example.com", SkylightPassword: "file-sky"));
        _keychain.Items[SecretStore.ServiceFor(SecretKey.SmtpPassword)] = "keychain-smtp";
        _keychain.Items[SecretStore.ServiceFor(SecretKey.AmazonRefreshToken)] = "keychain-refresh";

        var secrets = await new SecretStore(SecretsFile, _keychain).LoadAsync(CancellationToken.None);

        Assert.Equal("keychain-smtp", secrets.SmtpPassword);
        Assert.Equal("keychain-refresh", secrets.AmazonRefreshToken);
        Assert.Equal("file@example.com", secrets.SkylightEmail);
        Assert.Equal("file-sky", secrets.SkylightPassword);
    }

    [Fact]
    public async Task Every_secret_has_its_own_keychain_service()
    {
        var all = new Secrets("sid", "ubid", "at", "smtp", "refresh", "serial", "me@example.com", "sky");

        await new SecretStore(SecretsFile, _keychain).SaveAsync(all, CancellationToken.None);

        Assert.Equal(8, _keychain.Items.Count);
        Assert.All(_keychain.Items.Keys, service => Assert.StartsWith("skylight-photo-sync.", service));
        Assert.Equal(all, await new SecretStore(SecretsFile, _keychain).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saving_to_the_keychain_moves_secrets_out_of_the_file()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets("sid", "ubid", "at", "smtp"));
        _keychain.Items[SecretStore.ServiceFor(SecretKey.AmazonSessionId)] = "old-sid";
        var store = new SecretStore(SecretsFile, _keychain);

        await store.SaveAsync(new Secrets(null, null, null, "smtp", AmazonRefreshToken: "refresh"), CancellationToken.None);

        Assert.False(File.Exists(SecretsFile));
        Assert.Equal("smtp", _keychain.Items[SecretStore.ServiceFor(SecretKey.SmtpPassword)]);
        Assert.Equal("refresh", _keychain.Items[SecretStore.ServiceFor(SecretKey.AmazonRefreshToken)]);
        Assert.False(_keychain.Items.ContainsKey(SecretStore.ServiceFor(SecretKey.AmazonSessionId)));
    }

    [Fact]
    public async Task Without_a_keychain_saving_writes_the_0600_file()
    {
        await new SecretStore(SecretsFile, keychain: null)
            .SaveAsync(new Secrets(null, null, null, "smtp"), CancellationToken.None);

        Assert.Equal("smtp", ConfigLoader.LoadSecrets(SecretsFile).SmtpPassword);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SecretsFile));
    }

    [Fact]
    public async Task An_unreadable_keychain_falls_back_to_the_file()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "smtp"));
        _keychain.FailWith = "The user interaction is not allowed.";
        var store = new SecretStore(SecretsFile, _keychain);

        var secrets = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("smtp", secrets.SmtpPassword);
        Assert.Equal("The user interaction is not allowed.", store.KeychainProblem);
    }

    [Fact]
    public async Task An_unreadable_keychain_with_no_file_is_a_config_error()
    {
        _keychain.FailWith = "The user interaction is not allowed.";

        var ex = await Assert.ThrowsAsync<ConfigException>(
            () => new SecretStore(SecretsFile, _keychain).LoadAsync(CancellationToken.None));

        Assert.Contains("Keychain", ex.Message);
        Assert.Contains("The user interaction is not allowed.", ex.Message);
    }

    [Fact]
    public async Task A_failed_keychain_write_is_a_config_error_and_keeps_the_file()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "smtp"));
        var before = File.ReadAllBytes(SecretsFile);
        _keychain.FailWith = "write refused";

        var ex = await Assert.ThrowsAsync<ConfigException>(
            () => new SecretStore(SecretsFile, _keychain).SaveAsync(new Secrets(null, null, null, "new"), CancellationToken.None));

        Assert.Contains("write refused", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(SecretsFile));
    }

    [Fact]
    public async Task Import_moves_file_values_over_keychain_values()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "new-smtp"));
        _keychain.Items[SecretStore.ServiceFor(SecretKey.SmtpPassword)] = "old-smtp";
        _keychain.Items[SecretStore.ServiceFor(SecretKey.AmazonRefreshToken)] = "refresh";

        var moved = await new SecretStore(SecretsFile, _keychain).ImportFileAsync(CancellationToken.None);

        Assert.Equal(1, moved);
        Assert.False(File.Exists(SecretsFile));
        Assert.Equal("new-smtp", _keychain.Items[SecretStore.ServiceFor(SecretKey.SmtpPassword)]);
        Assert.Equal("refresh", _keychain.Items[SecretStore.ServiceFor(SecretKey.AmazonRefreshToken)]);
    }

    [Fact]
    public async Task Import_without_a_keychain_is_a_config_error()
    {
        ConfigLoader.WriteSecrets(SecretsFile, new Secrets(null, null, null, "smtp"));

        await Assert.ThrowsAsync<ConfigException>(
            () => new SecretStore(SecretsFile, keychain: null).ImportFileAsync(CancellationToken.None));

        Assert.True(File.Exists(SecretsFile));
    }
}
