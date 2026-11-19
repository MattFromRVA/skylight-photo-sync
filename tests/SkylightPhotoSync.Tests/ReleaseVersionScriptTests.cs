using System.Diagnostics;

namespace SkylightPhotoSync.Tests;

/// <summary>
/// Runs <c>scripts/release-version.sh</c>, the gate the publish workflow uses to turn a pushed
/// tag into the package version, so a typo tag is rejected before anything reaches nuget.org.
/// </summary>
public class ReleaseVersionScriptTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("v10.20.30", "10.20.30")]
    [InlineData("v1.2.3-rc.1", "1.2.3-rc.1")]
    [InlineData("v1.0.0-alpha-1.x", "1.0.0-alpha-1.x")]
    public async Task Valid_tag_prints_the_version_without_the_v(string tag, string expected)
    {
        var (exitCode, stdOut, _) = await RunScriptAsync(tag);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected + "\n", stdOut);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v01.2.3")]
    [InlineData("v1.2.3-")]
    [InlineData("v1.2.3-01")]
    [InlineData("v1.2.3+build")]
    [InlineData("vnext")]
    [InlineData("v1.2.3 ")]
    [InlineData("v1.2.3;echo injected")]
    [InlineData("v1.2.3\nv2.0.0")]
    [InlineData("")]
    public async Task Invalid_tag_fails_with_an_error_and_prints_no_version(string tag)
    {
        var (exitCode, stdOut, stdErr) = await RunScriptAsync(tag);

        Assert.Equal(1, exitCode);
        Assert.Equal("", stdOut);
        Assert.StartsWith("error: tag ", stdErr);
    }

    [Fact]
    public async Task Missing_argument_fails_with_usage()
    {
        var (exitCode, stdOut, stdErr) = await RunScriptAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal("", stdOut);
        Assert.StartsWith("usage: ", stdErr);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunScriptAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "release-version.sh"));
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdOut, await stdErr);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SkylightPhotoSync.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("SkylightPhotoSync.sln not found above " + AppContext.BaseDirectory);
    }
}
