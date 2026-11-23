using System.Globalization;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using Microsoft.Data.Sqlite;

namespace LanTransfer.Platform.Windows.Storage;

/// <summary>
/// Windows SQLite 数据库宿主（设计 M2、任务 21.2）：WAL 模式、user_version = 1，不降级不删库。
/// 所有写操作由单一锁串行执行，与 Android Room 端保持完全一致的表结构与事务语义。
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public const int CurrentVersion = 1;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS schema_meta (key TEXT NOT NULL PRIMARY KEY, value INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS trusted_device (
          device_id BLOB NOT NULL PRIMARY KEY,
          display_name TEXT NOT NULL, device_type INTEGER NOT NULL,
          public_key BLOB NOT NULL,
          auto_accept INTEGER NOT NULL DEFAULT 0,
          paired_at INTEGER NOT NULL,
          last_ip INTEGER, last_port INTEGER);
        CREATE TABLE IF NOT EXISTS text_message (
          seq INTEGER NOT NULL PRIMARY KEY,
          peer_id BLOB NOT NULL, message_id BLOB NOT NULL,
          direction INTEGER NOT NULL, peer_name TEXT NOT NULL,
          text_utf8 BLOB NOT NULL,
          sent_at INTEGER NOT NULL,
          recorded_at INTEGER NOT NULL, recorded_tz INTEGER NOT NULL,
          status INTEGER NOT NULL,
          UNIQUE (peer_id, message_id));
        CREATE INDEX IF NOT EXISTS ix_text_message_order ON text_message (recorded_at, seq);
        CREATE TABLE IF NOT EXISTS transfer_task (
          transfer_id BLOB NOT NULL PRIMARY KEY,
          peer_id BLOB NOT NULL, peer_name TEXT NOT NULL, direction INTEGER NOT NULL,
          state INTEGER NOT NULL,
          interrupt_reason INTEGER, interrupt_detail TEXT,
          receive_dir TEXT,
          recorded_at INTEGER NOT NULL, recorded_tz INTEGER NOT NULL,
          updated_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_transfer_task_order ON transfer_task (recorded_at);
        CREATE INDEX IF NOT EXISTS ix_transfer_task_peer_state ON transfer_task (peer_id, state);
        CREATE TABLE IF NOT EXISTS transfer_file (
          transfer_id BLOB NOT NULL, file_index INTEGER NOT NULL, seq INTEGER NOT NULL,
          original_name TEXT NOT NULL, size INTEGER NOT NULL, mime TEXT NOT NULL,
          mtime INTEGER,
          state INTEGER NOT NULL, persisted INTEGER NOT NULL DEFAULT 0,
          sha256 BLOB, source_ref TEXT, temp_ref TEXT,
          started_at TEXT,
          last_write_at INTEGER,
          final_name TEXT, full_path TEXT, stored_ref TEXT,
          PRIMARY KEY (transfer_id, file_index));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_transfer_file_seq ON transfer_file (seq);
        CREATE TABLE IF NOT EXISTS pending_cancel (
          peer_id BLOB NOT NULL, transfer_id BLOB NOT NULL, created_at INTEGER NOT NULL,
          PRIMARY KEY (peer_id, transfer_id));
        """;

    private readonly Lock _writeGate = new();
    private readonly string _connectionString;
    private readonly IClock _clock;
    private SqliteConnection? _writeConn;
    private string? _initError;

    public SqliteDatabase(string dbPath, IClock clock)
    {
        _clock = clock;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        _connectionString = builder.ToString();
        Initialize(dbPath);
    }

    public bool IsHealthy => _initError is null && _writeConn is not null;

    public string? InitError => _initError;

    internal IClock Clock => _clock;

    private void Initialize(string dbPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }

            long version;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                version = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (version > CurrentVersion)
            {
                conn.Dispose();
                _initError = $"数据库版本（{version}）高于当前程序支持的版本（{CurrentVersion}）";
                return;
            }

            if (version == 0)
            {
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SchemaSql;
                    cmd.ExecuteNonQuery();
                }

                using (var meta = conn.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = "INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('next_history_seq', 1);";
                    meta.ExecuteNonQuery();
                }

                using (var ver = conn.CreateCommand())
                {
                    ver.Transaction = tx;
                    ver.CommandText = $"PRAGMA user_version = {CurrentVersion};";
                    ver.ExecuteNonQuery();
                }

                tx.Commit();
            }

            _writeConn = conn;
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
        }
    }

    internal T WithWrite<T>(Func<SqliteConnection, T> action, Func<string, T> onFail)
    {
        lock (_writeGate)
        {
            if (_initError is not null || _writeConn is null)
            {
                return onFail(_initError ?? "database not open");
            }

            try
            {
                return action(_writeConn);
            }
            catch (Exception ex)
            {
                return onFail(ex.Message);
            }
        }
    }

    internal T WithRead<T>(Func<SqliteConnection, T> action, Func<string, T> onFail)
    {
        lock (_writeGate)
        {
            if (_initError is not null || _writeConn is null)
            {
                return onFail(_initError ?? "database not open");
            }

            try
            {
                return action(_writeConn);
            }
            catch (Exception ex)
            {
                return onFail(ex.Message);
            }
        }
    }

    internal static long AllocateSeq(SqliteConnection conn, SqliteTransaction tx)
    {
        long next;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT value FROM schema_meta WHERE key = 'next_history_seq';";
            var scalar = sel.ExecuteScalar();
            next = scalar is null ? 1 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "INSERT INTO schema_meta (key, value) VALUES ('next_history_seq', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            upd.Parameters.AddWithValue("$v", next + 1);
            upd.ExecuteNonQuery();
        }

        return next;
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            _writeConn?.Dispose();
            _writeConn = null;
        }
    }
}

public sealed class SqliteTrustStore(SqliteDatabase db) : ITrustStore
{
    private readonly Subject<Unit> _changes = new();

    public IObservable<Unit> Changes => _changes;

    public IReadOnlyList<TrustedDevice> All() =>
        db.WithRead<IReadOnlyList<TrustedDevice>>(
            conn =>
            {
                var list = new List<TrustedDevice>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT device_id, display_name, device_type, public_key, auto_accept, paired_at, last_ip, last_port FROM trusted_device ORDER BY paired_at ASC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(ReadTrusted(r));
                }

                return list;
            },
            _ => []);

    public TrustedDevice? Get(DeviceId id) =>
        db.WithRead<TrustedDevice?>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT device_id, display_name, device_type, public_key, auto_accept, paired_at, last_ip, last_port FROM trusted_device WHERE device_id = $id;";
                cmd.Parameters.AddWithValue("$id", id.Value.ToArray());
                using var r = cmd.ExecuteReader();
                return r.Read() ? ReadTrusted(r) : null;
            },
            _ => null);

    public StoreResult Upsert(TrustedDevice d)
    {
        var r = db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO trusted_device (device_id, display_name, device_type, public_key, auto_accept, paired_at, last_ip, last_port)
                    VALUES ($id, $name, $type, $pk, $auto, $paired, $ip, $port)
                    ON CONFLICT(device_id) DO UPDATE SET
                      display_name = excluded.display_name,
                      device_type = excluded.device_type,
                      public_key = excluded.public_key,
                      auto_accept = excluded.auto_accept,
                      paired_at = excluded.paired_at,
                      last_ip = excluded.last_ip,
                      last_port = excluded.last_port;
                    """;
                cmd.Parameters.AddWithValue("$id", d.DeviceId.Value.ToArray());
                cmd.Parameters.AddWithValue("$name", d.DisplayName);
                cmd.Parameters.AddWithValue("$type", (int)d.DeviceType);
                cmd.Parameters.AddWithValue("$pk", d.PublicKey.ToArray());
                cmd.Parameters.AddWithValue("$auto", d.AutoAccept ? 1 : 0);
                cmd.Parameters.AddWithValue("$paired", d.PairedAtUtcMs);
                cmd.Parameters.AddWithValue("$ip", d.LastAddress is { } ip ? (object)(long)ip.Bits : DBNull.Value);
                cmd.Parameters.AddWithValue("$port", d.LastPort is { } p ? (object)p : DBNull.Value);
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));
        if (r is StoreResult.Ok)
        {
            _changes.OnNext(Unit.Value);
        }

        return r;
    }

    public StoreResult Delete(DeviceId id)
    {
        var r = db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM trusted_device WHERE device_id = $id;";
                cmd.Parameters.AddWithValue("$id", id.Value.ToArray());
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));
        if (r is StoreResult.Ok)
        {
            _changes.OnNext(Unit.Value);
        }

        return r;
    }

    public StoreResult SetAutoAccept(DeviceId id, bool on)
    {
        var r = db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE trusted_device SET auto_accept = $on WHERE device_id = $id;";
                cmd.Parameters.AddWithValue("$on", on ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id.Value.ToArray());
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));
        if (r is StoreResult.Ok)
        {
            _changes.OnNext(Unit.Value);
        }

        return r;
    }

    public StoreResult UpdateName(DeviceId id, string name)
    {
        var r = db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE trusted_device SET display_name = $name WHERE device_id = $id;";
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$id", id.Value.ToArray());
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));
        if (r is StoreResult.Ok)
        {
            _changes.OnNext(Unit.Value);
        }

        return r;
    }

    public StoreResult RecordAddress(DeviceId id, Ipv4 ip, int port) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE trusted_device SET last_ip = $ip, last_port = $port WHERE device_id = $id;";
                cmd.Parameters.AddWithValue("$ip", (long)ip.Bits);
                cmd.Parameters.AddWithValue("$port", port);
                cmd.Parameters.AddWithValue("$id", id.Value.ToArray());
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    private static TrustedDevice ReadTrusted(SqliteDataReader r)
    {
        var id = new DeviceId(Bytes16.Of((byte[])r["device_id"]));
        var name = (string)r["display_name"];
        var typeCode = Convert.ToByte(r["device_type"], CultureInfo.InvariantCulture);
        var type = Enum.IsDefined((DeviceType)typeCode) ? (DeviceType)typeCode : DeviceType.Android;
        var pk = Bytes.Of((byte[])r["public_key"]);
        var auto = Convert.ToInt64(r["auto_accept"], CultureInfo.InvariantCulture) != 0;
        var pairedAt = Convert.ToInt64(r["paired_at"], CultureInfo.InvariantCulture);
        Ipv4? lastIp = r["last_ip"] is DBNull ? null : new Ipv4((uint)Convert.ToInt64(r["last_ip"], CultureInfo.InvariantCulture));
        int? lastPort = r["last_port"] is DBNull ? null : Convert.ToInt32(r["last_port"], CultureInfo.InvariantCulture);
        return new TrustedDevice(id, name, type, pk, auto, pairedAt, lastIp, lastPort);
    }
}

