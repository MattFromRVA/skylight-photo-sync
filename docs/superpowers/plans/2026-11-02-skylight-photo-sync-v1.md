# Skylight Photo Sync v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task below is also a GitHub issue in the v1 milestone; one task per agent session; issue number = task number.

**Goal:** A Mac-scheduled CLI that emails new Amazon Photos (from one album or all photos, within a lookback window) to a Skylight household address, never twice, and alerts once when Amazon cookies expire.

**Architecture:** An `IPhotoSource` yields candidate `Item`s; the pipeline filters them through a SQLite sent-state, downloads and normalizes them to JPEG, packs them into size-bounded batches, and hands each batch to an SMTP mailer. Amazon is one `IPhotoSource` implementation built last and verified against live fixtures; `LocalFolderSource` lets everything else be built and tested offline first. Mac-only tools (`sips`, `osascript`, `launchctl`) are called through `IProcessRunner` so tests can fake them.

**Tech Stack:** C# / .NET 10, System.CommandLine, MailKit, Microsoft.Data.Sqlite, Tomlyn, SkiaSharp 4.154.0 (+ `SkiaSharp.NativeAssets.Linux` for CI), Serilog (rolling file), xUnit, `SmtpServer` NuGet (test SMTP stub), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-skylight-photo-sync-design.md`

## Global Constraints

