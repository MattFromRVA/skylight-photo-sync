namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>A throwaway directory that is deleted when the test disposes it.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "skylight-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// Copies a fixture image to <paramref name="relativePath"/> under this directory and stamps
    /// the copy with <paramref name="lastWriteUtc"/>. Returns the full path.
    /// </summary>
    public string AddFixture(string fixtureName, string relativePath, DateTime lastWriteUtc)
    {
        var dest = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.Copy(Fixtures.Image(fixtureName), dest);
        File.SetLastWriteTimeUtc(dest, lastWriteUtc);
        return dest;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
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
