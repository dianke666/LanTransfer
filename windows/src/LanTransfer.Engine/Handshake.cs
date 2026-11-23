using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 连接前导与 Noise_XX 握手（设计 C1.5、C1.6，规范第 5、6 节），与 Kotlin 侧 Handshake.kt 对应。
// 握手消息在 TCP 上封装为 len:u16 ‖ noise_message。

/// <summary>带缓冲的读取器：握手阶段多读到的字节（对方紧接着发来的 record）留给连接的读循环。</summary>
internal sealed class ConnReader(ITcpConn conn)
{
    private const int BufSize = 64 * 1024;
    private readonly byte[] _buf = new byte[BufSize];
    private int _pos;
    private int _lim;

    /// <summary>读满 <paramref name="n"/> 字节；对方关闭时返回 null。</summary>
    public async Task<byte[]?> ReadExactlyAsync(int n, CancellationToken ct)
    {
        var output = new byte[n];
        var got = 0;
        while (got < n)
        {
            if (_pos == _lim)
            {
                var k = await conn.ReadAsync(_buf, ct).ConfigureAwait(false);
                if (k < 0)
                {
                    return null;
                }

                _pos = 0;
                _lim = k;
            }

            var take = Math.Min(n - got, _lim - _pos);
            _buf.AsSpan(_pos, take).CopyTo(output.AsSpan(got));
            _pos += take;
            got += take;
        }

        return output;
    }

    /// <summary>先交出缓冲中剩余的字节，再从连接读取；EOF 返回 -1。</summary>
    public ValueTask<int> ReadSomeAsync(Memory<byte> output, CancellationToken ct)
    {
        if (_pos < _lim)
        {
            var take = Math.Min(output.Length, _lim - _pos);
            _buf.AsSpan(_pos, take).CopyTo(output.Span);
            _pos += take;
            return ValueTask.FromResult(take);
        }

        return conn.ReadAsync(output, ct);
    }
}

/// <summary>截止时刻之前完成的结果。</summary>
internal readonly record struct Timed<T>(bool Expired, T Value);

internal static class Deadline
{
    /// <summary>在单调时钟的 <paramref name="deadlineMs"/> 之前执行 <paramref name="block"/>；已过期或超时返回 Expired。超时经 <see cref="IClock.Time"/> 计时。</summary>
    public static async Task<Timed<T>> RunAsync<T>(IClock clock, long deadlineMs, Func<CancellationToken, Task<T>> block, CancellationToken ct)
    {
        var remaining = deadlineMs - clock.MonotonicMs();
        if (remaining <= 0)
        {
            return new(true, default!);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining), clock.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return new(false, await block(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new(true, default!);
        }
    }
}

/// <summary>本机在握手中出示的身份。</summary>
internal sealed record LocalIdentity(DeviceId DeviceId, X25519KeyPair KeyPair, string DisplayName, DeviceType DeviceType);

/// <summary>发起方读完 msg2 后的决定。</summary>
internal abstract record Msg2Decision
{
    public sealed record Proceed(Intent Intent) : Msg2Decision;

    /// <summary>不发送 msg3，直接关闭；Failure 为 null 表示已并入同一对端的其他连接（不算失败）。</summary>
    public sealed record Stop(OpenFailure? Failure) : Msg2Decision;
}

internal abstract record HandshakeOutcome
{
    public sealed record Done(PeerIdentity Peer, Intent Intent, ProtocolVersion Version, RecordCipher Send, RecordCipher Receive, ConnReader Reader) : HandshakeOutcome;

    public sealed record Failed(OpenFailure Failure) : HandshakeOutcome;

