namespace SkylightPhotoSync.Imaging;

/// <summary>A file is of a kind the normalizer cannot turn into an attachment, or could not be decoded.</summary>
public sealed class UnsupportedMediaException : Exception
{
    public UnsupportedMediaException()
    {
    }

    public UnsupportedMediaException(string message)
        : base(message)
    {
    }

    public UnsupportedMediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
