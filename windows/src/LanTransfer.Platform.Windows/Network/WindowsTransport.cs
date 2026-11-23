using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Platform.Windows.Network;

/// <summary>
/// Windows 套接字传输实现（设计 C5.1、C7.1）：唯一允许创建套接字的类。
/// TCP 出站先绑定本地接口地址并设 SO_SNDBUF = 512 KiB；监听设 ExclusiveAddressUse；
/// UDP 接收开启 PacketInformation 并关闭 SIO_UDP_CONNRESET，按接口独立发送定向广播与组播。
/// </summary>
public sealed class WindowsTransport(INetworkMonitor network) : ITransport
{
    public const int SendBufferBytes = 512 * 1024;
    private const int SioUdpConnReset = -1744830452;

    public async Task<ConnectResult> ConnectAsync(LocalInterface via, Ipv4 ip, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            sock.NoDelay = true;
            sock.SendBufferSize = SendBufferBytes;
            try
            {
                sock.Bind(new IPEndPoint(ToIp(via.Address), 0));
            }
            catch (SocketException)
            {
                sock.Dispose();
                return new ConnectResult.BindFailed(VpnSuspected: false);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await sock.ConnectAsync(new IPEndPoint(ToIp(ip), port), timeoutCts.Token).ConfigureAwait(false);
                return new ConnectResult.Ok(new WindowsTcpConn(sock));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sock.Dispose();
                return new ConnectResult.Timeout();
            }
            catch (SocketException ex)
            {
                sock.Dispose();
                return ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => new ConnectResult.Refused(),
                    SocketError.TimedOut => new ConnectResult.Timeout(),
                    _ => new ConnectResult.Unreachable(),
                };
            }
        }
        catch (OperationCanceledException)
        {
            sock.Dispose();
            throw;
        }
        catch
        {
            sock.Dispose();
            return new ConnectResult.Unreachable();
        }
    }

    public ITcpListener? Listen(IReadOnlyList<int> candidatePorts)
    {
        foreach (var port in candidatePorts)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                sock.ExclusiveAddressUse = true;
                sock.Bind(new IPEndPoint(IPAddress.Any, port));
                sock.Listen(64);
                return new WindowsTcpListener(sock);
            }
            catch
            {
                sock.Dispose();
            }
        }

        return null;
    }

    public UdpOpenResult OpenUdp(int port)
    {
        var recv = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            recv.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            recv.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            try
            {
                recv.IOControl(SioUdpConnReset, [0], null);
            }
            catch
            {
                // 忽略旧环境或受限环境下的 IOControl 异常
            }

            recv.Bind(new IPEndPoint(IPAddress.Any, port));
            recv.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            var ep = new WindowsUdpEndpoint(recv, network);
            ep.RefreshMulticastMemberships();
            return new UdpOpenResult.Ok(ep);
        }
        catch (Exception ex)
        {
            recv.Dispose();
            return new UdpOpenResult.Failed(ex.Message);
        }
    }

    internal static IPAddress ToIp(Ipv4 ip) =>
        new([(byte)(ip.Bits >> 24), (byte)(ip.Bits >> 16), (byte)(ip.Bits >> 8), (byte)ip.Bits]);

    internal static Ipv4 FromIp(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return Ipv4.Of(b[0], b[1], b[2], b[3]);
    }

    private sealed class WindowsTcpConn : ITcpConn
    {
        private readonly Socket _sock;
        private int _closed;

        public WindowsTcpConn(Socket sock)
        {
            _sock = sock;
            _sock.NoDelay = true;
            _sock.SendBufferSize = SendBufferBytes;
            var l = (IPEndPoint)sock.LocalEndPoint!;
            var r = (IPEndPoint)sock.RemoteEndPoint!;
            Local = new SocketAddr(FromIp(l.Address), l.Port);
            Remote = new SocketAddr(FromIp(r.Address), r.Port);
        }

        public SocketAddr Local { get; }

        public SocketAddr Remote { get; }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                var n = await _sock.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                return n == 0 ? -1 : n;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return -1;
            }
        }

        public async ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            try
            {
                var sent = 0;
                while (sent < data.Length)
                {
                    var n = await _sock.SendAsync(data[sent..], SocketFlags.None, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        return false;
                    }

                    sent += n;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                try
                {
                    _sock.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                }

                _sock.Dispose();
            }
        }
    }

    private sealed class WindowsTcpListener : ITcpListener
    {
        private readonly Socket _sock;
        private int _closed;

        public WindowsTcpListener(Socket sock)
        {
            _sock = sock;
            Port = ((IPEndPoint)sock.LocalEndPoint!).Port;
        }

        public int Port { get; }

        public async ValueTask<ITcpConn?> AcceptAsync(CancellationToken ct = default)
        {
            try
            {
                var client = await _sock.AcceptAsync(ct).ConfigureAwait(false);
                return new WindowsTcpConn(client);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _sock.Dispose();
            }
        }
    }

    private sealed class WindowsUdpEndpoint : IUdpEndpoint
    {
        private static readonly IPAddress MulticastAddr = IPAddress.Parse(ProtocolConstants.MulticastGroup);

        private readonly Socket _recv;
        private readonly INetworkMonitor _network;
        private readonly ConcurrentDictionary<Ipv4, Socket> _senders = new();
        private readonly HashSet<Ipv4> _joined = [];
        private readonly Lock _joinGate = new();
        private readonly IDisposable _ifaceSub;
        private readonly byte[] _recvBuf = new byte[2048];
        private int _closed;

        public WindowsUdpEndpoint(Socket recv, INetworkMonitor network)
        {
            _recv = recv;
            _network = network;
            _ifaceSub = network.Interfaces.Subscribe(_ => RefreshMulticastMemberships());
        }

        public void RefreshMulticastMemberships()
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            lock (_joinGate)
            {
                foreach (var iface in _network.Interfaces.Value)
                {
                    if (_joined.Contains(iface.Address))
                    {
                        continue;
                    }

                    try
                    {
                        _recv.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddr, ToIp(iface.Address)));
                        _joined.Add(iface.Address);
                    }
                    catch
                    {
                        // 某些虚拟网卡可能不支持组播，定向广播仍可用
                    }
                }
            }
        }

        public async ValueTask<Datagram?> ReceiveAsync(CancellationToken ct = default)
        {
            while (Volatile.Read(ref _closed) == 0)
            {
                try
                {
                    var res = await _recv.ReceiveMessageFromAsync(_recvBuf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
                    if (res.RemoteEndPoint is not IPEndPoint remote || remote.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var src = FromIp(remote.Address);
                    var via = ResolveVia(res.PacketInformation.Interface, src);
                    return new Datagram(Bytes.Of(_recvBuf.AsSpan(0, res.ReceivedBytes)), src, remote.Port, via);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    if (Volatile.Read(ref _closed) != 0)
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        public async ValueTask<bool> SendAsync(LocalInterface via, Ipv4 dst, int port, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return false;
            }

            try
            {
                var sock = _senders.GetOrAdd(via.Address, CreateSender);
                await sock.SendToAsync(data, SocketFlags.None, new IPEndPoint(ToIp(dst), port), ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (_senders.TryRemove(via.Address, out var dead))
                {
                    dead.Dispose();
                }

                return false;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _ifaceSub.Dispose();
                _recv.Dispose();
                foreach (var s in _senders.Values)
                {
                    s.Dispose();
                }

                _senders.Clear();
            }
        }

        private static Socket CreateSender(Ipv4 localIp)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            s.Bind(new IPEndPoint(ToIp(localIp), 0));
            try
            {
                var ipAddr = ToIp(localIp);
                var idx = FindInterfaceIndex(ipAddr);
                if (idx >= 0)
                {
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(idx));
                }
            }
            catch
            {
            }

            return s;
        }

        private LocalInterface? ResolveVia(int interfaceIndex, Ipv4 src)
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var v4 = ni.GetIPProperties().GetIPv4Properties();
                    if (v4 is not null && v4.Index == interfaceIndex)
                    {
                        var match = _network.Interfaces.Value.FirstOrDefault(i => string.Equals(i.Name, ni.Name, StringComparison.OrdinalIgnoreCase));
                        if (match is not null)
                        {
                            return match;
                        }
                    }
                }
            }
            catch
            {
            }

            return _network.Interfaces.Value.FirstOrDefault(i => AddressRules.AcceptsInbound(src, i, [i]));
        }

        private static int FindInterfaceIndex(IPAddress addr)
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var props = ni.GetIPProperties();
                    if (props.UnicastAddresses.Any(u => u.Address.Equals(addr)))
                    {
                        return props.GetIPv4Properties()?.Index ?? -1;
                    }
                }
            }
            catch
            {
            }

            return -1;
        }
    }
}
