using LanTransfer.Domain;
using LanTransfer.Protocol;

namespace LanTransfer.Engine.Ports;

// C5.3 持久化端口，与 Kotlin 侧 Stores.kt 同名镜像。所有写方法返回 StoreResult，失败只触发提示（20.8），不抛出异常。
// Kotlin 的 TaskRecord 在 C# 侧就是 TransferTask。

public abstract record StoreResult
{
    private StoreResult()
    {
    }

    public sealed record Ok : StoreResult;

    /// <summary>AppendText 命中 UNIQUE (peer_id, message_id)：重复消息（11.8）。</summary>
    public sealed record Duplicate : StoreResult;

    public sealed record Failed(string Detail) : StoreResult;
}

public abstract record ListResult<T>
{
    private ListResult()
    {
    }

    public sealed record Ok(ValueList<T> Items) : ListResult<T>;

    public sealed record Failed(string Detail) : ListResult<T>;
}

/// <summary>启动时载入身份的结果（4.1、4.2、4.6）。</summary>
public abstract record IdentityLoad
{
    private IdentityLoad()
    {
    }

    /// <summary>得到了可用（已保存）的身份。</summary>
    public abstract record Ready(DeviceIdentity Identity) : IdentityLoad;

    public sealed record Loaded(DeviceIdentity Identity) : Ready(Identity);

    /// <summary>首次启动或应用数据被清除。</summary>
    public sealed record Created(DeviceIdentity Identity) : Ready(Identity);

    /// <summary>已保存的身份无法读取或已损坏，已生成新身份；界面提示需要重新配对（4.6）。</summary>
    public sealed record ResetCorrupted(DeviceIdentity Identity) : Ready(Identity);

    /// <summary>
    /// 需要新身份但无法保存（存储已满、DPAPI 不可用）：不启动网络，主界面显示错误和“重试”（Q14）。
    /// <paramref name="Corrupted"/> 为 true 时原身份已损坏（重试成功后结果为 ResetCorrupted）。
    /// </summary>
    public sealed record SaveFailed(string Detail, bool Corrupted) : IdentityLoad;
}

/// <summary>一条文字记录的标识：全局历史序号（M2 text_message.seq）。</summary>
public readonly record struct RecordId(long Seq);

/// <summary>传输记录界面用的投影：TransferFile 加上所属任务的对端、对端名称快照、方向、记录时刻与任务状态（Data Models M1）。</summary>
public sealed record FileRecord(
    TransferFile File,
    DeviceId Peer,
    string PeerName,
    Direction Direction,
    RecordTime RecordedAt,
    TaskState TaskState,
    InterruptInfo? Interrupt = null);

/// <summary>传输记录中的一条：文字或文件。排序键为（RecordedAt.UtcMs, Seq）（20.2）。</summary>
public abstract record HistoryRecord
{
    private HistoryRecord()
    {
    }

    public abstract RecordTime RecordedAt { get; }

    public abstract long Seq { get; }

    public sealed record Text(TextRecord Record) : HistoryRecord
    {
        public override RecordTime RecordedAt => Record.RecordedAt;

        public override long Seq => Record.Seq;
    }

    public sealed record File(FileRecord Record) : HistoryRecord
    {
        public override RecordTime RecordedAt => Record.RecordedAt;

        public override long Seq => Record.File.Seq;
    }
}

public interface IIdentityStore
{
    IdentityLoad LoadOrCreate();

    /// <summary>设备显示名称存放在设置中，身份重置后保留（M4、M5）。</summary>
    string DisplayName();

    /// <summary>只接收 DisplayNameRules.ValidateUserInput 返回的清理后名称（4.4、4.9）。</summary>
    StoreResult SetDisplayName(string name);
}

public interface ITrustStore
{
    IObservable<Unit> Changes { get; }

    IReadOnlyList<TrustedDevice> All();

    TrustedDevice? Get(DeviceId id);

    /// <summary>同一设备 ID 整条替换（9.9）。</summary>
    StoreResult Upsert(TrustedDevice d);

    /// <summary>同时删除“自动接收文件”设置（9.3）。</summary>
    StoreResult Delete(DeviceId id);

    StoreResult SetAutoAccept(DeviceId id, bool on);

    /// <summary>9.6</summary>
    StoreResult UpdateName(DeviceId id, string name);

    /// <summary>最近一次成功连接的地址（C4.1）。</summary>
    StoreResult RecordAddress(DeviceId id, Ipv4 ip, int port);
}

public interface IHistoryStore
{
    TextRecord? FindText(DeviceId peer, MessageId id);

    /// <summary>由存储分配 Seq（忽略 r.Seq）；（peer, messageId）已存在时返回 Duplicate（11.8）。</summary>
    StoreResult AppendText(TextRecord r);

    /// <summary>每条消息只保留 1 条记录（20.7）；记录不存在时不做任何事（迟到的应答只影响 0 行）。</summary>
    StoreResult UpdateTextStatus(MessageId id, DeviceId peer, TextStatus s);

    /// <summary>插入或更新一个文件行；已存在的行保留原来的 Seq。所属任务行不存在时一并创建。</summary>
    StoreResult UpsertFile(FileRecord r);

    /// <summary>按记录时间、再按插入顺序排序，最多 <paramref name="limit"/> 条（20.2）。</summary>
    ListResult<HistoryRecord> List(int limit = 10_000);

    /// <summary>删除一条文字记录（20.4）。</summary>
    StoreResult Delete(RecordId id);

    /// <summary>删除一条文件记录；任务没有剩余文件行时一并删除任务行（20.4、20.10）。</summary>
    StoreResult DeleteFileRecord(TransferId id, int index);

    /// <summary>删除全部文字、任务和文件行（20.5），不动 trusted_device、pending_cancel 和 next_history_seq。</summary>
    StoreResult ClearAll();
}

public interface ITaskStore
{
    /// <summary>插入或更新任务行和它的全部文件行；新文件行按 Index 顺序分配 Seq，已存在的行保留原 Seq。</summary>
    StoreResult Save(TransferTask t);

    TransferTask? Get(TransferId id);

    /// <summary>未结束（码值 &lt; 10）的任务（17.9）。</summary>
    IReadOnlyList<TransferTask> Unfinished();

    StoreResult AddPendingCancel(DeviceId peer, TransferId id);

    /// <summary>取出并删除该对端的全部待送达取消通知，按写入顺序（18.9）。</summary>
    IReadOnlyList<TransferId> TakePendingCancels(DeviceId peer);
}

public interface ISettingsStore
{
    /// <summary>当前值与之后的每次变化。</summary>
    IObservableState<Settings> Changes { get; }

    Settings Current();

    StoreResult Update(Func<Settings, Settings> transform);
}