    /// <summary>发起方在 msg2 之后按 Stop（Failure 为 null）停止。</summary>
    public sealed record Merged(PeerIdentity Peer) : HandshakeOutcome;
}

internal static class Handshake
{
    /// <summary>
    /// 发起方：前导 → msg1 → msg2（<paramref name="decide"/> 检查对方身份并选择 intent）→ msg3。失败时不关闭连接，由调用方关闭。
    /// 前导在截止时刻前未完成报 NoVersionResponse，之后的步骤超时报 Timeout。
    /// </summary>
    public static async Task<HandshakeOutcome> InitiateAsync(
        ITcpConn conn,
        LocalIdentity local,
        IEphemeralKeySource keys,
        IClock clock,
        long deadlineMs,
        ProtocolVersion version,
        Func<PeerIdentity, Msg2Decision> decide,
        CancellationToken ct)
    {
        var reader = new ConnReader(conn);
        var mine = PreambleCodec.Encode(version);
        if (!await conn.WriteAsync(mine, ct).ConfigureAwait(false))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        var pre = await Deadline.RunAsync(clock, deadlineMs, t => reader.ReadExactlyAsync(PreambleCodec.Size, t), ct).ConfigureAwait(false);
        if (pre.Expired)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.NoVersionResponse());
        }

        if (pre.Value is not { } theirs)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        if (Negotiate(version, theirs) is not ProtocolVersion negotiated)
        {
            return new HandshakeOutcome.Failed(NegotiationFailure(version, theirs));
        }

        var noise = new NoiseXX(Role.Initiator, local.KeyPair, NoiseXX.Prologue(mine, theirs), keys);
        if (!await WriteHandshakeAsync(conn, noise.WriteMessage([]), ct).ConfigureAwait(false))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        var msg2 = await ReadHandshakeAsync(reader, clock, deadlineMs, ct).ConfigureAwait(false);
        if (msg2.Failure is { } f2)
        {
            return new HandshakeOutcome.Failed(f2);
        }

