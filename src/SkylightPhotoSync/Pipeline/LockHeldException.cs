namespace SkylightPhotoSync.Pipeline;

/// <summary>Another run holds the lock file, so this one must exit without doing anything.</summary>
public sealed class LockHeldException : Exception
{
    public LockHeldException()
        : base("Another sync run is in progress (the lock file is held).")
    {
    }

    public LockHeldException(string message)
        : base(message)
    {
    }

    public LockHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
