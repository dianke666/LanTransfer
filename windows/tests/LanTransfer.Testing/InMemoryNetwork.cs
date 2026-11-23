using System.Threading.Channels;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

// 内存网络（设计 C5、任务 14.2），与 Kotlin 侧 InMemoryNetwork.kt 对应：多台“主机”各有一个 InMemoryTransport 和一组接口；
// TCP 连接是成对的内存管道（System.Threading.Channels），线上字节全部被捕获；UDP 按接口子网投递单播、定向广播与组播（含回环）。
// 可注入连接失败、端口占用、静默断网和 RST。

/// <summary>INetworkMonitor 替身：<see cref="Set"/> 改变活动接口集合；<see cref="FastPollingHistory"/> 记录 SetFastPolling 的调用。</summary>
public sealed class FakeNetworkMonitor(IEnumerable<LocalInterface>? initial = null) : INetworkMonitor
{
    private readonly ObservableState<ValueList<LocalInterface>> _state = new(new ValueList<LocalInterface>((initial ?? []).Distinct()));
    private readonly List<bool> _history = [];

    public IObservableState<ValueList<LocalInterface>> Interfaces => _state;

    public bool FastPolling { get; private set; }

    public IReadOnlyList<bool> FastPollingHistory
    {
        get
        {
            lock (_history)
            {
                return [.. _history];
            }
        }
    }

    public void Set(IEnumerable<LocalInterface> ifaces) => _state.Value = new ValueList<LocalInterface>(ifaces.Distinct());

    public void SetFastPolling(bool enabled)
    {
        lock (_history)
        {
            FastPolling = enabled;
            _history.Add(enabled);
        }
    }
}

/// <summary>注入的连接结果（优先于正常的路由与监听查找）。</summary>
public abstract record ConnectBehavior
{
    private ConnectBehavior()
    {
    }

    public sealed record Refuse : ConnectBehavior;

    public sealed record Unreachable : ConnectBehavior;

    /// <summary>对方不应答：等满超时后返回 Timeout（在网络的 TimeProvider 下）。</summary>
    public sealed record Hang : ConnectBehavior;

    public sealed record BindFail(bool VpnSuspected) : ConnectBehavior;
}

public sealed record ConnectAttempt(LocalInterface Via, SocketAddr Target);

public sealed record SentDatagram(string From, LocalInterface Via, Ipv4 Dst, int Port, Bytes Data);

/// <param name="time">Hang 行为等待超时所用的时间源；传入 FakeClock 时走虚拟时间。</param>
public sealed class InMemoryNetwork(TimeProvider? time = null)
{
    public static readonly Ipv4 MulticastGroup = Ipv4.Of(239, 255, 42, 58);
    public static readonly Ipv4 LimitedBroadcast = Ipv4.Of(255, 255, 255, 255);

    private readonly Lock _gate = new();
    private readonly List<InMemoryTransport> _hosts = [];
    private readonly List<InMemoryConnection> _connections = [];
    private readonly List<SentDatagram> _datagrams = [];
    private int _nextPort = 50_000;

    internal TimeProvider Time { get; } = time ?? TimeProvider.System;

