namespace SkylightPhotoSync.Alerts;

/// <summary>Canned alert bodies that need to be word-for-word stable across runs.</summary>
public static class AlertTexts
{
    /// <summary>Re-login steps sent once when Amazon rejects the saved cookies.</summary>
    public const string AuthExpiredBody =
        """
        Amazon Photos no longer accepts the saved login, so photo syncing is paused.
        Nothing will be sent to the Skylight frame until you log in again.

        This usually means the "Skylight Photo Sync" device was removed from your Amazon
        account (Manage Your Content and Devices), or Amazon revoked it. If you logged in
        with pasted browser cookies instead, they have simply expired.

        To fix it, in Terminal on the Mac that runs the sync:

            skylight-sync login

        It prints an amazon.com sign-in address. Open it in a browser, sign in (including
        the two-factor prompt), and paste back the address of the "page not found" page you
        land on. The tool registers itself as a device again and refreshes its own session
        from then on.

        Syncing resumes on the next scheduled run after the login succeeds.
        """;
}
