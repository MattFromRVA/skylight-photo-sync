using System.Runtime.InteropServices;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that need a real Mac tool. Skipped on any other OS.
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Skip = "Requires macOS.";
        }
    }
}
