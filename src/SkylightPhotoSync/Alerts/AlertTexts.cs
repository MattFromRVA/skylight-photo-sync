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

    /// <summary>Recovery steps sent once when Skylight refuses the sign-in rotation uses.</summary>
    public static string SkylightSignInBody(string reason) =>
        $"""
        Skylight did not accept the saved sign-in, so removing old photos from the frame is paused.
        New photos are still being emailed to the frame; they will pile up until this is fixed.

        What Skylight said: {reason}

        The tool will not try this email and password again on its own, so the account cannot be
        locked by repeated attempts. To fix it, on the Mac that runs the sync:

        1. Sign in once in the Skylight app or at https://app.ourskylight.com to confirm the
           email and password work (and to clear any CAPTCHA or lockout).
        2. Check [skylight] email and password in
           ~/.config/skylight-photo-sync/secrets.toml.
        3. In Terminal, run:

               skylight-sync prune --dry-run

        When that command signs in successfully, pruning resumes on the next scheduled run.
        Changing [skylight] email or password also makes the next run try again.
        """;
}