    public IReadOnlyList<InMemoryConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections];
            }
        }
    }

    public IReadOnlyList<SentDatagram> Datagrams
    {
        get
        {
            lock (_gate)
            {
                return [.. _datagrams];
            }
        }
    }

    /// <summary>新建一台主机。<paramref name="reportUdpVia"/> 为 false 时模拟 Android（收到的数据报不带接收接口）。</summary>
    public InMemoryTransport Host(string name, IEnumerable<LocalInterface> interfaces, bool reportUdpVia = true)
    {
        lock (_gate)
        {
            var h = new InMemoryTransport(this, name, new FakeNetworkMonitor(interfaces), reportUdpVia);
            _hosts.Add(h);
            return h;
        }
    }

    internal int AllocatePort()
    {
        lock (_gate)
        {
            return _nextPort++;
        }
    }

    internal InMemoryTransport? HostOwning(Ipv4 ip)
    {
        lock (_gate)
        {
            return _hosts.FirstOrDefault(h => h.Monitor.Interfaces.Value.Any(i => i.Address == ip));
        }
    }

    internal InMemoryConnection Link(InMemoryTransport client, SocketAddr clientAddr, InMemoryTransport server, SocketAddr serverAddr)
    {
        var c = new InMemoryConnection(client.Name, server.Name, clientAddr, serverAddr);
        lock (_gate)
        {
            _connections.Add(c);
        }

        return c;
    }

    internal bool DeliverUdp(InMemoryTransport sender, int srcPort, LocalInterface via, Ipv4 dst, int dstPort, ReadOnlySpan<byte> bytes)
    {
        if (sender.UdpSendFails || !sender.Monitor.Interfaces.Value.Contains(via))
        {
            return false;
        }

        var data = Bytes.Of(bytes);
        List<InMemoryTransport> targets;
        lock (_gate)
        {
            _datagrams.Add(new SentDatagram(sender.Name, via, dst, dstPort, data));
            targets = [.. _hosts];
        }

        var broadcastLike = dst == via.Broadcast || dst == MulticastGroup || dst == LimitedBroadcast;
        foreach (var h in targets)
        {
            if (h.UdpEndpoint(dstPort) is not { } ep)
            {
                continue;
            }

            // 每台主机最多从 1 个接口收到同一份数据报；只有与发送接口处于同一链路（子网）的接口能收到
            var r = h.Monitor.Interfaces.Value.FirstOrDefault(r => r.Contains(via.Address) && (broadcastLike || dst == r.Address));
            if (r is null)
            {
                continue;
            }

            ep.Deliver(new Datagram(data, via.Address, srcPort, h.ReportUdpVia ? r : null));
        }

        return true;
    }
}

/// <summary>一台主机的 Transport。</summary>
public sealed class InMemoryTransport : ITransport
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, InMemoryListener> _listeners = new();
    private readonly Dictionary<int, InMemoryUdpEndpoint> _udp = new();
    private readonly List<ConnectAttempt> _attempts = [];

    internal InMemoryTransport(InMemoryNetwork network, string name, FakeNetworkMonitor monitor, bool reportUdpVia)
    {
        Network = network;
        Name = name;
        Monitor = monitor;
        ReportUdpVia = reportUdpVia;
    }

    public string Name { get; }

    public FakeNetworkMonitor Monitor { get; }

    /// <summary>被其他程序占用的端口（5.7、5.9、5.10）。</summary>
    public HashSet<int> BlockedTcpPorts { get; } = [];

    public HashSet<int> BlockedUdpPorts { get; } = [];

    /// <summary>所有 UDP 发送都失败（5.10）。</summary>
    public bool UdpSendFails { get; set; }

    /// <summary>所有 TCP 监听都失败，包括由系统分配的端口（5.9）。</summary>
    public bool ListenFails { get; set; }

    /// <summary>按目标注入连接结果；返回 null 时按正常路由处理。</summary>
    public Func<SocketAddr, ConnectBehavior?> ConnectBehavior { get; set; } = _ => null;

    /// <summary>每次 Connect 调用（用于检查“不向非本地地址发包”，2.5）。</summary>
    public IReadOnlyList<ConnectAttempt> ConnectAttempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    public IReadOnlySet<int> ListeningPorts
    {
        get
        {
            lock (_gate)
            {
                return _listeners.Keys.ToHashSet();
            }
        }
    }

    internal InMemoryNetwork Network { get; }

    internal bool ReportUdpVia { get; }

    internal InMemoryUdpEndpoint? UdpEndpoint(int port)
    {
        lock (_gate)
        {
            return _udp.GetValueOrDefault(port);
        }
    }

    internal void RemoveListener(int port)
    {
        lock (_gate)
        {
            _listeners.Remove(port);
        }
    }

    internal void RemoveUdp(int port)
    {
        lock (_gate)
        {
            _udp.Remove(port);
        }
    }

    private InMemoryListener? ListenerAt(int port)
    {
        lock (_gate)
        {
            return _listeners.GetValueOrDefault(port);
        }
    }

    public async Task<ConnectResult> ConnectAsync(LocalInterface via, Ipv4 ip, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var target = new SocketAddr(ip, port);
        lock (_gate)
        {
            _attempts.Add(new ConnectAttempt(via, target));
        }

        if (!Monitor.Interfaces.Value.Contains(via))
        {
            return new ConnectResult.BindFailed(VpnSuspected: false);
        }

        switch (ConnectBehavior(target))
        {
            case Testing.ConnectBehavior.Refuse:
                return new ConnectResult.Refused();
            case Testing.ConnectBehavior.Unreachable:
                return new ConnectResult.Unreachable();
            case Testing.ConnectBehavior.Hang:
                await Task.Delay(timeout, Network.Time, ct).ConfigureAwait(false);
                return new ConnectResult.Timeout();
            case Testing.ConnectBehavior.BindFail b:
                return new ConnectResult.BindFailed(b.VpnSuspected);
        }

        var server = Network.HostOwning(ip);
        if (server is null || !via.Contains(ip))
        {
            return new ConnectResult.Unreachable();
        }

        if (server.ListenerAt(port) is not { } listener)
        {
            return new ConnectResult.Refused();
        }

        var conn = Network.Link(this, new SocketAddr(via.Address, Network.AllocatePort()), server, target);
        return listener.Offer(conn.ServerEnd) ? new ConnectResult.Ok(conn.ClientEnd) : new ConnectResult.Refused();
    }

    public ITcpListener? Listen(IReadOnlyList<int> candidatePorts)
    {
        lock (_gate)
        {
            if (ListenFails)
            {
                return null;
            }

            foreach (var p in candidatePorts)
            {
                var actual = p == 0 ? Network.AllocatePort() : p;
                if (BlockedTcpPorts.Contains(actual) || _listeners.ContainsKey(actual))
                {
                    continue;
                }

                var l = new InMemoryListener(this, actual);
                _listeners[actual] = l;
                return l;
            }

            return null;
        }
    }

    public UdpOpenResult OpenUdp(int port)
    {
        lock (_gate)
        {
            if (BlockedUdpPorts.Contains(port) || _udp.ContainsKey(port))
            {
                return new UdpOpenResult.Failed($"端口 {port} 已被占用");
            }

            var e = new InMemoryUdpEndpoint(this, port);
            _udp[port] = e;
            return new UdpOpenResult.Ok(e);
        }
    }
}

