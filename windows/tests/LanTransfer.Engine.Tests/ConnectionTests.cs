using LanTransfer.Domain;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>Connection 的单元测试（与 Kotlin 侧 ConnectionTest 对应）：Pending 白名单、记录层与解码错误、关闭、心跳与无响应。</summary>
public sealed class ConnectionTests
{
    private static Text TextOf(int n) => new(new MessageId(Bytes16.Of(Enumerable.Repeat((byte)n, 16).ToArray())), 1_700_000_000_000, Bytes.Of(System.Text.Encoding.UTF8.GetBytes("t" + n)));

    private static TextAck AckOf(int n) => new(new MessageId(Bytes16.Of(Enumerable.Repeat((byte)n, 16).ToArray())));

    [Fact]
    public async Task Pending_delivers_whitelist_skips_heartbeat_and_rejects_first_forbidden_message()
    {
        var t = await ConnUnderTest.CreateAsync(new FakeClock());
        var req = new PairRequest(Bytes.Of(Enumerable.Repeat((byte)7, 32).ToArray()));
        await t.Peer.SendAsync(req);
        await t.Peer.SendAsync(Heartbeat.Instance);
        await t.Peer.SendAsync(TextOf(1));
        await t.Peer.SendAsync(new PairCancel(PairCancelReason.User));
        Assert.Equal(new SessionResult(SessionResultCode.NotTrusted), await t.Peer.ReceiveAsync());
        Assert.Null(await t.Peer.ReceiveAsync()); // 随后关闭
        Assert.Equal(new CloseReason.PendingViolation(), await Kit.Within(t.Conn.Closed));
        Assert.Equal(new ProtocolMessage[] { req }, await t.DrainIncomingAsync());
    }

    [Fact]
    public async Task Initiator_activates_on_accepted_so_following_application_message_is_not_a_violation()
    {
        var t = await ConnUnderTest.CreateAsync(new FakeClock(), Role.Initiator);
        await t.Peer.SendAsync(new SessionResult(SessionResultCode.Accepted));
        await t.Peer.SendAsync(TextOf(2));
        Assert.Equal(new SessionResult(SessionResultCode.Accepted), await t.Conn.Incoming.ReadAsync(Kit.Ct));
        Assert.Equal(TextOf(2), await t.Conn.Incoming.ReadAsync(Kit.Ct));
        Assert.IsType<ConnState.Active>(t.Conn.State.Value);
    }

    [Fact]
    public async Task Responder_activates_on_pair_complete()
    {
        var t = await ConnUnderTest.CreateAsync(new FakeClock());
        await t.Peer.SendAsync(PairComplete.Instance);
        await t.Peer.SendAsync(TextOf(3));
        Assert.Equal(PairComplete.Instance, await t.Conn.Incoming.ReadAsync(Kit.Ct));
        Assert.Equal(TextOf(3), await t.Conn.Incoming.ReadAsync(Kit.Ct));
        Assert.IsType<ConnState.Active>(t.Conn.State.Value);
    }

    [Fact]
    public async Task Integrity_error_keeps_earlier_messages_ignores_later_bytes_and_closes()
    {
        var t = await ConnUnderTest.CreateAsync(new FakeClock());
        t.Conn.Activate();
        await t.Peer.SendAsync(TextOf(1));
        await t.Peer.SendRawAsync([0x00, 0x20, .. new byte[32]]); // 长度合法但无法解密的 record
        await t.Peer.SendAsync(TextOf(2));
        Assert.Equal(new CloseReason.IntegrityFailed(), await Kit.Within(t.Conn.Closed));
        Assert.Equal(new ProtocolMessage[] { TextOf(1) }, await t.DrainIncomingAsync());
        Assert.Null(await t.Peer.ReceiveAsync());
    }

    [Fact]
    public async Task Decode_error_keeps_earlier_messages_ignores_later_records_and_closes_with_protocol_error()
    {
        var t = await ConnUnderTest.CreateAsync(new FakeClock());
        t.Conn.Activate();
        await t.Peer.SendAsync(TextOf(1));
        await t.Peer.SendPlainAsync([0x12, 0x34, 0x20, 0, 0, 0, 0]); // magic 不符
        await t.Peer.SendAsync(TextOf(2));
        Assert.IsType<CloseReason.ProtocolError>(await Kit.Within(t.Conn.Closed));
        Assert.Equal(new ProtocolMessage[] { TextOf(1) }, await t.DrainIncomingAsync());
    }

