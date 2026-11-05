using System.Globalization;
using Microsoft.Data.Sqlite;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.State;

/// <summary>
/// The SQLite state behind <c>state.db</c>: what was sent to whom, what was skipped, run history,
/// the <c>needs_login</c> flag and the last alert time per kind. Opening the store creates the
/// schema when needed and switches the file to WAL mode. Timestamps are stored as ISO-8601 UTC text.
/// </summary>
public sealed class StateStore : IDisposable
{
    private const string _timestampFormat = "O";

    private static readonly HashSet<SkipReason> _permanentSkipReasons = [SkipReason.Oversize, SkipReason.UnsupportedType];

    private readonly SqliteConnection _connection;

    public StateStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        CreateSchema();
    }

    public bool IsSent(string itemId, string recipient) =>
        Scalar(
            "SELECT 1 FROM sent_items WHERE item_id = $itemId AND recipient = $recipient LIMIT 1",
            ("$itemId", itemId),
            ("$recipient", recipient)) is not null;

    public bool IsMd5Sent(string md5, string recipient) =>
        Scalar(
            "SELECT 1 FROM sent_items WHERE md5 = $md5 AND recipient = $recipient LIMIT 1",
            ("$md5", md5),
            ("$recipient", recipient)) is not null;

    public void MarkSent(IReadOnlyList<Item> items, string recipient, string batchId, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO sent_items (item_id, recipient, md5, batch_id, sent_at)
            VALUES ($itemId, $recipient, $md5, $batchId, $sentAt)
            """;
        var itemIdParam = command.Parameters.Add("$itemId", SqliteType.Text);
        var md5Param = command.Parameters.Add("$md5", SqliteType.Text);
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$sentAt", Format(sentAt));
        foreach (var item in items)
        {
            itemIdParam.Value = item.Id;
            md5Param.Value = (object?)item.Md5 ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void MarkSkipped(string itemId, SkipReason reason, string detail) =>
        Execute(
            """
            INSERT INTO skipped_items (item_id, reason, detail, skipped_at)
            VALUES ($itemId, $reason, $detail, $skippedAt)
            ON CONFLICT(item_id) DO UPDATE SET
                reason = excluded.reason,
                detail = excluded.detail,
                skipped_at = excluded.skipped_at
            """,
            ("$itemId", itemId),
            ("$reason", reason.ToString()),
            ("$detail", detail),
            ("$skippedAt", Format(DateTimeOffset.UtcNow)));

    /// <summary>
    /// True only when the item's recorded skip reason is permanent: <see cref="SkipReason.Oversize"/> or
    /// <see cref="SkipReason.UnsupportedType"/>, which are properties of the file. Other reasons stay recorded
    /// for diagnostics but return false so the item is retried on the next run.
    /// </summary>
    public bool IsSkipped(string itemId) =>
        Scalar("SELECT reason FROM skipped_items WHERE item_id = $itemId", ("$itemId", itemId)) is string reason
        && _permanentSkipReasons.Contains(Enum.Parse<SkipReason>(reason));

    public long StartRun(SourceMode mode) =>
        (long)Scalar(
            """
            INSERT INTO runs (started_at, mode, found, sent, skipped)
            VALUES ($startedAt, $mode, 0, 0, 0)
            RETURNING id
            """,
            ("$startedAt", Format(DateTimeOffset.UtcNow)),
            ("$mode", mode.ToString()))!;

    public void FinishRun(long runId, int found, int sent, int skipped, string? error) =>
        Execute(
            """
            UPDATE runs
            SET finished_at = $finishedAt, found = $found, sent = $sent, skipped = $skipped, error = $error
            WHERE id = $id
            """,
            ("$finishedAt", Format(DateTimeOffset.UtcNow)),
            ("$found", found),
            ("$sent", sent),
            ("$skipped", skipped),
            ("$error", error),
            ("$id", runId));

    /// <summary>Number of finished runs with an error since the last finished run without one.</summary>
    public int ConsecutiveFailures()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT error FROM runs WHERE finished_at IS NOT NULL ORDER BY id DESC";
        using var reader = command.ExecuteReader();
        var failures = 0;
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                break;
            }

            failures++;
        }

        return failures;
    }

    public void SetNeedsLogin(bool flag, DateTimeOffset? at) =>
        Execute(
            """
            INSERT INTO auth_state (id, needs_login, last_401_at)
            VALUES (1, $needsLogin, $at)
            ON CONFLICT(id) DO UPDATE SET
                needs_login = excluded.needs_login,
                last_401_at = excluded.last_401_at
            """,
            ("$needsLogin", flag ? 1 : 0),
            ("$at", at is null ? null : Format(at.Value)));

    public bool NeedsLogin() =>
        Scalar("SELECT needs_login FROM auth_state WHERE id = 1") is long value && value != 0;

    public DateTimeOffset? LastAlertAt(string kind) =>
        Scalar("SELECT last_at FROM alerts WHERE kind = $kind", ("$kind", kind)) is string text ? Parse(text) : null;

    public void RecordAlert(string kind, DateTimeOffset at) =>
        Execute(
            """
            INSERT INTO alerts (kind, last_at) VALUES ($kind, $at)
            ON CONFLICT(kind) DO UPDATE SET last_at = excluded.last_at
            """,
            ("$kind", kind),
            ("$at", Format(at)));

    /// <summary>Number of (item, recipient) deliveries recorded as sent, over all runs.</summary>
    public int TotalSent() =>
        (int)(long)Scalar("SELECT COUNT(*) FROM sent_items")!;

    public RunRecord? LastRun()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, started_at, finished_at, mode, found, sent, skipped, error
            FROM runs ORDER BY id DESC LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RunRecord(
            Id: reader.GetInt64(0),
            StartedAt: Parse(reader.GetString(1)),
            FinishedAt: reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
            Mode: Enum.Parse<SourceMode>(reader.GetString(3)),
            Found: reader.GetInt32(4),
            Sent: reader.GetInt32(5),
            Skipped: reader.GetInt32(6),
            Error: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>
    /// Starts over: forgets every sent item for every recipient and every skipped item.
    /// Returns the number of sent-item rows removed.
    /// </summary>
    public int ResetSent()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM skipped_items";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM sent_items";
        var removed = command.ExecuteNonQuery();
        transaction.Commit();
        return removed;
    }

    public void Dispose() => _connection.Dispose();

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString(_timestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, _timestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private void CreateSchema() =>
        Execute(
            """
            CREATE TABLE IF NOT EXISTS sent_items (
                item_id   TEXT NOT NULL,
                recipient TEXT NOT NULL,
                md5       TEXT,
                batch_id  TEXT NOT NULL,
                sent_at   TEXT NOT NULL,
                PRIMARY KEY (item_id, recipient)
            );
            CREATE INDEX IF NOT EXISTS ix_sent_items_md5 ON sent_items (md5, recipient);
            CREATE TABLE IF NOT EXISTS skipped_items (
                item_id    TEXT PRIMARY KEY,
                reason     TEXT NOT NULL,
                detail     TEXT NOT NULL,
                skipped_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at  TEXT NOT NULL,
                finished_at TEXT,
                mode        TEXT NOT NULL,
                found       INTEGER NOT NULL DEFAULT 0,
                sent        INTEGER NOT NULL DEFAULT 0,
                skipped     INTEGER NOT NULL DEFAULT 0,
                error       TEXT
            );
            CREATE TABLE IF NOT EXISTS auth_state (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                needs_login INTEGER NOT NULL DEFAULT 0,
                last_401_at TEXT
            );
            CREATE TABLE IF NOT EXISTS alerts (
                kind    TEXT PRIMARY KEY,
                last_at TEXT NOT NULL
            );
            """);

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteScalar();
    }

    private SqliteCommand CreateCommand(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
