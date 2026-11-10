# Troubleshooting

Start with these two, whatever the symptom:

```bash
skylight-sync status
tail -n 100 ~/Library/Logs/skylight-photo-sync/sync.log
```

`status` shows the last run's outcome and counts, how much has ever been sent, whether a login
is needed, and when launchd will run next. The log has each run's parameters, summary and error.
Per-item reasons for skipped files are in the state database:

```bash
sqlite3 ~/Library/Application\ Support/skylight-photo-sync/state.db \
  "select item_id, reason, detail, skipped_at from skipped_items order by skipped_at desc limit 20"
```

Running `skylight-sync run --dry-run` from a terminal reproduces most problems in the
foreground, with the log lines echoed to stderr.

## Exit codes

| Code | Meaning | What to do |
|---|---|---|
| 0 | Run completed (possibly with items skipped) | Nothing; check the log if fewer photos arrived than expected |
| 1 | Error: config problem, SMTP failure, Amazon returned an error, unknown `--source` | Read the `run failed: ...` line |
| 3 | `needs_login`: not logged in, the device was removed from the Amazon account, or the cookies expired | `skylight-sync login` ([below](#needs_login-exit-code-3)) |
| 4 | Lock held: another run is still going | Wait, or see [below](#lock-held-exit-code-4) |

Under launchd nobody sees the exit code, so the same facts arrive as `status` output, log lines,
and (for 3 and for three consecutive failures) an alert email and a macOS notification.

## needs_login (exit code 3)

Three messages share this exit code.

**`run failed: not logged in to Amazon Photos; run 'skylight-sync login' first.`** You have not
run `login` yet, you ran `logout`, or `secrets.toml` lost its `[amazon]` table. Follow
[cookies.md](cookies.md).

**`run failed: needs_login`** A previous run was rejected by Amazon (or you ran `logout`) and the
`needs_login` flag is set. Until it is cleared every run exits here immediately, without
contacting Amazon, so that a dead login does not produce an hourly stream of failed requests.
`status` shows `needs_login: yes`. Run `skylight-sync login`; a successful login clears the flag
and the next run proceeds.

**`run failed: auth_expired`** This is the run that discovered the problem: Amazon answered 401 or
403. With the device login the tool first minted a fresh cookie set and retried once; 401 again
means the refresh token itself is dead. Anything emailed earlier in the run stays marked as sent,
`needs_login` is set, one alert email (`Skylight Photo Sync: Amazon login expired`) and one macOS
notification go out, and later runs show `needs_login` as above. Fix is the same:
`skylight-sync login`.

Why it happens:

- **Device login** (`status` shows `auth: device login`): the "Skylight Photo Sync" device was
  removed from your Amazon account (Account > Manage Your Content and Devices > Devices), or
  Amazon revoked it, for example after a password change. This should be rare. `login` registers
  a new device; the old entry, if still listed, can be removed by hand.
- **Pasted cookies** (`auth: pasted cookies`): the cookies are copies of a browser session that
  Amazon rotates on its own schedule, anywhere from minutes to days. Logging out of Amazon in the
  browser you copied from also invalidates them at once. Switch to the device login with
  `skylight-sync login`.

## `login` problems

**The sign-in page shows an error or never reaches "page not found".** Each `login` run makes a
fresh address; open the one it just printed, exactly, and finish the whole sign-in including the
two-factor step. Email or SMS two-factor codes are reported to be unreliable on this particular
page: switch the account's 2FA to the Amazon app or an authenticator app, or approve the sign-in
from the Amazon app on your phone. A CAPTCHA, if shown, must be solved in the browser; the tool
never sees it.

**`login failed: The pasted address has no openid.oa2.authorization_code`** You pasted the
sign-in address, a shortened address, or only part of the landing page's address. After signing
in, copy the complete address of the `amazon.com/ap/maplanding?...` page (click the address bar,
Cmd-A, Cmd-C) and paste that.

**`login failed: Could not register the device: Amazon answered HTTP 400 (...)`** The code in
the pasted address is single-use and short-lived: run `login` again and paste within a minute.
If it fails every time with the same message, open an issue quoting the error line (it contains
no secrets) and use `login --cookies` meanwhile.

**`login failed: the new device login could not be completed (...)`** Registration worked
but a later step failed, usually because Amazon Photos rejected the fresh session (or Amazon did
not answer in time, or `secrets.toml` could not be written). The tool removed the device again and
wrote nothing. Try once more; then fall back to `login --cookies`. If the message instead says
`secrets.toml could not be put back as it was and may be damaged`, open `secrets.toml`, re-add
the `[smtp] password` if it is missing, and run `login` again.

**`warning: the previous device login could not be removed from your Amazon account`** A new
`login` (device or `--cookies`) replaced a saved device login and tried to remove the old device,
but Amazon could not be reached. The new login is saved and works; remove the older
"Skylight Photo Sync" entry under Manage Your Content and Devices by hand.

**`login --cookies` fails with `Amazon Photos rejected the cookies`**: the browser session had
already rotated between copying and pasting, or a cookie was truncated. Reload the Amazon Photos
page in the browser, copy again, paste again. Better: use the device login.

**`logout` says the device could not be removed.** The Amazon keys were removed from
`secrets.toml` anyway and `needs_login` is set. Remove "Skylight Photo Sync" by hand under
Account > Manage Your Content and Devices > Devices on amazon.com.

## HTTP 429 (rate limited)

`run failed: HttpRequestException: Amazon Photos kept returning HTTP 429 for https://... after 3 retries.`

Amazon throttled the session. The client already requests pages one at a time with a 0.5 s pause
between them, and on a 429 it waits **60 seconds** and retries, up to three times per request,
before giving up with this message. The run exits 1 and nothing is marked sent that was not
accepted by SMTP.

Usually the next hourly run succeeds on its own. If it keeps happening:

- Lower `[limits] max_items_per_run` (each item is one download request).
- Prefer `mode = "album"` over `all`; album listing is one small request per page, while
  all-mode search over a long `lookback_days` can page through a large library.
- Do not run `skylight-sync run` by hand at the same time as the schedule.
- Three failed runs in a row produce one `Skylight Photo Sync: Sync failing` alert; it stops
  on its own once a run succeeds.

## Photos not appearing on the frame

Work down the list.

1. **Did the run send anything?** `skylight-sync status`: the last run line should say `ok` with
   `sent` greater than 0. If `sent 0, skipped N`, the `skipped_items` query above says why each
   item was skipped (`VideoDisabled`, `Oversize`, `UnsupportedType`, `DownloadFailed`,
   `ProcessingFailed`). If `sent 0` and `found 0`, the album is empty inside the lookback window,
   you are in all mode with a short `lookback_days`, or everything in the window was already sent.
2. **Is the sender approved?** Skylight's default is "Senders who I approve": the first email
   from a new address is held until the account owner approves it (Skylight emails the owner;
   it can also be done in the Skylight app or on the frame). Approve `[email] from` once. Or set
   "Who can send photos?" to "Anyone" in the Skylight app.
3. **Is the address right?** `[email] to` must be the household address shown in the Skylight
   app, `<name>@ourskylight.com`. Send `skylight-sync test-email` to confirm one image arrives.
4. **Plus plan.** On a Skylight **Calendar**, photos are only displayed by the photo screensaver,
   which requires the Plus plan. Without it the emails are accepted but nothing shows them.
5. **Size.** Skylight accepts at most **25 MB per email**. The defaults
   (`max_attachments = 12`, `max_total_bytes = 17000000`, long edge 2048 px) stay under that
   with room for base64 overhead. If you raised them, run `skylight-sync test-email --size-probe`
   and see which probe sizes actually arrive; the subjects say how big each one is.
6. **Format.** Skylight accepts JPEG, HEIC, PNG and MP4, MOV, GIF. The tool sends JPEG (photos
   are re-encoded), GIF as-is, and videos as-is only when `include_videos = true`. Anything else
   is recorded in `skipped_items` as `UnsupportedType` and not retried.
7. **Did SMTP accept it?** A `send_failed: ...` error in `status` or the log means the mail
   server rejected the batch; see the SMTP sections below. Items in a rejected batch are not
   marked sent and are retried next run.
8. **Patience.** Skylight usually shows new photos within a few minutes, but it is not instant.

## Gmail: "Username and Password not accepted"

`test-email failed: Sending via smtp.gmail.com:587 failed: 5.7.8 Username and Password not accepted. ...`

Gmail does not accept your Google account password over SMTP. You need an **app password**:

1. Turn on 2-Step Verification for the Google account.
2. Go to <https://myaccount.google.com/apppasswords>, create one named e.g. "Skylight sync".
3. Put the 16-character password (spaces optional) in `secrets.toml` under
   `[smtp] password`, and make sure `[smtp] username` and `[email] from` are the full Gmail
   address.
4. `skylight-sync test-email`.

Google Workspace accounts may have SMTP or app passwords disabled by the administrator. Any other
SMTP provider that supports STARTTLS on port 587 works; set `[smtp] host`, `port` and `username`
accordingly. The tool requires STARTTLS for every host except `localhost`/loopback.

## Gmail: "An incomplete certificate revocation check occurred"

Versions before 2026-11-10 failed every connection to `smtp.gmail.com` from a Mac with
`SslHandshakeException ... An incomplete certificate revocation check occurred`. Google's mail
certificates publish a certificate revocation list (CRL) and no OCSP responder, and .NET on macOS
does not download CRLs, so MailKit's default revocation check could never finish.

The tool now skips only the revocation lookup when it connects (`CheckCertificateRevocation = false`
on the MailKit client). The certificate chain is still validated against the system roots, so a
forged, expired or wrong-host certificate is still rejected; what is lost is detection of a Google
certificate revoked before its expiry date. If you still see this error, the installed build
predates the fix; reinstall it from an updated clone. `dotnet tool update` does not pick up the fix, because the package
version stays `0.1.0` and NuGet reuses the cached package; follow the upgrade steps in
[setup.md](setup.md#2-install) instead:

```bash
git pull
dotnet tool uninstall --global SkylightPhotoSync
rm -rf ~/.nuget/packages/skylightphotosync
dotnet pack src/SkylightPhotoSync -c Release -o ./artifacts
dotnet tool install --global SkylightPhotoSync --add-source ./artifacts
```

## Other SMTP failures

- `send_failed: Sending via host:port failed: ...` with a connection or timeout error: the host
  or port is wrong, or the network is down. Try `skylight-sync test-email` from a terminal.
- A `552` or "message size exceeds" error: the batch is bigger than the server allows. Gmail's
  limit is 25 MB for the whole message after base64 encoding (about a third larger than the raw
  attachments). Lower `[email] max_total_bytes`.
- `secrets.toml is accessible by other users; it must be mode 0600.` Run
  `chmod 600 ~/.config/skylight-photo-sync/secrets.toml`. `run`, `test-email`, `login`, `logout`
  and `record-fixtures` refuse a loosely permissioned secrets file.

## Lock held (exit code 4)

`Another sync run holds the lock file /Users/you/Library/Application Support/skylight-photo-sync/run.lock.`

Two runs tried to overlap (a manual `run` while the hourly one is going, or an hourly run that is
still downloading a large backlog when the next interval fires). The second one exits
immediately with code 4 without sending anything, which is the intended behaviour. Wait for the
running one to finish; `status` shows `in progress` for the last run while it is.

The `run.lock` file staying on disk between runs is normal and harmless: the lock is held by an
open process, not by the file existing, and macOS releases it the moment that process exits, even
if it was killed. So a persistent exit 4 means some process really is still holding it. Find it
with `lsof ~/Library/Application\ Support/skylight-photo-sync/run.lock` and wait for it or stop
it; deleting the file does not help.

## `lookback_days = 0` refused in all mode

`run failed: [source] lookback_days = 0 is not allowed when mode = "all"; it would select every photo in the library.`

In `all` mode a window of 0 days means "my whole Amazon Photos library", which would be a very
large surprise on the frame. Set `lookback_days` to a positive number (`30` is the default when
the key is absent) or switch to `mode = "album"`, where 0 means "the whole album" and is allowed.
The same check applies to the command-line override `--mode all --lookback-days 0`.

## Album not found

`run failed: InvalidOperationException: Album 'Skylight' was not found in Amazon Photos. Albums seen: Family, Holidays 2025, ...`

`[source] album_name` must match an album name in Amazon Photos exactly, including case and
spaces. The message lists the albums the account does have; copy the right one into
`config.toml`. If the list is empty, the album may belong to another account in your Family
Vault (only your own albums are visible) or you signed in to a different Amazon account than you
expected (`skylight-sync login` prints the account name).

## HEIC photos are skipped or fail to convert

`sips could not convert '...' to JPEG (exit code N): ...`

HEIC and HEIF photos are converted with macOS's built-in `sips` before the final resize. This is
why the Mac is the supported platform: on Linux there is no `sips`, and every HEIC item fails
with this error (JPEG and PNG still work). On a Mac, this error for a single file usually means
the download is corrupt or the file is not really HEIC. The item is recorded in `skipped_items`
as `UnsupportedType` with this message as the detail, the run continues with the next item, and
the item is not retried unless you `reset --yes`.

## Resending everything: `reset --yes`

```bash
skylight-sync reset --yes
# Forgot 312 sent items; the next run starts over.
```

`reset` empties both `sent_items` and `skipped_items`, so the next run treats every item in the
window as new: it emails everything again and retries anything it had given up on. Use it after
wiping the frame, switching to a different Skylight address, or fixing whatever caused a batch of
skips. Without `--yes` it refuses and explains. It does not touch `needs_login`, the run history,
the config, or the secrets.

To resend a smaller set, lower `lookback_days` first, or use `run --limit N` after the reset so
the backlog goes out a few photos per run.

## launchd says "not installed" or "next run unknown"

- `launchd: not installed`: run `skylight-sync install-launchd`. It prints the plist path on
  success.
- `launchd: installed, next run unknown`: the plist exists but `launchctl print` did not report a
  next-run time, which happens when the agent is not actually loaded or while a run is in
  progress. `launchctl print gui/$(id -u)/com.mattfromrva.skylight-photo-sync` shows the agent's
  state. `skylight-sync uninstall-launchd` then `install-launchd` reloads it.
- The agent runs only while you are logged in to the Mac, and runs any interval it missed while
  the Mac was asleep as soon as it wakes.
- If you moved or reinstalled the tool (`dotnet tool update`, a different path), the plist still
  points at the old executable path. Run `install-launchd` again.

## Still stuck

Open an issue at <https://github.com/MattFromRVA/skylight-photo-sync/issues> with the output of
`skylight-sync status`, the relevant log lines, and your `config.toml`. **Never include
`secrets.toml`, the refresh token, or any cookie value.**
