namespace SkylightPhotoSync.Skylight;

/// <summary>A frame (photo frame or calendar) on the Skylight account.</summary>
public sealed record SkylightFrame(string Id, string Name);

/// <summary>
/// One photo on a frame. The Skylight API calls photos "messages". <see cref="FromEmail"/> is the
/// address that emailed it in, which is how rotation tells this tool's photos from everyone else's.
/// </summary>
public sealed record SkylightMessage(string Id, DateTimeOffset CreatedAt, string? FromEmail, bool Destroyed);

/// <summary>The few calls of Skylight's unofficial app API that rotation needs. Faked in tests.</summary>
public interface ISkylightClient
{
    Task<IReadOnlyList<SkylightFrame>> ListFramesAsync(CancellationToken ct);

    /// <summary>Every photo on the frame, all pages.</summary>
    Task<IReadOnlyList<SkylightMessage>> ListMessagesAsync(string frameId, CancellationToken ct);

    /// <summary>Deletes one photo from the frame (and the app). An already-deleted photo is not an error.</summary>
    Task DeleteMessageAsync(string frameId, string messageId, CancellationToken ct);
}

/// <summary>Skylight refused the request or answered something the client cannot read; the message is user-facing.</summary>
public class SkylightApiException : Exception
{
    public SkylightApiException(string message)
        : base(message)
    {
    }

    public SkylightApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Skylight did not let the saved email and password in: wrong credentials, a locked account, a
/// CAPTCHA or other challenge, or a sign-in page the client no longer understands. Trying the same
/// password again will not help and may lock the account, so callers pause instead of retrying
/// (see <see cref="SkylightSignInGate"/>). A Skylight outage (HTTP 5xx) or a network error is not this.
/// </summary>
public sealed class SkylightSignInException(string message) : SkylightApiException(message);
