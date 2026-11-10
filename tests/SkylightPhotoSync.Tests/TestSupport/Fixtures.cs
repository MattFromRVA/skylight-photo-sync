namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>Locates the fixture files copied next to the test assembly.</summary>
public static class Fixtures
{
    public static string Image(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name);

    /// <summary>
    /// A recorded, redacted Amazon response body (<c>Fixtures/Amazon</c>): the Drive v1 pages and
    /// <c>exchange_cookies.json</c>, the device login's cookie exchange.
    /// </summary>
    public static string Amazon(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Amazon", name);

    /// <summary>
    /// A hand-written placeholder for a device-login response (<c>Fixtures/AmazonAuth</c>): the
    /// register, token and deregister shapes from issue #30, not recordings (the register step runs
    /// once per login and cannot be replayed), every one carrying a top-level <c>_placeholder</c>.
    /// </summary>
    public static string AmazonAuth(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AmazonAuth", name);
}
