using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>SessionManager 的单元测试（任务 15.8，与 Kotlin 侧 SessionManagerTest 对应）：建立 Session、E1 每一种 OpenFailure、预算阈值、端口回退、断开检测。</summary>
public sealed class SessionManagerTests
{
    private static readonly LocalInterface IfA = Kit.Iface("wlan0", 192, 168, 1, 2);
    private static readonly LocalInterface IfB = Kit.Iface("wlan0", 192, 168, 1, 3);

    private static TextAck AckOf(int n) => new(new MessageId(Bytes16.Of(Enumerable.Repeat((byte)n, 16).ToArray())));

    /// <summary>两台设备 A（ID 较小）与 B，B 已在监听。</summary>
    private sealed class Pair2
    {
        public Pair2(SessionTimings? timings = null, ProtocolVersion? bVersion = null)
        {
            Net = new InMemoryNetwork(Clock.Time);
            A = new TestDevice("A", 1, IfA, Net, Clock, timings: timings);
            B = new TestDevice("B", 2, IfB, Net, Clock, timings: timings, version: bVersion);
            B.Listen();
        }

        public FakeClock Clock { get; } = new();

        public InMemoryNetwork Net { get; }

        public TestDevice A { get; }

        public TestDevice B { get; }

        public void MutualTrust()
        {
            A.Trusts(B);
            B.Trusts(A);
        }
    }

    private static async Task<Session> OpenOk(TestDevice from, Target target) =>
        Assert.IsType<OpenResult.Ok>(await Kit.Within(from.Manager.OpenAsync(target))).Session;

    // ---- 建立 Session ----

    [Fact]
    public async Task Mutual_trust_registers_same_connection_on_both_sides_and_messages_flow()
    {
        var p = new Pair2();
        p.MutualTrust();
        var s = await OpenOk(p.A, new Target.Device(p.B.Id));
        await Kit.WaitUntilAsync(() => p.B.Manager.GetSession(p.A.Id) is not null, "B registers");
        var sb = p.B.Manager.GetSession(p.A.Id)!;
        Assert.Equal(new PeerIdentity(p.B.Id, "B", p.B.Type, p.B.PublicKey), s.Peer);
        Assert.Equal(p.A.Id, sb.Peer.DeviceId);
        Assert.Equal(s.Connection.Local, sb.Connection.Remote);
        Assert.Equal(p.A.Id, s.Initiator);
        Assert.Equal(p.A.Id, sb.Initiator);
        Assert.True(s.Send(AckOf(1)));
        Assert.Equal(AckOf(1), await sb.Incoming.ReadAsync(Kit.Ct));
        Assert.True(sb.Send(AckOf(2), Priority.Text));
        Assert.Equal(AckOf(2), await s.Incoming.ReadAsync(Kit.Ct));
        Assert.Equal(new[] { p.B.Id }, p.A.Manager.Sessions.Value.Keys);
        Assert.Equal(IfB.Address, p.A.Trust.Get(p.B.Id)!.LastAddress);
        Assert.Equal(42580, p.A.Trust.Get(p.B.Id)!.LastPort);
        Assert.True(p.A.Transport.Monitor.FastPolling);
        Assert.Same(s, Assert.IsType<OpenResult.Ok>(await p.A.Manager.OpenAsync(new Target.Device(p.B.Id))).Session);
        Assert.Single(p.Net.Connections);
    }

    [Fact]
    public async Task Trusted_name_is_updated_after_authentication_and_close_unregisters_both_sides()
    {
        var p = new Pair2();
        p.A.Trusts(p.B, displayName: "旧名称");
        p.B.Trusts(p.A);
        await OpenOk(p.A, new Target.Device(p.B.Id));
        Assert.Equal("B", p.A.Trust.Get(p.B.Id)!.DisplayName);
        await Kit.WaitUntilAsync(() => p.B.Manager.GetSession(p.A.Id) is not null, "B registers");
        var sb = p.B.Manager.GetSession(p.A.Id)!;
        await p.A.Manager.CloseAsync(p.B.Id, SessionCloseReason.Normal).WaitAsync(TimeSpan.FromSeconds(10), Kit.Ct);
        Assert.Equal(new CloseReason.PeerClosed(SessionCloseReason.Normal), await Kit.Within(sb.Closed));
        await Kit.WaitUntilAsync(() => p.A.Manager.Sessions.Value.Count == 0 && p.B.Manager.Sessions.Value.Count == 0, "unregistered");
        await Kit.WaitUntilAsync(() => !p.A.Transport.Monitor.FastPolling, "slow polling");
    }

