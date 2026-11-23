using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

// 内存版存储（设计 C5.3、M2），与 Kotlin 侧 InMemoryStores.kt 对应：语义与 SQLite 实现一致，也是 Property 48、49 的参考模型。
// 每个写方法都可以通过 FailWrites(操作名) 注入失败（20.8）；失败时不做任何改动。操作名与 Kotlin 侧相同（"appendText"、"save" 等）。

internal static class Injected
{
    public static StoreResult Failure { get; } = new StoreResult.Failed("injected failure");
}

/// <summary>身份存储：<paramref name="initial"/> 为已保存的身份；<paramref name="corrupted"/> 为 true 时模拟 identity.bin 损坏（4.6）。显示名称独立保存，身份重置后保留。</summary>
public sealed class InMemoryIdentityStore(IRng rng, string defaultName, DeviceIdentity? initial = null, bool corrupted = false) : IIdentityStore
{
    private readonly Lock _gate = new();
    private DeviceIdentity? _current = initial;
    private string _name = initial?.DisplayName ?? defaultName;
    private bool _corrupted = corrupted;

    private int _loads;

    public bool FailWrites { get; set; }

    /// <summary>为 true 时新生成的身份无法保存：LoadOrCreate 返回 SaveFailed，状态不变（Q14）。</summary>
    public bool FailSaves { get; set; }

    /// <summary>LoadOrCreate 的调用次数。</summary>
    public int Loads => Volatile.Read(ref _loads);

    public IdentityLoad LoadOrCreate()
    {
        lock (_gate)
        {
            _loads++;
            if (_current is { } existing && !_corrupted)
            {
                return new IdentityLoad.Loaded(new DeviceIdentity(existing.DeviceId, existing.KeyPair, _name));
            }

            if (FailSaves)
            {
                return new IdentityLoad.SaveFailed("injected failure", _corrupted);
            }

            var created = new DeviceIdentity(new DeviceId(Bytes16.Of(rng.NextBytes(16))), X25519KeyPair.FromPrivateKey(rng.NextBytes(32)), _name);
            _current = created;
            var wasCorrupted = _corrupted;
            _corrupted = false;
            return wasCorrupted ? new IdentityLoad.ResetCorrupted(created) : new IdentityLoad.Created(created);
        }
    }

    public string DisplayName()
    {
        lock (_gate)
        {
            return _name;
        }
    }

    public StoreResult SetDisplayName(string name)
    {
        if (FailWrites)
        {
            return Injected.Failure;
        }

        lock (_gate)
        {
            _name = name;
        }

        return new StoreResult.Ok();
    }
}

public sealed class InMemoryTrustStore : ITrustStore
{
    private readonly Lock _gate = new();
    private readonly OrderedDictionary<DeviceId, TrustedDevice> _devices = new();
    private readonly Subject<Unit> _changes = new();

    public Func<string, bool> FailWrites { get; set; } = _ => false;

    public IObservable<Unit> Changes => _changes;

    private StoreResult Write(string op, Action block)
    {
        if (FailWrites(op))
        {
            return Injected.Failure;
        }

        lock (_gate)
        {
            block();
        }

        _changes.OnNext(Unit.Value);
        return new StoreResult.Ok();
    }

    private void Modify(DeviceId id, Func<TrustedDevice, TrustedDevice> f)
    {
        if (_devices.TryGetValue(id, out var d))
        {
            _devices[id] = f(d);
        }
    }

    public IReadOnlyList<TrustedDevice> All()
    {
        lock (_gate)
        {
            return [.. _devices.Values];
        }
    }

    public TrustedDevice? Get(DeviceId id)
    {
        lock (_gate)
        {
            return _devices.GetValueOrDefault(id);
        }
    }

    public StoreResult Upsert(TrustedDevice d) => Write("upsert", () => _devices[d.DeviceId] = d);

    public StoreResult Delete(DeviceId id) => Write("delete", () => _devices.Remove(id));

    public StoreResult SetAutoAccept(DeviceId id, bool on) => Write("setAutoAccept", () => Modify(id, d => d with { AutoAccept = on }));

