using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>一台由 EngineHost 组装的测试设备（全部端口都是内存实现），与 Kotlin 侧 HostDevice 对应。</summary>
internal sealed class HostDevice : IDisposable
{
    public HostDevice(
        string name,
        int seed,
        IEnumerable<LocalInterface> ifaces,
        InMemoryNetwork net,
        FakeClock clock,
        DeviceType type = DeviceType.Windows,
        FsProfile profile = FsProfile.Windows,
        InMemoryIdentityStore? identityStore = null,
        InMemoryTrustStore? trust = null)
    {
        Name = name;
        Type = type;
        Transport = net.Host(name, ifaces);
        IdentityStore = identityStore ?? new InMemoryIdentityStore(new SeededRng(seed), name);
        Trust = trust ?? new InMemoryTrustStore();
        Db = new InMemoryDatabase(clock.WallMs);
        History = new InMemoryHistoryStore(Db);
        Tasks = new InMemoryTaskStore(Db);
        Settings = new InMemorySettingsStore();
        Storage = new InMemoryReceiveStorage(profile, clock);
        Sources = new InMemorySourceFiles();
        Cache = new InMemoryOutgoingTextCache(Sources);
        Clipboard = new RecordingClipboard();
        Notifier = new RecordingNotifier();
        Lifecycle = new FakeAppLifecycle();
        Host = new EngineHost(
            new EnginePorts(
                Transport,
                Transport.Monitor,
                IdentityStore,
                Trust,
                History,
                Tasks,
                Settings,
                Storage,
                Sources,
                Cache,
                Clipboard,
                Notifier,
                Lifecycle,
                clock,
                new SeededRng(seed + 1_000)),
            type,
            profile);
    }

    public string Name { get; }

    public DeviceType Type { get; }

    public InMemoryTransport Transport { get; }

    public InMemoryIdentityStore IdentityStore { get; }

    public InMemoryTrustStore Trust { get; }

    public InMemoryDatabase Db { get; }

    public InMemoryHistoryStore History { get; }

    public InMemoryTaskStore Tasks { get; }

    public InMemorySettingsStore Settings { get; }

    public InMemoryReceiveStorage Storage { get; }

    public InMemorySourceFiles Sources { get; }

    public InMemoryOutgoingTextCache Cache { get; }

    public RecordingClipboard Clipboard { get; }

    public RecordingNotifier Notifier { get; }

    public FakeAppLifecycle Lifecycle { get; }

    public EngineHost Host { get; }

    public EngineServices Services => Host.Services.Value!;

    public DeviceIdentity Identity => Services.Identity;

    public DeviceId Id => Identity.DeviceId;

    public TrustedDevice Record() => new(Id, Name, Type, Identity.KeyPair.PublicKey, AutoAccept: false, PairedAtUtcMs: 1);

    public void TrustsEachOther(HostDevice other)
    {
        Trust.Upsert(other.Record());
        other.Trust.Upsert(Record());
    }

    public SourceRef File(string name, int size)
    {
        var r = new SourceRef($"src:{Name}/{name}");
        var data = new byte[size];
        new Random(StringComparer.Ordinal.GetHashCode(name)).NextBytes(data);
        Sources.Put(r, name, Bytes.Of(data));
        return r;
    }

    public IReadOnlyList<SentDatagram> Datagrams(InMemoryNetwork net) => [.. net.Datagrams.Where(d => d.From == Name)];

    public Task AwaitTaskAsync(TransferId id, TaskState state) =>
        Kit.Within(Services.Files.Tasks.FirstAsync(l => l.Any(t => t.Id == id && t.Task.State == state), Kit.Ct));

    public void Dispose() => Host.Dispose();
}

/// <summary>EngineHost 单元测试（任务 19.4，与 Kotlin 侧 EngineHostTest 对应）。</summary>
public sealed class EngineHostTests
{
    private static readonly LocalInterface WlanA = Kit.Iface("wlan0", 192, 168, 1, 2);
    private static readonly LocalInterface WlanB = Kit.Iface("wlan0", 192, 168, 1, 3);

