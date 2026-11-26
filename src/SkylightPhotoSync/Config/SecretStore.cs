namespace SkylightPhotoSync.Config;

/// <summary>One value in <see cref="Secrets"/>; each is a separate Keychain item.</summary>
public enum SecretKey
{
    AmazonRefreshToken,
    AmazonDeviceSerial,
    AmazonSessionId,
    AmazonUbid,
    AmazonAt,
    SmtpPassword,
    SkylightEmail,
    SkylightPassword,
}

/// <summary>
/// Where <see cref="Secrets"/> live (issue #15). With a Keychain, each value is its own item
/// (<see cref="ServiceFor"/>) and the Keychain wins over secrets.toml key by key; the file only fills
/// in values the Keychain lacks. Saving writes every value to the Keychain, removes the ones that are
/// now absent, and then deletes secrets.toml, so a save also moves anything left in the file. Without
/// a Keychain (not macOS, or tests) the 0600 secrets.toml is read and written as before. If the
/// Keychain cannot be read, the file is used instead (<see cref="KeychainProblem"/> says why); with
/// no file that is a <see cref="ConfigException"/>, as is any failed Keychain write.
/// </summary>
public sealed class SecretStore
{
    private const string _servicePrefix = "skylight-photo-sync.";

    private readonly string _secretsFile;
    private readonly IKeychain? _keychain;

    public SecretStore(string secretsFile, IKeychain? keychain)
    {
        _secretsFile = secretsFile;
        _keychain = keychain;
    }

    /// <summary>True when secrets are stored in the Keychain rather than secrets.toml.</summary>
    public bool UsesKeychain => _keychain is not null;

    /// <summary>Why the last <see cref="LoadAsync"/> fell back to secrets.toml, or null when it did not.</summary>
    public string? KeychainProblem { get; private set; }

    /// <summary>The Keychain service name of one secret, e.g. <c>skylight-photo-sync.smtp.password</c>.</summary>
    public static string ServiceFor(SecretKey key) => _servicePrefix + key switch
    {
        SecretKey.AmazonRefreshToken => "amazon.refresh_token",
        SecretKey.AmazonDeviceSerial => "amazon.device_serial",
        SecretKey.AmazonSessionId => "amazon.session_id",
        SecretKey.AmazonUbid => "amazon.ubid_main",
        SecretKey.AmazonAt => "amazon.at_main",
        SecretKey.SmtpPassword => "smtp.password",
        SecretKey.SkylightEmail => "skylight.email",
        SecretKey.SkylightPassword => "skylight.password",
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>
    /// The secrets, Keychain first and secrets.toml for the rest; all-empty when neither has any,
    /// so a dry run against a local folder works before login. A present but unreadable file fails loudly.
    /// </summary>
    public async Task<Secrets> LoadAsync(CancellationToken ct)
    {
        KeychainProblem = null;
        var fileExists = File.Exists(_secretsFile);
        var file = fileExists ? ConfigLoader.LoadSecrets(_secretsFile) : Empty;
        if (_keychain is null)
        {
            return file;
        }

        var fromKeychain = new Dictionary<SecretKey, string>();
        try
        {
            foreach (var key in Enum.GetValues<SecretKey>())
            {
                if (await _keychain.FindAsync(ServiceFor(key), ct) is { } value)
                {
                    fromKeychain[key] = value;
                }
            }
        }
        catch (KeychainException ex)
        {
            if (!fileExists)
            {
                throw new ConfigException(
                    $"Could not read secrets from the macOS Keychain: {ex.Message} Unlock the login keychain, or put the secrets in {_secretsFile}.",
                    ex);
            }

            KeychainProblem = ex.Message;
            return file;
        }

        return With(file, key => fromKeychain.TryGetValue(key, out var value) ? value : Get(file, key));
    }

    /// <summary>Stores exactly <paramref name="secrets"/>: null values are removed.</summary>
    public async Task SaveAsync(Secrets secrets, CancellationToken ct)
    {
        if (_keychain is null)
        {
            ConfigLoader.WriteSecrets(_secretsFile, secrets);
            return;
        }

        try
        {
            foreach (var key in Enum.GetValues<SecretKey>())
            {
                if (Get(secrets, key) is { } value)
                {
                    await _keychain.SetAsync(ServiceFor(key), value, ct);
                }
                else
                {
                    await _keychain.DeleteAsync(ServiceFor(key), ct);
                }
            }
        }
        catch (KeychainException ex)
        {
            throw new ConfigException($"Could not save secrets to the macOS Keychain: {ex.Message}", ex);
        }

        // Everything the file held is in the secrets just saved (they were loaded with it), so it can go.
        if (File.Exists(_secretsFile))
        {
            File.Delete(_secretsFile);
        }
    }

    /// <summary>
    /// <c>import-secrets</c>: moves every value in secrets.toml into the Keychain, replacing what is
    /// there (the file is the one the user just edited), then deletes the file. Returns how many values moved.
    /// </summary>
    public async Task<int> ImportFileAsync(CancellationToken ct)
    {
        if (_keychain is null)
        {
            throw new ConfigException("The macOS Keychain is not available here; secrets stay in secrets.toml.");
        }

        if (!File.Exists(_secretsFile))
        {
            return 0;
        }

        var file = ConfigLoader.LoadSecrets(_secretsFile);
        var count = Enum.GetValues<SecretKey>().Count(key => Get(file, key) is not null);
        if (count == 0)
        {
            return 0;
        }

        var current = await LoadAsync(ct);
        if (KeychainProblem is not null)
        {
            throw new ConfigException($"Could not read the macOS Keychain: {KeychainProblem}");
        }

        await SaveAsync(With(current, key => Get(file, key) ?? Get(current, key)), ct);
        return count;
    }

    private static Secrets Empty => new(null, null, null, null);

    private static string? Get(Secrets s, SecretKey key) => key switch
    {
        SecretKey.AmazonRefreshToken => s.AmazonRefreshToken,
        SecretKey.AmazonDeviceSerial => s.AmazonDeviceSerial,
        SecretKey.AmazonSessionId => s.AmazonSessionId,
        SecretKey.AmazonUbid => s.AmazonUbid,
        SecretKey.AmazonAt => s.AmazonAt,
        SecretKey.SmtpPassword => s.SmtpPassword,
        SecretKey.SkylightEmail => s.SkylightEmail,
        SecretKey.SkylightPassword => s.SkylightPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static Secrets With(Secrets s, Func<SecretKey, string?> value) => s with
    {
        AmazonRefreshToken = value(SecretKey.AmazonRefreshToken),
        AmazonDeviceSerial = value(SecretKey.AmazonDeviceSerial),
        AmazonSessionId = value(SecretKey.AmazonSessionId),
        AmazonUbid = value(SecretKey.AmazonUbid),
        AmazonAt = value(SecretKey.AmazonAt),
        SmtpPassword = value(SecretKey.SmtpPassword),
        SkylightEmail = value(SecretKey.SkylightEmail),
        SkylightPassword = value(SecretKey.SkylightPassword),
    };
}
