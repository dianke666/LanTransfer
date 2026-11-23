using System.Collections.Concurrent;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

// 会话层测试的公共装置（与 Kotlin 侧 SessionTestKit.kt 对应）：内存网络上的测试设备、直接构造的 Connection 与只持有密钥的“原始对端”。
// C# 的连接循环运行在线程池上，测试推进 FakeClock 之前要等连接稳定（Connection.IsQuiescent），否则续体可能在错误的虚拟时刻运行。

internal static class Kit
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static LocalInterface Iface(string name, byte a, byte b, byte c, byte d, int prefix = 24) =>
        new(name, IfaceKind.Wifi, Ipv4.Of(a, b, c, d), prefix);

    public static DeviceId DeviceIdOf(int b) => new(Bytes16.Of(Enumerable.Repeat((byte)b, 16).ToArray()));

    public static X25519KeyPair KeyPairOf(int seed) => X25519KeyPair.FromPrivateKey(Enumerable.Range(0, 32).Select(i => (byte)((seed * 31) + i + 1)).ToArray());

    /// <summary>轮询等待条件成立（真实时间上限）。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what = "condition", int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"timed out waiting for {what}");
            }

            await Task.Delay(1, Ct);
        }
    }

    public static Task<T> Within<T>(Task<T> task, int timeoutMs = 10_000) => task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), Ct);

    /// <summary>短暂的真实等待，用于确认某件事“没有发生”。</summary>
    public static Task Settle(int ms = 50) => Task.Delay(ms, Ct);

    /// <summary>
    /// 把 FakeClock 推进到 <paramref name="target"/>：每次只推进到下一个到期的定时器，并等待 <paramref name="conn"/> 稳定后再继续，
    /// 使连接里的心跳、超时都在精确的虚拟时刻发生。
    /// </summary>
    public static async Task AdvanceToAsync(FakeClock clock, Connection conn, long target, Func<long> framesReceived)
    {
        await WaitUntilAsync(() => conn.IsQuiescent(framesReceived()), "quiescence");
        while (clock.MonotonicMs() < target)
        {
            var next = Math.Min(target, clock.NextTimerDueMs ?? target);
            clock.AdvanceBy(Math.Max(0, next - clock.MonotonicMs()));
            await WaitUntilAsync(() => conn.IsQuiescent(framesReceived()), "quiescence");
        }
    }

    /// <summary>在内存中完成一次 NoiseXX，返回（发起方发送, 发起方接收, 接收方发送, 接收方接收）。</summary>
    public static RecordCipher[] CipherQuad()
    {
        var prologue = NoiseXX.Prologue(new byte[6], new byte[6]);
        var i = new NoiseXX(Role.Initiator, KeyPairOf(101), prologue, EphemeralKeySource.Secure);
        var r = new NoiseXX(Role.Responder, KeyPairOf(102), prologue, EphemeralKeySource.Secure);
        r.ReadMessage(i.WriteMessage([]));
        i.ReadMessage(r.WriteMessage([]));
        r.ReadMessage(i.WriteMessage([]));
        var (iSend, iRecv) = i.Split();
        var (rSend, rRecv) = r.Split();
        return [iSend, iRecv, rSend, rRecv];
    }
}

/// <summary>内存网络上的一台设备：SessionManager + 内存信任存储 + 可写的地址簿。设备 ID 的 16 个字节都是 idByte，便于控制大小关系。</summary>
internal sealed class TestDevice
{
    public TestDevice(
        string name,
        int idByte,
        LocalInterface iface,
        InMemoryNetwork net,
        IClock clock,
        DeviceType type = DeviceType.Android,
        ProtocolVersion? version = null,
        SessionTimings? timings = null,
        DeviceIdentity? identityOverride = null)
    {
        Name = name;
        Iface = iface;
        Type = type;
        Transport = net.Host(name, [iface]);
        Identity = identityOverride ?? new DeviceIdentity(Kit.DeviceIdOf(idByte), Kit.KeyPairOf(idByte), name);
        Manager = new SessionManager(
            Transport,
            Transport.Monitor,
            Identity,
            type,
            Trust,
            clock,
            EphemeralKeySource.Secure,
            addressBook: id => AddressBook.TryGetValue(id, out var a) ? a : null,
            version: version,
            timings: timings);
    }

