using LanTransfer.Protocol;

namespace LanTransfer.Domain;

// M1 领域实体（设计 Data Models → M1），与 Kotlin 侧 Entities.kt 对应。字节字段用按内容比较的 Bytes，列表字段用 ValueList。

/// <summary>传输方向。</summary>
public enum Direction
{
    Send = 0,
    Receive = 1,
}

/// <summary>记录时刻，以及当时本机的 UTC 偏移（20.1）。排序只看 UtcMs，显示时用 UtcMs + OffsetMin 还原当时的本机时间。</summary>
public readonly record struct RecordTime(long UtcMs, int OffsetMin);

/// <summary>发送方的源文件引用：Windows 为完整路径。</summary>
public readonly record struct SourceRef(string Value);

/// <summary>接收方临时文件引用。</summary>
public readonly record struct TempRef(string Value);

/// <summary>已接收完成的文件引用，供“打开文件”“打开所在位置”使用（19.8）。</summary>
public readonly record struct StoredFileRef(string Value);

/// <summary>本机身份。私钥只在 IdentityStore 和 NoiseXX 内部使用（4.7）。</summary>
public sealed class DeviceIdentity(DeviceId deviceId, X25519KeyPair keyPair, string displayName)
{
    public DeviceId DeviceId { get; } = deviceId;

    public X25519KeyPair KeyPair { get; } = keyPair;

    public string DisplayName { get; } = displayName;
}

/// <summary>已信任设备；LastAddress / LastPort 为最近一次成功连接的地址（C4.1）。</summary>
public sealed record TrustedDevice(
    DeviceId DeviceId,
    string DisplayName,
    DeviceType DeviceType,
    Bytes PublicKey,
    bool AutoAccept,
    long PairedAtUtcMs,
    Ipv4? LastAddress = null,
    int? LastPort = null);

/// <summary>文字记录；PeerName 是写入记录时对方显示名称的快照（20.1）。</summary>
public sealed record TextRecord(
    long Seq,
    DeviceId Peer,
    string PeerName,
    MessageId MessageId,
    Direction Direction,
    Bytes TextUtf8,
    long SentAtUtcMs,
    RecordTime RecordedAt,
    TextStatus Status);

/// <summary>中断原因；Detail 为写入失败的具体原因（17.6）。</summary>
public sealed record InterruptInfo(InterruptReason Reason, string? Detail = null);

/// <summary>
/// 任务中的一个文件。OriginalName / Size / Mime / Mtime 取自 File_Offer（RESTART 时更新 Size、Mtime，17.5）；
/// Persisted 在接收方为已落盘字节数、在发送方为已确认字节数；SourceRef 仅发送方；TempRef、StartedAt、LastWriteAtUtcMs 仅接收方；
/// FinalName、FullPath、StoredRef 仅接收方且状态为 Completed（19.8）。StartedAt 为本地时间（Kind = Unspecified）。
/// </summary>
public sealed record TransferFile(
    TransferId TransferId,
    int Index,
    long Seq,
    string OriginalName,
    long Size,
    string Mime,
    long? Mtime,
    FileState State,
    long Persisted = 0,
    Bytes? Sha256 = null,
    SourceRef? SourceRef = null,
    TempRef? TempRef = null,
    DateTime? StartedAt = null,
    long? LastWriteAtUtcMs = null,
    string? FinalName = null,
    string? FullPath = null,
    StoredFileRef? StoredRef = null);

/// <summary>传输任务（C5.3 中的 TaskRecord，包含 Files）。Interrupt 只在 State = Interrupted 时非空；ReceiveDir 仅接收方使用。</summary>
public sealed record TransferTask(
    TransferId TransferId,
    DeviceId Peer,
    string PeerName,
    Direction Direction,
    TaskState State,
    InterruptInfo? Interrupt,
    DirRef? ReceiveDir,
    RecordTime RecordedAt,
    long UpdatedAtUtcMs,
    ValueList<TransferFile> Files);

/// <summary>对“已中断”任务点击取消后待送达的取消通知（18.9）。</summary>
public sealed record PendingCancel(DeviceId Peer, TransferId TransferId, long CreatedAtUtcMs);

/// <summary>
/// 接收目录：序列化为 <c>default</c>、<c>tree:&lt;uri&gt;</c> 或 <c>path:&lt;绝对路径&gt;</c>。
/// 与 Kotlin 侧的差异：C# 嵌套类型的成员不能与类型同名，所以 Kotlin 的 <c>Path.path</c> 在这里是 <see cref="Path.Value"/>。
/// </summary>
public abstract record DirRef
{
    private const string DefaultText = "default";
    private const string TreePrefix = "tree:";
    private const string PathPrefix = "path:";

    /// <summary>使用时才解析为实际位置（16.1、16.2）。</summary>
    public sealed record Default : DirRef
    {
        public static Default Instance { get; } = new();
    }

    /// <summary>Android：SAF 目录树 URI。</summary>
    public sealed record Tree(string Uri) : DirRef;

    /// <summary>Windows：绝对路径。</summary>
    public sealed record Path(string Value) : DirRef;

    public string Serialize() => this switch
    {
        Default => DefaultText,
        Tree t => TreePrefix + t.Uri,
        Path p => PathPrefix + p.Value,
        _ => throw new InvalidOperationException("unknown DirRef"),
    };

    /// <summary>不认识的格式或前缀后为空时返回 null（调用方改用默认值并记录日志，M5）。</summary>
    public static DirRef? Parse(string s) =>
        s == DefaultText ? Default.Instance
        : s.StartsWith(TreePrefix, StringComparison.Ordinal) && s.Length > TreePrefix.Length ? new Tree(s[TreePrefix.Length..])
        : s.StartsWith(PathPrefix, StringComparison.Ordinal) && s.Length > PathPrefix.Length ? new Path(s[PathPrefix.Length..])
        : null;
}

/// <summary>设置项（M5）。BackgroundReceive 只在 Android 上使用，Windows 保留该字段以保持两端模型一致。</summary>
public sealed record Settings(DirRef ReceiveDirectory, bool AutoCopy = false, bool BackgroundReceive = false)
{
    public Settings()
        : this(DirRef.Default.Instance)
    {
    }
}
