namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>Locates the fixture files copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Image(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    /// <summary>A recorded, redacted Amazon response body (<c>Fixtures/Amazon</c>).</summary>
    public static string Amazon(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Amazon", name);
}
