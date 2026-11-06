# Amazon cookies: what they are and how to copy them

`skylight-sync` reads your Amazon Photos library the same way the Amazon Photos web app does,
using the private endpoints behind <https://www.amazon.com/photos>. Those endpoints accept a
logged-in browser session and nothing else, so the tool needs three cookies from a browser where
you are signed in to Amazon. This page shows how to copy them.

> Treat these cookie values like a password. Anyone who has them can read your Amazon account
> until they expire. `skylight-sync login` stores them in
> `~/.config/skylight-photo-sync/secrets.toml` with mode `0600` (readable only by you) and never
> prints or logs them. Do not paste them into chats, issues or screenshots.

## Why cookies at all

- Amazon Photos has **no public API**. The Amazon Drive API was retired, and the desktop app's
  two-way sync was discontinued on 29 February 2024. Downloading from the cloud is manual only.
- Every unofficial client (the `amazon-photos` Python library, Immich and Home Assistant
  integrations) therefore borrows the web app's session: the browser cookies `session-id`,
  `ubid-main` and `at-main`.
- The cookies belong to the browser's session, and the browser rotates them on Amazon's
  schedule. The copy in `secrets.toml` goes stale when that happens, which is why the tool has a
  `needs_login` state and an alert, and why `login` is a command you will run more than once.

A one-time **device login** that mints its own credentials and never expires this way is planned
in [issue #30](https://github.com/MattFromRVA/skylight-photo-sync/issues/30). Until that lands,
copied cookies are the only option.

## The three cookies

| Cookie | Also accepted as | Role |
|---|---|---|
| `session-id` | | Identifies the browser session |
| `ubid-main` | `ubid_main` | Browser/device identifier |
| `at-main` | `at_main` | The access token; the one that expires |

The underscore spellings exist because some tooling renames the cookies; `login` accepts either,
and `secrets.toml` may use either (`session_id`/`session-id`, `ubid_main`/`ubid-main`,
`at_main`/`at-main`). All three must be present.

## Before you start

1. Open <https://www.amazon.com/photos> in Safari or Chrome **on the Mac that will run the
   sync** and sign in, including any two-factor prompt. Make sure the Amazon Photos web app loads
   your photos.
2. Leave this browser signed in. **Logging out of Amazon in that browser invalidates the copied
   cookies immediately.** Browsing Amazon normally is fine; only signing out breaks them.

The easiest route is "Copy as cURL" from the browser's Network tab: it copies the whole request
including a `Cookie:` header, and `skylight-sync login` extracts the three cookies from it. You
never have to find the individual values.

## Safari

1. Enable the Develop menu if you have not: Safari > Settings > Advanced > tick
   **Show features for web developers** (older versions: "Show Develop menu in menu bar").
2. With the Amazon Photos tab in front, choose **Develop > Show Web Inspector** (Option-Cmd-I).
3. Click the **Network** tab in the inspector. If it is empty, reload the page (Cmd-R) so
   requests appear.
4. Click **any request to `amazon.com`** in the list (the first row is fine; it is the page
   itself).
5. Right-click that request and choose **Copy as cURL**. The command is now on your clipboard.

## Chrome (also Edge, Brave, Arc)

1. With the Amazon Photos tab in front, open DevTools: **View > Developer > Developer Tools**
   (Option-Cmd-I).
2. Click the **Network** tab. If it is empty, reload the page (Cmd-R).
3. Click **any request to `amazon.com`** in the list.
4. Right-click it and choose **Copy > Copy as cURL**. On Windows-style menus pick the
   "Copy as cURL (bash)" variant.

## Paste into `skylight-sync login`

In Terminal:

```bash
skylight-sync login
```

It prints

```
Paste the 'Copy as cURL' command (or session-id, ubid-main and at-main on three lines), then press Ctrl-D.
```

Paste (Cmd-V), press Return, then press **Ctrl-D** once to end the input. The pasted command can
span many lines and contain any other headers; `login` only reads the `Cookie:` header (it also
understands `-b`/`--cookie`). On success:

```
Amazon Photos: connected, 12345 items
Saved. Run 'skylight-sync run --dry-run' to verify.
```

You can also pipe the clipboard straight in, which avoids any terminal paste quirks:

```bash
pbpaste | skylight-sync login
```

### Pasting three values instead

If you prefer to copy the individual cookies (in Chrome: DevTools > Application > Cookies >
`https://www.amazon.com`; in Safari: Web Inspector > Storage > Cookies), `login` also accepts
exactly three non-empty lines, in this order:

```
<session-id value>
<ubid-main value>
<at-main value>
```

Anything else (two lines, four lines, a cURL command without a `Cookie:` header, a header missing
one of the three names) is rejected with a message saying what was expected, and nothing is
written.

## What `login` does with them

1. Parses the paste and refuses it if any of the three cookies is missing.
2. Writes `[amazon]` into `secrets.toml` (mode `0600`), keeping any `[smtp] password` already
   there.
3. Calls Amazon Photos once to confirm the cookies work and prints
   `Amazon Photos: connected, <count> items` (with `as <name>` when Amazon exposes a name
   for the account; it usually does not).
4. Clears the `needs_login` flag so the next scheduled run resumes.

If Amazon rejects the cookies, `login` exits 1 with
`login failed: Amazon Photos rejected the cookies ...`. They are saved anyway (so you can inspect
the file), but `needs_login` is not cleared. Usually this means the browser session had already
rotated: go back to the browser, reload the Amazon Photos page, copy again, and run `login` again.

## How long they last, and what happens when they stop working

Nobody outside Amazon knows the schedule. Reports from projects doing the same thing range from
**20 minutes to about 8 days**, usually a few days, and the lifetime is not predictable. The
tool is built around that:

- A scheduled run that gets 401/403 from Amazon stops, marks `needs_login`, and exits with code
  3. Photos that were already emailed in that run stay marked as sent.
- You get **one** email (subject `Skylight Photo Sync: Amazon login expired`) with the re-login
  steps and **one** macOS notification. Alerts of this kind are rate-limited to one per 24 hours.
- Every following scheduled run exits 3 immediately without touching Amazon, so an expired
  session never turns into a stream of failed requests.
- `skylight-sync status` shows `needs_login: yes`.
- Copy fresh cookies as above and run `skylight-sync login`. It clears the flag; the next
  scheduled run picks up where it left off.

If this happens often enough to be annoying, that is exactly the problem
[issue #30](https://github.com/MattFromRVA/skylight-photo-sync/issues/30) is for.

## Checking without waiting for the schedule

```bash
skylight-sync run --dry-run
```

exits 0 and prints `found N, sent S, skipped K, batches B (dry run)` (in a dry run `sent` is how
many would be sent; nothing is emailed) when the cookies work, or
exits 3 with `run failed: needs_login` / `run failed: auth_expired` when they do not. More in
[troubleshooting.md](troubleshooting.md#needs_login-exit-code-3).
