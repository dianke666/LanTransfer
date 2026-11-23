using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>DiscoveryService 单元测试（任务 19.4，与 Kotlin 侧 DiscoveryServiceTest 对应）。</summary>
public sealed class DiscoveryServiceTests
{
    private static readonly LocalInterface WlanA = Kit.Iface("wlan0", 192, 168, 1, 2);
    private static readonly LocalInterface WlanB = Kit.Iface("wlan0", 192, 168, 1, 3);
    private static readonly LocalInterface HotspotA = new("ap0", IfaceKind.Hotspot, Ipv4.Of(192, 168, 43, 1), 24);
    private static readonly LocalInterface EthA = new("eth0", IfaceKind.Ethernet, Ipv4.Of(10, 0, 0, 2), 24);

    private sealed class DiscDevice : IDisposable
    {
        public DiscDevice(string name, int idByte, IEnumerable<LocalInterface> ifaces, InMemoryNetwork net, FakeClock clock)
        {
            Transport = net.Host(name, ifaces);
            Trust = new InMemoryTrustStore();
            Identity = new DeviceIdentity(Kit.DeviceIdOf(idByte), Kit.KeyPairOf(idByte), name);
            Service = new DiscoveryService(Transport, Transport.Monitor, Identity, DeviceType.Windows, Trust, clock, new SeededRng(idByte));
        }

        public InMemoryTransport Transport { get; }

        public InMemoryTrustStore Trust { get; }

        public DeviceIdentity Identity { get; }

        public DiscoveryService Service { get; }

        public void Dispose() => Service.Dispose();
    }

    private sealed class DiscWorld : IDisposable
    {
        public DiscWorld()
        {
            Clock = new FakeClock();
            Net = new InMemoryNetwork(Clock);
            A = new DiscDevice("A", 1, [WlanA], Net, Clock);
            B = new DiscDevice("B", 2, [WlanB], Net, Clock);
        }

        public FakeClock Clock { get; }

        public InMemoryNetwork Net { get; }

        public DiscDevice A { get; }

        public DiscDevice B { get; }

        public IReadOnlyList<SentDatagram> SentBy(string name) => [.. Net.Datagrams.Where(d => d.From == name)];

        public void Dispose()
        {
            A.Dispose();
            B.Dispose();
        }
    }

    [Fact]
    public async Task Start_sends_immediately_with_jitter_and_new_interface_sends_immediately()
    {
        using var w = new DiscWorld();
        w.A.Transport.Monitor.Set([WlanA, HotspotA]);
        w.A.Service.Start(42580);
        await Kit.WaitUntilAsync(() => w.SentBy("A").Count == 4, "first announcement round");

        var first = w.SentBy("A");
        var expectedTargets = new[]
        {
            (WlanA, WlanA.Broadcast),
            (WlanA, DiscoveryService.Multicast),
            (HotspotA, HotspotA.Broadcast),
            (HotspotA, DiscoveryService.Multicast),
        };
        Assert.Equal(expectedTargets.OrderBy(x => x.Item1.Name).ThenBy(x => x.Item2.Bits), first.Select(d => (d.Via, d.Dst)).OrderBy(x => x.Via.Name).ThenBy(x => x.Dst.Bits));
        Assert.All(first, d => Assert.Equal(ProtocolConstants.DiscoveryPort, d.Port));
        Assert.Equal(new Announcement(w.A.Identity.DeviceId, "A", DeviceType.Windows, ProtocolVersion.Current, 42580), AnnouncementCodec.Decode(first[0].Data.ToArray()));

        // 推进虚拟时间 30 s，验证周期在 1800–2200 ms 且存在抖动
        var rounds = new List<long> { w.Clock.MonotonicMs() };
        var seen = first.Count;
        var end = w.Clock.MonotonicMs() + 30_000;
        while (w.Clock.MonotonicMs() < end)
        {
            var next = Math.Min(end, w.Clock.NextTimerDueMs ?? end);
            w.Clock.AdvanceBy(Math.Max(1, next - w.Clock.MonotonicMs()));
            await Task.Delay(2, Kit.Ct);
            var n = w.SentBy("A").Count;
            if (n != seen)
            {
                Assert.Equal(4, n - seen);
                seen = n;
                rounds.Add(w.Clock.MonotonicMs());
            }
        }

        Assert.True(rounds.Count >= 10);
        var gaps = rounds.Zip(rounds.Skip(1), (x, y) => y - x).ToList();
        Assert.All(gaps, g => Assert.InRange(g, 1_800, 2_200));
        Assert.True(gaps.Distinct().Count() > 1);

        // 新接口出现时立即只在该接口上发送
        w.A.Transport.Monitor.Set([WlanA, HotspotA, EthA]);
        await Kit.WaitUntilAsync(() => w.SentBy("A").Count >= seen + 2, "new interface round");
        var added = w.SentBy("A").Skip(seen).Take(2).ToList();
        Assert.Equal(
            new[] { (EthA, EthA.Broadcast), (EthA, DiscoveryService.Multicast) }.OrderBy(x => x.Item2.Bits),
            added.Select(d => (d.Via, d.Dst)).OrderBy(x => x.Dst.Bits));
    }