public sealed class SqliteHistoryStore(SqliteDatabase db) : IHistoryStore
{
    public TextRecord? FindText(DeviceId peer, MessageId id) =>
        db.WithRead<TextRecord?>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT seq, peer_id, message_id, direction, peer_name, text_utf8, sent_at, recorded_at, recorded_tz, status FROM text_message WHERE peer_id = $peer AND message_id = $mid;";
                cmd.Parameters.AddWithValue("$peer", peer.Value.ToArray());
                cmd.Parameters.AddWithValue("$mid", id.Value.ToArray());
                using var r = cmd.ExecuteReader();
                return r.Read() ? ReadText(r) : null;
            },
            _ => null);

    public StoreResult AppendText(TextRecord r) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var tx = conn.BeginTransaction();
                var seq = SqliteDatabase.AllocateSeq(conn, tx);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO text_message (seq, peer_id, message_id, direction, peer_name, text_utf8, sent_at, recorded_at, recorded_tz, status)
                    VALUES ($seq, $peer, $mid, $dir, $pname, $utf8, $sent, $rec, $tz, $status);
                    """;
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$peer", r.Peer.Value.ToArray());
                cmd.Parameters.AddWithValue("$mid", r.MessageId.Value.ToArray());
                cmd.Parameters.AddWithValue("$dir", (int)r.Direction);
                cmd.Parameters.AddWithValue("$pname", r.PeerName);
                cmd.Parameters.AddWithValue("$utf8", r.TextUtf8.ToArray());
                cmd.Parameters.AddWithValue("$sent", r.SentAtUtcMs);
                cmd.Parameters.AddWithValue("$rec", r.RecordedAt.UtcMs);
                cmd.Parameters.AddWithValue("$tz", r.RecordedAt.OffsetMin);
                cmd.Parameters.AddWithValue("$status", (int)r.Status);
                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    tx.Rollback();
                    return new StoreResult.Duplicate();
                }

                tx.Commit();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public StoreResult UpdateTextStatus(MessageId id, DeviceId peer, TextStatus s) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE text_message SET status = $s WHERE peer_id = $peer AND message_id = $mid;";
                cmd.Parameters.AddWithValue("$s", (int)s);
                cmd.Parameters.AddWithValue("$peer", peer.Value.ToArray());
                cmd.Parameters.AddWithValue("$mid", id.Value.ToArray());
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public StoreResult UpsertFile(FileRecord r) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var tx = conn.BeginTransaction();
                using (var tcmd = conn.CreateCommand())
                {
                    tcmd.Transaction = tx;
                    tcmd.CommandText = """
                        INSERT INTO transfer_task (transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at)
                        VALUES ($tid, $peer, $pname, $dir, $state, $ir, $id, NULL, $rec, $tz, $upd)
                        ON CONFLICT(transfer_id) DO UPDATE SET
                          state = excluded.state,
                          interrupt_reason = excluded.interrupt_reason,
                          interrupt_detail = excluded.interrupt_detail,
                          updated_at = excluded.updated_at;
                        """;
                    tcmd.Parameters.AddWithValue("$tid", r.File.TransferId.Value.ToArray());
                    tcmd.Parameters.AddWithValue("$peer", r.Peer.Value.ToArray());
                    tcmd.Parameters.AddWithValue("$pname", r.PeerName);
                    tcmd.Parameters.AddWithValue("$dir", (int)r.Direction);
                    tcmd.Parameters.AddWithValue("$state", (int)r.TaskState);
                    tcmd.Parameters.AddWithValue("$ir", r.Interrupt is { } i ? (object)(int)i.Reason : DBNull.Value);
                    tcmd.Parameters.AddWithValue("$id", r.Interrupt?.Detail is { } d ? (object)d : DBNull.Value);
                    tcmd.Parameters.AddWithValue("$rec", r.RecordedAt.UtcMs);
                    tcmd.Parameters.AddWithValue("$tz", r.RecordedAt.OffsetMin);
                    tcmd.Parameters.AddWithValue("$upd", db.Clock.WallMs());
                    tcmd.ExecuteNonQuery();
                }

                SqliteTaskStore.UpsertFileRow(conn, tx, r.File);
                tx.Commit();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public ListResult<HistoryRecord> List(int limit = 10_000) =>
        db.WithRead<ListResult<HistoryRecord>>(
            conn =>
            {
                var items = new List<HistoryRecord>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT
                      0 AS kind,
                      t.seq AS seq, t.recorded_at AS rec_at, t.recorded_tz AS rec_tz,
                      t.peer_id AS peer_id, t.peer_name AS peer_name, t.direction AS direction,
                      t.message_id AS msg_id, t.text_utf8 AS text_utf8, t.sent_at AS sent_at, t.status AS text_status,
                      NULL AS transfer_id, NULL AS file_index, NULL AS original_name, NULL AS size, NULL AS mime, NULL AS mtime,
                      NULL AS file_state, NULL AS persisted, NULL AS sha256, NULL AS source_ref, NULL AS temp_ref,
                      NULL AS started_at, NULL AS last_write_at, NULL AS final_name, NULL AS full_path, NULL AS stored_ref,
                      NULL AS task_state, NULL AS interrupt_reason, NULL AS interrupt_detail
                    FROM text_message t
                    UNION ALL
                    SELECT
                      1 AS kind,
                      f.seq AS seq, k.recorded_at AS rec_at, k.recorded_tz AS rec_tz,
                      k.peer_id AS peer_id, k.peer_name AS peer_name, k.direction AS direction,
                      NULL AS msg_id, NULL AS text_utf8, NULL AS sent_at, NULL AS text_status,
                      f.transfer_id AS transfer_id, f.file_index AS file_index, f.original_name AS original_name, f.size AS size, f.mime AS mime, f.mtime AS mtime,
                      f.state AS file_state, f.persisted AS persisted, f.sha256 AS sha256, f.source_ref AS source_ref, f.temp_ref AS temp_ref,
                      f.started_at AS started_at, f.last_write_at AS last_write_at, f.final_name AS final_name, f.full_path AS full_path, f.stored_ref AS stored_ref,
                      k.state AS task_state, k.interrupt_reason AS interrupt_reason, k.interrupt_detail AS interrupt_detail
                    FROM transfer_file f
                    INNER JOIN transfer_task k ON f.transfer_id = k.transfer_id
                    ORDER BY rec_at ASC, seq ASC
                    LIMIT $lim;
                    """;
                cmd.Parameters.AddWithValue("$lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var kind = r.GetInt32(0);
                    var seq = r.GetInt64(1);
                    var recAt = new RecordTime(r.GetInt64(2), r.GetInt32(3));
                    var peer = new DeviceId(Bytes16.Of((byte[])r["peer_id"]));
                    var peerName = (string)r["peer_name"];
                    var dir = (Direction)Convert.ToInt32(r["direction"], CultureInfo.InvariantCulture);
                    if (kind == 0)
                    {
                        var mid = new MessageId(Bytes16.Of((byte[])r["msg_id"]));
                        var utf8 = Bytes.Of((byte[])r["text_utf8"]);
                        var sentAt = Convert.ToInt64(r["sent_at"], CultureInfo.InvariantCulture);
                        var st = (TextStatus)Convert.ToInt32(r["text_status"], CultureInfo.InvariantCulture);
                        items.Add(new HistoryRecord.Text(new TextRecord(seq, peer, peerName, mid, dir, utf8, sentAt, recAt, st)));
                    }
                    else
                    {
                        var file = SqliteTaskStore.ReadFile(r, "transfer_id", "file_index", "seq", "original_name", "size", "mime", "mtime", "file_state", "persisted", "sha256", "source_ref", "temp_ref", "started_at", "last_write_at", "final_name", "full_path", "stored_ref");
                        var taskState = (TaskState)Convert.ToInt32(r["task_state"], CultureInfo.InvariantCulture);
                        InterruptInfo? interrupt = r["interrupt_reason"] is DBNull
                            ? null
                            : new InterruptInfo((InterruptReason)Convert.ToInt32(r["interrupt_reason"], CultureInfo.InvariantCulture), r["interrupt_detail"] as string);
                        items.Add(new HistoryRecord.File(new FileRecord(file, peer, peerName, dir, recAt, taskState, interrupt)));
                    }
                }

                return new ListResult<HistoryRecord>.Ok(new ValueList<HistoryRecord>(items));
            },
            err => new ListResult<HistoryRecord>.Failed(err));

    public StoreResult Delete(RecordId id) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM text_message WHERE seq = $seq;";
                cmd.Parameters.AddWithValue("$seq", id.Seq);
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public StoreResult DeleteFileRecord(TransferId id, int index) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var tx = conn.BeginTransaction();
                var tid = id.Value.ToArray();
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM transfer_file WHERE transfer_id = $tid AND file_index = $idx;";
                    del.Parameters.AddWithValue("$tid", tid);
                    del.Parameters.AddWithValue("$idx", index);
                    del.ExecuteNonQuery();
                }

                long remaining;
                using (var cnt = conn.CreateCommand())
                {
                    cnt.Transaction = tx;
                    cnt.CommandText = "SELECT COUNT(*) FROM transfer_file WHERE transfer_id = $tid;";
                    cnt.Parameters.AddWithValue("$tid", tid);
                    remaining = Convert.ToInt64(cnt.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                if (remaining == 0)
                {
                    using var dt = conn.CreateCommand();
                    dt.Transaction = tx;
                    dt.CommandText = "DELETE FROM transfer_task WHERE transfer_id = $tid;";
                    dt.Parameters.AddWithValue("$tid", tid);
                    dt.ExecuteNonQuery();
                }

                tx.Commit();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public StoreResult ClearAll() =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM text_message; DELETE FROM transfer_file; DELETE FROM transfer_task;";
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    private static TextRecord ReadText(SqliteDataReader r) =>
        new(
            Convert.ToInt64(r["seq"], CultureInfo.InvariantCulture),
            new DeviceId(Bytes16.Of((byte[])r["peer_id"])),
            (string)r["peer_name"],
            new MessageId(Bytes16.Of((byte[])r["message_id"])),
            (Direction)Convert.ToInt32(r["direction"], CultureInfo.InvariantCulture),
            Bytes.Of((byte[])r["text_utf8"]),
            Convert.ToInt64(r["sent_at"], CultureInfo.InvariantCulture),
            new RecordTime(Convert.ToInt64(r["recorded_at"], CultureInfo.InvariantCulture), Convert.ToInt32(r["recorded_tz"], CultureInfo.InvariantCulture)),
            (TextStatus)Convert.ToInt32(r["status"], CultureInfo.InvariantCulture));
}

public sealed class SqliteTaskStore(SqliteDatabase db) : ITaskStore
{
    public StoreResult Save(TransferTask t) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO transfer_task (transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at)
                        VALUES ($tid, $peer, $pname, $dir, $state, $ir, $id, $rdir, $rec, $tz, $upd)
                        ON CONFLICT(transfer_id) DO UPDATE SET
                          peer_name = excluded.peer_name,
                          state = excluded.state,
                          interrupt_reason = excluded.interrupt_reason,
                          interrupt_detail = excluded.interrupt_detail,
                          receive_dir = excluded.receive_dir,
                          updated_at = excluded.updated_at;
                        """;
                    cmd.Parameters.AddWithValue("$tid", t.TransferId.Value.ToArray());
                    cmd.Parameters.AddWithValue("$peer", t.Peer.Value.ToArray());
                    cmd.Parameters.AddWithValue("$pname", t.PeerName);
                    cmd.Parameters.AddWithValue("$dir", (int)t.Direction);
                    cmd.Parameters.AddWithValue("$state", (int)t.State);
                    cmd.Parameters.AddWithValue("$ir", t.Interrupt is { } i ? (object)(int)i.Reason : DBNull.Value);
                    cmd.Parameters.AddWithValue("$id", t.Interrupt?.Detail is { } d ? (object)d : DBNull.Value);
                    cmd.Parameters.AddWithValue("$rdir", t.ReceiveDir is { } rd ? (object)rd.Serialize() : DBNull.Value);
                    cmd.Parameters.AddWithValue("$rec", t.RecordedAt.UtcMs);
                    cmd.Parameters.AddWithValue("$tz", t.RecordedAt.OffsetMin);
                    cmd.Parameters.AddWithValue("$upd", t.UpdatedAtUtcMs);
                    cmd.ExecuteNonQuery();
                }

                foreach (var f in t.Files)
                {
                    UpsertFileRow(conn, tx, f);
                }

                tx.Commit();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public TransferTask? Get(TransferId id) =>
        db.WithRead<TransferTask?>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at FROM transfer_task WHERE transfer_id = $tid;";
                cmd.Parameters.AddWithValue("$tid", id.Value.ToArray());
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    return null;
                }

                var task = ReadTaskHeader(r);
                r.Close();
                return task with { Files = ReadFiles(conn, id) };
            },
            _ => null);

    public IReadOnlyList<TransferTask> Unfinished() =>
        db.WithRead<IReadOnlyList<TransferTask>>(
            conn =>
            {
                var headers = new List<TransferTask>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at FROM transfer_task WHERE state < 10 ORDER BY recorded_at ASC;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        headers.Add(ReadTaskHeader(r));
                    }
                }

                return [.. headers.Select(t => t with { Files = ReadFiles(conn, t.TransferId) })];
            },
            _ => []);

    public StoreResult AddPendingCancel(DeviceId peer, TransferId id) =>
        db.WithWrite<StoreResult>(
            conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO pending_cancel (peer_id, transfer_id, created_at) VALUES ($peer, $tid, $at);";
                cmd.Parameters.AddWithValue("$peer", peer.Value.ToArray());
                cmd.Parameters.AddWithValue("$tid", id.Value.ToArray());
                cmd.Parameters.AddWithValue("$at", db.Clock.WallMs());
                cmd.ExecuteNonQuery();
                return new StoreResult.Ok();
            },
            err => new StoreResult.Failed(err));

    public IReadOnlyList<TransferId> TakePendingCancels(DeviceId peer) =>
        db.WithWrite<IReadOnlyList<TransferId>>(
            conn =>
            {
                using var tx = conn.BeginTransaction();
                var list = new List<TransferId>();
                var peerBytes = peer.Value.ToArray();
                using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT transfer_id FROM pending_cancel WHERE peer_id = $peer ORDER BY created_at ASC;";
                    sel.Parameters.AddWithValue("$peer", peerBytes);
                    using var r = sel.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new TransferId(Bytes16.Of((byte[])r["transfer_id"])));
                    }
                }

                if (list.Count > 0)
                {
                    using var del = conn.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM pending_cancel WHERE peer_id = $peer;";
                    del.Parameters.AddWithValue("$peer", peerBytes);
                    del.ExecuteNonQuery();
                }

                tx.Commit();
                return list;
            },
            _ => []);

    internal static void UpsertFileRow(SqliteConnection conn, SqliteTransaction tx, TransferFile f)
    {
        var tid = f.TransferId.Value.ToArray();
        long? existingSeq = null;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT seq FROM transfer_file WHERE transfer_id = $tid AND file_index = $idx;";
            sel.Parameters.AddWithValue("$tid", tid);
            sel.Parameters.AddWithValue("$idx", f.Index);
            var v = sel.ExecuteScalar();
            if (v is not null and not DBNull)
            {
                existingSeq = Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
        }

        var seq = existingSeq ?? SqliteDatabase.AllocateSeq(conn, tx);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO transfer_file (
              transfer_id, file_index, seq, original_name, size, mime, mtime,
              state, persisted, sha256, source_ref, temp_ref, started_at, last_write_at,
              final_name, full_path, stored_ref)
            VALUES (
              $tid, $idx, $seq, $name, $size, $mime, $mtime,
              $state, $persisted, $sha, $src, $tmp, $started, $lwa,
              $fname, $fpath, $sref)
            ON CONFLICT(transfer_id, file_index) DO UPDATE SET
              size = excluded.size,
              mtime = excluded.mtime,
              state = excluded.state,
              persisted = excluded.persisted,
              sha256 = excluded.sha256,
              source_ref = excluded.source_ref,
              temp_ref = excluded.temp_ref,
              started_at = excluded.started_at,
              last_write_at = excluded.last_write_at,
              final_name = excluded.final_name,
              full_path = excluded.full_path,
              stored_ref = excluded.stored_ref;
            """;
        cmd.Parameters.AddWithValue("$tid", tid);
        cmd.Parameters.AddWithValue("$idx", f.Index);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$name", f.OriginalName);
        cmd.Parameters.AddWithValue("$size", f.Size);
        cmd.Parameters.AddWithValue("$mime", f.Mime);
        cmd.Parameters.AddWithValue("$mtime", f.Mtime is { } m ? (object)m : DBNull.Value);
        cmd.Parameters.AddWithValue("$state", (int)f.State);
        cmd.Parameters.AddWithValue("$persisted", f.Persisted);
        cmd.Parameters.AddWithValue("$sha", f.Sha256 is { } s ? (object)s.ToArray() : DBNull.Value);
        cmd.Parameters.AddWithValue("$src", f.SourceRef is { } sr ? (object)sr.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$tmp", f.TempRef is { } tr ? (object)tr.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$started", f.StartedAt is { } dt ? (object)dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$lwa", f.LastWriteAtUtcMs is { } lwa ? (object)lwa : DBNull.Value);
        cmd.Parameters.AddWithValue("$fname", f.FinalName is { } fn ? (object)fn : DBNull.Value);
        cmd.Parameters.AddWithValue("$fpath", f.FullPath is { } fp ? (object)fp : DBNull.Value);
        cmd.Parameters.AddWithValue("$sref", f.StoredRef is { } st ? (object)st.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static ValueList<TransferFile> ReadFiles(SqliteConnection conn, TransferId id)
    {
        var list = new List<TransferFile>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT transfer_id, file_index, seq, original_name, size, mime, mtime, state, persisted, sha256, source_ref, temp_ref, started_at, last_write_at, final_name, full_path, stored_ref FROM transfer_file WHERE transfer_id = $tid ORDER BY file_index ASC;";
        cmd.Parameters.AddWithValue("$tid", id.Value.ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadFile(r, "transfer_id", "file_index", "seq", "original_name", "size", "mime", "mtime", "state", "persisted", "sha256", "source_ref", "temp_ref", "started_at", "last_write_at", "final_name", "full_path", "stored_ref"));
        }

        return new ValueList<TransferFile>(list);
    }

    private static TransferTask ReadTaskHeader(SqliteDataReader r)
    {
        var tid = new TransferId(Bytes16.Of((byte[])r["transfer_id"]));
        var peer = new DeviceId(Bytes16.Of((byte[])r["peer_id"]));
        var peerName = (string)r["peer_name"];
        var dir = (Direction)Convert.ToInt32(r["direction"], CultureInfo.InvariantCulture);
        var state = (TaskState)Convert.ToInt32(r["state"], CultureInfo.InvariantCulture);
        InterruptInfo? interrupt = r["interrupt_reason"] is DBNull
            ? null
            : new InterruptInfo((InterruptReason)Convert.ToInt32(r["interrupt_reason"], CultureInfo.InvariantCulture), r["interrupt_detail"] as string);
        DirRef? receiveDir = r["receive_dir"] is string rd ? DirRef.Parse(rd) : null;
        var recordedAt = new RecordTime(Convert.ToInt64(r["recorded_at"], CultureInfo.InvariantCulture), Convert.ToInt32(r["recorded_tz"], CultureInfo.InvariantCulture));
        var updatedAt = Convert.ToInt64(r["updated_at"], CultureInfo.InvariantCulture);
        return new TransferTask(tid, peer, peerName, dir, state, interrupt, receiveDir, recordedAt, updatedAt, ValueList<TransferFile>.Empty);
    }

    internal static TransferFile ReadFile(
        SqliteDataReader r,
        string cTid,
        string cIdx,
        string cSeq,
        string cName,
        string cSize,
        string cMime,
        string cMtime,
        string cState,
        string cPersisted,
        string cSha,
        string cSrc,
        string cTmp,
        string cStarted,
        string cLwa,
        string cFname,
        string cFpath,
        string cSref)
    {
        var tid = new TransferId(Bytes16.Of((byte[])r[cTid]));
        var idx = Convert.ToInt32(r[cIdx], CultureInfo.InvariantCulture);
        var seq = Convert.ToInt64(r[cSeq], CultureInfo.InvariantCulture);
        var name = (string)r[cName];
        var size = Convert.ToInt64(r[cSize], CultureInfo.InvariantCulture);
        var mime = (string)r[cMime];
        long? mtime = r[cMtime] is DBNull ? null : Convert.ToInt64(r[cMtime], CultureInfo.InvariantCulture);
        var state = (FileState)Convert.ToInt32(r[cState], CultureInfo.InvariantCulture);
        var persisted = Convert.ToInt64(r[cPersisted], CultureInfo.InvariantCulture);
        Bytes? sha = r[cSha] is byte[] sb ? Bytes.Of(sb) : null;
        SourceRef? src = r[cSrc] is string sr ? new SourceRef(sr) : null;
        TempRef? tmp = r[cTmp] is string tr ? new TempRef(tr) : null;
        DateTime? startedAt = r[cStarted] is string st && DateTime.TryParseExact(st, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        long? lwa = r[cLwa] is DBNull ? null : Convert.ToInt64(r[cLwa], CultureInfo.InvariantCulture);
        var fname = r[cFname] as string;
        var fpath = r[cFpath] as string;
        StoredFileRef? sref = r[cSref] is string sf ? new StoredFileRef(sf) : null;
        return new TransferFile(tid, idx, seq, name, size, mime, mtime, state, persisted, sha, src, tmp, startedAt, lwa, fname, fpath, sref);
    }
}
