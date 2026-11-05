using System.Runtime.InteropServices;

namespace SkylightPhotoSync.Launchd;

/// <summary>The current user's numeric id, which names the <c>gui/&lt;uid&gt;</c> launchd domain.</summary>
internal static class NativeUid
{
    public static uint Current()
    {
        try
        {
            return GetUid();
        }
        catch (DllNotFoundException ex)
        {
            throw new LaunchdException($"Could not determine the current user id: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new LaunchdException($"Could not determine the current user id: {ex.Message}");
        }
    }

    // DllImport rather than LibraryImport: the latter needs AllowUnsafeBlocks for the whole project.
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
