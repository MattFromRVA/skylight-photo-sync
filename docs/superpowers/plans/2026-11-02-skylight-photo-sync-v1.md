# Skylight Photo Sync v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task below is also a GitHub issue in the v1 milestone; one task per agent session.

**Goal:** A Mac-scheduled CLI that emails new Amazon Photos (from one album or all photos, within a lookback window) to a Skylight household address, never twice, and alerts once when Amazon cookies expire.

**Architecture:** A `PhotoSource` protocol yields candidate `Item`s; the pipeline filters them through a SQLite sent-state, downloads and normalizes them to JPEG, packs them into size-bounded batches, and hands each batch to an SMTP mailer. Amazon is one `PhotoSource` implementation built last and verified against live fixtures; `LocalFolderSource` lets everything else be built and tested offline first.

**Tech Stack:** Python 3.12, `uv`, `typer`, `httpx`, `Pillow` + `pillow-heif`, stdlib `sqlite3` / `smtplib` / `email`, `aiosmtpd` (tests), `ruff`, `mypy`, `pytest`, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md`

## Global Constraints

- Python `>=3.12`; project managed with `uv`; package name `skylight-photo-sync`, import name `skylight_sync`, console script `skylight-sync`.
- Config at `~/.config/skylight-photo-sync/config.toml`; secrets at `~/.config/skylight-photo-sync/secrets.toml` (must be mode `0600`); state at `~/Library/Application Support/skylight-photo-sync/state.db`; logs at `~/Library/Logs/skylight-photo-sync/sync.log` (rotate 5 MB × 5). All four paths overridable via `SKYLIGHT_SYNC_HOME` env var (tests set it to a tmp dir; layout under it: `config.toml`, `secrets.toml`, `state.db`, `sync.log`).
- Defaults: `source.mode="album"`, album `lookback_days=0`, all-mode `lookback_days=30`, `include_videos=false`, `max_items_per_run=25`, `max_long_edge=2048`, `jpeg_quality=88`, `max_attachments=12`, `max_total_bytes=17_000_000`, `interval_minutes=60`, video size cap `22_000_000` bytes.
- `lookback_days=0` is refused in `all` mode.
- An item is marked sent only after the SMTP server accepts the message. Items are processed oldest-first (by capture date).
- GPS and all other EXIF are stripped from images after orientation is applied.
- Secrets never appear in logs, test fixtures, or the repo. `.gitignore` already excludes `secrets.toml`, `*.db`, `*.log`.
- Lint: `ruff check` and `ruff format --check` clean. Types: `mypy src` clean. Tests: `pytest` green with no network.
- Commit messages: conventional prefix (`feat:`, `fix:`, `test:`, `chore:`, `docs:`). Each task ends in a PR against `main`.

## Review Focus

Inputs the spec implies but no single task's tests would naturally exercise. Each has a test pinned to the owning task.

1. **Two scheduled runs overlap** (previous run still downloading when launchd fires again): the second must exit immediately without sending. → Task 9 lock-file test.
2. **A single photo larger than `max_total_bytes` even after resize** (e.g. a 40 MB panorama): it must be skipped and recorded as `skipped:oversize`, not block the oldest-first queue forever. → Task 6 + Task 9.
3. **HEIC with EXIF orientation 6 (rotated 90°)**: output JPEG must be upright with no EXIF. → Task 5.
4. **Cookies expire mid-run after some batches were sent**: already-sent items stay sent, `needs_login` is set, exactly one alert goes out, exit code non-zero. → Task 9.
5. **Switching `mode` from `album` to `all`** with existing state: nothing previously sent is resent, and the all-mode lookback default (30) applies if `lookback_days` is unset. → Task 2 + Task 9.

---

## File structure

```
pyproject.toml
.github/workflows/ci.yml
src/skylight_sync/
  __init__.py          __version__
  paths.py             resolve the four paths, honoring SKYLIGHT_SYNC_HOME
  config.py            Config/Secrets dataclasses, load + validate, mode defaults
  models.py            Item, Window, Batch dataclasses; SkipReason enum
  sources/__init__.py  PhotoSource protocol
  sources/local.py     LocalFolderSource
  sources/amazon.py    AmazonPhotosSource (thin client) + AuthExpired
  image.py             normalize(): HEIC/PNG→JPEG, orient, strip, resize; video passthrough
  state.py             StateStore (sqlite3): sent_items, skipped_items, runs, auth_state, alerts
  batching.py          pack(items) -> list[Batch]
  mailer.py            build_message(), Mailer.send()
  alerts.py            Alerter: email + macOS notification, 24h rate limit
  pipeline.py          run(config, secrets, source, store, mailer, alerter, dry_run) -> RunResult
  launchd.py           plist render/install/uninstall
  fixtures.py          record-fixtures: call endpoints once, redact, write JSON
  cli.py               typer app: login, run, status, test-email, install-launchd, uninstall-launchd, reset, record-fixtures
