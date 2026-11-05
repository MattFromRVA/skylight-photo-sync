namespace SkylightPhotoSync.Alerts;

/// <summary>
/// What an alert is about. Alerts are rate-limited per kind: one per 24 hours
/// (spec §6.7), keyed in the state store by <c>kind.ToString()</c>.
/// </summary>
public enum AlertKind
{
    /// <summary>Amazon rejected the saved cookies; syncing is paused until <c>skylight-sync login</c>.</summary>
    AuthExpired,

    /// <summary>Three or more consecutive scheduled runs have failed.</summary>
    RepeatedFailure,

    /// <summary>The SMTP server could not be reached, so only a notification is possible.</summary>
    SmtpDown,
}
