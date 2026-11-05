namespace SkylightPhotoSync.Mail;

/// <summary>The SMTP server could not be reached, refused the message, or the connection broke.</summary>
public sealed class SendFailedException : Exception
{
    public SendFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