internal sealed class InMemoryListener(InMemoryTransport owner, int port) : ITcpListener
{
    private readonly Channel<ITcpConn> _pending = Channel.CreateUnbounded<ITcpConn>();

    public int Port => port;

    public bool Offer(ITcpConn c) => _pending.Writer.TryWrite(c);

    public async ValueTask<ITcpConn?> AcceptAsync(CancellationToken ct = default)
    {
        while (await _pending.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_pending.Reader.TryRead(out var c))
            {
                return c;
            }
        }

        return null;
    }

    public void Close()
    {
        _pending.Writer.TryComplete();
        while (_pending.Reader.TryRead(out var c))
        {
            c.Close(); // 已排队但未被接受的连接一并关闭
        }

        owner.RemoveListener(port);
    }
}

internal sealed class InMemoryUdpEndpoint(InMemoryTransport owner, int localPort) : IUdpEndpoint
{
    private readonly Channel<Datagram> _inbox = Channel.CreateUnbounded<Datagram>();

    public void Deliver(Datagram d) => _inbox.Writer.TryWrite(d);

    public async ValueTask<Datagram?> ReceiveAsync(CancellationToken ct = default)
    {
        while (await _inbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_inbox.Reader.TryRead(out var d))
            {
                return d;
            }
        }

        return null;
    }

    public ValueTask<bool> SendAsync(LocalInterface via, Ipv4 dst, int port, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(owner.Network.DeliverUdp(owner, localPort, via, dst, port, data.Span));
    }

    public void Close()
    {
        _inbox.Writer.TryComplete();
        while (_inbox.Reader.TryRead(out _))
        {
        }

        owner.RemoveUdp(localPort);
    }
}

/// <summary>一条内存 TCP 连接：两端各一个 <see cref="ITcpConn"/>，并捕获两个方向的全部线上字节。</summary>
public sealed class InMemoryConnection
{
    private readonly Channel<byte[]> _toServer = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
    private readonly MemoryStream _fromClient = new();
    private readonly MemoryStream _fromServer = new();
    private volatile bool _blackholed;
    private volatile bool _reset;

