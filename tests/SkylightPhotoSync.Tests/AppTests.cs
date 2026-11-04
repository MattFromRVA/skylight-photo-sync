namespace SkylightPhotoSync.Tests;

public class AppTests
{
    [Fact]
    public async Task Version_flag_prints_name_and_version()
    {
        var out_ = new StringWriter(); var err = new StringWriter();
        var code = await App.RunAsync(["--version"], out_, err);
        Assert.Equal(0, code);
        Assert.Equal("skylight-sync 0.1.0", out_.ToString().Trim());
    }
}
