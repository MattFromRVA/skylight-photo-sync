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
5. Tells you, once and clearly, if its Amazon login stops working and needs a new sign-in.
6. Optionally **rotates** the frame: it keeps only the newest N photos it sent (say 30), deletes
   older ones, and resends old favourites once the album runs dry. Deleting uses Skylight's
   unofficial app API; see [setup step 13](docs/setup.md#13-optional-rotate-photos-on-the-frame).

**Direction (2026-11-16):** the next phase adds photos through Skylight's app API too, so one
signed-in connection both adds and removes them, with email kept as a fallback. The settings and
commands for it exist (`[delivery] method = "api"`, `skylight-sync test-upload`) and the upload
call is built, but none of it has been checked against a live Skylight account yet
([#44](https://github.com/MattFromRVA/skylight-photo-sync/issues/44)), so email is still the
default and the way to send photos: see
[setup step 14](docs/setup.md#14-unverified-add-photos-through-skylights-app-api), [Phase 2 of the plan](docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md#phase-2-skylight-api-delivery-decided-2026-11-16)
and spec §6.11.

## Quick start

The tool is a .NET global tool called `skylight-sync`. You need macOS and the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

**Install.** From NuGet:

```bash
dotnet tool install --global SkylightPhotoSync
skylight-sync --help
```

> This works from the first tagged release onward. No version is on NuGet yet, so until the first
> release is cut ([docs/releasing.md](docs/releasing.md)) the command reports that the package
> cannot be found; use the from-a-clone install below instead.

Or pack it from a clone and install from the local package folder:

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
skylight-sync login             # 2. sign in to Amazon once in your browser (docs/cookies.md)
# 3. Add the SMTP password to secrets.toml
skylight-sync test-email        # 4. one test image; approve the sender in Skylight when asked
skylight-sync run --dry-run     # 5. lists what would be sent, sends nothing
skylight-sync run --limit 1     # 6. sends one photo
skylight-sync install-launchd   # 7. runs hourly from now on
skylight-sync status
```

`skylight-sync test-upload` is the same check for the API path (one test image uploaded to each
frame). The upload is built but unverified against live Skylight until
[#44](https://github.com/MattFromRVA/skylight-photo-sync/issues/44).

## Documentation

- [docs/setup.md](docs/setup.md): prerequisites, install, every `config.toml` key,
  secrets, first run, the hourly schedule, where logs and state live, alerts.
- [docs/cookies.md](docs/cookies.md): the one-time browser device login behind
  `skylight-sync login`, and the pasted-cookie fallback (`login --cookies`).
- [docs/troubleshooting.md](docs/troubleshooting.md): `needs_login`, login problems, HTTP 429,
  photos not appearing on the frame, Gmail app passwords, exit codes.
- [docs/research-notes.md](docs/research-notes.md): why the design looks like this, with
  sources.
- [docs/releasing.md](docs/releasing.md): for the maintainer; how a `v*` tag publishes the tool
  to NuGet, and the one-time API key setup.

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

Photos are sent by email. An optional `[delivery] method = "api"` setting (upload through
Skylight's app API instead) exists but is **unverified**: the upload is built from the community
clients' code and tested only against hand-written stand-ins, not a live Skylight account
([#44](https://github.com/MattFromRVA/skylight-photo-sync/issues/44)), so leave it out unless you
are doing that check.
`skylight-sync status` shows which one is configured (`delivery: email`, or
`delivery: api (Kitchen, Hallway)`), and
[setup step 14](docs/setup.md#14-unverified-add-photos-through-skylights-app-api) describes
the API path.

Secrets (the Amazon device login's refresh token, SMTP password) live in a separate
`secrets.toml` with `0600` permissions and are never committed. The full list of keys and defaults is in
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
`reset`, `login`, `logout`, `install-launchd`, `uninstall-launchd` and `record-fixtures` commands.

**Verified against a live Amazon session on 2026-11-06.** The Amazon client was fixed against
Amazon's real Drive v1 API (the required `resourceVersion=V2` parameters, search context, date
filter and sort; see [docs/research-notes.md](docs/research-notes.md)) and is tested with
redacted fixtures recorded from a real account by `skylight-sync record-fixtures`. On that
account `run --dry-run` listed a 595-photo album end to end
([issue #12](https://github.com/MattFromRVA/skylight-photo-sync/issues/12)).

**Device login verified live on 2026-11-10.** Pasted cookies stopped working within an hour, so
`skylight-sync login` is a one-time browser sign-in that registers the tool as a device on your
Amazon account and refreshes its own session before every run
([issue #30](https://github.com/MattFromRVA/skylight-photo-sync/issues/30)). On a live account
`login`, `run --dry-run` and `status` worked as documented, and the per-run cookie exchange is
tested against a redacted recording made by `skylight-sync record-fixtures --auth`
(`tests/SkylightPhotoSync.Tests/Fixtures/AmazonAuth/exchange_cookies.json`). `login --cookies`
remains as the fallback.

In progress: adding photos through Skylight's app API (plan Phase 2). The delivery setting,
rotation by recorded photo id, `skylight-sync test-upload` and the `status` delivery line are
built and tested against an in-memory account
([#57](https://github.com/MattFromRVA/skylight-photo-sync/issues/57)–[#59](https://github.com/MattFromRVA/skylight-photo-sync/issues/59)).
The upload call itself ([#16](https://github.com/MattFromRVA/skylight-photo-sync/issues/16)) is
built from jakewilkins/skylight-frame's flow and tested against hand-written placeholder
responses only. Nothing on the API path has been verified against live Skylight yet; that is the
live check ([#44](https://github.com/MattFromRVA/skylight-photo-sync/issues/44)), and email stays
the default until
[#60](https://github.com/MattFromRVA/skylight-photo-sync/issues/60).

Publishing to NuGet: pushing a `v*` tag runs the
[publish workflow](.github/workflows/publish.yml), which packs the tool and pushes it to nuget.org
([docs/releasing.md](docs/releasing.md)). No release has been tagged yet. A Homebrew tap is a
follow-up ([issue #49](https://github.com/MattFromRVA/skylight-photo-sync/issues/49)).

Nightly build: [![Nightly](https://github.com/MattFromRVA/skylight-photo-sync/actions/workflows/nightly.yml/badge.svg)](https://github.com/MattFromRVA/skylight-photo-sync/actions/workflows/nightly.yml)
builds, formats and tests on Linux and macOS every night (07:00 UTC) and packs the
`skylight-sync` tool as a downloadable artifact. A failed scheduled run opens or updates the
"Nightly build failed" issue.

Design: [spec](docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md) and
[plan](docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md). Work is tracked in
[GitHub issues](https://github.com/MattFromRVA/skylight-photo-sync/issues): issues #1–#13 are
the v1 tasks in the plan; later issues are follow-ups. Agents: read [AGENTS.md](AGENTS.md) first.

## Built with

C# on .NET 10, packaged as a `dotnet` global tool. System.CommandLine, MailKit,
Microsoft.Data.Sqlite, Tomlyn, SkiaSharp, and macOS's own `sips` for HEIC decoding.

## Why email, and why a browser sign-in?

- **Skylight** has no public API. Emailing photos to the household address is the
  supported path and works for both Calendar (Plus plan) and Frame. Email can only add photos,
  though, so rotation uses the unofficial API the Skylight apps use, and from Phase 2 that API
  adds photos too (spec §6.11). Email stays as the fallback if Skylight changes the API.
- **Amazon Photos** has no public API and discontinued desktop sync in 2024. The only
  automated way to read your library is the web app's private endpoints, which need a
  logged-in Amazon session. Rather than store your password (and fight 2FA and CAPTCHAs
  every run) or copy browser cookies that expire within hours, the tool borrows the login
  flow of the Audible and Alexa apps: you sign in once in your own browser, the tool
  registers itself as a device you can see and remove under "Manage Your Content and
  Devices", and it mints a fresh session from that registration before every run. If the
  device is removed, the tool notices once and asks you to sign in again.

Details and sources: [docs/research-notes.md](docs/research-notes.md).

## License

MIT