    public string Name { get; }

    public LocalInterface Iface { get; }

    public DeviceType Type { get; }

    public InMemoryTransport Transport { get; }

    public DeviceIdentity Identity { get; }

    public DeviceId Id => Identity.DeviceId;

    public Bytes PublicKey => Identity.KeyPair.PublicKey;

    public InMemoryTrustStore Trust { get; } = new();

    public ConcurrentDictionary<DeviceId, SocketAddr> AddressBook { get; } = new();

    public SessionManager Manager { get; }

    public int Port { get; private set; }

    public SocketAddr Address => new(Iface.Address, Port);

    public int Listen()
    {
        Port = Assert.IsType<ListenResult.Ok>(Manager.StartListening()).Port;
        return Port;
    }

    public TrustedDevice Record(Bytes? publicKey = null, string? displayName = null) =>
        new(Id, displayName ?? Name, Type, publicKey ?? PublicKey, AutoAccept: false, PairedAtUtcMs: 1);

    /// <summary>把 <paramref name="other"/> 记为可信设备（key 默认是它的真实公钥），并把它的地址写进地址簿。</summary>
    public void Trusts(TestDevice other, Bytes? key = null, string? displayName = null)
    {
        Trust.Upsert(other.Record(key, displayName));
        Knows(other);
    }

    public void Knows(TestDevice other) => AddressBook[other.Id] = other.Address;
}

/// <summary>只持有密钥的对端：可以发送任意消息或任意字节，并解密读取被测连接写出的内容。</summary>
internal sealed class RawPeer(ITcpConn sock, RecordCipher send, RecordCipher recv)
{
    private readonly RecordReader _records = recv.OpenStream();
    private readonly FrameDecoder _decoder = new();
    private readonly Queue<ProtocolMessage> _queue = new();

    public ITcpConn Sock => sock;

    /// <summary>已发出的完整帧数（供 IsQuiescent 判断对方是否已处理完）。</summary>
    public long FramesSent { get; private set; }

    public async Task<bool> SendAsync(ProtocolMessage m)
    {
        var ok = await sock.WriteAsync(send.Seal(FrameEncoder.EncodeOrThrow(m)), Kit.Ct);
        if (ok)
        {
            FramesSent++;
        }

        return ok;
    }

    /// <summary>把任意明文封装为合法 record 发出（用于构造解码错误）。</summary>
    public ValueTask<bool> SendPlainAsync(byte[] plain) => sock.WriteAsync(send.Seal(plain), Kit.Ct);

    public ValueTask<bool> SendRawAsync(byte[] bytes) => sock.WriteAsync(bytes, Kit.Ct);

    /// <summary>下一条消息；对方关闭或记录层出错时返回 null。超过 <paramref name="timeoutMs"/>（真实时间）仍未收到时报错。</summary>
    public async Task<ProtocolMessage?> ReceiveAsync(int timeoutMs = 10_000)
    {
        var buf = new byte[64 * 1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Kit.Ct);
        cts.CancelAfter(timeoutMs);
        while (_queue.Count == 0)
        {
            var n = await sock.ReadAsync(buf, cts.Token);
            if (n < 0)
            {
                return null;
            }

            var r = _records.Feed(buf.AsSpan(0, n));
            foreach (var c in r.Chunks)
            {
                foreach (var m in _decoder.Feed(c.Span).Messages)
                {
                    _queue.Enqueue(m);
                }
            }

            if (r.IntegrityError)
            {
                return null;
            }
        }

        return _queue.Dequeue();
    }
}

