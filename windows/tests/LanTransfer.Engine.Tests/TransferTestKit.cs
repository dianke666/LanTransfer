using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

// 文件传输测试的公共装置（与 Kotlin 侧 TransferTestKit.kt 对应）。C# 的服务在线程池上运行：测试等待可观察的结果；
// 需要计时器时用 TextKit.StepToAsync 小步推进 FakeClock。
//
// 与 Kotlin 的差异：Kotlin 的测试在单线程虚拟时间里运行，用 8 MB 的文件就能在传输进行中观察；C# 的传输在真实时间里进行，
// 大文件也可能在测试动作之前传完。因此“传输进行中”的用例用 ReadGate 让发送方读盘停在 XferKit.PauseAt 处，测试动作完成后再放行。

internal static class XferKit
{
    /// <summary>“进行中”用例的文件大小。</summary>
    public const int Big = 2_000_000;

    /// <summary>ReadGate 让读盘停住的位置：接收方最多写入到这里（按 64 KiB 的块为 1 048 576 字节）。</summary>
    public const long PauseAt = 1_000_000;

    public static TransferId SentId(SendOfferResult r) => Assert.IsType<SendOfferResult.Sent>(r).Id;

    public static TransferId Tid(int b) => new(Bytes16.Of(Enumerable.Repeat((byte)b, 16).ToArray()));

    public static bool TerminalOrInterrupted(TaskState? s) => s is { } v && (v.IsTerminal() || v == TaskState.Interrupted);

    /// <summary>调试用：任务状态、中断原因与每个文件的“状态/已传输/大小”。</summary>
    public static string Describe(XferDevice d, TransferId id) =>
        d.View(id)?.Task is { } t ? $"{t.State} {t.Interrupt} [{string.Join(", ", t.Files.Select(f => $"{f.State}/{f.Persisted}/{f.Size}"))}]" : "-";

    /// <summary>运行 Pbt.Iterations(base) 个用例；种子 = 基准种子 + 序号（基准种子取自 LT_PBT_SEED，未设置时随机）。</summary>
    public static async Task ForSeedsAsync(int baseIterations, Func<int, Task> body)
    {
        var start = int.TryParse(Environment.GetEnvironmentVariable(Pbt.SeedEnv), out var s) ? s : Random.Shared.Next(0, int.MaxValue / 2);
        var n = Pbt.Iterations(baseIterations);
        for (var i = 0; i < n; i++)
        {
            var seed = start + i;
            try
            {
                await body(seed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"seed={seed}（可设 {Pbt.SeedEnv}={seed} 重放）: {e.Message}\n{e}");
            }
        }
    }

    /// <summary>A 到 B 发送一个停在 PauseAt 处的大文件，等接收方写入数据后断开全部连接，双方进入“已中断”。续传前须放行 gate。</summary>
    public static async Task<TransferId> InterruptedTransferAsync(this XferWorld w, ReadGate gate)
    {
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("big", Big, gate: gate)]));
        await w.B.AwaitDataAsync(id);
        w.Cut();
        await w.A.AwaitStateAsync(id, TaskState.Interrupted);
        await w.B.AwaitStateAsync(id, TaskState.Interrupted);
        return id;
    }
}

/// <summary>让源文件的读盘停在某个位置，直到 Release / Dispose（之后同一文件的读取不再停）。</summary>
internal sealed class ReadGate : IDisposable
{
    private readonly ManualResetEventSlim _open = new(false);

    public void Hold(InMemorySourceFiles.Entry e, long at = XferKit.PauseAt) =>
        e.BeforeRead = pos =>
        {
            if (pos >= at)
            {
                _open.Wait(TimeSpan.FromSeconds(60));
            }
        };

    public void Release() => _open.Set();

    public void Dispose() => _open.Set();
}

/// <summary>一台带 FileTransferEngine 的测试设备；dispatch 为 false 时不处理收到的应用消息。</summary>
internal sealed class XferDevice : IDisposable
{
    private readonly IDisposable? _dispatch;

    public XferDevice(TestDevice d, FakeClock clock, int seed, FsProfile profile = FsProfile.Android, TransferTimings? timings = null, bool dispatch = true)
    {
        D = d;
        Clock = clock;
        Db = new InMemoryDatabase(clock.WallMs);
        Store = new InMemoryTaskStore(Db);
        History = new InMemoryHistoryStore(Db);
        Storage = new InMemoryReceiveStorage(profile, clock);
        Cache = new InMemoryOutgoingTextCache(Sources);
        Engine = new FileTransferEngine(d.Manager, Storage, Sources, Store, d.Trust, Notifier, Settings, Lifecycle, Cache, clock, new SeededRng(seed), profile, timings);
        if (dispatch)
        {
            _dispatch = TextKit.DispatchTo(d.Manager, (s, m) =>
            {
                if (m is not FileChunk)
                {
                    Trace?.Invoke($"{d.Name} <- {m}");
                }

                Engine.OnMessage(s, m);
            });
        }
    }