- .NET 10 SDK, `net10.0`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`, file-scoped namespaces. Root namespace `SkylightPhotoSync`.
- Packaged as a .NET global tool: `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>skylight-sync</ToolCommandName>`, `<PackageId>SkylightPhotoSync</PackageId>`, version `0.1.0`.
- Config at `~/.config/skylight-photo-sync/config.toml`; secrets at `~/.config/skylight-photo-sync/secrets.toml` (must be mode `0600`); state at `~/Library/Application Support/skylight-photo-sync/state.db`; lock at `.../run.lock` beside it; logs at `~/Library/Logs/skylight-photo-sync/sync.log` (rolling, 5 MB × 5). All overridable via `SKYLIGHT_SYNC_HOME` env var (tests set it to a temp dir; layout under it: `config.toml`, `secrets.toml`, `state.db`, `run.lock`, `sync.log`).
- Defaults: `source.mode="album"`, album `lookback_days=0`, all-mode `lookback_days=30`, `include_videos=false`, `max_items_per_run=25`, `max_long_edge=2048`, `jpeg_quality=88`, `max_attachments=12`, `max_total_bytes=17_000_000`, `interval_minutes=60`, video size cap `22_000_000` bytes.
- `lookback_days=0` is refused in `all` mode.
- An item is marked sent only after the SMTP server accepts the message. Items are processed oldest-first (by capture date).
- GPS and all other EXIF are stripped from images after orientation is applied.
- Secrets never appear in logs, test fixtures, or the repo. `.gitignore` excludes `secrets.toml`, `*.db`, `*.log`, `bin/`, `obj/`.
- Quality gate for every PR: `dotnet build -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test` all clean, no network in tests.
- Tests needing a real Mac tool use `[MacOnlyFact]` (skips when not `OSPlatform.OSX`); everything else runs on Linux CI with fakes.
- Commit messages: conventional prefix (`feat:`, `fix:`, `test:`, `chore:`, `docs:`). Each task ends in a PR against `main`.

## Review Focus

Inputs the spec implies but no single task's tests would naturally exercise. Each has a test pinned to the owning task.

1. **Two scheduled runs overlap** (previous run still downloading when launchd fires again): the second must exit immediately without sending. → Task 9 lock-file test.
2. **A single photo larger than `max_total_bytes` even after resize** (e.g. a 40 MB panorama): skipped and recorded as `Oversize`, never blocking the oldest-first queue. → Task 6 + Task 9.
3. **HEIC with EXIF orientation 6 (rotated 90°)**: output JPEG upright with no EXIF. → Task 5.
4. **Cookies expire mid-run after some batches were sent**: already-sent items stay sent, `needs_login` set, exactly one alert, non-zero exit. → Task 9.
5. **Switching `mode` from `album` to `all`** with existing state: nothing previously sent is resent, and the all-mode lookback default (30) applies if `lookback_days` is unset. → Task 2 + Task 9.

---

## File structure

```
SkylightPhotoSync.sln
Directory.Build.props                 shared: net10.0, nullable, warnaserror, implicit usings
.editorconfig
.github/workflows/ci.yml
src/SkylightPhotoSync/
  SkylightPhotoSync.csproj            PackAsTool, ToolCommandName skylight-sync
  Program.cs                          -> App.RunAsync(args, Console.Out, Console.Error)
  App.cs                              builds the System.CommandLine root; RunAsync
  AppPaths.cs                         Resolve() honoring SKYLIGHT_SYNC_HOME
  Infrastructure/IProcessRunner.cs    Run(file, args) -> ProcessResult(ExitCode, StdOut, StdErr)
  Infrastructure/ProcessRunner.cs
  Config/AppConfig.cs                 records: SourceConfig, LimitsConfig, ImageConfig, EmailConfig, SmtpConfig, AlertsConfig, ScheduleConfig; enum SourceMode
  Config/Secrets.cs
  Config/ConfigLoader.cs              LoadConfig, LoadSecrets, WriteSecrets; ConfigException
  Models/Item.cs  Window.cs  Batch.cs  SkipReason.cs  ItemKind.cs
  Sources/IPhotoSource.cs             + AuthExpiredException
  Sources/LocalFolderSource.cs
  Sources/Amazon/AmazonPhotosSource.cs  AmazonEndpoints.cs  (Task 12)
  Imaging/IHeicConverter.cs  SipsHeicConverter.cs  ImageNormalizer.cs  (+ OversizeException, UnsupportedMediaException)
  State/StateStore.cs  RunRecord.cs
  Batching/BatchPacker.cs             PackResult
  Mail/MessageBuilder.cs  IMailer.cs  SmtpMailer.cs  SendFailedException.cs
  Alerts/INotifier.cs  OsascriptNotifier.cs  Alerter.cs  AlertKind.cs  AlertTexts.cs
  Pipeline/SyncPipeline.cs  RunResult.cs  LockHeldException.cs
  Launchd/LaunchdInstaller.cs
  Recording/FixtureRecorder.cs        (Task 12)
  Commands/*.cs                       one static class per CLI command
tests/SkylightPhotoSync.Tests/
  SkylightPhotoSync.Tests.csproj      xUnit, SmtpServer
  TestSupport/MacOnlyFactAttribute.cs  TestHome.cs (temp SKYLIGHT_SYNC_HOME)  FakeProcessRunner.cs  SmtpStub.cs
  Fixtures/Images/  tiny.jpg  tiny.png  tiny_rot6.heic  tiny.gif  tiny.mp4
  Fixtures/Amazon/  recorded + redacted JSON (Task 12)
  *Tests.cs                           one per module
```

---

### Task 1: Project scaffold and CI

**Files:**
- Create: `SkylightPhotoSync.sln`, `Directory.Build.props`, `.editorconfig`, `src/SkylightPhotoSync/SkylightPhotoSync.csproj`, `Program.cs`, `App.cs`, `Infrastructure/IProcessRunner.cs`, `Infrastructure/ProcessRunner.cs`, `tests/SkylightPhotoSync.Tests/SkylightPhotoSync.Tests.csproj`, `TestSupport/MacOnlyFactAttribute.cs`, `TestSupport/TestHome.cs`, `TestSupport/FakeProcessRunner.cs`, `AppTests.cs`, `.github/workflows/ci.yml`; Modify: `.gitignore` (add `bin/`, `obj/`, `*.user`, `.vs/`, `.idea/`).

**Interfaces:**
- Produces:
  - `public static class App { public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default); public const string Version = "0.1.0"; }` — `--version` prints `skylight-sync 0.1.0`, exit 0.
  - `public interface IProcessRunner { Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default); }` and `public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);` with `ProcessRunner` using `System.Diagnostics.Process`.
  - Tests: `MacOnlyFactAttribute : FactAttribute` (sets `Skip` when not macOS); `TestHome : IDisposable` (creates a temp dir, sets `SKYLIGHT_SYNC_HOME`, exposes `Dir`); `FakeProcessRunner : IProcessRunner` recording calls and returning scripted results.

- [x] **Step 1: Write the failing test**

```csharp
public class AppTests
{
    [Fact]
    public async Task Version_flag_prints_name_and_version()
    {
        var out_ = new StringWriter(); var err = new StringWriter();
        var code = await App.RunAsync(["--version"], out_, err);
        Assert.Equal(0, code);
        Assert.Equal("skylight-sync 0.1.0", out_.ToString().Trim());
    }
}
```

- [x] **Step 2: `dotnet test` → fails to compile (App missing).**
- [x] **Step 3: Create the solution, props, both projects and `App.cs`** using System.CommandLine with a root command whose `--version` option is handled explicitly so output goes to the supplied `TextWriter`. Add `IProcessRunner`/`ProcessRunner` and the three test-support types.
- [x] **Step 4: `dotnet build -warnaserror && dotnet format --verify-no-changes && dotnet test` → green.**
- [x] **Step 5: Create `.github/workflows/ci.yml`**: on push and pull_request; `ubuntu-latest`; `actions/setup-dotnet@v4` with `dotnet-version: 10.0.x`; run the three commands above.
- [x] **Step 6: Commit** `chore: scaffold .NET solution, CLI entry point and CI`.

---

### Task 2: Paths and configuration

**Files:**
- Create: `AppPaths.cs`, `Config/AppConfig.cs`, `Config/Secrets.cs`, `Config/ConfigLoader.cs`, `tests/.../ConfigLoaderTests.cs`, `AppPathsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AppPaths(string ConfigFile, string SecretsFile, string StateDb, string LockFile, string LogFile) { public static AppPaths Resolve(); }`
  - `public enum SourceMode { Album, All }`
  - `public sealed record SourceConfig(SourceMode Mode, string? AlbumName, int LookbackDays, bool IncludeVideos);` plus `LimitsConfig(int MaxItemsPerRun)`, `ImageConfig(int MaxLongEdge, int JpegQuality)`, `EmailConfig(IReadOnlyList<string> To, string From, int MaxAttachments, long MaxTotalBytes)`, `SmtpConfig(string Host, int Port, string Username)`, `AlertsConfig(string Email)`, `ScheduleConfig(int IntervalMinutes)`, and `AppConfig(SourceConfig Source, LimitsConfig Limits, ImageConfig Image, EmailConfig Email, SmtpConfig Smtp, AlertsConfig Alerts, ScheduleConfig Schedule)` — TOML keys exactly as spec §6.8.
  - `public sealed record Secrets(string? AmazonSessionId, string? AmazonUbid, string? AmazonAt, string? SmtpPassword, string? AmazonRefreshToken = null, string? AmazonDeviceSerial = null);` (the two optional members were added by issue #30 for the device login; the cookie members remain the fallback)
  - `public static class ConfigLoader { AppConfig LoadConfig(string path); Secrets LoadSecrets(string path); void WriteSecrets(string path, Secrets s); }` and `public sealed class ConfigException : Exception`.
  - `WriteSecrets` sets mode `0600` via `File.SetUnixFileMode`; `LoadSecrets` throws `ConfigException` mentioning `0600` if group/other bits are set.

- [x] **Step 1: Write failing tests**: `Album_mode_defaults_lookback_to_zero`, `All_mode_defaults_lookback_to_thirty`, `All_mode_rejects_lookback_zero`, `Album_mode_requires_album_name`, `Secrets_file_must_be_0600`, `WriteSecrets_sets_0600`, `Accepts_dash_and_underscore_cookie_keys` (`at-main` / `at_main` → `AmazonAt`), `Paths_honor_env`.
- [x] **Step 2: fail. Step 3: implement with Tomlyn (`TomlSerializer.Deserialize<TomlTable>`; Tomlyn 2.x replaced the old `Toml.ToModel`), mode-dependent default applied only when the key is absent. Step 4: green.**
- [x] **Step 5: Commit** `feat: config and secrets loading with mode-aware defaults`.

---

### Task 3: Models, IPhotoSource and LocalFolderSource

**Files:**
- Create: `Models/*.cs`, `Sources/IPhotoSource.cs`, `Sources/LocalFolderSource.cs`, `tests/.../LocalFolderSourceTests.cs`, `WindowTests.cs`, `tests/.../Fixtures/Images/*`, `scripts/make-fixtures.sh`

**Interfaces:**
- Produces:
  - `public enum ItemKind { Photo, Video }`; `public enum SkipReason { Oversize, VideoDisabled, UnsupportedType, DownloadFailed, ProcessingFailed }`
  - `public sealed record Item(string Id, string Name, ItemKind Kind, DateTimeOffset CapturedAt, long SizeBytes, string? Md5, string Mime);`
  - `public sealed record Window(DateTimeOffset? Since) { public static Window FromLookback(int days, DateTimeOffset now); }` (days 0 → `Since = null`).
  - `public sealed record Batch(string Id, IReadOnlyList<Item> Items, IReadOnlyList<string> Files, long TotalBytes);`
  - `public interface IPhotoSource { Task<IReadOnlyList<Item>> ListCandidatesAsync(Window window, SourceMode mode, string? albumName, CancellationToken ct); Task<string> DownloadAsync(Item item, string destDir, CancellationToken ct); }` — results oldest-first.
  - `public sealed class AuthExpiredException : Exception` (in `Sources`, so Task 9 can catch it before Task 12 exists).
  - `public sealed class LocalFolderSource(string root) : IPhotoSource` — `All` = every image/video under root; `Album` = files under `root/<albumName>`; `CapturedAt` from EXIF `DateTimeOriginal` (built-in `JpegExifReader`; ImageSharp was dropped here because 3.1.x carries published vulnerabilities and 3.2+/4.x need a Six Labors license key, see Task 5) else last-write time; `Md5` of bytes.

- [x] **Step 1: Add fixture images** via `scripts/make-fixtures.sh` (ImageSharp-free: uses `sips` on a Mac to make `tiny.jpg` 64×48, `tiny.png`, `tiny.gif`, and `tiny_rot6.heic` from a JPEG that carries EXIF orientation 6; verify with `sips -g all`). Commit the generated files and a ≤10 KB static `tiny.mp4`.
- [x] **Step 2: Write failing tests**: `FromLookback_zero_is_unbounded`, `All_mode_lists_oldest_first`, `Album_mode_only_subfolder`, `Window_filters_by_captured_at`, `Download_copies_file`.
- [x] **Step 3: fail. Step 4: implement. Step 5: green.**
- [x] **Step 6: Commit** `feat: item model, IPhotoSource and LocalFolderSource`.

---

### Task 4: SQLite state store

**Files:**
- Create: `State/StateStore.cs`, `State/RunRecord.cs`, `tests/.../StateStoreTests.cs`

**Interfaces:**
- Produces `public sealed class StateStore(string dbPath) : IDisposable` with:
  - `bool IsSent(string itemId, string recipient)`; `bool IsMd5Sent(string md5, string recipient)`
  - `void MarkSent(IReadOnlyList<Item> items, string recipient, string batchId, DateTimeOffset sentAt)`
  - `void MarkSkipped(string itemId, SkipReason reason, string detail)`; `bool IsSkipped(string itemId)` (true only for the permanent reasons `Oversize` and `UnsupportedType`; other reasons stay recorded but the item is retried)
  - `long StartRun(SourceMode mode)`; `void FinishRun(long runId, int found, int sent, int skipped, string? error)`
  - `int ConsecutiveFailures()` (trailing finished runs with non-null error; unfinished runs are ignored)
  - `void SetNeedsLogin(bool flag, DateTimeOffset? at)`; `bool NeedsLogin()`
  - `DateTimeOffset? LastAlertAt(string kind)`; `void RecordAlert(string kind, DateTimeOffset at)`
  - `RunRecord? LastRun()`; `int ResetSent()` (clears sent and skipped items; returns the sent rows removed)
  - `public sealed record RunRecord(long Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, SourceMode Mode, int Found, int Sent, int Skipped, string? Error);`
  - Schema created on open; `PRAGMA journal_mode=WAL`; timestamps stored as ISO-8601 UTC text.

- [x] **Step 1: Write failing tests** for each method plus `MarkSent_is_per_recipient` and `ConsecutiveFailures_resets_on_success`.
- [x] **Step 2: fail. Step 3: implement with Microsoft.Data.Sqlite, parameterized SQL. Step 4: green.**
- [x] **Step 5: Commit** `feat: sqlite state store for sent items, runs, auth and alerts`.

---

### Task 5: Image normalization

**Files:**
- Create: `Imaging/IHeicConverter.cs`, `Imaging/SipsHeicConverter.cs`, `Imaging/ImageNormalizer.cs`, `Imaging/OversizeException.cs`, `Imaging/UnsupportedMediaException.cs`, `tests/.../ImageNormalizerTests.cs`, `SipsHeicConverterTests.cs`

**Imaging library:** SkiaSharp 4.154.0 (MIT) plus the `SkiaSharp.NativeAssets.Linux` package so Ubuntu CI has the native library; `sips` is used only for HEIC → JPEG. SixLabors.ImageSharp is deliberately excluded: 3.1.x fails the NuGet vulnerability audit (NU1902/NU1903, fatal under `-warnaserror`), and 3.2+/4.x require a Six Labors license key (their `SixLabors_ValidateLicense` target fails Debug `-warnaserror` and Release builds without one). Do not re-add it.

**Interfaces:**
- Consumes `IProcessRunner`, `Item`, `ImageConfig`.
- Produces:
  - `public interface IHeicConverter { Task<string> ToJpegAsync(string heicPath, string destDir, CancellationToken ct); }`
  - `public sealed class SipsHeicConverter(IProcessRunner runner) : IHeicConverter` — runs `sips -s format jpeg -s formatOptions 100 <in> --out <dest>/<name>.jpg`; non-zero exit → `UnsupportedMediaException`.
  - `public sealed class ImageNormalizer(IHeicConverter heic, ImageConfig cfg, long videoCapBytes = 22_000_000) { Task<string> NormalizeAsync(string src, string destDir, Item item, CancellationToken ct); }`
    - photos: HEIC → via `heic` to JPEG first (macOS `sips`, behind `IHeicConverter`); then decode with SkiaSharp (`SKCodec.Create`, read `codec.EncodedOrigin`, decode to `SKBitmap`), apply that origin to get upright pixels, resize if long edge > `MaxLongEdge` (never upscale), and re-encode with `SKImage.Encode(SKEncodedImageFormat.Jpeg, cfg.JpegQuality)`. Re-encoding drops all EXIF, XMP and GPS, so no explicit stripping step is needed (the tests assert it). Output name `yyyy-MM-dd_HHmmss_<Id[..8]>.jpg` from `item.CapturedAt`; if that name is already taken in `destDir`, a numeric suffix is added before the extension (`-2`, `-3`, …) so no existing file is ever overwritten (applies to GIF and video too).
    - GIF: copy unchanged, same naming, `.gif`. Video: copy if `SizeBytes <= videoCapBytes` else throw `OversizeException`. Other → `UnsupportedMediaException`.

- [x] **Step 1: Write failing tests**: `Heic_rot6_becomes_upright_jpeg_without_exif` (`[MacOnlyFact]`, real `SipsHeicConverter`; assert width/height swapped relative to source and that the output JPEG has no APP1 Exif/XMP segment), `Heic_path_uses_converter` (fake `IHeicConverter` returning a JPEG, runs everywhere), `Png_to_jpeg`, `Large_image_resized_to_max_long_edge` (generate 4000×3000 in-test → 2048), `Small_image_not_upscaled`, `Gif_passthrough`, `Video_over_cap_throws_oversize`, `Output_filename_pattern`, `Sips_converter_invokes_expected_arguments` (FakeProcessRunner).
- [x] **Step 2: fail. Step 3: implement. Step 4: green.**
- [x] **Step 5: Commit** `feat: normalize photos to upright, EXIF-free, resized JPEG`.

---

### Task 6: Email batching

**Files:**
- Create: `Batching/BatchPacker.cs`, `tests/.../BatchPackerTests.cs`

**Interfaces:**
- Produces `public sealed record PackResult(IReadOnlyList<Batch> Batches, IReadOnlyList<(Item Item, SkipReason Reason)> Skipped);` and `public static class BatchPacker { PackResult Pack(IReadOnlyList<(Item Item, string File)> prepared, int maxAttachments, long maxTotalBytes); }`. Input oldest-first; order preserved; a file alone larger than `maxTotalBytes` → `Skipped` with `SkipReason.Oversize`. `Batch.Id` = `Guid.NewGuid().ToString("N")[..12]`.

- [x] **Step 1: Write failing tests**: `Splits_on_attachment_count` (13 items, max 12 → 2), `Splits_on_total_bytes`, `Oversize_single_item_is_skipped_not_blocking`, `Preserves_order`.
- [x] **Step 2–4: fail → implement → green. Step 5: Commit** `feat: pack prepared items into size-bounded email batches`.

---

### Task 7: SMTP mailer

**Files:**
- Create: `Mail/MessageBuilder.cs`, `Mail/IMailer.cs`, `Mail/SmtpMailer.cs`, `Mail/SendFailedException.cs`, `tests/.../TestSupport/SmtpStub.cs`, `MessageBuilderTests.cs`, `SmtpMailerTests.cs`

**Interfaces:**
- Produces:
  - `public static class MessageBuilder { MimeMessage Build(Batch batch, string to, string from, SourceMode mode, string? albumName, DateTimeOffset now); }` — subject `Skylight Photo Sync — {n} photos ({yyyy-MM-dd})`; plain body `Source: all` or `Source: album "{albumName}"`; one attachment per file with MIME type from extension.
  - `public interface IMailer { Task SendAsync(MimeMessage message, CancellationToken ct); }`
  - `public sealed class SmtpMailer(SmtpConfig cfg, string password, bool useStartTls = true) : IMailer` using MailKit `SmtpClient`; any `SmtpCommandException`/`SmtpProtocolException`/`SslHandshakeException`/`AuthenticationException`/socket/IO error → `SendFailedException`.
  - `SmtpStub : IAsyncDisposable` wraps the `SmtpServer` package on `127.0.0.1` with an ephemeral port, no auth, no TLS, exposes `Received: List<MimeMessage>` and `Port`.

- [x] **Step 1: Write failing tests**: `Subject_and_body`, `Attachments_present_with_mime`, `Send_delivers_to_stub`, `Send_failure_throws_SendFailed` (connect to a closed port).
- [x] **Step 2–4: fail → implement → green. Step 5: Commit** `feat: build MIME batches and send over SMTP`.

---

### Task 8: Rate-limited alerts

**Files:**
- Create: `Alerts/INotifier.cs`, `Alerts/OsascriptNotifier.cs`, `Alerts/AlertKind.cs`, `Alerts/AlertTexts.cs`, `Alerts/Alerter.cs`, `tests/.../AlerterTests.cs`

**Interfaces:**
- Consumes `StateStore`, `IMailer`, `IProcessRunner`.
- Produces:
  - `public enum AlertKind { AuthExpired, RepeatedFailure, SmtpDown }`
  - `public interface INotifier { Task NotifyAsync(string title, string body, CancellationToken ct); }`; `OsascriptNotifier(IProcessRunner runner)` runs `osascript -e 'display notification "<body>" with title "<title>"'`, swallows failures.
  - `public static class AlertTexts { public const string AuthExpiredBody = ...; }` — re-login steps (run `skylight-sync login`, where to copy cookies).
  - `public sealed class Alerter(StateStore store, IMailer? mailer, string alertTo, string from, INotifier notifier, Func<DateTimeOffset> now) { Task<bool> AlertAsync(AlertKind kind, string title, string body, CancellationToken ct); }` — returns `false` and does nothing if `store.LastAlertAt(kind.ToString())` is within 24 h; otherwise notifies, emails if `mailer != null && kind != SmtpDown`, records, returns `true`.

- [x] **Step 1: Write failing tests** with a fake `INotifier` and `SmtpStub`: `First_alert_sends_email_and_notification`, `Second_alert_within_24h_is_suppressed`, `Alert_after_24h_sends_again`, `SmtpDown_kind_skips_email`.
- [x] **Step 2–4: fail → implement → green. Step 5: Commit** `feat: rate-limited alerts by email and macOS notification`.

---

### Task 9: Sync pipeline

**Files:**
- Create: `Pipeline/SyncPipeline.cs`, `Pipeline/RunResult.cs`, `Pipeline/LockHeldException.cs`, `tests/.../SyncPipelineTests.cs`

**Interfaces:**
- Consumes Tasks 2–8.
- Produces:
  - `public sealed record RunResult(int Found, int Sent, int Skipped, int Batches, bool DryRun, string? Error);`
  - `public sealed class LockHeldException : Exception`
  - `public sealed class SyncPipeline(AppConfig cfg, IPhotoSource source, StateStore store, ImageNormalizer normalizer, IMailer mailer, Alerter alerter, Func<DateTimeOffset> now, string lockPath) { Task<RunResult> RunAsync(bool dryRun, int? limit, CancellationToken ct); }`
- Behavior, in order: open `lockPath` with `FileShare.None` (held for the run), else throw `LockHeldException`. If `store.NeedsLogin()` → return `RunResult` with `Error="needs_login"` without touching the source. `StartRun`. `Window.FromLookback(cfg.Source.LookbackDays, now())`. `ListCandidatesAsync`; drop items `IsSent` for every recipient, `IsSkipped`, or md5 already sent, then collapse byte-identical candidates within the run to the oldest (null `Md5` never collapses); drop videos when `IncludeVideos` is false (record `VideoDisabled` once per item). Take the first `min(limit ?? MaxItemsPerRun, …)`. For each: `DownloadAsync` → `NormalizeAsync` into a temp dir (deleted after the run); failures recorded as `DownloadFailed` / `ProcessingFailed` (and `Oversize` / `UnsupportedType` from the normalizer) and continue. `BatchPacker.Pack`; record its skipped. For each recipient, for each batch: `MessageBuilder.Build`, `SendAsync` unless `dryRun`, then `MarkSent`. On `AuthExpiredException`: `SetNeedsLogin(true, now())`, `AlertAsync(AuthExpired, …)`, `FinishRun(error:"auth_expired")`, return (already-sent items stay sent). On `SendFailedException`: stop sending, finish with error. After `FinishRun`, if `ConsecutiveFailures() >= 3` → `AlertAsync(RepeatedFailure, …)`.

- [x] **Step 1: Write failing tests** using `LocalFolderSource`, a real `StateStore` on temp, `SmtpStub` + `SmtpMailer`, fake notifier, a fake `IHeicConverter`, and a `FailingSource : IPhotoSource` wrapper that throws `AuthExpiredException` after N downloads: `Sends_new_items_and_marks_sent`, `Second_run_sends_nothing`, `Respects_max_items_per_run_oldest_first`, `Dry_run_sends_nothing_and_marks_nothing`, `Needs_login_short_circuits`, `Auth_expired_midrun_keeps_sent_sets_flag_alerts_once` (RF 4), `Lock_prevents_overlap` (RF 1: hold the lock file in the test, expect `LockHeldException`), `Oversize_item_skipped_and_queue_advances` (RF 2), `Mode_switch_does_not_resend` (RF 5), `Three_failures_trigger_repeated_failure_alert`.
- [x] **Step 2–4: fail → implement → green. Step 5: Commit** `feat: sync pipeline with dedupe, batching, lock and auth-expiry handling`.

---

### Task 10: CLI commands: run, status, test-email, reset, login

**Files:**
- Modify: `App.cs`, `State/StateStore.cs` (adds `int TotalSent()` for `status`); Create: `Commands/RunCommand.cs`, `StatusCommand.cs`, `TestEmailCommand.cs`, `ResetCommand.cs`, `LoginCommand.cs`, `CommandSupport.cs` (secrets-if-present and mailer construction shared by `run` and `test-email`), `Logging.cs`, `tests/.../CommandTests.cs`

**Interfaces:**
- Consumes Tasks 2–9.
- Produces commands on the System.CommandLine root (all invoked through `App.RunAsync`):
  - `run [--dry-run] [--mode album|all] [--lookback-days N] [--limit N] [--source local:PATH]` — CLI flags override config; `--source local:PATH` selects `LocalFolderSource`; without `--source` the default is Amazon (Task 12); until then exit 2 with `Amazon source not yet available`. Exit codes: 0 ok, 1 error, 3 `needs_login`, 4 lock held.
  - `status` — last run time/result, totals sent, `needs_login` state, next launchd run if installed (Task 11 `LaunchdInstaller.NextRun`; print `not installed` otherwise).
  - `test-email [--size-probe]` — sends one generated 800×600 JPEG to each `email.to`; with `--size-probe` sends 5 emails of ~5/10/15/20/24 MB generated-noise JPEGs with size-tagged subjects and prints `Check which arrived on the frame`. An SMTP rejection of one probe is reported and the series continues (base64 pushes the larger probes past Gmail's 25 MB limit); exit 1 only if no probe was accepted.
  - `reset --yes` — `store.ResetSent()`; refuses without `--yes`.
  - `login` — reads stdin: either a pasted cURL command (parse the `Cookie:` header for `session-id`, `ubid-main|ubid_main`, `at-main|at_main`) or three lines of values; `WriteSecrets`; `SetNeedsLogin(false, null)`; prints `Saved. Run 'skylight-sync run --dry-run' to verify.` (live check added in Task 12).
  - `Logging.Configure(AppPaths paths)`: Serilog to `paths.LogFile` with `fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 5` plus stderr at Information. The stderr sink is only added when stderr is a terminal: under launchd stderr is already appended to the same log file (Task 11 plist), so both sinks would record every line twice.

- [x] **Step 1: Write failing tests** via `App.RunAsync` with `TestHome`, a config file and stdin redirect: `Run_local_source_dry_run_exit_0`, `Run_exit_3_when_needs_login`, `Reset_requires_yes`, `Login_parses_curl_paste`, `Login_accepts_three_values`, `Status_prints_last_run`, `TestEmail_sends_one_message`.
- [x] **Step 2–4: fail → implement → green. Step 5: Commit** `feat: run, status, test-email, reset and login commands`.

---

### Task 11: launchd install and uninstall

**Files:**
- Create: `Launchd/LaunchdInstaller.cs`, `Commands/InstallLaunchdCommand.cs`, `Commands/UninstallLaunchdCommand.cs`, `tests/.../LaunchdInstallerTests.cs`; Modify: `App.cs`

**Interfaces:**
- Produces `public sealed class LaunchdInstaller(IProcessRunner runner, string plistDir, uint? uid = null)` (the optional `uid` names the `gui/<uid>` domain; it defaults to `getuid()` and tests inject it) with `public const string Label = "com.mattfromrva.skylight-photo-sync"`; `static string RenderPlist(string programPath, int intervalMinutes, string logFile)` (`ProgramArguments=[program,"run"]`, `StartInterval=intervalMinutes*60`, `RunAtLoad=true`, `StandardOutPath`/`StandardErrorPath`=logFile); `Task<string> InstallAsync(AppConfig cfg, string programPath, CancellationToken ct)` (writes plist, `launchctl bootstrap gui/<uid> <plist>`, falling back to `launchctl load`); `Task UninstallAsync(CancellationToken ct)`; `bool IsInstalled()`; `Task<DateTimeOffset?> NextRunAsync(CancellationToken ct)` (best-effort from `launchctl print`, null if unavailable). `programPath` defaults to `Environment.ProcessPath`. Default `plistDir` = `~/Library/LaunchAgents`.
- CLI: `install-launchd`, `uninstall-launchd`.

- [x] **Step 1: Write failing tests** with `FakeProcessRunner` and a temp `plistDir`: `RenderPlist_contains_interval_and_label`, `Install_writes_plist_and_calls_launchctl`, `Uninstall_removes_plist`.
- [x] **Step 2–4: fail → implement → green. Step 5: Commit** `feat: launchd agent install and uninstall`.

---

### Task 12: Amazon Photos source and fixture recorder (needs Matt's live session)

**Files:**
- Create: `Sources/Amazon/AmazonPhotosSource.cs`, `Sources/Amazon/AmazonEndpoints.cs`, `Recording/FixtureRecorder.cs` (namespace `Recording`, not `Fixtures`, which would shadow the tests' `Fixtures` helper), `Commands/RecordFixturesCommand.cs`, `tests/.../AmazonPhotosSourceTests.cs`, `tests/.../Fixtures/Amazon/*.json`; Modify: `Commands/LoginCommand.cs`, `Commands/RunCommand.cs`, `App.cs`

**Interfaces:**
- Produces `public sealed class AmazonPhotosSource(Secrets secrets, HttpMessageHandler? handler = null, string tld = "com", Func<TimeSpan, CancellationToken, Task>? delay = null) : IPhotoSource` plus `Task<(string Name, long Count)> WhoAmIAsync(CancellationToken ct)`.
- Endpoints (reference only; the live-verified shapes are in `docs/research-notes.md`): `GET https://www.amazon.{tld}/drive/v1/account/endpoint` → `metadataUrl`, `contentUrl`; `GET {metadataUrl}/nodes?filters=kind:VISUAL_COLLECTION` (albums); `GET {metadataUrl}/nodes/{albumId}/children?...sort=["contentDate ASC"]`; `GET {metadataUrl}/search?filters=type:(PHOTOS OR VIDEOS) AND contentDate:[... TO ...]&sort=["contentDate ASC"]`; `GET {contentUrl}/nodes/{id}/content`. Pagination via `startToken`. Cookies `session-id`, `ubid-main`, `at-main` (accept `_main` too) via `CookieContainer`. Browser-like `User-Agent`, `x-amzn-sessionid` header.
- Errors: 401/403 → `AuthExpiredException`; 429 → `delay(60 s)` and retry up to 3, then throw; sequential requests with a 0.5 s pause between pages.
- `FixtureRecorder.RecordAsync(AmazonPhotosSource source, string outDir, string albumName, CancellationToken ct) -> IReadOnlyList<string>`: calls account endpoint, album list, every page of the album and of an all-mode search (lookback 7), each trimmed to its first 3 nodes with the original counts under `_trimmed`, downloads the smallest kept photo; writes JSON with ids replaced by `node_0001…`, names/emails replaced, binary replaced by a generated stand-in `content_node_NNNN.jpg`.
- CLI: `record-fixtures`; `login` now calls `WhoAmIAsync` and prints `Amazon Photos: connected as {name}, {count} items` when the root node exposes a name, else `Amazon Photos: connected, {count} items` (the usual case: the real root node has no name and its ownerId is opaque, so it is never printed), or the auth error; `run` default source becomes Amazon.

- [x] **Step 1 (agent): Implement the client and recorder** against the documented endpoint shapes, with unit tests driven by hand-written placeholder fixtures (clearly named `placeholder_*.json`) and a scripted `HttpMessageHandler`: `Album_mode_lists_album_children_oldest_first`, `All_mode_uses_search_with_date_range`, `Http401_throws_AuthExpired`, `Http429_backs_off_and_retries`, `Pagination_follows_startToken`, `Accepts_underscore_cookie_names`.
- [x] **Step 2 (Matt):** `skylight-sync login` → `skylight-sync record-fixtures` → commit the redacted fixtures, replacing the placeholders. (Recorded 2026-11-06; every item page is written, trimmed to three nodes, with the original counts under `_trimmed`.)
- [x] **Step 3 (agent): Fix the client against the real fixtures**; `skylight-sync run --dry-run` on Matt's Mac lists expected photos (2026-11-06: `found 595, sent 3, skipped 24, batches 1 (dry run)` with `--limit 3`; the 24 skips are videos with `include_videos = false`). `skylight-sync run --limit 1` landing one photo on the Calendar waits for the real Skylight and SMTP addresses in `config.toml`.
- [x] **Step 4: Commit** `feat: Amazon Photos source with recorded fixtures`.

---

### Task 13: Setup, cookies and troubleshooting docs

**Files:**
- Modify: `README.md`; Create: `docs/setup.md`, `docs/cookies.md`, `docs/troubleshooting.md`

**Deliverable:** README Quick start matches the real CLI (including `dotnet tool install`, or pack-and-install from a clone with `--add-source`); `docs/setup.md` walks spec §7 end to end including Skylight sender approval and the Plus plan note; `docs/cookies.md` explains copying the three cookies (Safari and Chrome dev tools → Network → any `amazon.com` request → Copy as cURL) and that `skylight-sync login` accepts the paste; a Troubleshooting section covers `needs_login`, 429s, photos not appearing (check approval, check 25 MB), Gmail app passwords. Keep the "Runs on a Mac" callout.

- [x] **Step 1: Write docs. Step 2: Verify every documented command exists via `skylight-sync --help`. Step 3: Commit** `docs: setup, cookies and troubleshooting guides`.


### Issue 30: device login with refresh token replaces pasted cookies (follow-up to Tasks 10, 12, 13)

**Files:**
- Create: `Sources/Amazon/AmazonDeviceAuth.cs`, `Commands/LogoutCommand.cs`, `tests/.../AmazonDeviceAuthTests.cs`, `tests/.../Fixtures/AmazonAuth/placeholder_*.json`; Modify: `Config/Secrets.cs`, `Config/ConfigLoader.cs`, `Sources/Amazon/AmazonPhotosSource.cs`, `Commands/LoginCommand.cs`, `Commands/StatusCommand.cs`, `Commands/CommandSupport.cs`, `Commands/RunCommand.cs`, `Commands/RecordFixturesCommand.cs`, `Alerts/AlertTexts.cs`, `App.cs`, the docs

**Interfaces:**
- `public sealed class AmazonDeviceAuth(HttpMessageHandler? handler = null, string tld = "com") : IDisposable` with `static Uri BuildSignInUrl(string tld, string codeVerifier, string serial)`, `static string ParseAuthorizationCode(string pastedUrl)`, `static string NewCodeVerifier()`, `static string NewSerial()`, `static string ClientId(string serial)`, `Task<DeviceRegistration> RegisterAsync(string authorizationCode, string codeVerifier, string serial, CancellationToken ct)`, `Task<IReadOnlyList<AmazonCookie>> ExchangeCookiesAsync(string refreshToken, CancellationToken ct)` (401/403 or no cookies → `AuthExpiredException`), `Task<string> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct)`, `Task DeregisterAsync(string accessToken, CancellationToken ct)`; other failures → `DeviceAuthException` naming the status and Amazon's `response.error` code/message, never a token. The storefront is a constructor argument rather than a per-call one because every call needs it.
- `public sealed record AmazonCookie(string Name, string Value, DateTimeOffset? Expires)`; `public sealed record DeviceRegistration(string RefreshToken, string AccessToken, TimeSpan AccessTokenLifetime, IReadOnlyList<AmazonCookie> Cookies, string CustomerName)`.
- `AmazonPhotosSource`: constructor signature unchanged. With `Secrets.AmazonRefreshToken` set it mints cookies through `ExchangeCookiesAsync` before its first request and once more on the first 401/403 of the run (retrying that request once), sends `x-amzn-sessionid` from the minted `session-id`, and loads every minted cookie for `.amazon.{tld}` and the resolved hosts; without it, the three pasted cookies as before.
- Secrets file: `[amazon] refresh_token`, `device_serial` (device login) or `session_id`/`ubid_main`/`at_main` (cookies); `[smtp] password` kept by every writer. Neither set present is an error only when Amazon is needed (`not logged in to Amazon Photos; run 'skylight-sync login' first.`, exit 3 from `run`).
- CLI: `login` (device login: prints the sign-in URL and instructions, reads the pasted `/ap/maplanding` address, registers, proves the login with `WhoAmIAsync`, then writes secrets and clears `needs_login`; prints `Amazon Photos: connected as <name>, N items` and `Device registered as "Skylight Photo Sync"; remove it under Manage Your Content and Devices to revoke.`; any failure after registration deregisters and writes nothing), `login --cookies` (the Task 10/12 paste flow, unchanged), `logout` (deregister, drop the Amazon keys, `needs_login` on), `status` adds `auth: device login | pasted cookies | not logged in`.

- [x] **Step 1: Write failing tests** with `ScriptedHandler` and `placeholder_*.json` fixtures: `SignInUrl_has_pkce_and_device_client_id`, `ParseAuthorizationCode_extracts_code_and_rejects_missing`, `Register_posts_expected_body_and_parses_tokens`, `Exchange_posts_form_and_loads_cookies`, `Exchange_401_throws_AuthExpired`, `Source_refreshes_cookies_before_first_request`, `Source_retries_once_after_401_then_throws`, `Source_without_refresh_token_uses_cookies_unchanged`, `Login_device_flow_writes_refresh_token_not_password`, `Login_failure_after_register_deregisters_and_writes_nothing`, `Logout_deregisters_and_clears_amazon_keys`, `Secrets_roundtrip_refresh_token`; every command test asserts no fake token reaches output or the log.
- [x] **Step 2–4: fail → implement → green. Step 5: Docs** (`docs/cookies.md` leads with the device login, cookies as the fallback; `docs/setup.md` step 4/5; `docs/troubleshooting.md`; README; `docs/research-notes.md` "documented, awaiting live verification").
- [x] **Step 6: Commit** `feat: one-time device login with refresh-token cookie exchange`.
- [ ] **Step 7 (Matt): live verification** — `skylight-sync login`, `skylight-sync run --dry-run --limit 3`, `skylight-sync status`; then replace the `placeholder_*.json` fixtures with redacted recordings and tick this box.
---

## Post-v1 follow-ups (filed as issues, not in the v1 milestone)

- Family Vault / multi-account reading.
- Store secrets in macOS Keychain instead of a 0600 file.
- Optional `SkylightApiSender` using the community-documented private API, as an alternative to email.
- Publish the global tool to NuGet (and/or a Homebrew tap).

## Self-review

- Spec coverage: §6.1 → T1, T11; §6.2 → T2, T3, T9; §6.3 → T4, T9, T10 (`reset`); §6.4 → T5; §6.5 → T6, T7; §6.6 → T3, T12; §6.7 → T8, T9, T10; §6.8 → T2; §6.9 → T10, T11, T12; §6.10 → T1; §7 → T13; §8 → task order (T12 last).
- Types consistent: `Item`, `Window`, `Batch`, `SkipReason`, `SourceMode`, `IPhotoSource`, `AuthExpiredException`, `StateStore`, `ImageNormalizer`, `IHeicConverter`, `IMailer`, `Alerter`, `AlertKind`, `RunResult`, `IProcessRunner` used with the same names throughout.
- Review Focus items each pinned to T9 / T6 / T5 / T9 / T2+T9.
- Mac-only seams: `IProcessRunner` (T1) feeds `SipsHeicConverter` (T5), `OsascriptNotifier` (T8), `LaunchdInstaller` (T11); `IHeicConverter` (T5) lets T9 run on Linux.