    [Fact]
    public async Task Untrusted_peer_yields_pending_pair_connections_that_promote_to_sessions_after_pair_complete()
    {
        var p = new Pair2();
        var mine = Assert.IsType<OpenResult.Pairing>(await Kit.Within(p.A.Manager.OpenAsync(new Target.Address(IfB.Address, p.B.Port)))).Connection;
        Assert.Equal(Intent.Pair, mine.Intent);
        Assert.Equal(Role.Initiator, mine.Role);
        var theirs = await p.B.Manager.PendingInbound.ReadAsync(Kit.Ct);
        Assert.Equal(Intent.Pair, theirs.Intent);
        Assert.Equal(p.A.Id, theirs.Peer.DeviceId);
        var req = new PairRequest(Bytes.Of(Enumerable.Repeat((byte)1, 32).ToArray()));
        mine.Send(req);
        Assert.Equal(req, await theirs.Incoming.ReadAsync(Kit.Ct));
        Assert.Empty(p.A.Manager.Sessions.Value);
        mine.Send(PairComplete.Instance);
        Assert.Equal(PairComplete.Instance, await theirs.Incoming.ReadAsync(Kit.Ct));
        var sb = theirs.Promote()!;
        var sa = mine.Promote()!;
        Assert.Equal(p.A.Id, sa.Initiator);
        Assert.Equal(p.A.Id, sb.Initiator);
        sa.Send(AckOf(3));
        Assert.Equal(AckOf(3), await sb.Incoming.ReadAsync(Kit.Ct));
        Assert.Null(p.A.Trust.Get(p.B.Id)); // 信任由 PairingService 写入，SessionManager 不写
    }

    [Fact]
    public async Task Pending_violation_gets_not_trusted_and_pairing_consumer_never_sees_it()
    {
        var p = new Pair2();
        var mine = Assert.IsType<OpenResult.Pairing>(await Kit.Within(p.A.Manager.OpenAsync(new Target.Address(IfB.Address, p.B.Port)))).Connection;
        var theirs = await p.B.Manager.PendingInbound.ReadAsync(Kit.Ct);
        mine.Send(AckOf(9));
        Assert.Equal(new SessionResult(SessionResultCode.NotTrusted), await mine.Incoming.ReadAsync(Kit.Ct));
        Assert.Equal(new CloseReason.PendingViolation(), await Kit.Within(theirs.Closed));
        Assert.False(await theirs.Incoming.WaitToReadAsync(Kit.Ct));
        Assert.Equal(new CloseReason.PeerClosed(null), await Kit.Within(mine.Closed));
    }

    // ---- E1：每一种 OpenFailure ----

    private static OpenFailure Failure(OpenResult r) => Assert.IsType<OpenResult.Failed>(r).Failure;

    [Fact]
    public async Task NotLocal_fails_immediately_without_any_connect_attempt()
    {
        var p = new Pair2();
        Assert.Equal(new OpenFailure.NotLocal(), Failure(await p.A.Manager.OpenAsync(new Target.Address(Ipv4.Of(8, 8, 8, 8), 42580))));
        Assert.Equal(new OpenFailure.NotLocal(), Failure(await p.A.Manager.OpenAsync(new Target.Address(Ipv4.Of(10, 0, 0, 9), 42580))));
        Assert.Empty(p.A.Transport.ConnectAttempts);
        Assert.Equal(0, p.Clock.MonotonicMs());
    }

    [Fact]
    public async Task SelfConnection_by_own_address_and_by_device_id_in_msg2()
    {
        var p = new Pair2();
        Assert.Equal(new OpenFailure.SelfConnection(), Failure(await p.A.Manager.OpenAsync(new Target.Address(IfA.Address, 42580))));
        Assert.Empty(p.A.Transport.ConnectAttempts);
        var twin = new TestDevice("A2", 1, Kit.Iface("wlan0", 192, 168, 1, 4), p.Net, p.Clock, identityOverride: p.A.Identity);
        twin.Listen();
        Assert.Equal(new OpenFailure.SelfConnection(), Failure(await Kit.Within(p.A.Manager.OpenAsync(new Target.Address(twin.Iface.Address, twin.Port)))));
        await Kit.Settle();
        Assert.Empty(twin.Manager.Sessions.Value);
    }