    [Fact]
    public async Task Peers_discover_each_other_and_expire_after_10s_keeping_trust()
    {
        using var w = new DiscWorld();
        w.A.Trust.Upsert(new TrustedDevice(w.B.Identity.DeviceId, "B", DeviceType.Windows, w.B.Identity.KeyPair.PublicKey, AutoAccept: false, PairedAtUtcMs: 1));
        w.A.Service.Start(42580);
        w.B.Service.Start(42590);

        await Kit.WaitUntilAsync(() => w.A.Service.Devices.Value.Count == 1 && w.B.Service.Devices.Value.Count == 1, "mutual discovery");
        var eb = Assert.Single(w.A.Service.Devices.Value);
        Assert.Equal(w.B.Identity.DeviceId, eb.DeviceId);
        Assert.Equal(WlanB.Address, eb.Ip);
        Assert.Equal((ushort)42590, eb.Announcement.Port);
        Assert.True(eb.Trusted);
        Assert.Equal(new SocketAddr(WlanB.Address, 42590), w.A.Service.AddressOf(w.B.Identity.DeviceId));
        Assert.False(Assert.Single(w.B.Service.Devices.Value).Trusted);

        var t0 = w.Clock.MonotonicMs();
        w.B.Service.Stop();
        Assert.Equal(DiscoveryStatus.Stopped.Instance, w.B.Service.Status.Value);
        Assert.Empty(w.B.Service.Devices.Value);

        await TextKit.StepToAsync(w.Clock, t0 + 10_000, step: 1_000);
        Assert.Single(w.A.Service.Devices.Value);

        await TextKit.StepToAsync(w.Clock, t0 + 12_000, step: 1_000);
        await Kit.WaitUntilAsync(() => w.A.Service.Devices.Value.Count == 0, "expiry removal");
        Assert.NotNull(w.A.Trust.Get(w.B.Identity.DeviceId));
    }

    [Fact]
    public async Task Trust_changes_update_flag_and_invalid_datagrams_are_ignored()
    {
        using var w = new DiscWorld();
        w.A.Service.Start(42580);
        w.B.Service.Start(42580);
        await Kit.WaitUntilAsync(() => w.A.Service.Devices.Value.Count == 1, "A sees B");
        Assert.False(Assert.Single(w.A.Service.Devices.Value).Trusted);

        w.A.Trust.Upsert(new TrustedDevice(w.B.Identity.DeviceId, "B", DeviceType.Windows, w.B.Identity.KeyPair.PublicKey, AutoAccept: false, PairedAtUtcMs: 1));
        await Kit.WaitUntilAsync(() => Assert.Single(w.A.Service.Devices.Value).Trusted, "trusted updated");

        var before = w.A.Service.Devices.Value;
        var junkIface = Kit.Iface("wlan0", 192, 168, 1, 9);
        var junk = Assert.IsType<UdpOpenResult.Ok>(w.Net.Host("J", [junkIface]).OpenUdp(50_123)).Endpoint;
        await junk.SendAsync(junkIface, Ipv4.Of(192, 168, 1, 255), ProtocolConstants.DiscoveryPort, new byte[] { 1, 2, 3 }, Kit.Ct);
        await Kit.Settle();
        Assert.Equal(before, w.A.Service.Devices.Value);
    }

    [Fact]
    public async Task Unavailable_when_port_blocked_or_send_fails_recovers_on_success()
    {
        using var w = new DiscWorld();
        w.A.Transport.BlockedUdpPorts.Add(ProtocolConstants.DiscoveryPort);
        w.A.Service.Start(42580);
        Assert.IsType<DiscoveryStatus.Unavailable>(w.A.Service.Status.Value);
        await Kit.Settle();
        Assert.Empty(w.SentBy("A"));

        w.B.Transport.UdpSendFails = true;
        w.B.Service.Start(42580);
        await Kit.WaitUntilAsync(() => w.B.Service.Status.Value is DiscoveryStatus.Unavailable { Reason: DiscoveryService.SendFailed }, "send failed status");

        w.B.Transport.UdpSendFails = false;
        await TextKit.StepToAsync(w.Clock, w.Clock.MonotonicMs() + 2_500, step: 500);
        await Kit.WaitUntilAsync(() => w.B.Service.Status.Value is DiscoveryStatus.Running, "recovered running");
    }
}
