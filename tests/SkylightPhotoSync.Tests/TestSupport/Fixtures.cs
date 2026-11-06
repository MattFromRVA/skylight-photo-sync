namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>Locates the fixture files copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Image(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    /// <summary>A hand-written (<c>placeholder_*.json</c>) or recorded Amazon response body.</summary>
    public static string Amazon(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Amazon", name);
}