    public StoreResult UpdateName(DeviceId id, string name) => Write("updateName", () => Modify(id, d => d with { DisplayName = name }));

    public StoreResult RecordAddress(DeviceId id, Ipv4 ip, int port) =>
        Write("recordAddress", () => Modify(id, d => d with { LastAddress = ip, LastPort = port }));
}

/// <summary>
/// HistoryStore 与 TaskStore 共用的内存“数据库”（M2）：text_message、transfer_task + transfer_file、pending_cancel，
/// 以及 next_history_seq。文字记录和文件记录共用 Seq 计数器。
/// </summary>
public sealed class InMemoryDatabase(Func<long>? wallMs = null)
{
    private readonly Func<long> _wallMs = wallMs ?? (() => 0L);

    internal long NextSeq { get; set; } = 1;

    internal OrderedDictionary<(DeviceId Peer, MessageId Id), TextRecord> Texts { get; } = new();

    /// <summary>任务行；Files 按 Index 排序。</summary>
    internal OrderedDictionary<TransferId, TransferTask> Tasks { get; } = new();

    internal OrderedDictionary<(DeviceId Peer, TransferId Id), PendingCancel> PendingCancelRows { get; } = new();

    internal Lock Gate { get; } = new();

    /// <summary>返回 true 时该次写入失败（参数为操作名，如 "appendText"、"save"）。</summary>
    public Func<string, bool> FailWrites { get; set; } = _ => false;

    public bool FailReads { get; set; }

    internal long Now() => _wallMs();

    /// <summary>快照：当前的全部 pending_cancel 行。</summary>
    public IReadOnlyList<PendingCancel> PendingCancels()
    {
        lock (Gate)
        {
            return [.. PendingCancelRows.Values];
        }
    }

    /// <summary>把 <paramref name="files"/> 合并进任务行：已存在的行保留 Seq，新行按 Index 顺序分配 Seq。</summary>
    internal ValueList<TransferFile> MergeFiles(IEnumerable<TransferFile> existing, IEnumerable<TransferFile> files)
    {
        var byIndex = existing.ToDictionary(f => f.Index);
        foreach (var f in files.OrderBy(f => f.Index))
        {
            var seq = byIndex.TryGetValue(f.Index, out var old) ? old.Seq : NextSeq++;
            byIndex[f.Index] = f with { Seq = seq };
        }

        return new ValueList<TransferFile>(byIndex.Values.OrderBy(f => f.Index));
    }
}

public sealed class InMemoryHistoryStore(InMemoryDatabase db) : IHistoryStore
{
    private StoreResult Write(string op, Func<StoreResult> block)
    {
        if (db.FailWrites(op))
        {
            return Injected.Failure;
        }

        lock (db.Gate)
        {
            return block();
        }
    }

    public TextRecord? FindText(DeviceId peer, MessageId id)
    {
        lock (db.Gate)
        {
            return db.Texts.GetValueOrDefault((peer, id));
        }
    }

    public StoreResult AppendText(TextRecord r) => Write("appendText", () =>
    {
        var key = (r.Peer, r.MessageId);
        if (db.Texts.ContainsKey(key))
        {
            return new StoreResult.Duplicate();
        }

        db.Texts[key] = r with { Seq = db.NextSeq++ };
        return new StoreResult.Ok();
    });

    public StoreResult UpdateTextStatus(MessageId id, DeviceId peer, TextStatus s) => Write("updateTextStatus", () =>
    {
        if (db.Texts.TryGetValue((peer, id), out var t))
        {
            db.Texts[(peer, id)] = t with { Status = s };
        }

        return new StoreResult.Ok();
    });

    public StoreResult UpsertFile(FileRecord r) => Write("upsertFile", () =>
    {
        var id = r.File.TransferId;
        var task = db.Tasks.GetValueOrDefault(id)
            ?? new TransferTask(id, r.Peer, r.PeerName, r.Direction, r.TaskState, r.Interrupt, null, r.RecordedAt, db.Now(), ValueList<TransferFile>.Empty);
        db.Tasks[id] = task with { Files = db.MergeFiles(task.Files, [r.File]) };
        return new StoreResult.Ok();
    });