    [Fact]
    public async Task BindFailed_Refused_Unreachable()
    {
        var p = new Pair2();
        p.A.Transport.ConnectBehavior = t => t.Port == 1 ? new ConnectBehavior.BindFail(VpnSuspected: true) : null;
        Assert.Equal(new OpenFailure.BindFailed(true), Failure(await p.A.Manager.OpenAsync(new Target.Address(IfB.Address, 1))));
        Assert.Equal(new OpenFailure.Refused(), Failure(await p.A.Manager.OpenAsync(new Target.Address(IfB.Address, 9))));
        Assert.Equal(new OpenFailure.Unreachable(), Failure(await p.A.Manager.OpenAsync(new Target.Address(Ipv4.Of(192, 168, 1, 77), 42580))));
        Assert.Equal(new OpenFailure.Unreachable(), Failure(await p.A.Manager.OpenAsync(new Target.Device(Kit.DeviceIdOf(55))))); // 没有已知地址
    }

    [Fact]
    public async Task Timeout_budget_is_still_pending_at_9999ms_and_fails_exactly_at_10000ms()
    {
        var p = new Pair2();
        p.A.Trusts(p.B);
        p.A.Transport.ConnectBehavior = _ => new ConnectBehavior.Hang();
        var r = p.A.Manager.OpenAsync(new Target.Device(p.B.Id));
        await Kit.WaitUntilAsync(() => p.Clock.PendingTimers > 0, "connect timer");
        p.Clock.AdvanceBy(9_999);
        await Kit.Settle();
        Assert.False(r.IsCompleted);
        p.Clock.AdvanceBy(1);
        Assert.Equal(new OpenFailure.Timeout(), Failure(await Kit.Within(r)));
        Assert.Equal(10_000, p.Clock.MonotonicMs());
    }

    [Fact]
    public async Task NoVersionResponse_when_no_preamble_and_Timeout_when_preamble_but_no_handshake()
    {
        var clock = new FakeClock();
        var net = new InMemoryNetwork(clock.Time);
        var a = new TestDevice("A", 1, IfA, net, clock);
        var raw = net.Host("raw", [IfB]);
        var l = raw.Listen([42580])!;
        var accepted = l.AcceptAsync(Kit.Ct).AsTask(); // 接受后什么也不发
        var r = a.Manager.OpenAsync(new Target.Address(IfB.Address, 42580));
        await accepted;
        await Kit.WaitUntilAsync(() => clock.PendingTimers > 0, "preamble timer");
        clock.AdvanceBy(9_999);
        await Kit.Settle();
        Assert.False(r.IsCompleted);
        clock.AdvanceBy(1);
        Assert.Equal(new OpenFailure.NoVersionResponse(), Failure(await Kit.Within(r)));

        var accepted2 = l.AcceptAsync(Kit.Ct).AsTask();
        var r2 = a.Manager.OpenAsync(new Target.Address(IfB.Address, 42580));
        var c2 = (await accepted2)!;
        await c2.WriteAsync(PreambleCodec.Encode(ProtocolVersion.Current), Kit.Ct);
        var got = 0;
        var buf = new byte[256];
        while (got < PreambleCodec.Size + 2 + 32)
        {
            got += await c2.ReadAsync(buf, Kit.Ct); // 等发起方写出前导与 msg1，此时它已在等 msg2
        }

        await Kit.WaitUntilAsync(() => clock.PendingTimers > 0, "handshake timer");
        clock.AdvanceBy(10_000);
        Assert.Equal(new OpenFailure.Timeout(), Failure(await Kit.Within(r2)));
    }

    [Fact]
    public async Task VersionIncompatible_on_both_sides()
    {
        var p = new Pair2(bVersion: new ProtocolVersion(2, 0));
        var shown = p.B.Manager.InboundFailures.FirstAsync(Kit.Ct);
        Assert.Equal(
            new OpenFailure.VersionIncompatible(ProtocolVersion.V1_0, new ProtocolVersion(2, 0)),
            Failure(await Kit.Within(p.A.Manager.OpenAsync(new Target.Address(IfB.Address, p.B.Port)))));
        Assert.Equal(new OpenFailure.VersionIncompatible(new ProtocolVersion(2, 0), ProtocolVersion.V1_0), (await Kit.Within(shown)).Failure);
    }

    [Fact]
    public async Task ProtocolError_on_bad_preamble_magic_and_zero_length_handshake_message()
    {
        var clock = new FakeClock();
        var net = new InMemoryNetwork(clock.Time);
        var a = new TestDevice("A", 1, IfA, net, clock);
        var raw = net.Host("raw", [IfB]);
        var l = raw.Listen([42580])!;
        _ = Task.Run(async () => await (await l.AcceptAsync(Kit.Ct))!.WriteAsync("XXXX\u0001\u0000"u8.ToArray(), Kit.Ct), Kit.Ct);
        Assert.Equal(new OpenFailure.ProtocolError(), Failure(await Kit.Within(a.Manager.OpenAsync(new Target.Address(IfB.Address, 42580)))));
        byte[] zeroLength = [.. PreambleCodec.Encode(ProtocolVersion.Current), 0, 0];
        _ = Task.Run(async () => await (await l.AcceptAsync(Kit.Ct))!.WriteAsync(zeroLength, Kit.Ct), Kit.Ct);
        Assert.Equal(new OpenFailure.ProtocolError(), Failure(await Kit.Within(a.Manager.OpenAsync(new Target.Address(IfB.Address, 42580)))));
    }

