using SkylightPhotoSync.Imaging;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class SipsHeicConverterTests
{
    [Fact]
    public async Task Sips_converter_invokes_expected_arguments()
    {
        var runner = new FakeProcessRunner().Enqueue(0);
        var converter = new SipsHeicConverter(runner);
        using var dest = new TempDir();
        var destDir = dest.Path;

        var output = await converter.ToJpegAsync("/photos/IMG_0001.heic", destDir, CancellationToken.None);

        var expectedOutput = Path.Combine(destDir, "IMG_0001.jpg");
        Assert.Equal(expectedOutput, output);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("sips", call.FileName);
        Assert.Equal(
            ["-s", "format", "jpeg", "-s", "formatOptions", "100", "/photos/IMG_0001.heic", "--out", expectedOutput],
            call.Args);
    }

    [Fact]
    public async Task Sips_nonzero_exit_throws_unsupported()
    {
        var runner = new FakeProcessRunner().Enqueue(1, stdErr: "Error 4: no file was found\n");
        var converter = new SipsHeicConverter(runner);

        var ex = await Assert.ThrowsAsync<UnsupportedMediaException>(() =>
            converter.ToJpegAsync("/photos/missing.heic", "/tmp/out", CancellationToken.None));

        Assert.Contains("missing.heic", ex.Message);
        Assert.Contains("no file was found", ex.Message);
    }
}
