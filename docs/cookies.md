# Amazon login: device login (default) and pasted cookies (fallback)

`skylight-sync` reads your Amazon Photos library the same way the Amazon Photos web app does,
using the private endpoints behind <https://www.amazon.com/photos>. Those endpoints accept a
logged-in Amazon session and nothing else. There are two ways to give the tool one:

- **Device login (default, `skylight-sync login`).** You sign in to Amazon once in your own
  browser. The tool registers itself on your account as a device called "Skylight Photo Sync"
  and receives a long-lived refresh token. Before every run it trades that token for a fresh set
  of session cookies, so nothing expires on Amazon's schedule. Your password never touches the
  tool, and no cookie copying is involved.
- **Pasted cookies (fallback, `skylight-sync login --cookies`).** You copy three cookies from a
  browser where you are signed in. They stop working whenever the browser rotates its session,
  anywhere from twenty minutes to a few days later. Use this only if the device login does not
  work for your account or storefront.

> Treat the refresh token and the cookies like a password. Anyone who has them can read your
> Amazon account. `skylight-sync login` stores them in
> `~/.config/skylight-photo-sync/secrets.toml` with mode `0600` (readable only by you) and never
> prints or logs them. Do not paste them into chats, issues or screenshots.

> **Status (2026-11).** The device login is implemented against the documented Audible/Alexa
> flow and is awaiting its first verification against a live Amazon account. If `login` fails at
> the sign-in page or at registration, fall back to `login --cookies` and open an issue with the
> error line (never with any token).

## Why there is no simple "enter your password"

- Amazon Photos has **no public API**. The Amazon Drive API was retired, and the desktop app's
  two-way sync was discontinued on 29 February 2024. Downloading from the cloud is manual only.
- A scripted password login would also need your two-factor codes every time, would be served
  CAPTCHAs, and would put your password and 2FA seed on disk. So the tool never asks for the
  password; it borrows the login flow that the Audible and Alexa apps use, where you sign in
  yourself and only a revocable device token is kept.

## Device login, step by step

In Terminal:

```bash
skylight-sync login
```

It prints something like:

```
Sign in to Amazon once in your browser; the tool then registers itself as a device and refreshes its own session from now on.

1. Open this address in a browser (any device; copy it exactly):

https://www.amazon.com/ap/signin?openid.oa2.response_type=code&openid.oa2.code_challenge_method=S256&...

2. Sign in with your Amazon account, including the two-factor prompt (...).
3. You will land on a "page not found" at amazon.com/ap/maplanding. That is expected.
4. Copy the whole address from the address bar of that page and paste it here, then press Return.
5. Afterwards, clear this terminal window's scrollback (Terminal: Cmd-K); the address contains a single-use sign-in code.
Paste the address of the /ap/maplanding page:
```

1. Copy the long `https://www.amazon.com/ap/signin?...` address and open it in Safari or Chrome
   (on this Mac or on your phone; it does not matter where you sign in). Each `login` run makes a
   new address; do not reuse an old one.
2. Sign in. Complete the two-factor prompt. **Use the Amazon app approval or an authenticator
   app if you can**; codes sent by email or SMS are reported to be flaky on this sign-in page.
   Solve a CAPTCHA if one appears. Amazon may show a consent page for the "Audible for iPhone"
   style device registration; accept it.
3. The browser lands on a blank or "page not found" page whose address starts with
   `https://www.amazon.com/ap/maplanding?` and contains `openid.oa2.authorization_code=`. That
   page is the result, not an error.
4. Copy the **entire** address from the address bar (click it, Cmd-A, Cmd-C), paste it into the
   Terminal and press Return. The code in it is single-use and is useless after a minute or so.
5. When `login` finishes (successfully or not), clear the Terminal window's scrollback with
   Cmd-K: the tool never prints the address or any token, but the Terminal itself shows what you
   pasted. `login` reminds you with a last line saying so.

On success:

```
Amazon Photos: connected as Matt Chaulklin, 52015 items
Device registered as "Skylight Photo Sync"; remove it under Manage Your Content and Devices to revoke.
Saved. Run 'skylight-sync run --dry-run' to verify.
Clear this terminal window's scrollback now (Terminal: Cmd-K); the address you pasted contains a single-use sign-in code.
```

What `login` did:

1. Registered this tool on your account as a device (it appears as "Matt's Skylight Photo Sync"
   under Amazon > Account > Manage Your Content and Devices > Devices).
2. Traded the device's refresh token for session cookies and called Amazon Photos once to make
   sure they work.
3. Only then wrote `[amazon] refresh_token` and `device_serial` to `secrets.toml` (mode `0600`),
   keeping any `[smtp] password`, and cleared the `needs_login` flag.
4. If `secrets.toml` already held a device login, removed that older device from the account
   (or, when Amazon cannot be reached, printed a warning asking you to remove it by hand).

If step 2 or 3 fails (Amazon Photos rejects the fresh session, Amazon does not answer in time,
or `secrets.toml` cannot be written), `login` removes the device again, writes nothing, and exits 1. Run it again; if it keeps failing, use `login --cookies`.

### Revoking and logging out

```bash
skylight-sync logout
```

removes the device from your Amazon account, deletes the Amazon keys from `secrets.toml` (the
SMTP password stays) and sets `needs_login`, so scheduled runs stop until the next `login`. You
can also remove the device on Amazon's site at any time; the next run then fails with
`auth_expired`, you get one alert, and `login` registers a new device.

### How long it lasts