    internal InMemoryConnection(string clientHost, string serverHost, SocketAddr clientAddr, SocketAddr serverAddr)
    {
        ClientHost = clientHost;
        ServerHost = serverHost;
        ClientAddr = clientAddr;
        ServerAddr = serverAddr;
        ClientEnd = new InMemoryTcpConn(this, incoming: _toClient, outgoing: _toServer, capture: _fromClient, local: clientAddr, remote: serverAddr);
        ServerEnd = new InMemoryTcpConn(this, incoming: _toServer, outgoing: _toClient, capture: _fromServer, local: serverAddr, remote: clientAddr);
    }

    public string ClientHost { get; }

    public string ServerHost { get; }

    public SocketAddr ClientAddr { get; }

    public SocketAddr ServerAddr { get; }

    public InMemoryTcpConn ClientEnd { get; }

    public InMemoryTcpConn ServerEnd { get; }

    public bool IsOpen => !ClientEnd.IsClosed && !ServerEnd.IsClosed && !_reset;

    internal bool Blackholed => _blackholed;

    internal bool IsReset => _reset;

    /// <summary>客户端写出的全部字节（含静默断网后丢失的部分）。</summary>
    public byte[] BytesFromClient()
    {
        lock (_fromClient)
        {
            return _fromClient.ToArray();
        }
    }

    public byte[] BytesFromServer()
    {
        lock (_fromServer)
        {
            return _fromServer.ToArray();
        }
    }

    /// <summary>静默断网（网线拔出、接口消失）：之后写出的字节全部丢失，双方都读不到 EOF，只能靠心跳超时发现（18.6）。</summary>
    public void Blackhole() => _blackholed = true;

    /// <summary>RST：双方立即读到 -1，在途数据丢失，之后的写入失败（18.8）。</summary>
    public void Reset()
    {
        _reset = true;
        _toServer.Writer.TryComplete();
        _toClient.Writer.TryComplete();
    }
}

public sealed class InMemoryTcpConn : ITcpConn
{
    private readonly InMemoryConnection _connection;
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private readonly MemoryStream _capture;
    private byte[]? _pending;
    private int _pendingOff;
    private volatile bool _closed;

    internal InMemoryTcpConn(InMemoryConnection connection, Channel<byte[]> incoming, Channel<byte[]> outgoing, MemoryStream capture, SocketAddr local, SocketAddr remote)
    {
        _connection = connection;
        _incoming = incoming;
        _outgoing = outgoing;
        _capture = capture;
        Local = local;
        Remote = remote;
    }

    public SocketAddr Local { get; }

    public SocketAddr Remote { get; }

    public bool IsClosed => _closed;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0)
        {
            throw new ArgumentException("buffer must not be empty", nameof(buffer));
        }

        if (_closed || _connection.IsReset)
        {
            return -1;
        }

        var chunk = _pending;
        if (chunk is null)
        {
            while (true)
            {
                if (_incoming.Reader.TryRead(out chunk))
                {
                    break;
                }

                if (!await _incoming.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return -1;
                }
            }

            _pendingOff = 0;
        }

        if (_closed || _connection.IsReset)
        {
            return -1;
        }

        var n = Math.Min(buffer.Length, chunk.Length - _pendingOff);
        chunk.AsSpan(_pendingOff, n).CopyTo(buffer.Span);
        _pendingOff += n;
        _pending = _pendingOff == chunk.Length ? null : chunk;
        return n;
    }

    public ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_closed || _connection.IsReset)
        {
            return ValueTask.FromResult(false);
        }

        if (data.Length == 0)
        {
            return ValueTask.FromResult(true);
        }

        var copy = data.ToArray();
        lock (_capture)
        {
            _capture.Write(copy);
        }

        if (_connection.Blackholed)
        {
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(_outgoing.Writer.TryWrite(copy));
    }

    /// <summary>正常关闭：对方读完已发出的数据后读到 EOF；对方之后的写入失败。</summary>
    public void Close()
    {
        _closed = true;
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        while (_incoming.Reader.TryRead(out _))
        {
        }
    }
}
