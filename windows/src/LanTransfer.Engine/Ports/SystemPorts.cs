using LanTransfer.Domain;
using LanTransfer.Protocol;

namespace LanTransfer.Engine.Ports;

// C5.4 系统服务端口，与 Kotlin 侧 System.kt 同名镜像。随机数端口 IRng 定义在领域层（LanTransfer.Domain.IRng），
// Noise 临时密钥来源 IEphemeralKeySource 定义在协议层，引擎层直接复用。

public abstract record ClipRead
{
    private ClipRead()
    {
    }

    /// <summary>与 Kotlin 侧的差异：嵌套类型的成员不能与类型同名，所以 Kotlin 的 <c>Text.text</c> 在这里是 <see cref="Text.Body"/>。</summary>
    public sealed record Text(string Body) : ClipRead;

    public sealed record NoText : ClipRead;

    /// <summary>无法访问剪贴板（被占用超过 1 s 等，13.9）。</summary>
    public sealed record Failed : ClipRead;
}

public abstract record ClipWrite
{
    private ClipWrite()
    {
    }

    public sealed record Ok : ClipWrite;

    /// <summary>写入失败，或读回后逐码点比对不一致（13.3）。</summary>
    public sealed record Failed : ClipWrite;
}

public interface IClipboard
{
    Task<ClipRead> ReadTextAsync(CancellationToken ct = default);

    Task<ClipWrite> WriteTextAsync(string s, CancellationToken ct = default);
}

/// <summary>对应 19.6 / 19.5 / 19.3 / 19.11 的提示。</summary>
public enum LocateResult
{
    Ok,
    NotFound,
    NoHandler,
    NoFileManager,
    NoAccess,
}

public interface IFileLocator
{
    LocateResult OpenFile(StoredFileRef f, string mime);

    LocateResult RevealInFolder(StoredFileRef f);

    LocateResult OpenDirectory(DirRef dir);
}

/// <summary>通知的标识：平台适配层映射为固定的通知标识，用于更新和移除通知（21.1、21.8）。</summary>
public abstract record NotifyKey
{
    private NotifyKey()
    {
    }

    public sealed record Text(DeviceId Peer, MessageId MessageId) : NotifyKey;

    public sealed record PairingRequest(DeviceId Peer) : NotifyKey;

    /// <summary>任务级通知：文件接收请求、传输进度、任务问题。</summary>
    public sealed record Transfer(TransferId TransferId) : NotifyKey;

    /// <summary>单个文件完成或出错。</summary>
    public sealed record File(TransferId TransferId, int Index) : NotifyKey;
}

public enum RequestKind
{
    Pairing,
    FileOffer,
}

public interface INotifier
{
    /// <summary>未授权或被关闭时为 false，主界面提示（21.11）。</summary>
    IObservableState<bool> Enabled { get; }

    /// <summary>21.6</summary>
    void Text(NotifyKey key, string peerName, string preview);

    /// <summary>21.7</summary>
    void FileCompleted(NotifyKey key, string peerName, string fileName, StoredFileRef file);

    /// <summary>21.8；fileCount 只在 FileOffer 时给出。</summary>
    void Request(NotifyKey key, string peerName, RequestKind kind, int? fileCount);

    /// <summary>21.10</summary>
    void Problem(NotifyKey key, string peerName, string? fileName, string status);

    /// <summary>21.1，Android 专用（前台服务通知）；Windows 实现可忽略。</summary>
    void Progress(TransferId task, string peerName, string fileName, int percent);

    void Cancel(NotifyKey key);
}

public interface IAppLifecycle
{
    /// <summary>Background_State。</summary>
    IObservableState<bool> Background { get; }
}

/// <summary>
/// 时钟。<see cref="Time"/> 供引擎的 Task.Delay、CancellationTokenSource 超时与 PeriodicTimer 使用，
/// 与 <see cref="MonotonicMs"/> 保持一致（测试中注入 FakeClock 即可让全部计时走虚拟时间）。
/// </summary>
public interface IClock
{
    TimeProvider Time { get; }

    /// <summary>单调时钟毫秒（计时、超时只用它）。</summary>
    long MonotonicMs();

    /// <summary>UTC 毫秒（记录时刻、sent_at）。</summary>
    long WallMs();

    /// <summary>本机当前本地时间（回退文件名、显示），Kind = Unspecified。</summary>
    DateTime LocalNow();
}

/// <summary>生产环境时钟。</summary>
public sealed class SystemClock : IClock
{
    private SystemClock()
    {
    }

    public static SystemClock Instance { get; } = new();

    public TimeProvider Time => TimeProvider.System;

    public long MonotonicMs() => Environment.TickCount64;

    public long WallMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public DateTime LocalNow() => DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
}
