using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

// 系统服务端口的测试替身（与 Kotlin 侧 FakeSystem.kt 对应）：记录全部调用，可注入失败。

/// <summary>由种子决定的随机数（测试用）。</summary>
public sealed class SeededRng(long seed) : IRng
{
    private readonly Random _random = new(unchecked((int)(seed ^ (seed >>> 32))));

    public byte[] NextBytes(int n)
    {
        var b = new byte[n];
        lock (_random)
        {
            _random.NextBytes(b);
        }

        return b;
    }
}

/// <summary>剪贴板替身：<see cref="Writes"/> 记录每次写入的原文；<see cref="FailReads"/> / <see cref="FailWrites"/> 模拟剪贴板被占用（13.9）。</summary>
public sealed class RecordingClipboard(string? initial = null) : IClipboard
{
    private readonly List<string> _writes = [];

    public string? Content { get; private set; } = initial;

    public IReadOnlyList<string> Writes
    {
        get
        {
            lock (_writes)
            {
                return [.. _writes];
            }
        }
    }

    public bool FailReads { get; set; }

    public bool FailWrites { get; set; }

    public Task<ClipRead> ReadTextAsync(CancellationToken ct = default) =>
        Task.FromResult<ClipRead>(FailReads ? new ClipRead.Failed() : Content is { } s ? new ClipRead.Text(s) : new ClipRead.NoText());

    public Task<ClipWrite> WriteTextAsync(string s, CancellationToken ct = default)
    {
        lock (_writes)
        {
            _writes.Add(s);
        }

        if (FailWrites)
        {
            return Task.FromResult<ClipWrite>(new ClipWrite.Failed());
        }

        Content = s;
        return Task.FromResult<ClipWrite>(new ClipWrite.Ok());
    }

    /// <summary>模拟其他应用改动了剪贴板。</summary>
    public void SetExternally(string? s) => Content = s;
}

public abstract record LocateCall
{
    private LocateCall()
    {
    }

    public sealed record OpenFile(StoredFileRef File, string Mime) : LocateCall;

    public sealed record Reveal(StoredFileRef File) : LocateCall;

    public sealed record OpenDirectory(DirRef Dir) : LocateCall;
}

/// <summary>IFileLocator 替身：<see cref="Calls"/> 记录调用，<see cref="Result"/> 决定返回值。</summary>
public sealed class RecordingFileLocator(Func<LocateCall, LocateResult>? result = null) : IFileLocator
{
    private readonly List<LocateCall> _calls = [];

    public Func<LocateCall, LocateResult> Result { get; set; } = result ?? (_ => LocateResult.Ok);

    public IReadOnlyList<LocateCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    private LocateResult Record(LocateCall c)
    {
        lock (_calls)
        {
            _calls.Add(c);
        }

        return Result(c);
    }

    public LocateResult OpenFile(StoredFileRef f, string mime) => Record(new LocateCall.OpenFile(f, mime));

    public LocateResult RevealInFolder(StoredFileRef f) => Record(new LocateCall.Reveal(f));

    public LocateResult OpenDirectory(DirRef dir) => Record(new LocateCall.OpenDirectory(dir));
}

public abstract record NotifyEvent
{
    private NotifyEvent()
    {
    }

    public sealed record Text(NotifyKey Key, string PeerName, string Preview) : NotifyEvent;

    public sealed record FileCompleted(NotifyKey Key, string PeerName, string FileName, StoredFileRef File) : NotifyEvent;

    public sealed record Request(NotifyKey Key, string PeerName, RequestKind Kind, int? FileCount) : NotifyEvent;

    public sealed record Problem(NotifyKey Key, string PeerName, string? FileName, string Status) : NotifyEvent;

    public sealed record Progress(TransferId Task, string PeerName, string FileName, int Percent) : NotifyEvent;

    public sealed record Cancel(NotifyKey Key) : NotifyEvent;
}

/// <summary>INotifier 替身：<see cref="Events"/> 按顺序记录全部调用，<see cref="Active"/> 为当前仍显示的通知（Cancel 后移除）。</summary>
public sealed class RecordingNotifier(bool enabled = true) : INotifier
{
    private readonly Lock _gate = new();
    private readonly ObservableState<bool> _enabled = new(enabled);
    private readonly List<NotifyEvent> _events = [];
    private readonly OrderedDictionary<NotifyKey, NotifyEvent> _active = new();

    public IObservableState<bool> Enabled => _enabled;

    public IReadOnlyList<NotifyEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>当前仍显示的通知，按首次显示的顺序。</summary>
    public IReadOnlyList<KeyValuePair<NotifyKey, NotifyEvent>> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _active];
            }
        }
    }

    public void SetEnabled(bool on) => _enabled.Value = on;

    private void Show(NotifyKey key, NotifyEvent e)
    {
        lock (_gate)
        {
            _events.Add(e);
            _active[key] = e;
        }
    }

    public void Text(NotifyKey key, string peerName, string preview) => Show(key, new NotifyEvent.Text(key, peerName, preview));

    public void FileCompleted(NotifyKey key, string peerName, string fileName, StoredFileRef file) =>
        Show(key, new NotifyEvent.FileCompleted(key, peerName, fileName, file));

    public void Request(NotifyKey key, string peerName, RequestKind kind, int? fileCount) =>
        Show(key, new NotifyEvent.Request(key, peerName, kind, fileCount));

    public void Problem(NotifyKey key, string peerName, string? fileName, string status) =>
        Show(key, new NotifyEvent.Problem(key, peerName, fileName, status));

    public void Progress(TransferId task, string peerName, string fileName, int percent) =>
        Show(new NotifyKey.Transfer(task), new NotifyEvent.Progress(task, peerName, fileName, percent));

    public void Cancel(NotifyKey key)
    {
        lock (_gate)
        {
            _events.Add(new NotifyEvent.Cancel(key));
            _active.Remove(key);
        }
    }
}

/// <summary>前后台状态替身。</summary>
public sealed class FakeAppLifecycle(bool background = false) : IAppLifecycle
{
    private readonly ObservableState<bool> _state = new(background);

    public IObservableState<bool> Background => _state;

    public void SetBackground(bool on) => _state.Value = on;
}
