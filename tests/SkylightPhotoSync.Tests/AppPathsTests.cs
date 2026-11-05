using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class AppPathsTests
{
    [Fact]
    public void Paths_honor_env()
    {
        using var home = new TestHome();

        var paths = AppPaths.Resolve();

        Assert.Equal(Path.Combine(home.Dir, "config.toml"), paths.ConfigFile);
        Assert.Equal(Path.Combine(home.Dir, "secrets.toml"), paths.SecretsFile);
        Assert.Equal(Path.Combine(home.Dir, "state.db"), paths.StateDb);
        Assert.Equal(Path.Combine(home.Dir, "run.lock"), paths.LockFile);
        Assert.Equal(Path.Combine(home.Dir, "sync.log"), paths.LogFile);
    }

    [Fact]
    public void Paths_default_to_mac_locations_when_env_is_unset()
    {
        var previous = Environment.GetEnvironmentVariable(TestHome.EnvVar);
        Environment.SetEnvironmentVariable(TestHome.EnvVar, null);
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = AppPaths.Resolve();

            Assert.Equal(Path.Combine(userHome, ".config", "skylight-photo-sync", "config.toml"), paths.ConfigFile);
            Assert.Equal(Path.Combine(userHome, ".config", "skylight-photo-sync", "secrets.toml"), paths.SecretsFile);
            Assert.Equal(Path.Combine(userHome, "Library", "Application Support", "skylight-photo-sync", "state.db"), paths.StateDb);
            Assert.Equal(Path.Combine(userHome, "Library", "Application Support", "skylight-photo-sync", "run.lock"), paths.LockFile);
            Assert.Equal(Path.Combine(userHome, "Library", "Logs", "skylight-photo-sync", "sync.log"), paths.LogFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestHome.EnvVar, previous);
        }
    }
}
