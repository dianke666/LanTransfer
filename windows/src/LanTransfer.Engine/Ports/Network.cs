using LanTransfer.Domain;
using LanTransfer.Protocol;

namespace LanTransfer.Engine.Ports;

// C5.1 网络端口，与 Kotlin 侧 com.lantransfer.engine.ports.Network.kt 同名镜像（接口加 I 前缀）。
// ITransport 是唯一能创建套接字的地方，只由 LocalNetworkGuard（AddressRules.Classify 返回 Local 之后）调用。
// 不提供任何按主机名连接的接口（2.7）。可能失败的方法都返回结果类型，不向引擎层抛出平台异常；
// 只有调用方取消（CancellationToken）时抛出 OperationCanceledException。

/// <summary>套接字地址。</summary>
public readonly record struct SocketAddr(Ipv4 Ip, int Port)
{
    public override string ToString() => $"{Ip}:{Port}";
}

public interface INetworkMonitor
{
    /// <summary>只包含带 Private_IPv4_Address 的活动接口（2.6、3.1），按 C3.2 的接口顺序排列，无重复。</summary>
    IObservableState<ValueList<LocalInterface>> Interfaces { get; }

    /// <summary>存在 Session 时每 1 s 轮询，否则每 2 s（18.8）。</summary>
    void SetFastPolling(bool enabled);
}

public abstract record ConnectResult
{
    private ConnectResult()
    {
    }

    public sealed record Ok(ITcpConn Conn) : ConnectResult;

    /// <summary>无法把套接字绑定到接口（通常是 VPN 禁止绕过，2.10）；此时没有发出任何数据包。</summary>
    public sealed record BindFailed(bool VpnSuspected) : ConnectResult;

    public sealed record Refused : ConnectResult;

    public sealed record Unreachable : ConnectResult;

    public sealed record Timeout : ConnectResult;
}

public interface ITcpConn
{
    SocketAddr Local { get; }

    SocketAddr Remote { get; }

    /// <summary>读入 <paramref name="buffer"/>，返回读到的字节数（≥ 1）；-1 表示 EOF 或连接被重置（C1.11 都判定为“连接被对方关闭”）。</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>全部写出时返回 true；连接已关闭或已断开时返回 false。</summary>
    ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    void Close();
}

public interface ITcpListener
{
    int Port { get; }

    /// <summary>下一个入站连接；监听已关闭时返回 null。</summary>
    ValueTask<ITcpConn?> AcceptAsync(CancellationToken ct = default);

    void Close();
}

/// <summary>收到的 UDP 数据报。<see cref="Via"/> 为实际接收接口（Windows 由 IPPacketInformation 给出），Android 上为 null。</summary>
public sealed record Datagram(Bytes Data, Ipv4 Src, int SrcPort, LocalInterface? Via);

public interface IUdpEndpoint
{
    /// <summary>下一个数据报；端点已关闭时返回 null。</summary>
    ValueTask<Datagram?> ReceiveAsync(CancellationToken ct = default);

    /// <summary>经接口 <paramref name="via"/> 发往 dst:port（单播、定向广播或组播）；发送失败时返回 false。</summary>
    ValueTask<bool> SendAsync(LocalInterface via, Ipv4 dst, int port, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    void Close();
}

public abstract record UdpOpenResult
{
    private UdpOpenResult()
    {
    }

    public sealed record Ok(IUdpEndpoint Endpoint) : UdpOpenResult;

    /// <summary>端口被占用、系统拒绝访问等（5.10）。</summary>
    public sealed record Failed(string Reason) : UdpOpenResult;
}

public interface ITransport
{
    /// <summary>从接口 <paramref name="via"/> 连接 ip:port，<paramref name="timeout"/> 内未建立返回 Timeout。</summary>
    Task<ConnectResult> ConnectAsync(LocalInterface via, Ipv4 ip, int port, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>按顺序尝试 <paramref name="candidatePorts"/>（0 表示由系统分配），返回第一个成功的监听；全部失败时返回 null（5.7、5.9）。</summary>
    ITcpListener? Listen(IReadOnlyList<int> candidatePorts);

    UdpOpenResult OpenUdp(int port);
}
