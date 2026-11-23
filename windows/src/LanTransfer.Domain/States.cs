namespace LanTransfer.Domain;

// M3 状态枚举（设计 Data Models → M3），与 Kotlin 侧 States.kt 对应。码值会写入数据库，只能追加新值，不能重新编号；终态码值都 ≥ 10。

/// <summary>文字消息状态（M3.1）。</summary>
public enum TextStatus
{
    Sending = 0,
    Delivered = 1,
    Failed = 2,
    Received = 3,
}

/// <summary>传输任务状态（M3.2）。</summary>
public enum TaskState
{
    Offering = 0,
    AwaitingUser = 1,
    Running = 2,
    Interrupted = 3,
    Resuming = 4,
    Completed = 10,
    Rejected = 11,
    NoResponse = 12,
    NoSpace = 13,
    CannotSave = 14,
    ConnectionLost = 15,
    Cancelled = 16,
    PeerCancelled = 17,
    OfferInvalid = 18,
}

/// <summary>任务中断原因（M3.2）。</summary>
public enum InterruptReason
{
    PeerUnresponsive = 1,
    PeerClosed = 2,
    LocalNetworkLost = 3,
    IntegrityFailed = 4,
    ProtocolError = 5,
    WriteFailed = 6,
    AppExit = 7,
    AppKilled = 8,
}

/// <summary>文件状态（M3.3）。WriteFailed 不是终态：临时文件保留，继续时按 PARTIAL 续传。</summary>
public enum FileState
{
    Waiting = 0,
    Transferring = 1,
    Verifying = 2,
    WriteFailed = 3,
    Completed = 10,
    ReadFailed = 11,
    ChecksumFailed = 12,
}

/// <summary>
/// 配对结局（M3.4 中 Done 的结果）。ConnectFailed 的具体原因（OpenFailure）由引擎层附带，不在领域枚举中。只在内存中使用。
/// </summary>
public enum PairingResult
{
    Success,
    Rejected,
    AutoRejected,
    Busy,
    Timeout,
    Cancelled,
    CodeMismatch,
    TokenInvalid,
    Disconnected,
    ConnectFailed,

    /// <summary>保存或提交信任失败（设计 E 表“配对时写入信任”），界面同样提示“配对失败，请重试”。M3.4 未列出，此处补充。</summary>
    StoreFailed,
}

/// <summary>状态枚举的辅助判断（对应 Kotlin 的 isTerminal / isActive 属性）。</summary>
public static class StateExtensions
{
    public static bool IsTerminal(this TaskState s) => (int)s >= 10;

    /// <summary>“正在进行”：Running 或 Resuming（临时文件清理据此排除，M4）。</summary>
    public static bool IsActive(this TaskState s) => s is TaskState.Running or TaskState.Resuming;

    public static bool IsTerminal(this FileState s) => (int)s >= 10;
}