tests/
  conftest.py          tmp SKYLIGHT_SYNC_HOME, sample images, aiosmtpd fixture
  fixtures/images/     tiny.jpg, tiny.png, tiny_rot6.heic, tiny.gif, tiny.mp4
  fixtures/amazon/     recorded + redacted JSON (Task 12)
  test_*.py            one per module
```

---

### Task 1: Project scaffold and CI

**Files:**
- Create: `pyproject.toml`, `src/skylight_sync/__init__.py`, `src/skylight_sync/cli.py`, `.github/workflows/ci.yml`, `tests/test_cli_version.py`, `tests/conftest.py`

**Interfaces:**
- Produces: `skylight_sync.__version__: str = "0.1.0"`; `cli.app: typer.Typer`; `skylight-sync --version` prints `skylight-sync 0.1.0`.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_cli_version.py
from typer.testing import CliRunner
from skylight_sync.cli import app

def test_version_flag():
    result = CliRunner().invoke(app, ["--version"])
    assert result.exit_code == 0
    assert result.output.strip() == "skylight-sync 0.1.0"
```

- [ ] **Step 2: Run it to verify it fails**

Run: `uv run pytest tests/test_cli_version.py -v` → FAIL (module not found).

- [ ] **Step 3: Create `pyproject.toml`** with `[project] name="skylight-photo-sync" version="0.1.0" requires-python=">=3.12"`, dependencies `typer`, `httpx`, `pillow`, `pillow-heif`; dev group `pytest`, `aiosmtpd`, `ruff`, `mypy`; `[project.scripts] skylight-sync = "skylight_sync.cli:app"`; build backend `hatchling`; `[tool.ruff] line-length=100 target-version="py312"`; `[tool.mypy] strict=true` with `ignore_missing_imports=true`.

- [ ] **Step 4: Create `cli.py`** with a `typer.Typer(no_args_is_help=True)` app and a `--version` eager callback option that prints `skylight-sync {__version__}` and exits. Add `conftest.py` with an autouse fixture setting `SKYLIGHT_SYNC_HOME` to `tmp_path`.

- [ ] **Step 5: Run the test to verify it passes**; run `uv run ruff check . && uv run ruff format --check . && uv run mypy src` → clean.

- [ ] **Step 6: Create `.github/workflows/ci.yml`**: on push and pull_request; `astral-sh/setup-uv@v5`; `uv sync --all-groups`; run ruff check, ruff format --check, mypy src, pytest. macOS runner not required (`ubuntu-latest`).

- [ ] **Step 7: Commit** `chore: scaffold project, CLI entry point and CI`.

---

### Task 2: Paths and configuration

**Files:**
- Create: `src/skylight_sync/paths.py`, `src/skylight_sync/config.py`, `tests/test_config.py`

**Interfaces:**
- Produces:
  - `paths.Paths` dataclass with `config_file, secrets_file, state_db, log_file: Path`; `paths.resolve() -> Paths` (uses `SKYLIGHT_SYNC_HOME` if set, else the four macOS locations in Global Constraints).
  - `config.SourceConfig(mode: Literal["album","all"], album_name: str | None, lookback_days: int, include_videos: bool)`
  - `config.Config(source: SourceConfig, limits: LimitsConfig, image: ImageConfig, email: EmailConfig, smtp: SmtpConfig, alerts: AlertsConfig, schedule: ScheduleConfig)` with field names and defaults exactly as spec §6.8.
  - `config.Secrets(amazon_session_id: str | None, amazon_ubid: str | None, amazon_at: str | None, smtp_password: str | None)`
  - `config.load_config(path: Path) -> Config`, `config.load_secrets(path: Path) -> Secrets`, `config.ConfigError(ValueError)`.
  - `config.write_secrets(path: Path, secrets: Secrets) -> None` writes TOML and `chmod 0600`.

