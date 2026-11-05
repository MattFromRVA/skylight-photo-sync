namespace SkylightPhotoSync.Imaging;

/// <summary>A video is larger than the configured cap and cannot be emailed.</summary>
public sealed class OversizeException : Exception
{
    public OversizeException()
    {
    }

    public OversizeException(string message)
        : base(message)
    {
    }

    public OversizeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
