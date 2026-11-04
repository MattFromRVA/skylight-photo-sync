# Working in this repo (for coding agents and humans)

Read first, in this order:

1. `docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md` — what we are building and why.
2. `docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md` — the task breakdown. Every v1
   GitHub issue is one task in this plan; the issue number equals the task number.
3. The issue you are assigned. Its "Depends on" issues must be merged first.

## Rules

- Always start from the latest `main` (the default branch; there is no `master`). Before working a
  ticket, `git fetch origin` and branch from `origin/main`; before opening or updating a PR, bring
  the branch up to date with `origin/main` again (merge or rebase) so CI and review see current code.
- One issue per branch per PR. Branch name `task-<n>-<slug>`. PR title starts with `Task <n>:`.
- Run every change through the no-mistakes pipeline before it reaches `main`: commit on the task
  branch, then `no-mistakes axi run --intent "<what the ticket asks for>"` and drive it until it
  reports `checks-passed`. Fix findings through the pipeline (`no-mistakes axi respond`), not by
  hand while a run is active. A fresh clone needs `no-mistakes init` once first.
- Test-first: write the failing test from the plan, see it fail, implement, see it pass.
- Before opening a PR: `dotnet build -warnaserror && dotnet format --verify-no-changes && dotnet test`.
- No network in tests. No secrets anywhere in the repo. `secrets.toml`, `*.db`, `*.log` are ignored.
- Follow the plan's exact names and signatures (Interfaces blocks); neighbors depend on them.
  If a signature must change, update the plan in the same PR and say why in the PR body.
- Keep the plan's `- [ ]` checkboxes updated in your PR.
- Target platform is macOS. CI runs on Linux; anything Mac-only (`sips`, `launchctl`, `osascript`)
  must be behind an interface that tests can fake, and tests that truly need the Mac tool use the
  `MacOnlyFact` attribute so they skip on Linux.
- C# conventions: nullable enabled, warnings as errors, file-scoped namespaces, `record` types for
  config and models, `async`/`await` with `CancellationToken` on I/O.
- Task 12 (Amazon client) needs Matt's live Amazon session for fixture recording. Do not
  invent fixture data as if it were real; label placeholders clearly.