    [Fact]
    public async Task HandshakeAuthFailed_when_msg2_cannot_be_decrypted()
    {
        var clock = new FakeClock();
        var net = new InMemoryNetwork(clock.Time);
        var a = new TestDevice("A", 1, IfA, net, clock);
        var raw = net.Host("raw", [IfB]);
        var l = raw.Listen([42580])!;
        byte[] garbage = [.. PreambleCodec.Encode(ProtocolVersion.Current), 0, 100, .. Enumerable.Repeat((byte)3, 100)];
        _ = Task.Run(async () => await (await l.AcceptAsync(Kit.Ct))!.WriteAsync(garbage, Kit.Ct), Kit.Ct);
        Assert.Equal(new OpenFailure.HandshakeAuthFailed(), Failure(await Kit.Within(a.Manager.OpenAsync(new Target.Address(IfB.Address, 42580)))));
    }

    [Fact]
    public async Task KeyMismatch_does_not_send_msg3_and_keeps_saved_key()
    {
        var p = new Pair2();
        var wrong = Kit.KeyPairOf(99).PublicKey;
        p.A.Trusts(p.B, key: wrong);
        p.B.Trusts(p.A);
        Assert.Equal(new OpenFailure.KeyMismatch("B"), Failure(await Kit.Within(p.A.Manager.OpenAsync(new Target.Device(p.B.Id)))));
        await Kit.Settle();
        Assert.Equal(wrong, p.A.Trust.Get(p.B.Id)!.PublicKey);
        Assert.Empty(p.B.Manager.Sessions.Value);
        Assert.Empty(p.A.Manager.Sessions.Value);
    }

    [Fact]
    public async Task FingerprintMismatch_on_qr_target()
    {
        var p = new Pair2();
        var qr = new QrPayload(p.B.Id, "B", new ValueList<uint>([IfB.Address.Bits]), (ushort)p.B.Port, Bytes.Of(new byte[32]), Bytes16.Of(Enumerable.Repeat((byte)5, 16).ToArray()));
        Assert.Equal(new OpenFailure.FingerprintMismatch(), Failure(await Kit.Within(p.A.Manager.OpenAsync(new Target.Qr(qr)))));
    }

    [Fact]
    public async Task NotTrusted_keeps_local_trust()
    {
        var p = new Pair2();
        p.A.Trusts(p.B);
        Assert.Equal(new OpenFailure.NotTrusted(), Failure(await Kit.Within(p.A.Manager.OpenAsync(new Target.Device(p.B.Id)))));
        Assert.NotNull(p.A.Trust.Get(p.B.Id));
        await Kit.Settle();
        Assert.Empty(p.B.Manager.Sessions.Value);
    }

    [Fact]
    public async Task PeerKeyMismatch_and_responder_reports_KeyMismatch()
    {
        var p = new Pair2();
        p.A.Trusts(p.B);
        p.B.Trusts(p.A, key: Kit.KeyPairOf(98).PublicKey);
        var shown = p.B.Manager.InboundFailures.FirstAsync(Kit.Ct);
        Assert.Equal(new OpenFailure.PeerKeyMismatch("B"), Failure(await Kit.Within(p.A.Manager.OpenAsync(new Target.Device(p.B.Id)))));
        Assert.Equal(new OpenFailure.KeyMismatch("A"), (await Kit.Within(shown)).Failure);
    }

    // ---- 扫码目标 ----