    private sealed class HostWorld : IDisposable
    {
        public HostWorld()
        {
            Clock = new FakeClock();
            Net = new InMemoryNetwork(Clock);
            A = new HostDevice("A", 1, [WlanA], Net, Clock, DeviceType.Android, FsProfile.Android);
            B = new HostDevice("B", 2, [WlanB], Net, Clock, DeviceType.Windows, FsProfile.Windows);
        }

        public FakeClock Clock { get; }

        public InMemoryNetwork Net { get; }

        public HostDevice A { get; }

        public HostDevice B { get; }

        public void Dispose()
        {
            A.Dispose();
            B.Dispose();
        }
    }

    [Fact]
    public async Task First_start_creates_identity_listens_then_announces_and_is_idempotent()
    {
        using var w = new HostWorld();
        Assert.Equal(new StartResult.Running(42580), await w.A.Host.StartNetworkingAsync(Kit.Ct));
        Assert.IsType<IdentityLoad.Created>(w.A.Host.Identity.Value);
        Assert.Contains(42580, w.A.Transport.ListeningPorts);
        await Kit.WaitUntilAsync(() => w.A.Datagrams(w.Net).Count > 0, "first announcement");
        var ann = AnnouncementCodec.Decode(w.A.Datagrams(w.Net)[0].Data.ToArray());
        Assert.NotNull(ann);
        Assert.Equal((ushort)42580, ann.Port);
        Assert.Equal(DiscoveryStatus.Running.Instance, w.A.Services.Discovery.Status.Value);
        Assert.Equal(new StartResult.Running(42580), await w.A.Host.StartNetworkingAsync(Kit.Ct));
        Assert.Equal(1, w.A.IdentityStore.Loads);
    }

    [Fact]
    public async Task Corrupted_identity_resets_while_keeping_trusted_devices()
    {
        var clock = new FakeClock();
        var net = new InMemoryNetwork(clock);
        var old = new DeviceIdentity(Kit.DeviceIdOf(7), Kit.KeyPairOf(7), "A");
        var trust = new InMemoryTrustStore();
        trust.Upsert(new TrustedDevice(Kit.DeviceIdOf(9), "旧设备", DeviceType.Windows, Bytes.Of(Enumerable.Repeat((byte)1, 32).ToArray()), AutoAccept: false, PairedAtUtcMs: 1));
        using var a = new HostDevice("A", 1, [WlanA], net, clock, identityStore: new InMemoryIdentityStore(new SeededRng(1), "A", old, corrupted: true), trust: trust);

        Assert.Equal(new StartResult.Running(42580), await a.Host.StartNetworkingAsync(Kit.Ct));
        var reset = Assert.IsType<IdentityLoad.ResetCorrupted>(a.Host.Identity.Value);
        Assert.NotEqual(old.DeviceId, reset.Identity.DeviceId);
        Assert.Equal(reset.Identity.DeviceId, a.Id);
        Assert.NotNull(a.Trust.Get(Kit.DeviceIdOf(9)));
    }

    [Fact]
    public async Task Unsaved_identity_does_not_listen_or_announce_and_recovers_on_retry()
    {
        using var w = new HostWorld();
        w.A.IdentityStore.FailSaves = true;
        Assert.Equal(new StartResult.IdentityNotSaved("injected failure"), await w.A.Host.StartNetworkingAsync(Kit.Ct));
        Assert.IsType<IdentityLoad.SaveFailed>(w.A.Host.Identity.Value);
        Assert.Null(w.A.Host.Services.Value);
        Assert.Empty(w.A.Transport.ListeningPorts);
        Assert.Empty(w.A.Datagrams(w.Net));

        w.A.IdentityStore.FailSaves = false;
        Assert.Equal(new StartResult.Running(42580), await w.A.Host.StartNetworkingAsync(Kit.Ct));
        Assert.IsType<IdentityLoad.Created>(w.A.Host.Identity.Value);
    }

