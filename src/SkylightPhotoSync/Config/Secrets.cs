namespace SkylightPhotoSync.Config;

/// <summary>
/// Contents of secrets.toml: the three Amazon session cookies and the SMTP password.
/// Any value may be absent (for example before the user has logged in). Never log these.
/// </summary>
public sealed record Secrets(string? AmazonSessionId, string? AmazonUbid, string? AmazonAt, string? SmtpPassword);