/// <summary>记录每次写入时刻（单调时钟）与长度的 ITcpConn 包装。写循环每次写出 1 个 Frame 封装成的全部 record。</summary>
internal sealed class RecordingConn(ITcpConn inner, IClock clock) : ITcpConn
{
    /// <summary>HEARTBEAT 帧（7 字节帧头、空载荷）封装成 1 条 record 后的长度：2 + 7 + 16。</summary>
    public const int HeartbeatWireLength = 25;

    private readonly List<(long Time, int Length)> _writes = [];

    public IReadOnlyList<(long Time, int Length)> Writes
    {
        get
        {
            lock (_writes)
            {
                return [.. _writes];
            }
        }
    }

    public SocketAddr Local => inner.Local;

    public SocketAddr Remote => inner.Remote;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);

    public ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        lock (_writes)
        {
            _writes.Add((clock.MonotonicMs(), data.Length));
        }

        return inner.WriteAsync(data, ct);
    }

    public void Close() => inner.Close();
}

/// <summary>每次写入都要等测试放行 1 次的 ITcpConn 包装（检查写循环的选择顺序）。</summary>
internal sealed class GatedConn(ITcpConn inner) : ITcpConn
{
    private readonly SemaphoreSlim _permits = new(0);
    private int _entered;
    private int _completed;

    public int Entered => Volatile.Read(ref _entered);

    public int Completed => Volatile.Read(ref _completed);

    public SocketAddr Local => inner.Local;

    public SocketAddr Remote => inner.Remote;

    public void Release() => _permits.Release();

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);

    public async ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _entered);
        await _permits.WaitAsync(ct);
        var ok = await inner.WriteAsync(data, ct);
        Interlocked.Increment(ref _completed);
        return ok;
    }

    public void Close() => inner.Close();
}

/// <summary>被测 Connection 与对面的 RawPeer，经内存网络上的一条 TCP 连接相连。</summary>
internal sealed record ConnUnderTest(Connection Conn, RawPeer Peer, ITcpConn Wire)
{
    public static async Task<ConnUnderTest> CreateAsync(IClock clock, Role role = Role.Responder, SessionTimings? timings = null, Func<ITcpConn, ITcpConn>? wrap = null)
    {
        var net = new InMemoryNetwork(clock.Time);
        var ia = Kit.Iface("wlan0", 192, 168, 1, 2);
        var ib = Kit.Iface("wlan0", 192, 168, 1, 3);
        var a = net.Host("peer", [ia]);
        var b = net.Host("local", [ib]);
        var listener = b.Listen([42580])!;
        var accepted = listener.AcceptAsync(Kit.Ct).AsTask();
        var peerSock = Assert.IsType<ConnectResult.Ok>(await a.ConnectAsync(ia, ib.Address, 42580, TimeSpan.FromSeconds(1), Kit.Ct)).Conn;
        var raw = (await accepted)!;
        var localSock = wrap is null ? raw : wrap(raw);
        var q = Kit.CipherQuad();
        var (mySend, myRecv, peerSend, peerRecv) = role == Role.Responder ? (q[2], q[3], q[0], q[1]) : (q[0], q[1], q[2], q[3]);
        var conn = new Connection(localSock, new ConnReader(localSock), role, ProtocolVersion.Current, mySend, myRecv, clock, timings);
        conn.Start();
        return new ConnUnderTest(conn, new RawPeer(peerSock, peerSend, peerRecv), localSock);
    }

    /// <summary>读出连接已交付的全部消息（连接必须已关闭）。</summary>
    public async Task<List<ProtocolMessage>> DrainIncomingAsync()
    {
        var list = new List<ProtocolMessage>();
        await foreach (var m in Conn.Incoming.ReadAllAsync(Kit.Ct))
        {
            list.Add(m);
        }

        return list;
    }
}
