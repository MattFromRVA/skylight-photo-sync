using System.Globalization;
using Microsoft.Data.Sqlite;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Models;

namespace SkylightPhotoSync.State;

/// <summary>
/// The SQLite state behind <c>state.db</c>: what was sent to whom, which uploaded photos are on which
/// frame, what was skipped, run history,
/// the <c>needs_login</c> flag, a Skylight sign-in failure and the last alert time per kind. Opening the store creates the
/// schema when needed and switches the file to WAL mode. Timestamps are stored as ISO-8601 UTC text.
/// </summary>
public sealed class StateStore : IDisposable
{
    private const string _timestampFormat = "O";

    /// <summary>Stored in <c>runs.mode</c> by <see cref="RecordFailedRun"/>; read back as a null mode.</summary>
    private const string _unknownMode = "unknown";

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

    /// <summary>True when the item, or a byte-identical copy of it, was already sent to <paramref name="recipient"/>.</summary>
    public bool WasSentTo(Item item, string recipient)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IsSent(item.Id, recipient) || (item.Md5 is not null && IsMd5Sent(item.Md5, recipient));
    }

    /// <summary>
    /// The Skylight photo ("message") id recorded with the item's latest send to <paramref name="recipient"/>;
    /// <see langword="null"/> when it was emailed, or never sent there.
    /// </summary>
    public string? SentMessageId(string itemId, string recipient) =>
        Scalar(
            "SELECT message_id FROM sent_items WHERE item_id = $itemId AND recipient = $recipient",
            ("$itemId", itemId),
            ("$recipient", recipient)) as string;

    /// <summary>
    /// Records the items as sent to <paramref name="recipient"/>: an email address, or
    /// <c>skylight:frame:&lt;id&gt;</c> for an API upload. Sending an item again (rotation's recycling)
    /// keeps one row and moves its <c>sent_at</c>, <c>batch_id</c> and <c>message_id</c> to the latest send.
    /// <paramref name="messageId"/> is the Skylight photo id an API upload returned; email has none.
    /// Every such id is also kept as a <see cref="RecordedUpload"/>, so the copy an earlier send left
    /// on the frame stays known after the row moves on to the newer one.
    /// </summary>
    public void MarkSent(IReadOnlyList<Item> items, string recipient, string batchId, DateTimeOffset sentAt, string? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sent_items (item_id, recipient, md5, batch_id, sent_at, message_id)
            VALUES ($itemId, $recipient, $md5, $batchId, $sentAt, $messageId)
            ON CONFLICT(item_id, recipient) DO UPDATE SET
                batch_id = excluded.batch_id,
                sent_at = excluded.sent_at,
                message_id = excluded.message_id
            """;
        var itemIdParam = command.Parameters.Add("$itemId", SqliteType.Text);
        var md5Param = command.Parameters.Add("$md5", SqliteType.Text);
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$sentAt", Format(sentAt));
        command.Parameters.AddWithValue("$messageId", (object?)messageId ?? DBNull.Value);
        foreach (var item in items)
        {
            itemIdParam.Value = item.Id;
            md5Param.Value = (object?)item.Md5 ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        if (messageId is not null)
        {
            using var upload = _connection.CreateCommand();
            upload.Transaction = transaction;
            upload.CommandText =
                """
                INSERT INTO uploaded_messages (recipient, message_id, item_id, uploaded_at)
                VALUES ($recipient, $messageId, $itemId, $uploadedAt)
                ON CONFLICT(recipient, message_id) DO NOTHING
                """;
            var uploadItemIdParam = upload.Parameters.Add("$itemId", SqliteType.Text);
            upload.Parameters.AddWithValue("$recipient", recipient);
            upload.Parameters.AddWithValue("$messageId", messageId);
            upload.Parameters.AddWithValue("$uploadedAt", Format(sentAt));
            foreach (var item in items)
            {
                uploadItemIdParam.Value = item.Id;
                upload.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Every photo this tool uploaded to <paramref name="recipient"/> (<c>skylight:frame:&lt;id&gt;</c>)
    /// that it has not deleted again, oldest upload first. These ids are the only API-delivered
    /// photos rotation may delete. A photo someone removed from the frame by hand stays listed.
    /// </summary>
    public IReadOnlyList<RecordedUpload> RecordedUploads(string recipient)
    {
        using var command = CreateCommand(
            """
            SELECT message_id, item_id, uploaded_at FROM uploaded_messages
            WHERE recipient = $recipient ORDER BY uploaded_at, rowid
            """,
            [("$recipient", recipient)]);
        using var reader = command.ExecuteReader();
        var result = new List<RecordedUpload>();
        while (reader.Read())
        {
            result.Add(new RecordedUpload(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2))));
        }

        return result;
    }

    /// <summary>Drops a recorded upload once its photo was deleted from the frame.</summary>
    public void ForgetUpload(string recipient, string messageId) =>
        Execute(
            "DELETE FROM uploaded_messages WHERE recipient = $recipient AND message_id = $messageId",
            ("$recipient", recipient),
            ("$messageId", messageId));

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

    /// <summary>
    /// Records a run that failed before it could start, such as one whose config.toml could not be read.
    /// It is finished at once and has no mode, and it counts towards <see cref="ConsecutiveFailures"/>.
    /// </summary>
    public void RecordFailedRun(string error)
    {
        var at = Format(DateTimeOffset.UtcNow);
        Execute(
            """
            INSERT INTO runs (started_at, finished_at, mode, found, sent, skipped, error)
            VALUES ($at, $at, $mode, 0, 0, 0, $error)
            """,
            ("$at", at),
            ("$mode", _unknownMode),
            ("$error", error));
    }

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

    /// <summary>Forgets the last alert of <paramref name="kind"/>, so the next one is not rate-limited away.</summary>
    public void ClearAlert(string kind) =>
        Execute("DELETE FROM alerts WHERE kind = $kind", ("$kind", kind));

    /// <summary>
    /// Records that Skylight refused the sign-in, which pauses rotation's pruning. <paramref name="fingerprint"/>
    /// identifies the credentials that failed without containing them (see <c>SkylightSignInGate</c>).
    /// </summary>
    public void SetSkylightSignInFailure(string reason, string fingerprint, DateTimeOffset at) =>
        Execute(
            """
            INSERT INTO skylight_auth_state (id, failed_at, reason, fingerprint)
            VALUES (1, $at, $reason, $fingerprint)
            ON CONFLICT(id) DO UPDATE SET
                failed_at = excluded.failed_at,
                reason = excluded.reason,
                fingerprint = excluded.fingerprint
            """,
            ("$at", Format(at)),
            ("$reason", reason),
            ("$fingerprint", fingerprint));

    public void ClearSkylightSignInFailure() =>
        Execute("DELETE FROM skylight_auth_state");

    /// <summary>The recorded Skylight sign-in failure, or <see langword="null"/> when pruning is not paused.</summary>
    public SkylightSignInFailure? SkylightSignInFailure()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT failed_at, reason, fingerprint FROM skylight_auth_state WHERE id = 1";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SkylightSignInFailure(Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2))
            : null;
    }

    /// <summary>Each sent item's most recent send time over all recipients, keyed by item id.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> LastSentTimes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT item_id, MAX(sent_at) FROM sent_items GROUP BY item_id";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = Parse(reader.GetString(1));
        }

        return result;
    }

    /// <summary>Records that rotation deleted Skylight photo <paramref name="messageId"/> from a frame.</summary>
    public void MarkPruned(string frameId, string messageId, DateTimeOffset createdAt, DateTimeOffset prunedAt) =>
        Execute(
            """
            INSERT INTO pruned_messages (frame_id, message_id, created_at, pruned_at)
            VALUES ($frameId, $messageId, $createdAt, $prunedAt)
            ON CONFLICT(frame_id, message_id) DO UPDATE SET pruned_at = excluded.pruned_at
            """,
            ("$frameId", frameId),
            ("$messageId", messageId),
            ("$createdAt", Format(createdAt)),
            ("$prunedAt", Format(prunedAt)));

    /// <summary>Number of Skylight photos rotation has deleted, over all runs.</summary>
    public int TotalPruned() =>
        (int)(long)Scalar("SELECT COUNT(*) FROM pruned_messages")!;

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
            Mode: Enum.TryParse<SourceMode>(reader.GetString(3), out var mode) ? mode : null,
            Found: reader.GetInt32(4),
            Sent: reader.GetInt32(5),
            Skipped: reader.GetInt32(6),
            Error: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>
    /// Starts over: forgets every sent item for every recipient and every skipped item.
    /// Returns the number of sent-item rows removed. Recorded uploads are kept: those photos are
    /// still on the frame, and rotation has to be able to delete them once they are sent again.
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

    /// <summary>
    /// Forgets every skipped item so permanently skipped ones are tried again; sent items are kept.
    /// Returns the number of skipped-item rows removed.
    /// </summary>
    public int ResetSkipped() =>
        Execute("DELETE FROM skipped_items");

    public void Dispose() => _connection.Dispose();

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString(_timestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, _timestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private void CreateSchema()
    {
        var hadUploads = Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'uploaded_messages'") is not null;
        Execute(
            """
            CREATE TABLE IF NOT EXISTS sent_items (
                item_id   TEXT NOT NULL,
                recipient TEXT NOT NULL,
                md5       TEXT,
                batch_id  TEXT NOT NULL,
                sent_at   TEXT NOT NULL,
                message_id TEXT,
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
            CREATE TABLE IF NOT EXISTS skylight_auth_state (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                failed_at   TEXT NOT NULL,
                reason      TEXT NOT NULL,
                fingerprint TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pruned_messages (
                frame_id   TEXT NOT NULL,
                message_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                pruned_at  TEXT NOT NULL,
                PRIMARY KEY (frame_id, message_id)
            );
            CREATE TABLE IF NOT EXISTS uploaded_messages (
                recipient   TEXT NOT NULL,
                message_id  TEXT NOT NULL,
                item_id     TEXT NOT NULL,
                uploaded_at TEXT NOT NULL,
                PRIMARY KEY (recipient, message_id)
            );
            """);

        // state.db files from before API delivery have no message_id; CREATE TABLE IF NOT EXISTS leaves them as they were.
        if (Scalar("SELECT 1 FROM pragma_table_info('sent_items') WHERE name = 'message_id'") is null)
        {
            Execute("ALTER TABLE sent_items ADD COLUMN message_id TEXT");
        }

        // A state.db that recorded message ids before uploaded_messages existed: carry them over, once.
        if (!hadUploads)
        {
            Execute(
                """
                INSERT OR IGNORE INTO uploaded_messages (recipient, message_id, item_id, uploaded_at)
                SELECT recipient, message_id, item_id, sent_at FROM sent_items WHERE message_id IS NOT NULL
                """);
        }
    }

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
