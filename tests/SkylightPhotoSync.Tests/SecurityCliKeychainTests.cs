using System.Diagnostics;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class SecurityCliKeychainTests
{
    [Fact]
    public void Parses_a_plain_password_line()
    {
        Assert.Equal("abcd", SecurityCliKeychain.ParsePassword("password: \"abcd\"\n"));
    }

    [Fact]
    public void Parses_a_hex_password_line_as_utf8()
    {
        // `security` prints anything that is not plain printable ASCII as hex, then a quoted rendering.
        Assert.Equal("pä😀", SecurityCliKeychain.ParsePassword("password: 0x70C3A4F09F9880  \"p\\303\\244\\360\\237\\230\\200\"\n"));
    }

    [Fact]
    public void An_unrecognised_password_line_is_an_error()
    {
        Assert.Throws<KeychainException>(() => SecurityCliKeychain.ParsePassword("something else"));
    }

    [Fact]
    public void Quotes_values_for_interactive_mode()
    {
        Assert.Equal("\"a b\\\"c\\\\d$x'e\"", SecurityCliKeychain.Quote("a b\"c\\d$x'e"));
    }

    [Fact]
    public async Task Refuses_a_value_with_a_line_break()
    {
        var keychain = new SecurityCliKeychain("/nonexistent.keychain-db");

        await Assert.ThrowsAsync<KeychainException>(() => keychain.SetAsync("svc", "a\nb", CancellationToken.None));
    }

    /// <summary>Uses a throwaway keychain file, never the login keychain.</summary>
    [MacOnlyFact]
    public async Task Round_trips_through_a_real_keychain()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "test.keychain-db");
        await Security("create-keychain", "-p", "test-only", path);
        await Security("unlock-keychain", "-p", "test-only", path);
        try
        {
            var keychain = new SecurityCliKeychain(path);
            const string service = "skylight-photo-sync.test";

            Assert.Null(await keychain.FindAsync(service, CancellationToken.None));
            await keychain.SetAsync(service, "first", CancellationToken.None);
            Assert.Equal("first", await keychain.FindAsync(service, CancellationToken.None));
            await keychain.SetAsync(service, "a b\"c\\d$x'e pä😀", CancellationToken.None);
            Assert.Equal("a b\"c\\d$x'e pä😀", await keychain.FindAsync(service, CancellationToken.None));
            await keychain.DeleteAsync(service, CancellationToken.None);
            Assert.Null(await keychain.FindAsync(service, CancellationToken.None));
            await keychain.DeleteAsync(service, CancellationToken.None);
        }
        finally
        {
            await Security("delete-keychain", path);
        }
    }

    private static async Task Security(params string[] args)
    {
        var info = new ProcessStartInfo("/usr/bin/security") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