    public ListResult<HistoryRecord> List(int limit = 10_000)
    {
        if (db.FailReads)
        {
            return new ListResult<HistoryRecord>.Failed("injected failure");
        }

        lock (db.Gate)
        {
            var all = new List<HistoryRecord>();
            all.AddRange(db.Texts.Values.Select(t => new HistoryRecord.Text(t)));
            foreach (var t in db.Tasks.Values)
            {
                all.AddRange(t.Files.Select(f => new HistoryRecord.File(new FileRecord(f, t.Peer, t.PeerName, t.Direction, t.RecordedAt, t.State, t.Interrupt))));
            }

            return new ListResult<HistoryRecord>.Ok(new ValueList<HistoryRecord>(all.OrderBy(r => r.RecordedAt.UtcMs).ThenBy(r => r.Seq).Take(limit)));
        }
    }

    public StoreResult Delete(RecordId id) => Write("delete", () =>
    {
        foreach (var key in db.Texts.Where(kv => kv.Value.Seq == id.Seq).Select(kv => kv.Key).ToList())
        {
            db.Texts.Remove(key);
        }

        return new StoreResult.Ok();
    });

    public StoreResult DeleteFileRecord(TransferId id, int index) => Write("deleteFileRecord", () =>
    {
        if (db.Tasks.TryGetValue(id, out var t))
        {
            var rest = t.Files.Where(f => f.Index != index).ToList();
            if (rest.Count == 0)
            {
                db.Tasks.Remove(id);
            }
            else
            {
                db.Tasks[id] = t with { Files = new ValueList<TransferFile>(rest) };
            }
        }

        return new StoreResult.Ok();
    });

    public StoreResult ClearAll() => Write("clearAll", () =>
    {
        db.Texts.Clear();
        db.Tasks.Clear();
        return new StoreResult.Ok();
    });
}

public sealed class InMemoryTaskStore(InMemoryDatabase db) : ITaskStore
{
    public StoreResult Save(TransferTask t)
    {
        if (db.FailWrites("save"))
        {
            return Injected.Failure;
        }

        lock (db.Gate)
        {
            var existing = db.Tasks.TryGetValue(t.TransferId, out var old) ? old.Files : ValueList<TransferFile>.Empty;
            db.Tasks[t.TransferId] = t with { Files = db.MergeFiles(existing, t.Files) };
            return new StoreResult.Ok();
        }
    }

    public TransferTask? Get(TransferId id)
    {
        lock (db.Gate)
        {
            return db.Tasks.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<TransferTask> Unfinished()
    {
        lock (db.Gate)
        {
            return [.. db.Tasks.Values.Where(t => !t.State.IsTerminal())];
        }
    }

    public StoreResult AddPendingCancel(DeviceId peer, TransferId id)
    {
        if (db.FailWrites("addPendingCancel"))
        {
            return Injected.Failure;
        }

        lock (db.Gate)
        {
            db.PendingCancelRows.TryAdd((peer, id), new PendingCancel(peer, id, db.Now()));
            return new StoreResult.Ok();
        }
    }

    public IReadOnlyList<TransferId> TakePendingCancels(DeviceId peer)
    {
        lock (db.Gate)
        {
            var mine = db.PendingCancelRows.Values.Where(p => p.Peer == peer).ToList();
            foreach (var p in mine)
            {
                db.PendingCancelRows.Remove((p.Peer, p.TransferId));
            }

            return [.. mine.Select(p => p.TransferId)];
        }
    }
}

public sealed class InMemorySettingsStore(Settings? initial = null) : ISettingsStore
{
    private readonly ObservableState<Settings> _state = new(initial ?? new Settings());

    public bool FailWrites { get; set; }

    public IObservableState<Settings> Changes => _state;

    public Settings Current() => _state.Value;

    public StoreResult Update(Func<Settings, Settings> transform)
    {
        if (FailWrites)
        {
            return Injected.Failure;
        }

        _state.Update(transform);
        return new StoreResult.Ok();
    }
}
