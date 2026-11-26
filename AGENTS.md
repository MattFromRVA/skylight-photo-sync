# Working in this repo (for coding agents and humans)

## The project in one paragraph

`skylight-sync` is a .NET 10 global tool that runs hourly on Matt's Mac under `launchd`. It finds
new photos in an Amazon Photos album (or all photos), converts and resizes them, and gets them onto
the family's Skylight Calendar frame, remembering what it sent so nothing goes twice. v1 delivers by
email to the household `@ourskylight.com` address. Optional rotation keeps only the newest N photos
by deleting older ones through Skylight's unofficial app API. **Current direction:** upload through
that same API, chosen by `[delivery] method = "email" | "api"`, with email kept as the default and
fallback. Live checks against Matt's real Amazon and Skylight accounts (#44) need Matt; an agent
cannot do them.

## Read first

1. `docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md`: what we are building and why
   (API delivery is §6.11).
2. `docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md`: the task breakdown and the exact
   interface names. v1 issues map to its numbered tasks; post-v1 issues (#44 on) do not, so go by
   the issue's own description and the plan section it names.
3. The issue you are assigned. Its "Depends on" issues must be merged first.

## Branches, commits and PRs

- Start from the latest `main` (there is no `master`): `git fetch origin`, branch from
  `origin/main`, and bring the branch up to date with `origin/main` again before opening or updating
  a PR.
- One issue per branch per PR. Branch names are short descriptive slugs (`delivery-seam`,
  `fix-heic-rotation`); no task or issue number needed.
- PR titles and commit subjects are a plain imperative summary of the change, e.g.
  "Add test-upload command and delivery line in status". No "Task N:" prefix.
- Put `Closes #N` in the PR body to link the issue.
- Run every change through the no-mistakes pipeline before it reaches `main`: commit on the branch,
  then `no-mistakes axi run --intent "<what the issue asks for>"` and drive it until it reports
  `checks-passed`. Fix findings through the pipeline (`no-mistakes axi respond`), not by hand while a
  run is active. A fresh clone needs `no-mistakes init` once.

## Code rules

- Test-first: write the failing test, see it fail, implement, see it pass.
- Gate before every PR: `dotnet build -warnaserror && dotnet format --verify-no-changes && dotnet test`.
- No network in tests. No secrets anywhere in the repo (`secrets.toml`, `*.db`, `*.log` are ignored).
- Use the plan's exact names and signatures (its Interfaces blocks); other work depends on them. If
  one must change, update the plan in the same PR and say why in the PR body. Keep the plan's
  `- [ ]` checkboxes current.
- Target is macOS; CI runs on Linux. Mac-only tools (`sips`, `launchctl`, `osascript`) go behind an
  interface tests can fake. Tests that truly need the Mac tool use `[MacOnlyFact]` so they skip on
  Linux.
- C#: nullable enabled, warnings as errors, file-scoped namespaces, `record` types for config and
  models, `async`/`await` with a `CancellationToken` on I/O.
- Fixtures from live accounts (Amazon, Skylight) must be real, redacted recordings made by Matt.
  Never present invented responses as real: label them clearly as hand-written placeholders, and
  never commit tokens, emails, or frame ids.