    [Fact]
    public async Task Peer_session_close_and_eof_map_to_peer_closed()
    {
        var clock = new FakeClock();
        var t = await ConnUnderTest.CreateAsync(clock);
        await t.Peer.SendAsync(new SessionClose(SessionCloseReason.AppExit));
        Assert.Equal(new CloseReason.PeerClosed(SessionCloseReason.AppExit), await Kit.Within(t.Conn.Closed));
        Assert.Equal(InterruptReason.PeerClosed, new CloseReason.PeerClosed(SessionCloseReason.AppExit).ToInterruptReason());
        Assert.Null(new CloseReason.PeerClosed(SessionCloseReason.Duplicate).ToInterruptReason());

        var t2 = await ConnUnderTest.CreateAsync(clock);
        t2.Peer.Sock.Close();
        Assert.Equal(new CloseReason.PeerClosed(null), await Kit.Within(t2.Conn.Closed));
    }

    [Fact]
    public async Task Graceful_close_writes_queued_control_messages_then_session_close_then_eof()
    {
        var t = await ConnUnderTest.CreateAsync(new FakeClock());
        t.Conn.Activate();
        t.Conn.Enqueue(AckOf(0), Priority.Control);
        Assert.Equal(new CloseReason.Local(SessionCloseReason.Normal), await Kit.Within(t.Conn.CloseGracefullyAsync(SessionCloseReason.Normal)));
        Assert.False(t.Conn.Enqueue(TextOf(1), Priority.Text));
        Assert.Equal(AckOf(0), await t.Peer.ReceiveAsync());
        Assert.Equal(new SessionClose(SessionCloseReason.Normal), await t.Peer.ReceiveAsync());
        Assert.Null(await t.Peer.ReceiveAsync());
    }

    [Fact]
    public async Task Heartbeat_only_after_5s_idle_and_unresponsive_close_exactly_at_15s()
    {
        var clock = new FakeClock();
        RecordingConn? rec = null;
        var t = await ConnUnderTest.CreateAsync(clock, wrap: c => rec = new RecordingConn(c, clock));
        await Kit.AdvanceToAsync(clock, t.Conn, 4_999, () => 0);
        Assert.Empty(rec!.Writes);
        await Kit.AdvanceToAsync(clock, t.Conn, 5_000, () => 0);
        Assert.Equal(new long[] { 5_000 }, rec.Writes.Select(w => w.Time));
        Assert.Equal(Heartbeat.Instance, await t.Peer.ReceiveAsync());
        await Kit.AdvanceToAsync(clock, t.Conn, 14_999, () => 0);
        Assert.IsType<ConnState.Pending>(t.Conn.State.Value);
        Assert.Equal(new long[] { 5_000, 10_000 }, rec.Writes.Select(w => w.Time));
        await Kit.AdvanceToAsync(clock, t.Conn, 15_000, () => 0);
        Assert.Equal(new CloseReason.PeerUnresponsive(), await Kit.Within(t.Conn.Closed));
        Assert.Equal(15_000, clock.MonotonicMs());
    }

    [Fact]
    public async Task No_heartbeat_while_sending_and_peer_heartbeat_every_9s_keeps_connection()
    {
        var clock = new FakeClock();
        RecordingConn? rec = null;
        var t = await ConnUnderTest.CreateAsync(clock, wrap: c => rec = new RecordingConn(c, clock));
        t.Conn.Activate();
        for (var i = 0; i < 10; i++)
        {
            await Kit.AdvanceToAsync(clock, t.Conn, (i + 1) * 3_000L, () => t.Peer.FramesSent);
            t.Conn.Enqueue(new FileChunk(new TransferId(Bytes16.Of(new byte[16])), 0, (ulong)i, Bytes.Of([1])), Priority.Control);
            if (i % 3 == 2)
            {
                await t.Peer.SendAsync(Heartbeat.Instance);
            }

            await Kit.WaitUntilAsync(() => t.Conn.IsQuiescent(t.Peer.FramesSent), "quiescence");
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(i => i * 3_000L), rec!.Writes.Select(w => w.Time));
        for (var i = 0; i < 10; i++)
        {
            Assert.IsType<FileChunk>(await t.Peer.ReceiveAsync());
        }

        Assert.IsType<ConnState.Active>(t.Conn.State.Value);
    }
}
