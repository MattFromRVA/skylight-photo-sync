namespace SkylightPhotoSync;

/// <summary>
/// Locations of the tool's files. Every path lives under <c>SKYLIGHT_SYNC_HOME</c> when that
/// variable is set (tests point it at a temp directory); otherwise the standard macOS places.
/// </summary>
public sealed record AppPaths(string ConfigFile, string SecretsFile, string StateDb, string LockFile, string LogFile)
{
    public const string HomeEnvVar = "SKYLIGHT_SYNC_HOME";

    private const string _appDirName = "skylight-photo-sync";

    /// <summary>The cached Skylight API tokens (mode 0600), next to <see cref="StateDb"/>.</summary>
    public string SkylightTokenFile =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(StateDb)) ?? ".", "skylight-token.json");

    public static AppPaths Resolve()
    {
        var overrideHome = Environment.GetEnvironmentVariable(HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            return new AppPaths(
                ConfigFile: Path.Combine(overrideHome, "config.toml"),
                SecretsFile: Path.Combine(overrideHome, "secrets.toml"),
                StateDb: Path.Combine(overrideHome, "state.db"),
                LockFile: Path.Combine(overrideHome, "run.lock"),
                LogFile: Path.Combine(overrideHome, "sync.log"));
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(userHome, ".config", _appDirName);
        var stateDir = Path.Combine(userHome, "Library", "Application Support", _appDirName);
        var logDir = Path.Combine(userHome, "Library", "Logs", _appDirName);

        return new AppPaths(
            ConfigFile: Path.Combine(configDir, "config.toml"),
            SecretsFile: Path.Combine(configDir, "secrets.toml"),
            StateDb: Path.Combine(stateDir, "state.db"),
            LockFile: Path.Combine(stateDir, "run.lock"),
            LogFile: Path.Combine(logDir, "sync.log"));
    }
}
