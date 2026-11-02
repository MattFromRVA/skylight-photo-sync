# Skylight Photo Sync

Automatically send new Amazon Photos to a Skylight Calendar or Skylight Frame by email,
on a schedule, from your Mac.

> **Runs on a Mac.** This tool is designed to run as a background job on a Mac that is
> on regularly (a desktop or a laptop that is usually at home). It uses macOS `launchd`
> for scheduling and macOS notifications for alerts. It will probably run elsewhere, but
> only the Mac is supported.

## What it does

Every hour (configurable) it:

1. Looks in your Amazon Photos for anything new, either in **one curated album** or in
   **all photos**, within a lookback window you choose (last 30 days, 60 days, …).
2. Downloads the new items, converts HEIC to JPEG, resizes them for the frame, and strips
   location data.
3. Emails them in batches to your Skylight household address
   (`yourname@ourskylight.com`), which is Skylight's officially supported way to add
   photos.
4. Remembers what it sent so nothing is sent twice.
5. Tells you, once and clearly, if Amazon's login cookies expire and need refreshing.

## Why email, and why cookies?

- **Skylight** has no public API. Emailing photos to the household address is the
  supported path and works for both Calendar (Plus plan) and Frame.
- **Amazon Photos** has no public API and discontinued desktop sync in 2024. The only
  automated way to read your library is the web app's private endpoints, authenticated
  with cookies from a logged-in browser session. Those cookies expire on Amazon's
  schedule, so this tool is built to notice and ask you to log in again.

Details and sources: [docs/research-notes.md](docs/research-notes.md).

## Status

Design and planning. See the
[design spec](docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md) and the
[implementation plan](docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md). Work is tracked in
[GitHub issues](https://github.com/MattFromRVA/skylight-photo-sync/issues): issues #1–#13 are
the v1 tasks (one per agent session, issue number = plan task number); #14–#17 are follow-ups.
Agents: read [AGENTS.md](AGENTS.md) first.

## Quick start (once implemented)

```bash
uv tool install skylight-photo-sync
skylight-sync login          # paste Amazon cookies
# edit ~/.config/skylight-photo-sync/config.toml
skylight-sync test-email     # approve the sender in Skylight when prompted
skylight-sync run --dry-run
skylight-sync run
skylight-sync install-launchd
```

## Configuration at a glance

```toml
[source]
mode = "album"          # "album" (recommended) or "all"
album_name = "Skylight"
lookback_days = 30      # 30, 60, 90 … ; 0 = whole album (album mode only)
include_videos = false

[email]
to = ["yourhousehold@ourskylight.com"]
```

Secrets (Amazon cookies, SMTP password) live in a separate `secrets.toml` with `0600`
permissions and are never committed.

## Requirements

- macOS
- Python 3.12+ and [`uv`](https://docs.astral.sh/uv/)
- An Amazon Photos account
- A Skylight Calendar with the Plus plan (for the photo screensaver) or a Skylight Frame
- An SMTP account to send from (Gmail with an app password works)

## License

MIT
