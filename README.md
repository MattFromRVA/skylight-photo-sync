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

## Quick start

The tool is a .NET global tool called `skylight-sync`. You need macOS and the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

**Install.** Once the package is published to NuGet
([issue #17](https://github.com/MattFromRVA/skylight-photo-sync/issues/17)) this will be:

```bash
dotnet tool install --global SkylightPhotoSync
```

Until then, pack it from a clone and install from the local package folder:

```bash
git clone https://github.com/MattFromRVA/skylight-photo-sync.git
cd skylight-photo-sync
dotnet pack src/SkylightPhotoSync -c Release -o ./artifacts
dotnet tool install --global SkylightPhotoSync --add-source ./artifacts
skylight-sync --help
```

**First run.** Each step is explained in [docs/setup.md](docs/setup.md).

```bash
mkdir -p ~/.config/skylight-photo-sync
# 1. Write config.toml (album, Skylight address, SMTP server); see docs/setup.md for a full example.
skylight-sync login             # 2. paste your Amazon cookies (docs/cookies.md)
# 3. Add the SMTP password to secrets.toml
skylight-sync test-email        # 4. one test image; approve the sender in Skylight when asked
skylight-sync run --dry-run     # 5. counts what would be sent, sends nothing
skylight-sync run --limit 1     # 6. sends one photo
skylight-sync install-launchd   # 7. runs hourly from now on
skylight-sync status
```

## Documentation

- [docs/setup.md](docs/setup.md): prerequisites, install, every `config.toml` key,
  secrets, first run, the hourly schedule, where logs and state live, alerts.
- [docs/cookies.md](docs/cookies.md): copying the three Amazon cookies from Safari or
  Chrome and pasting them into `skylight-sync login`.
- [docs/troubleshooting.md](docs/troubleshooting.md): `needs_login`, HTTP 429, photos not
  appearing on the frame, Gmail app passwords, exit codes.
- [docs/research-notes.md](docs/research-notes.md): why the design looks like this, with
  sources.

## Configuration at a glance

`~/.config/skylight-photo-sync/config.toml`:

```toml
[source]
mode = "album"          # "album" (recommended) or "all"
album_name = "Skylight"
lookback_days = 0       # album mode: 0 = whole album; all mode: must be > 0 (default 30)
include_videos = false

[email]
to = ["yourhousehold@ourskylight.com"]
from = "you@gmail.com"

[smtp]
host = "smtp.gmail.com"
port = 587
username = "you@gmail.com"
```

Secrets (Amazon cookies, SMTP password) live in a separate `secrets.toml` with `0600`
permissions and are never committed. The full list of keys and defaults is in
[docs/setup.md](docs/setup.md#3-write-configtoml).

## Requirements

- macOS (for `launchd`, `sips` HEIC conversion and notifications)
- .NET 10 SDK (the tool is written in C#)
- An Amazon Photos account
- A Skylight Calendar with the Plus plan (for the photo screensaver) or a Skylight Frame
- An SMTP account to send from (Gmail with an app password works)

## Status

**v1 code is complete.** All twelve code tasks of the
[implementation plan](docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md) are
merged: configuration, the sync pipeline, SQLite sent-state, image normalization, email
batching, SMTP, alerts, the Amazon Photos client, and the `run`, `status`, `test-email`,
`reset`, `login`, `install-launchd`, `uninstall-launchd` and `record-fixtures` commands.

**Not yet verified against a live Amazon session.** The Amazon client is written against
documented endpoint shapes and tested with clearly labelled placeholder fixtures. Recording
real, redacted fixtures with `skylight-sync record-fixtures` and fixing the client against
them is still pending ([issue #12](https://github.com/MattFromRVA/skylight-photo-sync/issues/12),
Steps 2 and 3). Until that lands, expect `login` or `run` to fail against the real service;
`run --source local:PATH` exercises everything except Amazon.

Planned: a one-time browser device login that replaces pasted cookies and stops them
expiring every few days ([issue #30](https://github.com/MattFromRVA/skylight-photo-sync/issues/30)),
and publishing to NuGet ([issue #17](https://github.com/MattFromRVA/skylight-photo-sync/issues/17)).

Nightly build: [![Nightly](https://github.com/MattFromRVA/skylight-photo-sync/actions/workflows/nightly.yml/badge.svg)](https://github.com/MattFromRVA/skylight-photo-sync/actions/workflows/nightly.yml)
builds, formats and tests on Linux and macOS every night (07:00 UTC) and packs the
`skylight-sync` tool as a downloadable artifact. A failed scheduled run opens or updates the
"Nightly build failed" issue.

Design: [spec](docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md) and
[plan](docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md). Work is tracked in
[GitHub issues](https://github.com/MattFromRVA/skylight-photo-sync/issues): issues #1–#13 are
the v1 tasks (one per agent session, issue number = plan task number); later numbers are
follow-ups. Agents: read [AGENTS.md](AGENTS.md) first.

## Built with

C# on .NET 10, packaged as a `dotnet` global tool. System.CommandLine, MailKit,
Microsoft.Data.Sqlite, Tomlyn, SkiaSharp, and macOS's own `sips` for HEIC decoding.

## Why email, and why cookies?

- **Skylight** has no public API. Emailing photos to the household address is the
  supported path and works for both Calendar (Plus plan) and Frame.
- **Amazon Photos** has no public API and discontinued desktop sync in 2024. The only
  automated way to read your library is the web app's private endpoints, authenticated
  with cookies from a logged-in browser session. Those cookies expire on Amazon's
  schedule, so this tool is built to notice and ask you to log in again.

Details and sources: [docs/research-notes.md](docs/research-notes.md).

## License

MIT
