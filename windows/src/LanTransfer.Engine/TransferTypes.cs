using LanTransfer.Domain;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// FileTransferEngine 的对外类型（设计 C4.5），与 Kotlin 侧 TransferTypes.kt 同名镜像。

/// <summary>一个传输任务的界面快照。</summary>
/// <param name="Progress">任务级进度：已进入终态的文件按完整大小计入（18.1）。</param>
/// <param name="Files">每个文件的进度，与 Task.Files 一一对应；速度与剩余时间只对当前文件给出。</param>
/// <param name="CheckingPrefix">续传时正在从磁盘重新计算已传输部分的哈希（M3.2：Resuming 显示“正在校验已传输部分”）。</param>
public sealed record TransferTaskView(TransferTask Task, Progress Progress, ValueList<Progress> Files, bool CheckingPrefix)
{
    public TransferId Id => Task.TransferId;

    /// <summary>Offering、AwaitingUser、Running、Resuming、Interrupted（20.11）。</summary>
    public bool Unfinished => !Task.State.IsTerminal();
}

/// <summary><see cref="FileTransferEngine.SendAsync"/> 的结果。</summary>
public abstract record SendOfferResult
{
    private SendOfferResult()
    {
    }

    /// <summary>已发出 FILE_OFFER，任务处于 Offering。</summary>
    public sealed record Sent(TransferId Id) : SendOfferResult;

    /// <summary>待发送列表为空。</summary>
    public sealed record Empty : SendOfferResult;

    /// <summary>超过 1000 个文件（14.8）；列表应已由 PendingListRules 拦截，这里兜底。</summary>
    public sealed record TooMany : SendOfferResult;

    /// <summary>这些源文件无法读取元数据或是文件夹（14.9）；未发送 Offer，保留待发送列表。</summary>
    public sealed record Unreadable(ValueList<SourceRef> Refs) : SendOfferResult;

    /// <summary>10 s 内无法建立 Session：不发送 Offer，保留待发送列表（14.10）。</summary>
    public sealed record ConnectFailed(OpenFailure Failure) : SendOfferResult;
}

/// <summary><see cref="FileTransferEngine.ResumeAsync"/> 的结果。</summary>
public abstract record ResumeResult
{
    private ResumeResult()
    {
    }

    /// <summary>已建立 Session 并发出 RESUME_REQUEST / RESUME_STATE，任务处于 Resuming。</summary>
    public sealed record Started : ResumeResult;

    /// <summary>任务不存在或不是“已中断”。</summary>
    public sealed record NotResumable : ResumeResult;

    /// <summary>10 s 内无法建立 Session：任务保持“已中断”，保留临时文件（17.10）。</summary>
    public sealed record ConnectFailed(OpenFailure Failure) : ResumeResult;
}

/// <summary>需要界面提示的事件。</summary>
public abstract record TransferNotice
{
    private TransferNotice()
    {
    }

    /// <summary>任务状态写入失败（20.8）；传输照常进行。</summary>
    public sealed record StoreFailed(string Detail) : TransferNotice;

    /// <summary>pending_cancel 写入失败：本机仍显示“已取消”，提示取消通知可能无法送达对方（E5、18.9）。</summary>
    public sealed record PendingCancelNotSaved(TransferId Id) : TransferNotice;
}

/// <summary>传输相关的时限与大小（需求与设计规定的数值；只为测试注入而做成参数）。</summary>
public sealed record TransferTimings
{
    public static TransferTimings Default { get; } = new();

    /// <summary>接收方等待用户响应的时限（15.4）。</summary>
    public long ReceiverOfferMs { get; init; } = 60_000;

    /// <summary>发送方等待 FILE_OFFER_REPLY 的保底时限（15.4）。</summary>
    public long SenderOfferMs { get; init; } = 65_000;

    /// <summary>点击“继续”后进入 Running 的时限（17.10）。</summary>
    public long ResumeMs { get; init; } = 10_000;

    /// <summary>FILE_CHUNK 的数据长度（C4.5）。</summary>
    public int ChunkBytes { get; init; } = 64 * 1024;

    /// <summary>每个任务未确认数据的上限（C1.13）。</summary>
    public long WindowBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>接收方每写入这么多字节回复一次 FILE_ACK（C1.13）。</summary>
    public long AckBytes { get; init; } = 256L * 1024;

    /// <summary>接收方距上次 FILE_ACK 这么久仍有未确认数据时回复一次（C1.13）。</summary>
    public long AckIntervalMs { get; init; } = 200;

    /// <summary>进度快照的最短发布间隔（并发模型：250 ms）。</summary>
    public long PublishIntervalMs { get; init; } = 250;

    /// <summary>Persisted / LastWriteAt 的最短写库间隔（M2）。</summary>
    public long SaveIntervalMs { get; init; } = 1_000;

    /// <summary>后台进度通知的最短更新间隔（21.1）。</summary>
    public long ProgressNotifyMs { get; init; } = 1_000;

    /// <summary>TempFileJanitor 的运行周期（17.7）。</summary>
    public long JanitorIntervalMs { get; init; } = 60 * 60 * 1000L;
}
