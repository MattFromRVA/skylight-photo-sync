namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>Locates the fixture files copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Image(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);
}
