using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

// 文字服务测试的公共装置（与 Kotlin 侧 TextTestKit.kt 对应）。

internal static class TextKit
{
    /// <summary>
    /// 测试用分发器（正式的分发由任务 19 的 EngineHost 完成）：把每个新注册 Session 的应用消息交给 <paramref name="handle"/>。
    /// 须在任何 Session 建立之前调用。
    /// </summary>
    public static IDisposable DispatchTo(SessionManager manager, Action<Session, ProtocolMessage> handle) =>
        manager.Events.Subscribe(e =>
        {
            if (e is not SessionEvent.Opened o)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var m in o.Session.Incoming.ReadAllAsync(Kit.Ct))
                    {
                        handle(o.Session, m);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        });

    public static MessageId MessageIdOf(int n) => new(Bytes16.Of(Enumerable.Repeat((byte)n, 16).ToArray()));

    /// <summary>原始 TEXT：消息 ID 的 16 字节都是 n，正文为任意字节。</summary>
    public static Text RawText(int n, byte[] body, long sentAt = 1_700_000_000_000) => new(MessageIdOf(n), sentAt, Bytes.Of(body));

    public static byte[] Utf8(string s) => StrictUtf8.Encode(s);

    /// <summary>小步推进虚拟时间到 <paramref name="target"/>，每步留出真实时间让定时器的续体（心跳等）运行。</summary>
    public static async Task StepToAsync(FakeClock clock, long target, long step = 250)
    {
        while (clock.MonotonicMs() < target)
        {
            clock.AdvanceBy(Math.Min(step, target - clock.MonotonicMs()));
            await Task.Delay(2, Kit.Ct);
        }
    }

    /// <summary>读取 Session 的下一条应用消息（真实时间上限 10 s）。</summary>
    public static async Task<ProtocolMessage> NextAsync(Session s) => await Kit.Within(s.Incoming.ReadAsync(Kit.Ct).AsTask());
}

/// <summary>一台带 TextService 的测试设备。dispatch 为 false 时不处理收到的应用消息（模拟对方不回应），测试直接读 Session.Incoming。</summary>
internal sealed class TextDevice
{
    private readonly List<TextNotice> _notices = [];

    public TextDevice(TestDevice d, FakeClock clock, int seed, bool dispatch = true)
    {
        D = d;
        Db = new InMemoryDatabase(clock.WallMs);
        History = new InMemoryHistoryStore(Db);
        Cache = new InMemoryOutgoingTextCache(Sources);
        Service = new TextService(d.Manager, History, d.Trust, Clipboard, Notifier, Settings, Lifecycle, Cache, clock, new SeededRng(seed));
        Service.Notices.Subscribe(n =>
        {
            lock (_notices)
            {
                _notices.Add(n);
            }
        });
        if (dispatch)
        {
            TextKit.DispatchTo(d.Manager, (s, m) => Service.OnMessage(s, m));
        }
    }

    public TestDevice D { get; }

    public InMemoryDatabase Db { get; }

    public InMemoryHistoryStore History { get; }

    public RecordingClipboard Clipboard { get; } = new();

    public RecordingNotifier Notifier { get; } = new();

    public InMemorySettingsStore Settings { get; } = new();

    public FakeAppLifecycle Lifecycle { get; } = new();

    public InMemorySourceFiles Sources { get; } = new();

    public InMemoryOutgoingTextCache Cache { get; }

    public TextService Service { get; }

    public IReadOnlyList<TextNotice> Notices
    {
        get
        {
            lock (_notices)
            {
                return [.. _notices];
            }
        }
    }

    public IReadOnlyList<TextItem> Items(TestDevice peer) => Service.Conversation(peer.Id).Value;

    public TextStatus? StatusOf(TestDevice peer, MessageId id) => Items(peer).SingleOrDefault(i => i.Key.MessageId == id)?.Status;

    public Task WaitStatusAsync(TestDevice peer, MessageId id, TextStatus s) => Kit.WaitUntilAsync(() => StatusOf(peer, id) == s, $"status {s}");

    public List<TextRecord> TextRecords() =>
        [.. Assert.IsType<ListResult<HistoryRecord>.Ok>(History.List()).Items.OfType<HistoryRecord.Text>().Select(t => t.Record)];
}

/// <summary>A、B 两台互相信任的设备，B 在监听，A 知道 B 的地址。</summary>
internal sealed class TextWorld
{
    public TextWorld(bool aDispatch = true, bool bDispatch = true)
    {
        Net = new InMemoryNetwork(Clock.Time);
        A = new TextDevice(new TestDevice("A", 1, Kit.Iface("wlan0", 192, 168, 1, 2), Net, Clock), Clock, 1, aDispatch);
        var b = new TestDevice("B", 2, Kit.Iface("wlan0", 192, 168, 1, 3), Net, Clock);
        b.Listen();
        B = new TextDevice(b, Clock, 2, bDispatch);
        A.D.Trusts(B.D);
        B.D.Trusts(A.D);
    }

    public FakeClock Clock { get; } = new();

    public InMemoryNetwork Net { get; }

    public TextDevice A { get; }

    public TextDevice B { get; }

    /// <summary>A 到 B 的 Session（A 发起）。</summary>
    public async Task<Session> OpenABAsync() =>
        Assert.IsType<OpenResult.Ok>(await Kit.Within(A.D.Manager.OpenAsync(new Target.Device(B.D.Id)))).Session;

    /// <summary>B 一侧与 A 的 Session（等待注册完成）。</summary>
    public async Task<Session> SessionBAAsync()
    {
        await Kit.WaitUntilAsync(() => B.D.Manager.GetSession(A.D.Id) is not null, "B 的 Session");
        return B.D.Manager.GetSession(A.D.Id)!;
    }
}
