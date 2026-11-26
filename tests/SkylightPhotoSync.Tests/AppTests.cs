using System.Reflection;

namespace SkylightPhotoSync.Tests;

public class AppTests
{
    [Fact]
    public async Task Version_flag_prints_name_and_the_version_the_assembly_was_built_with()
    {
        // The publish workflow builds with -p:Version=<tag>, so the expected value is whatever
        // this build was stamped with rather than a literal.
        var stamped = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = stamped.Split('+')[0];
        Assert.Matches(@"^\d+\.\d+\.\d+", expected);

        var out_ = new StringWriter(); var err = new StringWriter();
        var code = await App.RunAsync(["--version"], out_, err);

        Assert.Equal(0, code);
        Assert.Equal($"skylight-sync {expected}", out_.ToString().Trim());
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+0123abc", "1.2.3")]
    [InlineData("2.0.0-rc.1+0123abc", "2.0.0-rc.1")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void DisplayVersion_drops_the_source_revision_suffix(string? informationalVersion, string expected)
    {
        Assert.Equal(expected, App.DisplayVersion(informationalVersion));
    }
}