    public TestDevice D { get; }

    public FakeClock Clock { get; }

    public InMemoryDatabase Db { get; }

    public InMemoryTaskStore Store { get; }

    public InMemoryHistoryStore History { get; }

    public InMemoryReceiveStorage Storage { get; }

    public InMemorySourceFiles Sources { get; } = new();

    public InMemoryOutgoingTextCache Cache { get; }

    public RecordingNotifier Notifier { get; } = new();

    public InMemorySettingsStore Settings { get; } = new();

    public FakeAppLifecycle Lifecycle { get; } = new();

    public FileTransferEngine Engine { get; }

    /// <summary>调试：收到的每条非数据块消息。</summary>
    public Action<string>? Trace { get; set; }

    public TransferTaskView? View(TransferId id) => Engine.Tasks.Value.FirstOrDefault(v => v.Id == id);

    public TransferTask TaskOf(TransferId id) => View(id)!.Task;

    public TaskState? State(TransferId id) => View(id)?.Task.State;

    /// <summary>放入一个源文件，内容为种子化的随机字节；给出 gate 时读盘停在 XferKit.PauseAt 处。</summary>
    public SourceRef File(string name, int size, int? seed = null, ReadGate? gate = null)
    {
        var r = new SourceRef($"src:{D.Name}/{name}");
        var bytes = new byte[size];
        new Random(seed ?? StableHash(name)).NextBytes(bytes);
        var e = Sources.Put(r, name, Bytes.Of(bytes));
        gate?.Hold(e);
        return r;
    }

    public Bytes Content(SourceRef r) => Sources.EntryOf(r)!.Data;

    /// <summary>默认接收目录中已提交的文件。</summary>
    public Dictionary<string, Bytes> Received() => Storage.FilesIn(DirRef.Default.Instance).ToDictionary(kv => kv.Key, kv => kv.Value);

    public Task<ValueList<TransferTaskView>> AwaitAsync(Func<ValueList<TransferTaskView>, bool> pred, int timeoutMs = 20_000) =>
        Kit.Within(Engine.Tasks.FirstAsync(pred, Kit.Ct), timeoutMs);

    public Task<ValueList<TransferTaskView>> AwaitStateAsync(TransferId id, TaskState s) => AwaitAsync(l => l.Any(v => v.Id == id && v.Task.State == s));

    /// <summary>等到接收方写入了该任务第 index 个文件的数据。</summary>
    public Task<ValueList<TransferTaskView>> AwaitDataAsync(TransferId id, int pos = 0) =>
        AwaitAsync(l => l.Any(v => v.Id == id && v.Task.Files[pos].Persisted > 0));

    /// <summary>停止分发与引擎（属性测试一个用例跑完后释放，避免上百个用例的数据一直被测试的取消令牌引用）。</summary>
    public void Dispose()
    {
        _dispatch?.Dispose();
        Engine.Dispose();
    }

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s)
        {
            h = unchecked((h * 31) + c);
        }

        return h;
    }
}

/// <summary>A（发送方）与 B（接收方，监听）互相信任。Dispose 时断开全部连接（结束各 Session 的分发循环）并停止两台设备的引擎。</summary>
internal sealed class XferWorld : IDisposable
{
    public XferWorld(TransferTimings? timings = null, bool bDispatch = true)
    {
        Net = new InMemoryNetwork(Clock.Time);
        A = new XferDevice(new TestDevice("A", 1, Kit.Iface("wlan0", 192, 168, 1, 2), Net, Clock), Clock, 1, timings: timings);
        var b = new TestDevice("B", 2, Kit.Iface("wlan0", 192, 168, 1, 3), Net, Clock);
        b.Listen();
        B = new XferDevice(b, Clock, 2, timings: timings, dispatch: bDispatch);
        A.D.Trusts(B.D);
        B.D.Trusts(A.D);
    }

    public FakeClock Clock { get; } = new();

    public InMemoryNetwork Net { get; }

    public XferDevice A { get; }

    public XferDevice B { get; }

    /// <summary>B 对 A 开启“自动接收文件”。</summary>
    public void AutoAccept() => B.D.Trust.SetAutoAccept(A.D.Id, true);

    /// <summary>断开当前全部连接（RST）。</summary>
    public void Cut()
    {
        foreach (var c in Net.Connections)
        {
            c.Reset();
        }
    }

    /// <summary>断开某台主机的全部连接。</summary>
    public void Cut(string host)
    {
        foreach (var c in Net.Connections.Where(c => c.ClientHost == host || c.ServerHost == host))
        {
            c.Reset();
        }
    }

    public void Dispose()
    {
        Cut();
        A.Dispose();
        B.Dispose();
    }
}
