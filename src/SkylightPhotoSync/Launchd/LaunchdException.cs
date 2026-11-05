namespace SkylightPhotoSync.Launchd;

/// <summary>Raised when <c>launchctl</c> refuses an install or uninstall; the message is user-facing.</summary>
public sealed class LaunchdException : Exception
{
    public LaunchdException(string message)
        : base(message)
    {
    }
}
