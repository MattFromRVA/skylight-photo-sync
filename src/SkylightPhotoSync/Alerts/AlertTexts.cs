namespace SkylightPhotoSync.Alerts;

/// <summary>Canned alert bodies that need to be word-for-word stable across runs.</summary>
public static class AlertTexts
{
    /// <summary>Re-login steps sent once when Amazon rejects the saved cookies.</summary>
    public const string AuthExpiredBody =
        """
        Amazon Photos no longer accepts the saved login cookies, so photo syncing is paused.
        Nothing will be sent to the Skylight frame until you log in again.

        To fix it:

        1. On this Mac, open a browser, go to https://www.amazon.com and sign in.
        2. Open the browser's developer tools (View > Developer > Developer Tools in Chrome,
           or Develop > Show Web Inspector in Safari) and find the cookies for amazon.com
           under Application > Cookies (Chrome) or Storage > Cookies (Safari).
        3. Copy the values of these three cookies: session-id, ubid-main, at-main.
           Alternatively, right-click any request to amazon.com in the Network tab and
           choose "Copy as cURL".
        4. In Terminal, run:

               skylight-sync login

           and paste the three values (or the copied cURL command) when asked.

        Syncing resumes on the next scheduled run after the login succeeds.
        """;
}