        byte[] payload;
        try
        {
            payload = noise.ReadMessage(msg2.Bytes!);
        }
        catch (HandshakeAuthException)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.HandshakeAuthFailed());
        }

        var id = HandshakeIdentityCodec.Decode(payload, expectIntent: false, negotiated);
        if (id is null || !DisplayNameRules.IsAcceptableRemote(id.DisplayName))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.HandshakeAuthFailed());
        }

        var peer = new PeerIdentity(id.DeviceId, id.DisplayName, id.DeviceType, Bytes.Of(noise.RemoteStatic!));
        Intent intent;
        switch (decide(peer))
        {
            case Msg2Decision.Stop s:
                return s.Failure is { } f ? new HandshakeOutcome.Failed(f) : new HandshakeOutcome.Merged(peer);
            case Msg2Decision.Proceed p:
                intent = p.Intent;
                break;
            default:
                throw new InvalidOperationException("unknown decision");
        }

        var msg3 = noise.WriteMessage(HandshakeIdentityCodec.Encode(new HandshakeIdentity(local.DeviceId, local.DisplayName, local.DeviceType, intent)));
        if (!await WriteHandshakeAsync(conn, msg3, ct).ConfigureAwait(false))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        var (send, receive) = noise.Split();
        return new HandshakeOutcome.Done(peer, intent, negotiated, send, receive, reader);
    }

    /// <summary>
    /// 接收方：前导 → msg1（载荷必须为空）→ msg2 → msg3（必须含 intent）。失败时不关闭连接。
    /// 前导 magic 不符、对方提前关闭报 ProtocolError（静默关闭）；版本不兼容、握手认证失败需要在接收方提示（E1）。
    /// </summary>
    public static async Task<HandshakeOutcome> RespondAsync(
        ITcpConn conn,
        LocalIdentity local,
        IEphemeralKeySource keys,
        IClock clock,
        long deadlineMs,
        ProtocolVersion version,
        CancellationToken ct)
    {
        var reader = new ConnReader(conn);
        var mine = PreambleCodec.Encode(version);
        if (!await conn.WriteAsync(mine, ct).ConfigureAwait(false))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        var pre = await Deadline.RunAsync(clock, deadlineMs, t => reader.ReadExactlyAsync(PreambleCodec.Size, t), ct).ConfigureAwait(false);
        if (pre.Expired)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.NoVersionResponse());
        }

        if (pre.Value is not { } theirs)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        if (Negotiate(version, theirs) is not ProtocolVersion negotiated)
        {
            return new HandshakeOutcome.Failed(NegotiationFailure(version, theirs));
        }

        var noise = new NoiseXX(Role.Responder, local.KeyPair, NoiseXX.Prologue(theirs, mine), keys);
        var msg1 = await ReadHandshakeAsync(reader, clock, deadlineMs, ct).ConfigureAwait(false);
        if (msg1.Failure is { } f1)
        {
            return new HandshakeOutcome.Failed(f1);
        }

        try
        {
            if (noise.ReadMessage(msg1.Bytes!).Length != 0)
            {
                return new HandshakeOutcome.Failed(new OpenFailure.HandshakeAuthFailed());
            }
        }
        catch (HandshakeAuthException)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.HandshakeAuthFailed());
        }

        var msg2 = noise.WriteMessage(HandshakeIdentityCodec.Encode(new HandshakeIdentity(local.DeviceId, local.DisplayName, local.DeviceType)));
        if (!await WriteHandshakeAsync(conn, msg2, ct).ConfigureAwait(false))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.ProtocolError());
        }

        var msg3 = await ReadHandshakeAsync(reader, clock, deadlineMs, ct).ConfigureAwait(false);
        if (msg3.Failure is { } f3)
        {
            return new HandshakeOutcome.Failed(f3);
        }

        byte[] payload;
        try
        {
            payload = noise.ReadMessage(msg3.Bytes!);
        }
        catch (HandshakeAuthException)
        {
            return new HandshakeOutcome.Failed(new OpenFailure.HandshakeAuthFailed());
        }

        var id = HandshakeIdentityCodec.Decode(payload, expectIntent: true, negotiated);
        if (id?.Intent is not { } intent || !DisplayNameRules.IsAcceptableRemote(id.DisplayName))
        {
            return new HandshakeOutcome.Failed(new OpenFailure.HandshakeAuthFailed());
        }

        var peer = new PeerIdentity(id.DeviceId, id.DisplayName, id.DeviceType, Bytes.Of(noise.RemoteStatic!));
        var (send, receive) = noise.Split();
        return new HandshakeOutcome.Done(peer, intent, negotiated, send, receive, reader);
    }

    private static ProtocolVersion? Negotiate(ProtocolVersion local, byte[] theirs) =>
        PreambleCodec.Decode(theirs) is { } remote && PreambleCodec.Negotiate(local, remote) is Negotiation.Compatible c ? c.Version : null;

    private static OpenFailure NegotiationFailure(ProtocolVersion local, byte[] theirs) =>
        PreambleCodec.Decode(theirs) is { } remote ? new OpenFailure.VersionIncompatible(local, remote) : new OpenFailure.ProtocolError();

    private static ValueTask<bool> WriteHandshakeAsync(ITcpConn conn, byte[] msg, CancellationToken ct)
    {
        var output = new byte[2 + msg.Length];
        output[0] = (byte)(msg.Length >> 8);
        output[1] = (byte)msg.Length;
        msg.CopyTo(output, 2);
        return conn.WriteAsync(output, ct);
    }

    private readonly record struct HsRead(byte[]? Bytes, OpenFailure? Failure);

    /// <summary>读一条握手消息：超时报 Timeout，对方关闭或长度为 0 报 ProtocolError。</summary>
    private static async Task<HsRead> ReadHandshakeAsync(ConnReader reader, IClock clock, long deadlineMs, CancellationToken ct)
    {
        var r = await Deadline.RunAsync(
            clock,
            deadlineMs,
            async t =>
            {
                var len = await reader.ReadExactlyAsync(2, t).ConfigureAwait(false);
                if (len is null)
                {
                    return null;
                }

                var n = (len[0] << 8) | len[1];
                return n == 0 ? [] : await reader.ReadExactlyAsync(n, t).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        if (r.Expired)
        {
            return new HsRead(null, new OpenFailure.Timeout());
        }

        return r.Value is { Length: > 0 } bytes ? new HsRead(bytes, null) : new HsRead(null, new OpenFailure.ProtocolError());
    }
}
