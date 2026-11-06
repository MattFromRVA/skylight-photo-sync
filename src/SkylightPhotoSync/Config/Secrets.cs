namespace SkylightPhotoSync.Config;

/// <summary>
/// Contents of secrets.toml. Amazon is authenticated either by the device login's refresh token
/// (<see cref="AmazonRefreshToken"/>, with the serial the device was registered under) or, as the
/// fallback, by the three pasted browser cookies. Any value may be absent (for example before the
/// user has logged in); a file with neither is only an error when Amazon is actually needed.
/// Never log these: the refresh token is as powerful as the cookies.
/// </summary>
public sealed record Secrets(
    string? AmazonSessionId,
    string? AmazonUbid,
    string? AmazonAt,
    string? SmtpPassword,
    string? AmazonRefreshToken = null,
    string? AmazonDeviceSerial = null);