- [ ] **Step 1: Write failing tests**

```python
def test_album_mode_defaults_lookback_zero(tmp_path): ...   # omit lookback_days → 0
def test_all_mode_defaults_lookback_thirty(tmp_path): ...  # mode="all", omit → 30
def test_all_mode_rejects_lookback_zero(tmp_path):         # mode="all", lookback_days=0 → ConfigError
def test_album_mode_requires_album_name(tmp_path):          # mode="album" without album_name → ConfigError
def test_secrets_file_must_be_0600(tmp_path):               # 0644 → ConfigError mentioning "0600"
def test_write_secrets_sets_0600(tmp_path): ...
def test_accepts_dash_and_underscore_cookie_keys(tmp_path): # [amazon] at-main / at_main both load into amazon_at
def test_paths_honor_env(monkeypatch, tmp_path): ...
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement `paths.py` and `config.py`** using `tomllib`; dataclasses with defaults; validation in `load_config` after parsing; mode-dependent default for `lookback_days` applied only when the key is absent.
- [ ] **Step 4: Run tests, lint, mypy → green.**
- [ ] **Step 5: Commit** `feat: config and secrets loading with mode-aware defaults`.

---

### Task 3: Models, PhotoSource protocol, LocalFolderSource

**Files:**
- Create: `src/skylight_sync/models.py`, `src/skylight_sync/sources/__init__.py`, `src/skylight_sync/sources/local.py`, `tests/test_local_source.py`, `tests/fixtures/images/*`

**Interfaces:**
- Produces:
  - `models.Item(id: str, name: str, kind: Literal["photo","video"], captured_at: datetime, size_bytes: int, md5: str | None, mime: str)`
  - `models.Window(since: datetime | None)`; `Window.from_lookback(days: int, now: datetime) -> Window` (days 0 → `since=None`).
  - `models.SkipReason` enum: `oversize`, `video_disabled`, `unsupported_type`, `download_failed`, `processing_failed`.
  - `sources.PhotoSource` Protocol: `list_candidates(self, window: Window, mode: Literal["album","all"], album_name: str | None) -> list[Item]` (sorted oldest-first) and `download(self, item: Item, dest_dir: Path) -> Path`.
  - `sources.local.LocalFolderSource(root: Path)`: `all` mode = every image/video under `root`; `album` mode = files under `root / album_name`. `captured_at` from EXIF `DateTimeOriginal` when present else file mtime; `md5` computed from file bytes.

- [ ] **Step 1: Add fixture images** (generate with Pillow in a `scripts/make_fixtures.py`, committed): `tiny.jpg` (64×48), `tiny.png`, `tiny.gif`, `tiny_rot6.heic` (64×48 content with EXIF orientation 6 written via pillow-heif), `tiny.mp4` (any ≤ 10 KB valid mp4; a static one may be committed).
- [ ] **Step 2: Write failing tests**: `test_window_from_lookback_zero_is_unbounded`, `test_local_all_mode_lists_oldest_first`, `test_local_album_mode_only_subfolder`, `test_local_window_filters_by_captured_at`, `test_local_download_copies_file`.
- [ ] **Step 3: Run to verify they fail.**
- [ ] **Step 4: Implement** models, protocol, `LocalFolderSource`.
- [ ] **Step 5: Run tests, lint, mypy → green.**
- [ ] **Step 6: Commit** `feat: item model, PhotoSource protocol and LocalFolderSource`.

---

### Task 4: SQLite state store

**Files:**
- Create: `src/skylight_sync/state.py`, `tests/test_state.py`

**Interfaces:**
- Produces `state.StateStore(db_path: Path)` with:
  - `is_sent(item_id: str, recipient: str) -> bool`; `is_md5_sent(md5: str, recipient: str) -> bool`
  - `mark_sent(items: list[Item], recipient: str, batch_id: str, sent_at: datetime) -> None`
  - `mark_skipped(item_id: str, reason: SkipReason, detail: str) -> None`; `is_skipped(item_id) -> bool`
  - `start_run(mode: str) -> int`; `finish_run(run_id: int, found: int, sent: int, skipped: int, error: str | None) -> None`
  - `consecutive_failures() -> int` (trailing runs with non-null error)
  - `set_needs_login(flag: bool, at: datetime | None) -> None`; `needs_login() -> bool`
  - `last_alert_at(kind: str) -> datetime | None`; `record_alert(kind: str, at: datetime) -> None`
  - `last_run() -> RunRecord | None`; `reset_sent() -> int`
  - Schema created on first open; `PRAGMA journal_mode=WAL`.

- [ ] **Step 1: Write failing tests** covering each method, plus `test_mark_sent_is_per_recipient` and `test_consecutive_failures_resets_on_success`.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** with stdlib `sqlite3`, parameterized SQL, `datetime` stored as ISO-8601 UTC text.
- [ ] **Step 4: Run tests, lint, mypy → green.**
- [ ] **Step 5: Commit** `feat: sqlite state store for sent items, runs, auth and alerts`.

---

### Task 5: Image normalization

**Files:**
- Create: `src/skylight_sync/image.py`, `tests/test_image.py`

**Interfaces:**
- Produces `image.normalize(src: Path, dest_dir: Path, item: Item, cfg: ImageConfig, video_cap_bytes: int = 22_000_000) -> Path`:
  - photos (JPEG/HEIC/PNG): open via Pillow (pillow-heif registered), `ImageOps.exif_transpose`, convert to RGB, resize so long edge ≤ `cfg.max_long_edge` (never upscale), save JPEG `quality=cfg.jpeg_quality` with **no** `exif` argument. Output name `YYYY-MM-DD_HHMMSS_<id[:8]>.jpg` from `item.captured_at`.
  - GIF: copy unchanged with same naming and `.gif`.
  - video (`mp4`/`mov`): copy unchanged if `size_bytes <= video_cap_bytes`, else raise `image.Oversize`.
  - Unsupported mime → raise `image.Unsupported`.

- [ ] **Step 1: Write failing tests**: `test_heic_rot6_becomes_upright_jpeg_without_exif` (output `.size == (48, 64)`-style orientation check and `getexif()` empty), `test_png_to_jpeg`, `test_large_image_resized_to_max_long_edge` (generate 4000×3000 in-test → long edge 2048), `test_small_image_not_upscaled`, `test_gif_passthrough`, `test_video_over_cap_raises_oversize`, `test_output_filename_pattern`.
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement `image.py`.**
- [ ] **Step 4: Run tests, lint, mypy → green.**
- [ ] **Step 5: Commit** `feat: normalize photos to upright, EXIF-free, resized JPEG`.

---

### Task 6: Batching

**Files:**
- Create: `src/skylight_sync/batching.py`, `tests/test_batching.py`

**Interfaces:**
- Consumes `models.Item`, `models.SkipReason`.
- Produces `models.Batch(id: str, items: list[Item], files: list[Path], total_bytes: int)` and `batching.pack(prepared: list[tuple[Item, Path]], max_attachments: int, max_total_bytes: int) -> tuple[list[Batch], list[tuple[Item, SkipReason]]]`. Input is oldest-first; batches preserve order; a file alone larger than `max_total_bytes` goes to the skipped list with `SkipReason.oversize`. `Batch.id` is `uuid4().hex[:12]`.

- [ ] **Step 1: Write failing tests**: `test_splits_on_attachment_count` (13 items, max 12 → 2 batches), `test_splits_on_total_bytes`, `test_oversize_single_item_is_skipped_not_blocking` (Review Focus 2), `test_preserves_order`.
- [ ] **Step 2: Run to verify they fail.** **Step 3: Implement.** **Step 4: Green.**
- [ ] **Step 5: Commit** `feat: pack prepared items into size-bounded email batches`.

---

### Task 7: Mailer

**Files:**
- Create: `src/skylight_sync/mailer.py`, `tests/test_mailer.py`

**Interfaces:**
- Produces:
  - `mailer.build_message(batch: Batch, to: str, from_addr: str, mode: str, album_name: str | None, now: datetime) -> email.message.EmailMessage` with subject `Skylight Photo Sync — {n} photos ({YYYY-MM-DD})`, one-line plain body `Source: {mode}` or `Source: album "{album_name}"`, attachments named by file basename with correct MIME type.
  - `mailer.Mailer(host: str, port: int, username: str, password: str, use_starttls: bool = True)`; `Mailer.send(msg: EmailMessage) -> None` raises `mailer.SendFailed` on any SMTP error.
- Tests use an `aiosmtpd` `Controller` fixture on `127.0.0.1` ephemeral port with no auth/TLS (`use_starttls=False`).

- [ ] **Step 1: Write failing tests**: `test_subject_and_body`, `test_attachments_present_with_mime`, `test_send_delivers_to_stub_server`, `test_send_failure_raises`.
- [ ] **Step 2–4: fail → implement with `smtplib.SMTP` → green.**
- [ ] **Step 5: Commit** `feat: build MIME batches and send over SMTP`.

---

### Task 8: Alerts

**Files:**
- Create: `src/skylight_sync/alerts.py`, `tests/test_alerts.py`

**Interfaces:**
- Produces `alerts.Alerter(store: StateStore, mailer: Mailer | None, alert_to: str, from_addr: str, notify: Callable[[str, str], None] = alerts.macos_notify, now: Callable[[], datetime] = utcnow)`:
  - `Alerter.alert(kind: Literal["auth_expired","repeated_failure","smtp_down"], title: str, body: str) -> bool` → returns `False` and does nothing if `store.last_alert_at(kind)` is within 24 h; else calls `notify(title, body)`, emails if `mailer` is not None and kind != `smtp_down`, records alert, returns `True`.
  - `alerts.macos_notify(title, body)` runs `osascript -e 'display notification ...'` via `subprocess.run`, swallowing errors.
  - `alerts.AUTH_EXPIRED_BODY` constant: the re-login steps text (run `skylight-sync login`, where to copy cookies).

- [ ] **Step 1: Write failing tests** with a fake `notify` and the aiosmtpd fixture: `test_first_alert_sends_email_and_notification`, `test_second_alert_within_24h_is_suppressed`, `test_alert_after_24h_sends_again`, `test_smtp_down_kind_skips_email`.
- [ ] **Step 2–4: fail → implement → green.** **Step 5: Commit** `feat: rate-limited alerts by email and macOS notification`.

---

### Task 9: Pipeline

**Files:**
- Create: `src/skylight_sync/pipeline.py`, `tests/test_pipeline.py`

**Interfaces:**
- Consumes everything from Tasks 2–8.
- Produces:
  - `pipeline.RunResult(found: int, sent: int, skipped: int, batches: int, dry_run: bool, error: str | None)`
  - `pipeline.run(cfg: Config, source: PhotoSource, store: StateStore, mailer: Mailer, alerter: Alerter, *, dry_run: bool = False, limit: int | None = None, now: Callable[[], datetime] = utcnow, lock_path: Path | None = None) -> RunResult`
  - `pipeline.LockHeld(Exception)`
- Behavior, in order: acquire exclusive `fcntl.flock` on `lock_path` (default `state.db` sibling `run.lock`), else raise `LockHeld`. If `store.needs_login()` → return `RunResult(error="needs_login")` without touching source. `start_run`. `window = Window.from_lookback(cfg.source.lookback_days, now())`. `candidates = source.list_candidates(...)`, drop items where `is_sent` for every recipient, `is_skipped`, or md5 already sent; drop videos when `include_videos` is false (record skip `video_disabled` once). Take the first `min(limit or cfg.limits.max_items_per_run, ...)`. For each: `download` → `normalize` into a `TemporaryDirectory`; failures recorded as `download_failed`/`processing_failed` and continue. `pack`. For each recipient, for each batch: `build_message`, `mailer.send` unless `dry_run`, then `mark_sent`. On `AuthExpired` from the source: `set_needs_login(True)`, `alerter.alert("auth_expired", ...)`, `finish_run(error="auth_expired")`, return with error (items already marked sent stay sent). On `SendFailed`: stop sending, finish with error. After `finish_run`, if `consecutive_failures() >= 3` → `alerter.alert("repeated_failure", ...)`.

- [ ] **Step 1: Write failing tests** using `LocalFolderSource`, real `StateStore` on tmp, aiosmtpd mailer, fake notify, and a `FailingSource` wrapper that raises `AuthExpired` after N downloads:
  `test_sends_new_items_and_marks_sent`, `test_second_run_sends_nothing`, `test_respects_max_items_per_run_oldest_first`, `test_dry_run_sends_nothing_and_marks_nothing`, `test_needs_login_short_circuits`, `test_auth_expired_midrun_keeps_sent_sets_flag_alerts_once` (Review Focus 4), `test_lock_prevents_overlap` (Review Focus 1: hold the lock in the test, expect `LockHeld`), `test_oversize_item_skipped_and_queue_advances` (Review Focus 2), `test_mode_switch_does_not_resend` (Review Focus 5), `test_three_failures_trigger_repeated_failure_alert`.
- [ ] **Step 2–4: fail → implement → green.**
- [ ] **Step 5: Commit** `feat: sync pipeline with dedupe, batching, lock and auth-expiry handling`.

---

### Task 10: CLI commands (except Amazon-specific)

**Files:**
- Modify: `src/skylight_sync/cli.py`; Create: `tests/test_cli.py`

**Interfaces:**
- Consumes Tasks 2–9.
- Produces commands:
  - `run [--dry-run] [--mode album|all] [--lookback-days N] [--limit N] [--source local:PATH]` — CLI flags override config; `--source local:PATH` selects `LocalFolderSource` (default is Amazon, Task 12; until then `run` without `--source` exits 2 with "Amazon source not yet available"). Exit 0 on success, 1 on error, 3 on `needs_login`, 4 on `LockHeld`.
  - `status` — prints last run time/result, totals sent, `needs_login` state, next launchd run if installed (Task 11 provides `launchd.next_run()`; print `not installed` otherwise).
  - `test-email [--size-probe]` — sends `tests/fixtures/images/tiny.jpg`-equivalent generated 800×600 JPEG to each `email.to`; with `--size-probe` sends 5 emails of ~5/10/15/20/24 MB generated noise JPEGs, each subject tagged with its size, and prints "Check which arrived on the frame".
  - `reset --yes` — calls `store.reset_sent()`; refuses without `--yes`.
  - `login` — prompts for either a pasted cURL command (parse `Cookie:` header for `session-id`, `ubid-main|ubid_main`, `at-main|at_main`) or the three values; writes secrets via `write_secrets`; `set_needs_login(False)`; prints "Saved. Run `skylight-sync run --dry-run` to verify." (Live connectivity check added in Task 12.)
  - Logging configured to `paths.log_file` with `RotatingFileHandler(maxBytes=5_000_000, backupCount=5)` plus stderr at INFO.

- [ ] **Step 1: Write failing tests** with `CliRunner`: `test_run_local_source_dry_run_exit_0`, `test_run_exit_code_3_when_needs_login`, `test_reset_requires_yes`, `test_login_parses_curl_paste`, `test_login_accepts_three_values`, `test_status_prints_last_run`, `test_test_email_sends_one_message`.
- [ ] **Step 2–4: fail → implement → green.** **Step 5: Commit** `feat: run, status, test-email, reset and login commands`.

---

### Task 11: launchd install/uninstall

**Files:**
- Create: `src/skylight_sync/launchd.py`, `tests/test_launchd.py`; Modify: `cli.py`

**Interfaces:**
- Produces `launchd.LABEL = "com.mattfromrva.skylight-photo-sync"`; `launchd.render_plist(program: Path, interval_minutes: int, log_file: Path) -> str` (`ProgramArguments=[program, "run"]`, `StartInterval=interval_minutes*60`, `RunAtLoad=true`, `StandardOutPath/StandardErrorPath=log_file`); `launchd.install(cfg: Config, plist_dir: Path = ~/Library/LaunchAgents) -> Path` (writes plist, runs `launchctl bootstrap gui/$UID <plist>`; falls back to `launchctl load`); `launchd.uninstall(plist_dir) -> None`; `launchd.is_installed(plist_dir) -> bool`; `launchd.next_run(plist_dir) -> datetime | None` (best-effort from `launchctl print`; None if unavailable). `program` resolved via `shutil.which("skylight-sync")`.
- CLI: `install-launchd`, `uninstall-launchd`.

- [ ] **Step 1: Write failing tests**: `test_render_plist_contains_interval_and_label`, `test_install_writes_plist_and_calls_launchctl` (monkeypatch `subprocess.run`), `test_uninstall_removes_plist`.
- [ ] **Step 2–4: fail → implement → green.** **Step 5: Commit** `feat: launchd agent install and uninstall`.

---

### Task 12: Amazon Photos source and fixture recorder (needs Matt's live session)

**Files:**
- Create: `src/skylight_sync/sources/amazon.py`, `src/skylight_sync/fixtures.py`, `tests/test_amazon_source.py`, `tests/fixtures/amazon/*.json`; Modify: `cli.py`

**Interfaces:**
- Produces `sources.amazon.AuthExpired(Exception)`, `sources.amazon.AmazonPhotosSource(secrets: Secrets, client: httpx.Client | None = None, tld: str = "com")` implementing `PhotoSource`, plus `whoami() -> tuple[str, int]` (display name, total item count) used by `login`.
- Endpoints (reference only; verify live): `GET https://www.amazon.{tld}/drive/v1/account/endpoint` → `metadataUrl`, `contentUrl`; `GET {metadataUrl}/nodes?filters=kind:VISUAL_COLLECTION` (albums); `GET {metadataUrl}/nodes/{albumId}/children?...sort=["contentDate ASC"]` (album items); `GET {metadataUrl}/search?filters=type:(PHOTOS OR VIDEOS) AND contentDate:[... TO ...]&sort=["contentDate ASC"]` (all mode); `GET {contentUrl}/nodes/{id}/content` (download). Pagination via `startToken`. Cookies: `session-id`, `ubid-main`, `at-main` (also accept `_main`). Headers: browser-like `User-Agent`, `x-amzn-sessionid`.
- Errors: 401/403 → `AuthExpired`; 429 → sleep 60 s, retry up to 3, then raise; sequential requests, 0.5 s pause between pages.
- `fixtures.record(source, out_dir) -> list[Path]`: calls account endpoint, album list, first page of the configured album, first page of all-mode search (lookback 7), downloads one small item; writes JSON with ids replaced by `node_0001…`, names/emails replaced, binary replaced by `tiny.jpg`.
- CLI: `record-fixtures`; `login` now calls `whoami()` and prints `Amazon Photos: connected as {name}, {count} items` or the auth error.

- [ ] **Step 1 (agent): Implement the client and `record-fixtures` against the documented endpoints with unit tests driven by hand-written placeholder fixtures matching the library's documented shapes.** Tests: `test_album_mode_lists_album_children_oldest_first`, `test_all_mode_uses_search_with_date_range`, `test_401_raises_auth_expired`, `test_429_backs_off_and_retries` (monkeypatch sleep), `test_pagination_follows_start_token`, `test_accepts_underscore_cookie_names`.
- [ ] **Step 2 (Matt):** `skylight-sync login` → `skylight-sync record-fixtures` → commit the redacted fixtures, replacing the placeholders.
- [ ] **Step 3 (agent): Fix the client against the real fixtures**; `skylight-sync run --dry-run` on Matt's Mac lists expected photos; `skylight-sync run --limit 1` lands one photo on the Calendar.
- [ ] **Step 4: Commit** `feat: Amazon Photos source with recorded fixtures`.

---

### Task 13: Documentation and first-run guide

**Files:**
- Modify: `README.md`; Create: `docs/setup.md`, `docs/cookies.md`

**Deliverable:** README Quick start matches the real CLI; `docs/setup.md` walks spec §7 end to end including Skylight sender approval and Plus plan note; `docs/cookies.md` explains copying the three cookies (Safari and Chrome dev tools → Network → any `amazon.com` request → Copy as cURL) and that `skylight-sync login` accepts the paste; a Troubleshooting section covers `needs_login`, 429s, photos not appearing (check approval, check 25 MB), Gmail app passwords. Keep "Runs on a Mac" callout.

- [ ] **Step 1: Write docs. Step 2: Verify every command in docs exists via `skylight-sync --help`. Step 3: Commit** `docs: setup, cookies and troubleshooting guides`.

---

## Post-v1 follow-ups (filed as issues, not in the v1 milestone)

- Family Vault / multi-account reading.
- Store secrets in macOS Keychain instead of a 0600 file.
- Optional `SkylightApiSender` using the community-documented private API, as an alternative to email.
- Homebrew tap or `uv tool` publishing.

## Self-review

- Spec coverage: §6.1 → T1, T11; §6.2 → T2, T3, T9; §6.3 → T4, T9, T10 (`reset`); §6.4 → T5; §6.5 → T6, T7; §6.6 → T3, T12; §6.7 → T8, T9, T10; §6.8 → T2; §6.9 → T10, T11, T12; §6.10 → T1; §7 → T13; §8 → task order (T12 last).
- Types consistent: `Item`, `Window`, `Batch`, `SkipReason`, `StateStore`, `Mailer`, `Alerter`, `RunResult`, `AuthExpired` used with the same names throughout.
- Review Focus items each pinned to T9 / T6 / T5 / T9 / T2+T9.
