using System.Globalization;
using Microsoft.Data.Sqlite;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class StateStoreTests : IDisposable
{
    private static readonly DateTimeOffset _sentAt = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(-5));

    private readonly TempDir _dir = new();
    private readonly string _dbPath;
    private readonly StateStore _store;

    public StateStoreTests()
    {
        _dbPath = Path.Combine(_dir.Path, "state", "state.db");
        _store = new StateStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static Item MakeItem(string id, string? md5 = null) =>
        new(id, $"{id}.jpg", ItemKind.Photo, _sentAt.AddDays(-1), 1234, md5, "image/jpeg");

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private object? Scalar(string sql)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void Open_creates_schema_and_parent_directory_in_wal_mode()
    {
        Assert.True(File.Exists(_dbPath));

        var tables = new List<string>();
        using (var conn = OpenRaw())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("sent_items", tables);
        Assert.Contains("skipped_items", tables);
        Assert.Contains("runs", tables);
        Assert.Contains("auth_state", tables);
        Assert.Contains("alerts", tables);
        Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
    }

    [Fact]
    public void Reopening_an_existing_database_keeps_its_data()
    {
        _store.MarkSent([MakeItem("a")], "frame@example.com", "batch-1", _sentAt);
        _store.Dispose();

        using var reopened = new StateStore(_dbPath);

        Assert.True(reopened.IsSent("a", "frame@example.com"));
    }

    [Fact]
    public void IsSent_is_false_for_unknown_item()
    {
        Assert.False(_store.IsSent("nope", "frame@example.com"));
    }

    [Fact]
    public void MarkSent_marks_every_item_in_the_list()
    {
        _store.MarkSent([MakeItem("a"), MakeItem("b")], "frame@example.com", "batch-1", _sentAt);

        Assert.True(_store.IsSent("a", "frame@example.com"));
        Assert.True(_store.IsSent("b", "frame@example.com"));
        Assert.False(_store.IsSent("c", "frame@example.com"));
    }

    [Fact]
    public void MarkSent_is_per_recipient()
    {
        _store.MarkSent([MakeItem("a")], "frame@example.com", "batch-1", _sentAt);

        Assert.True(_store.IsSent("a", "frame@example.com"));
        Assert.False(_store.IsSent("a", "calendar@example.com"));
    }

    [Fact]
    public void MarkSent_twice_for_the_same_item_and_recipient_does_not_throw()
    {
        _store.MarkSent([MakeItem("a")], "frame@example.com", "batch-1", _sentAt);
        _store.MarkSent([MakeItem("a")], "frame@example.com", "batch-2", _sentAt.AddHours(1));

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sent_items"));
    }

    [Fact]
    public void MarkSent_stores_batch_id_and_sent_at_as_iso8601_utc_text()
    {
        _store.MarkSent([MakeItem("a")], "frame@example.com", "batch-1", _sentAt);

        Assert.Equal("batch-1", Scalar("SELECT batch_id FROM sent_items"));
        var stored = Assert.IsType<string>(Scalar("SELECT sent_at FROM sent_items"));
        Assert.EndsWith("Z", stored);
        var parsed = DateTimeOffset.ParseExact(stored, "O", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(_sentAt, parsed);
    }

    [Fact]
    public void IsMd5Sent_matches_md5_per_recipient()
    {
        _store.MarkSent([MakeItem("a", md5: "abc123")], "frame@example.com", "batch-1", _sentAt);

        Assert.True(_store.IsMd5Sent("abc123", "frame@example.com"));
        Assert.False(_store.IsMd5Sent("abc123", "calendar@example.com"));
        Assert.False(_store.IsMd5Sent("zzz999", "frame@example.com"));
    }

    [Fact]
    public void MarkSent_accepts_items_without_md5()
    {
        _store.MarkSent([MakeItem("a", md5: null)], "frame@example.com", "batch-1", _sentAt);

        Assert.True(_store.IsSent("a", "frame@example.com"));
        Assert.False(_store.IsMd5Sent("", "frame@example.com"));
    }

    [Fact]
    public void MarkSkipped_then_IsSkipped()
    {
        Assert.False(_store.IsSkipped("a"));

        _store.MarkSkipped("a", SkipReason.Oversize, "41 MB after resize");

        Assert.True(_store.IsSkipped("a"));
        Assert.False(_store.IsSkipped("b"));
        Assert.Equal("Oversize", Scalar("SELECT reason FROM skipped_items WHERE item_id = 'a'"));
        Assert.Equal("41 MB after resize", Scalar("SELECT detail FROM skipped_items WHERE item_id = 'a'"));
    }

    [Fact]
    public void MarkSkipped_twice_keeps_the_latest_reason()
    {
        _store.MarkSkipped("a", SkipReason.DownloadFailed, "timeout");
        _store.MarkSkipped("a", SkipReason.ProcessingFailed, "decode error");

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM skipped_items"));
        Assert.Equal("ProcessingFailed", Scalar("SELECT reason FROM skipped_items WHERE item_id = 'a'"));
    }

    [Fact]
    public void StartRun_returns_increasing_ids()
    {
        var first = _store.StartRun(SourceMode.Album);
        var second = _store.StartRun(SourceMode.All);

        Assert.True(second > first);
    }

    [Fact]
    public void LastRun_is_null_before_any_run()
    {
        Assert.Null(_store.LastRun());
    }

    [Fact]
    public void LastRun_reflects_an_unfinished_run()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var id = _store.StartRun(SourceMode.All);

        var run = _store.LastRun();

        Assert.NotNull(run);
        Assert.Equal(id, run.Id);
        Assert.Equal(SourceMode.All, run.Mode);
        Assert.InRange(run.StartedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(TimeSpan.Zero, run.StartedAt.Offset);
        Assert.Null(run.FinishedAt);
        Assert.Equal(0, run.Found);
        Assert.Equal(0, run.Sent);
        Assert.Equal(0, run.Skipped);
        Assert.Null(run.Error);
    }

    [Fact]
    public void FinishRun_records_counts_and_error_on_the_latest_run()
    {
        _store.StartRun(SourceMode.Album);
        var id = _store.StartRun(SourceMode.Album);

        _store.FinishRun(id, found: 10, sent: 7, skipped: 2, error: "smtp down");

        var run = _store.LastRun();
        Assert.NotNull(run);
        Assert.Equal(id, run.Id);
        Assert.NotNull(run.FinishedAt);
        Assert.True(run.FinishedAt >= run.StartedAt);
        Assert.Equal(10, run.Found);
        Assert.Equal(7, run.Sent);
        Assert.Equal(2, run.Skipped);
        Assert.Equal("smtp down", run.Error);
    }

    [Fact]
    public void FinishRun_with_null_error_is_a_success()
    {
        var id = _store.StartRun(SourceMode.Album);

        _store.FinishRun(id, found: 3, sent: 3, skipped: 0, error: null);

        var run = _store.LastRun();
        Assert.NotNull(run);
        Assert.Null(run.Error);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public void Run_timestamps_are_stored_as_iso8601_utc_text()
    {
        var id = _store.StartRun(SourceMode.Album);
        _store.FinishRun(id, 0, 0, 0, null);

        foreach (var column in new[] { "started_at", "finished_at" })
        {
            var stored = Assert.IsType<string>(Scalar($"SELECT {column} FROM runs WHERE id = {id}"));
            Assert.EndsWith("Z", stored);
            DateTimeOffset.ParseExact(stored, "O", CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public void ConsecutiveFailures_is_zero_with_no_runs()
    {
        Assert.Equal(0, _store.ConsecutiveFailures());
    }

    [Fact]
    public void ConsecutiveFailures_counts_trailing_failed_runs()
    {
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, null);
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 1");
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 2");
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 3");

        Assert.Equal(3, _store.ConsecutiveFailures());
    }

    [Fact]
    public void ConsecutiveFailures_resets_on_success()
    {
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 1");
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 2");
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, null);

        Assert.Equal(0, _store.ConsecutiveFailures());

        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 3");

        Assert.Equal(1, _store.ConsecutiveFailures());
    }

    [Fact]
    public void ConsecutiveFailures_ignores_unfinished_runs()
    {
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 1");
        _store.FinishRun(_store.StartRun(SourceMode.Album), 0, 0, 0, "boom 2");
        _store.StartRun(SourceMode.Album);

        Assert.Equal(2, _store.ConsecutiveFailures());
    }

    [Fact]
    public void NeedsLogin_is_false_by_default()
    {
        Assert.False(_store.NeedsLogin());
    }

    [Fact]
    public void SetNeedsLogin_true_then_false()
    {
        _store.SetNeedsLogin(true, _sentAt);

        Assert.True(_store.NeedsLogin());
        var stored = Assert.IsType<string>(Scalar("SELECT last_401_at FROM auth_state"));
        Assert.Equal(_sentAt, DateTimeOffset.ParseExact(stored, "O", CultureInfo.InvariantCulture));
        Assert.EndsWith("Z", stored);

        _store.SetNeedsLogin(false, null);

        Assert.False(_store.NeedsLogin());
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM auth_state"));
    }

    [Fact]
    public void LastAlertAt_is_null_for_unknown_kind()
    {
        Assert.Null(_store.LastAlertAt("AuthExpired"));
    }

    [Fact]
    public void RecordAlert_then_LastAlertAt_round_trips_in_utc_per_kind()
    {
        _store.RecordAlert("AuthExpired", _sentAt);

        var at = _store.LastAlertAt("AuthExpired");

        Assert.NotNull(at);
        Assert.Equal(_sentAt, at.Value);
        Assert.Equal(TimeSpan.Zero, at.Value.Offset);
        Assert.Null(_store.LastAlertAt("RepeatedFailure"));
    }

    [Fact]
    public void RecordAlert_overwrites_the_previous_time_for_that_kind()
    {
        _store.RecordAlert("AuthExpired", _sentAt);
        _store.RecordAlert("AuthExpired", _sentAt.AddDays(2));

        Assert.Equal(_sentAt.AddDays(2), _store.LastAlertAt("AuthExpired"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM alerts"));
    }

    [Fact]
    public void ResetSent_clears_sent_items_and_returns_the_count()
    {
        _store.MarkSent([MakeItem("a", "m1"), MakeItem("b", "m2")], "frame@example.com", "batch-1", _sentAt);
        _store.MarkSent([MakeItem("a", "m1")], "calendar@example.com", "batch-1", _sentAt);
        _store.MarkSkipped("c", SkipReason.Oversize, "too big");

        var removed = _store.ResetSent();

        Assert.Equal(3, removed);
        Assert.False(_store.IsSent("a", "frame@example.com"));
        Assert.False(_store.IsMd5Sent("m1", "frame@example.com"));
        Assert.True(_store.IsSkipped("c"));
        Assert.Equal(0, _store.ResetSent());
    }

    [Fact]
    public void Dispose_releases_the_database_file()
    {
        _store.MarkSent([MakeItem("a")], "frame@example.com", "batch-1", _sentAt);
        _store.Dispose();
        SqliteConnection.ClearAllPools();

        File.Delete(_dbPath);

        Assert.False(File.Exists(_dbPath));
    }
}
