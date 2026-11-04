namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>
/// Creates a temporary directory and points <c>SKYLIGHT_SYNC_HOME</c> at it for the lifetime
/// of the test. Disposing restores the previous value and deletes the directory.
/// </summary>
public sealed class TestHome : IDisposable
{
    public const string EnvVar = "SKYLIGHT_SYNC_HOME";

    private readonly string? _previous;

    public TestHome()
    {
        Dir = Path.Combine(Path.GetTempPath(), "skylight-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        _previous = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, Dir);
    }

    public string Dir { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _previous);
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: never fail a test over temp cleanup.
        }
    }
}
