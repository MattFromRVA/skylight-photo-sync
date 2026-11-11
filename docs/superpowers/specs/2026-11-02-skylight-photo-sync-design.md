# Skylight Photo Sync — Design

**Date:** 2026-11-02
**Status:** Draft for approval (builder-check applied 2026-11-02; switched to C#/.NET 2026-11-04)
**Owner:** Matt (MattFromRVA)

## 1. Intent

Matt's family photos land in Amazon Photos automatically from their phones. Their
Skylight Calendar shows a photo screensaver when idle, but today getting a photo onto it
means opening the Skylight app or forwarding an email by hand. This tool removes that
step: a background job on Matt's Mac notices new Amazon Photos and emails them to the
Skylight household address on a schedule, so the frame keeps itself fresh.

Success looks like: Matt drops a photo into an album on his phone (or just takes one, in
"all photos" mode), and within the hour it is on the Calendar, with no further action.
If the pipeline breaks (most likely because Amazon's session cookies expired), Matt is
told clearly and once, not left to notice a stale frame weeks later.

## 2. Research summary (why the design looks like this)

Full notes with sources: [`docs/research-notes.md`](../../research-notes.md).

- **Skylight accepts photos by email.** Every household has a fixed address
  `<name>@ourskylight.com`. Email is the officially supported ingestion path and works for
  Skylight Calendar (photo screensaver requires the Plus plan). Limit 25 MB per email;
  JPEG/HEIC/PNG and MP4/MOV/GIF accepted. New senders need one-time approval by the owner
  when privacy is set to "Senders who I approve".
- **Skylight has no public API.** A community-documented private API exists
  (`app.ourskylight.com/api`, OAuth PKCE). We do not build on it in v1.
- **Amazon Photos has no public API** and discontinued desktop two-way sync in Feb 2024.
  The only automated read path is the web app's private endpoints, authenticated with
  cookies copied from a logged-in browser (`session-id`, `ubid-main`, `at-main`). The
  unofficial `amazon-photos` Python library documents these endpoints but is unmaintained
  (last release Jan 2024) with known cookie-naming and 429 rate-limit bugs. Cookies expire
  on Amazon's schedule and must be refreshed by a human.
- **Prior art:** `immich-skylight-mailer` (TypeScript) does the same job from a different
  photo source: pull new photos → JPEG → batched SMTP emails → Skylight, with a sent-state
  file and cron scheduling. We follow that shape.

## 3. Goals

1. Automatically email new Amazon Photos to a Skylight household address on a schedule.
2. Two source modes, switchable in config: **one curated album** or **all photos**.
3. A configurable lookback window (e.g. last 30 / 60 / 90 days) that bounds what is
   considered "new", in both modes.
4. Never send the same photo twice. Never flood the frame.
5. Fail loudly: when Amazon auth breaks, Matt gets one clear alert and instructions.
6. Simple to run on a Mac: one install command sets up the schedule.
7. Simple to hand to coding agents: small modules, clear seams, tests that run offline.

## 4. Non-goals (v1)

- Removing photos from Skylight, or mirroring album deletions. Email is one-way.
  (Superseded after v1 by the optional `[rotation]` feature, which deletes the tool's own older
  photos through Skylight's unofficial app API; see `2026-11-10-skylight-rotation-spike.md`.
  Mirroring album deletions is still out of scope.)
- Using Skylight's private API. The sender is behind an interface so it could be added.
- Windows or Linux support. It will likely work, but the Mac is the target and only
  launchd scheduling is provided.
- Uploading *to* Amazon Photos.
- Reading other family members' Amazon accounts or the Family Vault. **(call)** v1 reads
  exactly one Amazon account: the one whose cookies are pasted. Photos other people add
  to a *shared album* owned by that account are included; photos that only exist in
  another member's account are not. Family Vault support is a follow-up issue.
- A GUI. This is a CLI plus a scheduled job.
- Automating Amazon login (no password handling, no headless browser). Cookies are
  pasted by the user.

## 5. Anti-goals (what would make this a failure even if it "works")

- Sending hundreds of photos to the frame on first run or after a config change.
- Sending screenshots, receipts, or private images that happen to land in Amazon Photos
  (mitigated by album mode being the recommended default and by the lookback cap).
- Silently stopping. A frame that quietly stops updating is worse than one that alerts.
- Leaking location data: GPS EXIF is stripped before sending.
- Storing Amazon cookies or the SMTP password in the repo or in world-readable files.

## 6. Decisions

Decisions marked **(Matt)** were made by Matt in conversation. Decisions marked
**(call)** were made by Claude as sensible defaults and are open to change.

### 6.1 Platform and runtime

- **(Matt)** Runs on Matt's Mac. The README says so prominently.
- **(Matt)** C# on .NET 10 (LTS), packaged as a .NET global tool (`dotnet tool install`)
  with command name `skylight-sync`. Reason: Matt's other projects are .NET and he will
  maintain this one. The Python reference client is used only as endpoint documentation.
- **(call)** Libraries: System.CommandLine (CLI), MailKit (SMTP), Microsoft.Data.Sqlite
  (state), Tomlyn (TOML config), SkiaSharp (orientation, resize, JPEG encode; re-encoding
  drops EXIF/GPS), Serilog with a rolling file sink (logs), xUnit + the `SmtpServer` NuGet
  package (in-process SMTP stub for tests).
- **(call)** Scheduling via a launchd LaunchAgent (`~/Library/LaunchAgents/`), installed
  by `skylight-sync install-launchd`, default interval 60 minutes.
- **(call)** Delivery via SMTP to the Skylight household address. Default example is
  Gmail with an app password; any SMTP server works. Email is the officially supported
  Skylight path.

### 6.2 Source modes and windows

- **(Matt)** `source.mode = "album" | "all"`, toggled in config.
  - `album`: only items in the named Amazon Photos album (`source.album_name`).
  - `all`: every photo (and optionally video) in the account.
- **(Matt)** `source.lookback_days` (e.g. 30, 60, 90) bounds what is considered.
  `0` means no date bound and is allowed in album mode only; it is refused in `all` mode.
- **(call)** Defaults differ by mode: album mode defaults to `lookback_days = 0` (the whole
  album, flood-guarded by `max_items_per_run`) so that an old photo dropped into the album
  today still gets sent; `all` mode defaults to `lookback_days = 30`. Setting the key
  explicitly overrides either default.
- **(call)** The lookback filters on the item's *capture date* (Amazon `contentDate`,
  falling back to `createdDate`). Amazon album children are believed to carry only the
  photo's own dates, not an "added to album" time, which is why album mode defaults to
  no bound. If the builder finds an added-to-album timestamp, prefer it in album mode.
- **(call)** `source.include_videos = false` by default. When true, videos ≤ 22 MB are
  sent as-is; larger ones are logged and skipped.
- **(call)** Per-run cap `limits.max_items_per_run = 25`. Items beyond the cap wait for
  the next run, oldest first. This is the flood guard, especially on first run.

### 6.3 Dedupe and state

- **(call)** SQLite database at
  `~/Library/Application Support/skylight-photo-sync/state.db`. Tables: `sent_items`
  (item_id, md5, sent_at, recipient, batch_id; `item_id` is the source's id, i.e. the Amazon
  node id for Amazon), `skipped_items` (item_id, reason, detail, skipped_at), `runs`
  (started_at, finished_at, mode, found, sent, skipped, error), `auth_state` (needs_login
  flag, last_401_at), `alerts` (kind, last_at).
- Dedupe key is the Amazon node id, with md5 as a second check so the same image added
  twice (e.g. re-uploaded) is not resent.
- An item is marked sent only after the SMTP server accepts the message.
- Switching `mode` or `lookback_days` does not reset state; already-sent items stay sent.
- `skylight-sync reset --yes` clears sent and skipped state deliberately.
  `skylight-sync reset --skipped` clears only the skipped state (issue #47).

### 6.4 Image processing

- **(call)** Convert HEIC/PNG to JPEG. Apply EXIF orientation, then strip all EXIF
  (removes GPS). Resize so the long edge ≤ `image.max_long_edge` (default 2048; the 15"
  Skylight Calendar panel is 1920×1080). JPEG quality `image.jpeg_quality` (default 88).
- **(call)** HEIC decoding uses macOS itself: the built-in `sips` tool converts HEIC to an
  intermediate JPEG (EXIF preserved), then SkiaSharp loads it, applies the EXIF
  orientation (`SKCodec.EncodedOrigin`), converts to sRGB (iPhones shoot Display P3), resizes
  and re-encodes, which drops all EXIF/XMP/GPS metadata (only an sRGB ICC profile remains).
  .NET imaging libraries cannot decode HEIC on their own, and using `sips` fits the
  Mac-only decision without pulling in a HEIC codec (libheif) dependency. The HEIC step
  sits behind an `IHeicConverter` interface so tests can fake it on Linux CI.
- GIFs are passed through unchanged.
- Processing happens in a temp directory that is deleted after each run.

### 6.5 Email batching

- **(call)** Up to `email.max_attachments = 12` per message and
  `email.max_total_bytes = 17_000_000` raw attachment bytes. Base64 adds ~37%, so 17 MB
  raw is ~23.5 MB on the wire, under Skylight's 25 MB and Gmail's practical ceiling. There
  is no bounce or delivery feedback from Skylight, so this is a safety limit, not a tuning
  knob: items are marked sent when SMTP accepts the message, and an email Skylight drops
  is lost silently. `skylight-sync test-email --size-probe` can send a one-time series of
  growing test emails so the user can find the real ceiling by watching the frame. Subject: `Skylight Photo Sync — N photos (YYYY-MM-DD)`. Body: one
  line listing the source mode and album. Filenames: `YYYY-MM-DD_HHMMSS_<shortid>.jpg`.
- Items are sent oldest-first so the frame's order is roughly chronological.
- `email.to` may list several addresses (e.g. a Calendar and a Frame). State is tracked
  per recipient.

### 6.6 Amazon Photos client

- **(call)** Write a thin in-repo client (`skylight_sync/amazon.py`) rather than depending
  on the unmaintained `amazon-photos` package. Use that package's source and docs as the
  endpoint reference: account endpoint discovery, album listing, album children, search by
  date range, node content download. Accept both `at-main`/`ubid-main` and
  `at_main`/`ubid_main` cookie spellings. Sequential requests; on HTTP 429 back off 60 s,
  max 3 retries; on 401 raise `AuthExpired` immediately (no retries).
- Cookies are entered with `skylight-sync login`, which accepts either the three cookie
  values or a pasted "Copy as cURL" from browser dev tools, and writes
  `~/.config/skylight-photo-sync/secrets.toml` with mode 0600.
- The client is behind a `PhotoSource` protocol (`list_candidates(window) ->
  Iterable[Item]`, `download(item, dest)`) so a different source (local folder, iCloud,
  Immich) could be added later. A `LocalFolderSource` is included for testing and as a
  working fallback.

### 6.7 Failure handling and alerts

- **(call)** On `AuthExpired`: set `auth_state.needs_login`, send ONE alert email to
  `alerts.email` (default: the SMTP username) with the re-login steps, post a macOS
  notification via `osascript`, and exit non-zero. Subsequent scheduled runs check the
  flag and exit quickly without hammering Amazon until `skylight-sync login` clears it.
- Any other error: log, exit non-zero, no alert on the first occurrence; alert after 3
  consecutive failed runs. Alerts are rate-limited to one per 24 h per kind.
- Logs: `~/Library/Logs/skylight-photo-sync/sync.log`, rotated at 5 MB × 5.
- `skylight-sync status` prints last run, items sent, auth state, next scheduled run.
- `--dry-run` does everything except send, and prints what would be emailed.

### 6.8 Configuration

`~/.config/skylight-photo-sync/config.toml` (non-secret) and `secrets.toml` (0600).

```toml
# config.toml
[source]
mode = "album"            # "album" | "all"
album_name = "Skylight"
lookback_days = 0         # album default 0 (whole album); all-mode default 30; 0 refused in all mode
include_videos = false

[limits]
max_items_per_run = 25

[image]
max_long_edge = 2048
jpeg_quality = 88

[email]
to = ["yourhousehold@ourskylight.com"]
from = "you@gmail.com"
max_attachments = 12
max_total_bytes = 17000000

[smtp]
host = "smtp.gmail.com"
port = 587
username = "you@gmail.com"

[alerts]
email = "you@gmail.com"

[schedule]
interval_minutes = 60
```

```toml
# secrets.toml (0600)
[amazon]
session_id = "..."
ubid_main = "..."
at_main = "..."

[smtp]
password = "app-password-here"
```

### 6.9 CLI

```
skylight-sync login            # one-time browser device login (issue #30); --cookies pastes cookies instead; clears needs_login
skylight-sync logout           # remove the device from the Amazon account; sets needs_login
skylight-sync run [--dry-run] [--mode album|all] [--lookback-days N] [--limit N]
skylight-sync status
skylight-sync test-email [--size-probe]   # one small test image; probe sends growing sizes
skylight-sync install-launchd  # writes and loads the LaunchAgent plist
skylight-sync uninstall-launchd
skylight-sync reset --yes
skylight-sync reset --skipped  # only the skipped items; no --yes needed (issue #47)
```

### 6.10 Repository and quality

- MIT license. Public repo `MattFromRVA/skylight-photo-sync`.
- .NET 10 solution: `src/SkylightPhotoSync` (console app, packed as a global tool) and
  `tests/SkylightPhotoSync.Tests` (xUnit). `TreatWarningsAsErrors`, nullable enabled,
  `dotnet format --verify-no-changes` in CI. GitHub Actions runs build, format check and
  tests on push and PR (no network, no secrets). Tests that need macOS tools (`sips`,
  `osascript`, `launchctl`) are skipped on Linux via a `MacOnlyFact` attribute; the
  code paths that call them are behind interfaces and tested with fakes everywhere.
- Tests use recorded JSON fixtures for the Amazon endpoints and an in-process SMTP stub
  so the whole pipeline runs offline.
- Work is tracked as GitHub issues sized for a single agent session each, labelled by
  area, with a milestone for v1.

## 7. First-run experience (acceptance walkthrough)

1. `dotnet tool install --global SkylightPhotoSync` (or pack from a clone and install with
   `--add-source`).
2. `skylight-sync login` → paste cookies → "Amazon Photos: connected, 12,345 items".
3. Edit `config.toml`: album name, Skylight address, SMTP.
4. `skylight-sync test-email` → one photo arrives; Matt approves the sender in the Skylight
   approval email (if privacy is "Senders who I approve").
5. `skylight-sync run --dry-run` → lists up to 25 photos it would send.
6. `skylight-sync run` → photos appear on the Calendar within minutes.
7. `skylight-sync install-launchd` → runs hourly from now on.
8. Weeks later cookies expire → one alert email + Mac notification → `skylight-sync login`
   → back to normal.

## 8. Build sequencing constraint

The Amazon client can only be verified against a live, logged-in Amazon session, which
only Matt can provide, and the reference library is two years stale. Therefore:

- Everything other than the Amazon client is built and tested first against
  `LocalFolderSource`, so the pipeline, state, email, CLI and launchd pieces are provably
  working before Amazon is touched.
- The Amazon client ships with a `skylight-sync record-fixtures` command that calls each
  endpoint once, redacts ids and personal fields, and writes JSON fixtures under
  `tests/SkylightPhotoSync.Tests/Fixtures/Amazon/`. Matt runs it once after `login`; the recorded fixtures then
  drive the offline tests.
- The Amazon client issue is the last in the v1 milestone and is marked as needing Matt's
  participation for verification.

## 9. Left to the builder

- Exact Amazon endpoint shapes and which date field best represents "added to album".
- Whether to use raw `HttpClient` + `System.Text.Json` or a small typed client wrapper.
- Fixture recording approach and redaction of real node ids.
- Whether `install-launchd` uses `StartInterval` or `StartCalendarInterval`.
- Keychain storage for secrets (optional follow-up; file with 0600 is acceptable for v1).
- Whether to create the rotated-HEIC test fixture with `sips` on a Mac or commit a static
  file; either is fine as long as it has EXIF orientation 6.

## 10. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Amazon changes private endpoints | Medium | Thin client, fixtures, loud failure, `PhotoSource` seam |
| Cookies expire often | High | One-alert flow, `login` command, `needs_login` gate |
| Amazon rate limits / blocks | Medium | Sequential requests, 60 s backoff, hourly cadence, small per-run cap |
| Skylight changes email limits | Low | Config-driven batching |
| Gmail blocks SMTP | Low | App password docs; any SMTP works |
