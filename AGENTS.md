# Working in this repo (for coding agents and humans)

Read first, in this order:

1. `docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md` — what we are building and why.
2. `docs/superpowers/plans/2026-11-02-skylight-photo-sync-v1.md` — the task breakdown. Every v1
   GitHub issue is one task in this plan; the issue number equals the task number.
3. The issue you are assigned. Its "Depends on" issues must be merged first.

## Rules

- One issue per branch per PR. Branch name `task-<n>-<slug>`. PR title starts with `Task <n>:`.
- Test-first: write the failing test from the plan, see it fail, implement, see it pass.
- Before opening a PR: `uv run ruff check . && uv run ruff format --check . && uv run mypy src && uv run pytest`.
- No network in tests. No secrets anywhere in the repo. `secrets.toml`, `*.db`, `*.log` are ignored.
- Follow the plan's exact names and signatures (Interfaces blocks); neighbors depend on them.
  If a signature must change, update the plan in the same PR and say why in the PR body.
- Keep the plan's `- [ ]` checkboxes updated in your PR.
- Target platform is macOS. CI runs on Linux; anything Mac-only (launchd, osascript) must be
  behind a seam that tests can fake.
- Task 12 (Amazon client) needs Matt's live Amazon session for fixture recording. Do not
  invent fixture data as if it were real; label placeholders clearly.
