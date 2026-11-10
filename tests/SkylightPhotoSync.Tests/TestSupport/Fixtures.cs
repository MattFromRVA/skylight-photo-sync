namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>Locates the fixture files copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Image(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    /// <summary>
    /// A recorded, redacted Amazon Drive v1 response body (<c>Fixtures/Amazon</c>).
    /// </summary>
    public static string Amazon(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Amazon", name);

    /// <summary>
    /// A device-login response (<c>Fixtures/AmazonAuth</c>): <c>exchange_cookies.json</c>, the
    /// redacted recording of the cookie exchange (top-level <c>_recorded</c>), and the hand-written
    /// <c>placeholder_*.json</c> register, token and deregister shapes from issue #30 (the register
    /// step runs once per login and cannot be replayed), each carrying a top-level <c>_placeholder</c>.
    /// </summary>
    public static string AmazonAuth(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AmazonAuth", name);
}