    [Fact]
    public async Task Listen_failure_does_not_announce_and_recovers_on_retry()
    {
        using var w = new HostWorld();
        w.A.Transport.ListenFails = true;
        Assert.Equal(StartResult.ListenFailed.Instance, await w.A.Host.StartNetworkingAsync(Kit.Ct));
        await Kit.Settle();
        Assert.Empty(w.A.Datagrams(w.Net));
        Assert.Equal(DiscoveryStatus.Stopped.Instance, w.A.Services.Discovery.Status.Value);

        w.A.Transport.ListenFails = false;
        Assert.Equal(new StartResult.Running(42580), await w.A.Host.StartNetworkingAsync(Kit.Ct));
        await Kit.WaitUntilAsync(() => w.A.Datagrams(w.Net).Count > 0, "announcements after recovery");
    }

    [Fact]
    public async Task Discovery_routes_text_and_file_transfer_and_unpair_cancels_tasks()
    {
        using var w = new HostWorld();
        await w.A.Host.StartNetworkingAsync(Kit.Ct);
        await w.B.Host.StartNetworkingAsync(Kit.Ct);
        w.A.TrustsEachOther(w.B);

        await TextKit.StepToAsync(w.Clock, w.Clock.MonotonicMs() + 2_500, step: 500);
        await Kit.WaitUntilAsync(() => w.B.Services.Discovery.Devices.Value.Any(d => d.DeviceId == w.A.Id), "B discovers A");

        Assert.IsType<SendCheck.Sent>(await w.B.Services.Text.SendAsync(w.A.Id, "你好，来自 Windows"));
        var items = await Kit.Within(w.A.Services.Text.Conversation(w.B.Id).FirstAsync(l => l.Count > 0, Kit.Ct));
        Assert.Equal("你好，来自 Windows", Assert.Single(items).Body);

        w.A.Trust.SetAutoAccept(w.B.Id, true);
        var src = w.B.File("报告.pdf", 200_000);
        var tid = Assert.IsType<SendOfferResult.Sent>(await w.B.Services.Files.SendAsync(w.A.Id, [src])).Id;
        await w.A.AwaitTaskAsync(tid, TaskState.Completed);
        Assert.Equal(w.B.Sources.EntryOf(src)!.Data, w.A.Storage.FilesIn(DirRef.Default.Instance).Single(kv => kv.Key == "报告.pdf").Value);

        // 解除配对会取消会话并移除信任
        await w.A.Services.Pairing.UnpairAsync(w.B.Id);
        Assert.Null(w.A.Trust.Get(w.B.Id));
        await Kit.WaitUntilAsync(() => w.A.Services.Sessions.GetSession(w.B.Id) is null, "session closed on unpair");
    }

    [Fact]
    public async Task Shutdown_stops_all_and_restart_reuses_identity()
    {
        using var w = new HostWorld();
        await w.A.Host.StartNetworkingAsync(Kit.Ct);
        await w.B.Host.StartNetworkingAsync(Kit.Ct);
        w.A.TrustsEachOther(w.B);

        await TextKit.StepToAsync(w.Clock, w.Clock.MonotonicMs() + 2_500, step: 500);
        await Kit.WaitUntilAsync(() => w.A.Services.Discovery.Devices.Value.Any(d => d.DeviceId == w.B.Id), "A discovers B");

        await w.A.Services.Text.SendAsync(w.B.Id, "hi");
        await Kit.Within(w.B.Services.Text.Conversation(w.A.Id).FirstAsync(l => l.Count > 0, Kit.Ct));
        Assert.NotNull(w.A.Services.Sessions.GetSession(w.B.Id));

        await w.A.Host.ShutdownNetworkingAsync(SessionCloseReason.AppExit);
        Assert.Null(w.A.Host.Networking.Value);
        Assert.Empty(w.A.Services.Sessions.Sessions.Value);
        Assert.Empty(w.A.Transport.ListeningPorts);
        Assert.Equal(DiscoveryStatus.Stopped.Instance, w.A.Services.Discovery.Status.Value);

        Assert.Equal(new StartResult.Running(42580), await w.A.Host.StartNetworkingAsync(Kit.Ct));
        Assert.Equal(1, w.A.IdentityStore.Loads);
    }
}