The refresh token stays valid until the device is removed from the account (by you, or by
Amazon). Projects using the same flow report tokens that keep working for years. The cookies it
mints are good for days, but the tool does not rely on that: it mints a fresh set at the start of
every run, and once more if Amazon answers 401 during a run.

## Pasted cookies (fallback)

```bash
skylight-sync login --cookies
```

The tool needs three cookies from a browser where you are signed in to Amazon:

| Cookie | Also accepted as | Role |
|---|---|---|
| `session-id` | | Identifies the browser session |
| `ubid-main` | `ubid_main` | Browser/device identifier |
| `at-main` | `at_main` | The access token; the one that expires |

The underscore spellings exist because some tooling renames the cookies; `login --cookies`
accepts either, and `secrets.toml` may use either (`session_id`/`session-id`,
`ubid_main`/`ubid-main`, `at_main`/`at-main`). All three must be present.

### Before you start

1. Open <https://www.amazon.com/photos> in Safari or Chrome **on the Mac that will run the
   sync** and sign in, including any two-factor prompt. Make sure the Amazon Photos web app loads
   your photos.
2. Leave this browser signed in. **Logging out of Amazon in that browser invalidates the copied
   cookies immediately.** Browsing Amazon normally is fine; only signing out breaks them.

The easiest route is "Copy as cURL" from the browser's Network tab: it copies the whole request
including a `Cookie:` header, and `login --cookies` extracts the three cookies from it. You never
have to find the individual values.

### Safari

1. Enable the Develop menu if you have not: Safari > Settings > Advanced > tick
   **Show features for web developers** (older versions: "Show Develop menu in menu bar").
2. With the Amazon Photos tab in front, choose **Develop > Show Web Inspector** (Option-Cmd-I).
3. Click the **Network** tab in the inspector. If it is empty, reload the page (Cmd-R) so
   requests appear.
4. Click **any request to `amazon.com`** in the list (the first row is fine; it is the page
   itself).
5. Right-click that request and choose **Copy as cURL**. The command is now on your clipboard.

### Chrome (also Edge, Brave, Arc)

1. With the Amazon Photos tab in front, open DevTools: **View > Developer > Developer Tools**
   (Option-Cmd-I).
2. Click the **Network** tab. If it is empty, reload the page (Cmd-R).
3. Click **any request to `amazon.com`** in the list.
4. Right-click it and choose **Copy > Copy as cURL**. On Windows-style menus pick the
   "Copy as cURL (bash)" variant.

### Paste into `skylight-sync login --cookies`

```bash
skylight-sync login --cookies
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
pbpaste | skylight-sync login --cookies
```

#### Pasting three values instead

If you prefer to copy the individual cookies (in Chrome: DevTools > Application > Cookies >
`https://www.amazon.com`; in Safari: Web Inspector > Storage > Cookies), `login --cookies` also
accepts exactly three non-empty lines, in this order:

```
<session-id value>
<ubid-main value>
<at-main value>
```

Anything else (two lines, four lines, a cURL command without a `Cookie:` header, a header missing
one of the three names) is rejected with a message saying what was expected, and nothing is
written.

### What `login --cookies` does with them

1. Parses the paste and refuses it if any of the three cookies is missing.
2. Writes `[amazon]` into `secrets.toml` (mode `0600`), replacing any device-login keys there
   and keeping `[smtp] password`. A replaced device login's device is removed from the account
   (if Amazon cannot be reached, a warning asks you to remove it under Manage Your Content and
   Devices).
3. Calls Amazon Photos once to confirm the cookies work and prints
   `Amazon Photos: connected, <count> items` (with `as <name>` when Amazon exposes a name
   for the account; it usually does not).
4. Clears the `needs_login` flag so the next scheduled run resumes.

If Amazon rejects the cookies, `login --cookies` exits 1 with
`login failed: Amazon Photos rejected the cookies ...`. They are saved anyway (so you can inspect
the file), but `needs_login` is not cleared. Usually this means the browser session had already
rotated: go back to the browser, reload the Amazon Photos page, copy again, and run it again.

### How long cookies last

Nobody outside Amazon knows the schedule. Reports from projects doing the same thing range from
**20 minutes to about 8 days**, usually a few days, and the lifetime is not predictable. On
2026-11-06 a set copied for this project was rejected after about fifteen minutes. That is why
the device login is the default.

## What happens when the login stops working

Both kinds of login fail the same way, and the tool is built around it:

- A scheduled run that gets 401/403 from Amazon (with the device login: even after minting a
  fresh cookie set) stops, marks `needs_login`, and exits with code 3. Photos that were already
  emailed in that run stay marked as sent.
- You get **one** email (subject `Skylight Photo Sync: Amazon login expired`) with the re-login
  steps and **one** macOS notification. Alerts of this kind are rate-limited to one per 24 hours.
- Every following scheduled run exits 3 immediately without touching Amazon, so a dead login
  never turns into a stream of failed requests.
- `skylight-sync status` shows `needs_login: yes` and which kind of login is saved
  (`auth: device login`, `auth: pasted cookies` or `auth: not logged in`).
- Run `skylight-sync login` (or `login --cookies`). It clears the flag; the next scheduled run
  picks up where it left off.

## Checking without waiting for the schedule

```bash
skylight-sync run --dry-run
```

exits 0 and prints `found N, sent S, skipped K, batches B (dry run)` (in a dry run `sent` is how
many would be sent; nothing is emailed) when the login works, or
exits 3 with `run failed: needs_login` / `run failed: auth_expired` when it does not. More in
[troubleshooting.md](troubleshooting.md#needs_login-exit-code-3).