    [Fact]
    public async Task Qr_skips_non_local_candidates_and_gives_each_candidate_5s()
    {
        var p = new Pair2();
        var fp = Bytes.Of(Fingerprint.Of(p.B.PublicKey.Span));
        var ips = new ValueList<uint>([Ipv4.Of(8, 8, 8, 8).Bits, IfB.Address.Bits, Ipv4.Of(192, 168, 1, 9).Bits]);
        var qr = new QrPayload(p.B.Id, "B", ips, (ushort)p.B.Port, fp, Bytes16.Of(new byte[16]));
        p.A.Transport.ConnectBehavior = _ => new ConnectBehavior.Hang();
        var r = p.A.Manager.OpenAsync(new Target.Qr(qr));
        await Kit.WaitUntilAsync(() => p.Clock.PendingTimers > 0, "first candidate");
        p.Clock.AdvanceBy(4_999);
        await Kit.Settle();
        Assert.Single(p.A.Transport.ConnectAttempts);
        p.Clock.AdvanceBy(1);
        await Kit.WaitUntilAsync(() => p.A.Transport.ConnectAttempts.Count == 2 && p.Clock.PendingTimers > 0, "second candidate");
        p.Clock.AdvanceBy(5_000);
        Assert.Equal(new OpenFailure.Timeout(), Failure(await Kit.Within(r)));
        Assert.Equal(new[] { IfB.Address, Ipv4.Of(192, 168, 1, 9) }, p.A.Transport.ConnectAttempts.Select(x => x.Target.Ip));

        p.A.Transport.ConnectBehavior = _ => null;
        var ok = Assert.IsType<OpenResult.Pairing>(await Kit.Within(p.A.Manager.OpenAsync(new Target.Qr(qr))));
        Assert.Equal(Intent.QrPair, ok.Connection.Intent);
        Assert.Equal(Intent.QrPair, (await p.B.Manager.PendingInbound.ReadAsync(Kit.Ct)).Intent);
    }

    [Fact]
    public async Task Qr_with_only_non_local_candidates_is_NotLocal()
    {
        var p = new Pair2();
        var qr = new QrPayload(p.B.Id, "B", new ValueList<uint>([Ipv4.Of(8, 8, 8, 8).Bits]), (ushort)p.B.Port, Bytes.Of(new byte[32]), Bytes16.Of(new byte[16]));
        Assert.Equal(new OpenFailure.NotLocal(), Failure(await p.A.Manager.OpenAsync(new Target.Qr(qr))));
        Assert.Empty(p.A.Transport.ConnectAttempts);
    }

    // ---- 监听端口 ----

    [Fact]
    public void Listen_falls_back_42580_then_42590_42599_then_system_port_then_fails()
    {
        var clock = new FakeClock();
        var net = new InMemoryNetwork(clock.Time);
        var d1 = new TestDevice("D1", 1, IfA, net, clock);
        d1.Transport.BlockedTcpPorts.Add(42580);
        Assert.Equal(new ListenResult.Ok(42590), d1.Manager.StartListening());
        var d2 = new TestDevice("D2", 2, IfB, net, clock);
        d2.Transport.BlockedTcpPorts.UnionWith([42580, .. Enumerable.Range(42590, 10)]);
        Assert.True(Assert.IsType<ListenResult.Ok>(d2.Manager.StartListening()).Port >= 50_000);
        var d3 = new TestDevice("D3", 3, Kit.Iface("wlan0", 192, 168, 1, 4), net, clock);
        d3.Transport.BlockedTcpPorts.UnionWith([42580, .. Enumerable.Range(42590, 10), .. Enumerable.Range(50_000, 11)]);
        Assert.Equal(new ListenResult.Failed(), d3.Manager.StartListening());
    }

    // ---- 断开检测（18.8） ----

    [Fact]
    public async Task Reset_is_detected_as_peer_closed_on_both_sides()
    {
        var p = new Pair2();
        p.MutualTrust();
        var s = await OpenOk(p.A, new Target.Device(p.B.Id));
        await Kit.WaitUntilAsync(() => p.B.Manager.GetSession(p.A.Id) is not null, "B registers");
        var sb = p.B.Manager.GetSession(p.A.Id)!;
        Assert.Single(p.Net.Connections).Reset();
        Assert.Equal(new CloseReason.PeerClosed(null), await Kit.Within(s.Closed));
        Assert.Equal(new CloseReason.PeerClosed(null), await Kit.Within(sb.Closed));
        Assert.Equal(0, p.Clock.MonotonicMs());
        await Kit.WaitUntilAsync(() => p.A.Manager.Sessions.Value.Count == 0, "unregistered");
    }

    [Fact]
    public async Task Local_interface_loss_closes_with_LocalNetworkLost_and_peer_sees_eof()
    {
        var p = new Pair2();
        p.MutualTrust();
        var s = await OpenOk(p.A, new Target.Device(p.B.Id));
        await Kit.WaitUntilAsync(() => p.B.Manager.GetSession(p.A.Id) is not null, "B registers");
        var sb = p.B.Manager.GetSession(p.A.Id)!;
        p.A.Transport.Monitor.Set([]);
        Assert.Equal(new CloseReason.LocalNetworkLost(), await Kit.Within(s.Closed));
        Assert.Equal(new CloseReason.PeerClosed(null), await Kit.Within(sb.Closed));
        Assert.Equal(0, p.Clock.MonotonicMs());
    }
}
