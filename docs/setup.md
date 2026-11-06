# Setup: from nothing to an hourly sync

This walks through the first run end to end: install the tool, point it at one Amazon Photos
album and one Skylight address, prove the email path works, send one photo, then let
`launchd` take over. Budget about twenty minutes, most of it waiting for emails.

> **Status (2026-11).** Everything on this page is implemented. The Amazon Photos client was
> verified against a live Amazon session on 2026-11-06 (`run --dry-run` end to end; see the
> README's status section). The one-time device login (`skylight-sync login`) is implemented
> against the documented flow and awaiting its first live verification; `login --cookies` is the
> verified fallback. Everything from `test-email` onward can also be exercised with
> `run --source local:PATH`.

## 1. Prerequisites

- **A Mac** that is on regularly. Scheduling uses `launchd`, HEIC conversion uses `sips`,
  alerts use macOS notifications. The code builds and its tests run on Linux, but only the Mac
  is supported for actually running it.
- **.NET 10 SDK.** `dotnet --version` should print `10.x`. Install from
  <https://dotnet.microsoft.com/download/dotnet/10.0> or `brew install dotnet`.
- **A Skylight Calendar or Skylight Frame** and its household email address
  (`<name>@ourskylight.com`, shown in the Skylight app under the device's settings). On a
  **Skylight Calendar the photo screensaver requires the Plus plan**; without it the photos
  arrive but nothing displays them. A Skylight Frame needs no plan.
- **An Amazon Photos account** and a browser to sign in with (any device). If you want album mode
  (recommended), create the album in Amazon Photos first and put a few photos in it.
- **An SMTP account to send from.** Gmail works: turn on 2-Step Verification, then create an
  [app password](https://myaccount.google.com/apppasswords) for "Mail". You will use the app
  password, not your Google password. Any other SMTP server that supports STARTTLS also works.

## 2. Install

Once the package is on NuGet ([issue #17](https://github.com/MattFromRVA/skylight-photo-sync/issues/17)):

```bash
dotnet tool install --global SkylightPhotoSync
```

Until then, from a clone:

```bash
git clone https://github.com/MattFromRVA/skylight-photo-sync.git
cd skylight-photo-sync
dotnet pack src/SkylightPhotoSync -c Release -o ./artifacts
dotnet tool install --global SkylightPhotoSync --add-source ./artifacts
```

To upgrade a from-clone install later, `git pull`, then reinstall. The package version stays
`0.1.0` between releases, so `dotnet tool update` would see nothing new and NuGet would reuse the
old cached package; remove both first:

```bash
dotnet tool uninstall --global SkylightPhotoSync
rm -rf ~/.nuget/packages/skylightphotosync
dotnet pack src/SkylightPhotoSync -c Release -o ./artifacts
dotnet tool install --global SkylightPhotoSync --add-source ./artifacts
```

Check it is on your `PATH` (the installer puts it in `~/.dotnet/tools`):

```bash
skylight-sync --version    # skylight-sync 0.1.0
skylight-sync --help
```

## 3. Write config.toml

Non-secret settings live in `~/.config/skylight-photo-sync/config.toml`. Create the directory and
the file:

```bash
mkdir -p ~/.config/skylight-photo-sync
open -e ~/.config/skylight-photo-sync/config.toml   # or your editor of choice
```

A complete file with every key. Only the ones marked **required** must be present; everything
else falls back to the default shown.

```toml
[source]
# "album": send what is in one Amazon Photos album (recommended; you curate what the
# frame shows). "all": send every photo in your library inside the lookback window.
mode = "album"                 # default "album"

# Required in album mode. Matched exactly against your album names.
album_name = "Skylight"

# How far back to look, in days. In album mode 0 means the whole album (default 0).
# In all mode 0 is refused (it would select your entire library); default 30.
lookback_days = 0

# Videos are skipped unless this is true. Skylight accepts MP4/MOV/GIF; videos larger
# than [email] max_total_bytes (and never more than 22 MB) are skipped as oversize because
# they will not fit in one email.
include_videos = false         # default false

[limits]
# Most items one run will send. Hourly runs with this cap drain a backlog gently and
# keep each run well under Amazon's rate limits.
max_items_per_run = 25         # default 25

[image]
# Photos are re-encoded as upright JPEGs with no EXIF (location data is stripped),
# downscaled so the long edge is at most this many pixels. 2048 is plenty for a frame.
max_long_edge = 2048           # default 2048
jpeg_quality = 88              # default 88

[email]
# Required. One or more Skylight household addresses; every photo goes to each of them.
to = ["yourhousehold@ourskylight.com"]

# Required. The From address; this is the sender Skylight asks you to approve (step 6).
from = "you@gmail.com"

# Batching. Attachments are packed oldest-first into emails of at most
# max_attachments files and max_total_bytes of attachment data. Skylight accepts
# 25 MB per email and Gmail sends at most 25 MB; 17 MB of raw bytes becomes about
# 23 MB once base64-encoded, which is why the default sits where it does.
max_attachments = 12           # default 12
max_total_bytes = 17000000     # default 17_000_000

[smtp]
# Required: host and username. The password goes in secrets.toml (step 5).
host = "smtp.gmail.com"
port = 587                     # default 587; STARTTLS is required except for localhost
username = "you@gmail.com"

[alerts]
# Where the "cookies expired" and "sync keeps failing" emails go.
# Defaults to [smtp] username when omitted.
email = "you@gmail.com"

[schedule]
# How often launchd runs `skylight-sync run` once installed (step 9).
interval_minutes = 60          # default 60
```

Config mistakes are reported as `run failed: [section] key ...` with the exact problem, for
example `[source] lookback_days = 0 is not allowed when mode = "all"`.

### Album mode vs all mode

- **`album`** (recommended): drop photos into one album in Amazon Photos and they appear on the
  frame within the hour. `lookback_days = 0` sends the whole album; set it to, say, `60` to ignore
  older album entries.
- **`all`**: every photo you take lands on the frame. `lookback_days` must be greater than 0 and
  defaults to 30, so the first run does not try to send your whole library.

You can switch modes later by editing `mode` (and `lookback_days` if needed). Nothing already
sent is sent again, because dedupe is by Amazon item id, not by mode. For a one-off you can also
override from the command line: `skylight-sync run --mode all --lookback-days 7`.

## 4. Log in to Amazon Photos

`skylight-sync login` signs you in once through your own browser and registers the tool on your
Amazon account as a device named "Skylight Photo Sync". From then on it refreshes its own
session before every run; nothing to copy, nothing that expires on Amazon's schedule. The full
walkthrough, including the cookie fallback, is in [cookies.md](cookies.md). In short:

```bash
skylight-sync login
```

1. It prints a long `https://www.amazon.com/ap/signin?...` address. Open it in a browser.
2. Sign in, including the two-factor prompt (Amazon app approval or an authenticator app is the
   most reliable).
3. You land on a "page not found" at `amazon.com/ap/maplanding`. Copy the whole address from the
   address bar, paste it into the Terminal, press Return.

On success:

```
Amazon Photos: connected as Matt Chaulklin, 12345 items
Device registered as "Skylight Photo Sync"; remove it under Manage Your Content and Devices to revoke.
Saved. Run 'skylight-sync run --dry-run' to verify.
Clear this terminal window's scrollback now (Terminal: Cmd-K); the address you pasted contains a single-use sign-in code.
```

`login` registers the device, checks the new login against Amazon Photos, and only then writes
`refresh_token` and `device_serial` to `~/.config/skylight-photo-sync/secrets.toml` with mode
`0600` and clears the `needs_login` flag. If the check fails it removes the device again and
writes nothing. `skylight-sync logout` removes the device and the keys again.

If the device login does not work for you, `skylight-sync login --cookies` takes the three
session cookies copied from a signed-in browser instead ([cookies.md](cookies.md#pasted-cookies-fallback)).

## 5. Add the SMTP password to secrets.toml

`login` does not know your mail password; add it by hand to the same file. Open
`~/.config/skylight-photo-sync/secrets.toml` and add an `[smtp]` table, keeping the `[amazon]`
table `login` wrote:

```toml
[amazon]
refresh_token = "..."    # written by `skylight-sync login`
device_serial = "..."

[smtp]
password = "your-gmail-app-password"
```

(`login --cookies` writes `session_id`, `ubid_main` and `at_main` instead; the dashed spellings
`session-id`, `ubid-main`, `at-main` are accepted too.) Running `login` again later rewrites the
`[amazon]` table and keeps your `[smtp] password`.

If you created the file yourself, make sure only you can read it, or `run`, `test-email`, `login`,
`logout` and `record-fixtures` will refuse to start:

```bash
chmod 600 ~/.config/skylight-photo-sync/secrets.toml
```

## 6. Send a test email and approve the sender

```bash
skylight-sync test-email
# Sent test image to yourhousehold@ourskylight.com
```

This sends one generated 800x600 JPEG to each address in `[email] to`. It proves the SMTP
settings and the Skylight address before any real photo is touched.

**Approve the sender once.** Skylight's default privacy setting for photos is "Senders who I
approve". The first email from a new sender does not display; instead Skylight asks the account
owner to approve that sender (an approval email to the account owner; the pending sender can also
be approved in the Skylight app or on the frame). Approve `[email] from` once and every later
email from it is accepted. If you would rather not approve, set "Who can send photos?" to
"Anyone" in the Skylight app.

After approving, the test image should be in the frame's photo library within a few minutes. On
a Skylight Calendar you need the Plus plan for photos to show as the screensaver.

### Optional: find the real size limit

```bash
skylight-sync test-email --size-probe
# Sent 5 MB probe (5,242,880 bytes) to yourhousehold@ourskylight.com
# Sent 10 MB probe ...
# ...
# Check which arrived on the frame
```

This sends five emails with generated-noise JPEGs of roughly 5, 10, 15, 20 and 24 MB, with the
size in the subject. Check which arrived on the frame; the largest one that did tells you how
high `[email] max_total_bytes` can safely go. Your SMTP server may reject the biggest probes
(base64 pushes 20 MB of attachment past Gmail's 25 MB message limit): that is reported per probe
and the series continues, and the command only fails if no probe was accepted.

## 7. Dry run

```bash
skylight-sync run --dry-run
# found 25, sent 25, skipped 0, batches 2 (dry run)
```

A dry run does everything except send: lists the album, downloads and normalizes up to
`max_items_per_run` photos, packs them into batches, and marks nothing as sent. It prints the
counts (`found` is the number of new items in the window; in a dry run `sent` is how many would
be sent and `batches` is how many emails they would take), not the individual file names. Exit code 0 means Amazon, your config and the image
pipeline all work. Items that cannot be sent (videos while `include_videos = false`, files too
large for one email, unsupported types) are counted under `skipped` and recorded with their
reason in the state database (see step 10); a dry run records them too.

Useful variations, all of which exist as real flags (`skylight-sync run --help`):

```bash
skylight-sync run --dry-run --limit 3                 # look at only three
skylight-sync run --dry-run --mode all --lookback-days 7
skylight-sync run --dry-run --source "local:$HOME/Pictures/test" --mode all --lookback-days 3650
```

`--source local:PATH` reads a folder on this Mac instead of Amazon Photos, which lets you exercise
the whole pipeline before `login` works. Write the path out in full or with `$HOME`; the tool does
not expand `~`. In `all` mode it reads every photo under that folder (within the lookback window,
by file capture date, so use a large one). In `album` mode it reads `PATH/<album_name>/` instead,
so with the config above `--source "local:$HOME/Pictures/test"` alone expects your photos in
`$HOME/Pictures/test/Skylight/`.

## 8. Send one real photo

```bash
skylight-sync run --limit 1
# found 25, sent 1, skipped 0, batches 1
```

The oldest unsent photo (by capture date) is emailed. It should appear on the frame within a few
minutes. An item is only marked as sent after the SMTP server accepts the message, so a failed
send is retried next run and a successful one is never repeated. When you are happy,
`skylight-sync run` sends up to `max_items_per_run`.

## 9. Install the hourly schedule

```bash
skylight-sync install-launchd
# /Users/you/Library/LaunchAgents/com.mattfromrva.skylight-photo-sync.plist
skylight-sync status
# last run: 2026-11-06 14:02:11 -05:00 ok (found 25, sent 25, skipped 0)
# total sent: 26
# needs_login: no
# auth: device login
# launchd: installed, next run 2026-11-06 15:02:11 -05:00
```

`install-launchd` writes a per-user LaunchAgent labelled `com.mattfromrva.skylight-photo-sync`
that runs `skylight-sync run` every `[schedule] interval_minutes` minutes (`StartInterval`) and
once at login (`RunAtLoad`), appending the tool's output to the log file. It loads the agent with
`launchctl bootstrap gui/<uid>` (falling back to `launchctl load`), so the first run starts
immediately. Reinstalling after changing `interval_minutes` replaces the agent.

`skylight-sync uninstall-launchd` unloads the agent and deletes the plist. Your config, secrets
and sent-state are untouched.

The agent only runs while you are logged in to the Mac; a sleeping Mac runs the missed job when
it wakes. Two overlapping runs cannot happen: the second exits at once with exit code 4 (see
[troubleshooting](troubleshooting.md#lock-held-exit-code-4)).

## 10. Where things live

| What | Path |
|---|---|
| Config | `~/.config/skylight-photo-sync/config.toml` |
| Secrets (mode 0600) | `~/.config/skylight-photo-sync/secrets.toml` |
| Sent-state database (SQLite) | `~/Library/Application Support/skylight-photo-sync/state.db` |
| Run lock | `~/Library/Application Support/skylight-photo-sync/run.lock` |
| Log (rolls at 5 MB, keeps 5) | `~/Library/Logs/skylight-photo-sync/sync.log` |
| LaunchAgent plist | `~/Library/LaunchAgents/com.mattfromrva.skylight-photo-sync.plist` |
| Scratch downloads (deleted after each run) | `$TMPDIR/skylight-photo-sync/run-<id>/` |

Setting the environment variable `SKYLIGHT_SYNC_HOME` puts all of the first five in one directory
instead (`config.toml`, `secrets.toml`, `state.db`, `run.lock`, `sync.log` directly under it).
That is how the tests isolate themselves and is handy for trying a second configuration.

The log records each run's start (mode, album, lookback, limit, source), its summary line and any
error. When run from a terminal, the log lines also go to stderr; under launchd they go only to
the log file. `tail -f ~/Library/Logs/skylight-photo-sync/sync.log` is the quickest way to watch
a run.

Per-item detail lives in the state database rather than the log. `sent_items` has one row per
item and recipient; `skipped_items` has the reason and detail for every item the pipeline gave up
on (these are not retried until `reset --yes`). macOS ships `sqlite3`, so:

```bash
sqlite3 ~/Library/Application\ Support/skylight-photo-sync/state.db \
  "select item_id, reason, detail, skipped_at from skipped_items order by skipped_at desc limit 20"
```

## 11. Alerts and how to recover

Two things raise an alert. An alert is a macOS notification plus an email to `[alerts] email`,
rate-limited to one per kind per 24 hours.

**Amazon login expired** (subject `Skylight Photo Sync: Amazon login expired`). Amazon no longer
accepts the saved login: with the device login that means the "Skylight Photo Sync" device was
removed from the account or revoked; with pasted cookies it means they expired. `run` exits with
code 3, sets `needs_login`, sends this one alert, and every later scheduled run exits 3
immediately without contacting Amazon. The email contains the re-login steps; they boil down to
running `skylight-sync login` again ([cookies.md](cookies.md)), which clears the flag. Syncing
resumes on the next scheduled run.

**Sync failing** (subject `Skylight Photo Sync: Sync failing`). Three scheduled runs in a row
failed for some other reason (SMTP down, Amazon returning errors). A broken `config.toml` never
triggers this alert: the run stops before it is recorded, so the error only shows in the log. The body
carries the last error; `skylight-sync status` and the log have the rest. Nothing is paused: the
next run tries again. See [troubleshooting.md](troubleshooting.md).

## 12. Everyday commands

```bash
skylight-sync status             # last run, totals, needs_login, login kind, next launchd run
skylight-sync run                # one run now, same as the schedule
skylight-sync run --dry-run      # preview
skylight-sync login              # sign in again (device login); login --cookies for pasted cookies
skylight-sync logout             # remove the device from your Amazon account and forget the login
skylight-sync reset --yes        # forget everything sent and skipped; next run resends the whole window
skylight-sync uninstall-launchd  # stop the schedule
```

`reset` without `--yes` refuses and explains what it would do. `record-fixtures` is for
contributors: it records redacted Amazon API responses as test fixtures and is not part of
normal use.
