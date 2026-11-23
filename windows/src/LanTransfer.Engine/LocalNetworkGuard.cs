using LanTransfer.Domain;
using LanTransfer.Engine.Ports;

namespace LanTransfer.Engine;

/// <summary>
/// 出站连接的统一出口（设计“网络接口识别与零流量策略”、需求 2.3、2.5、2.9、2.10），与 Kotlin 侧 <c>LocalNetworkGuard</c> 对应：
/// 只有 <see cref="AddressRules.Classify"/> 返回 Local 才调用 <see cref="ITransport.ConnectAsync"/>，并绑定到该接口；NotLocal / Self 立即失败，不发出任何数据包。
/// </summary>
public sealed class LocalNetworkGuard(ITransport transport, INetworkMonitor network)
{
    public abstract record Outcome
    {
        private Outcome()
        {
        }

        public sealed record Connected(ITcpConn Conn, LocalInterface Via) : Outcome;

        public sealed record Failed(OpenFailure Failure) : Outcome;
    }

    /// <summary>入站连接所在的本地接口；找不到时为 null。</summary>
    public sealed record InboundRoute(LocalInterface? Via);

    public Route Route(Ipv4 ip) => AddressRules.Classify(ip, network.Interfaces.Value);

    public async Task<Outcome> ConnectAsync(Ipv4 ip, int port, long timeoutMs, CancellationToken ct = default)
    {
        LocalInterface via;
        switch (Route(ip))
        {
            case Domain.Route.Self:
                return new Outcome.Failed(new OpenFailure.SelfConnection());
            case Domain.Route.Local l:
                via = l.Iface;
                break;
            default:
                return new Outcome.Failed(new OpenFailure.NotLocal());
        }

        if (timeoutMs <= 0)
        {
            return new Outcome.Failed(new OpenFailure.Timeout());
        }

        return await transport.ConnectAsync(via, ip, port, TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false) switch
        {
            ConnectResult.Ok ok => new Outcome.Connected(ok.Conn, via),
            ConnectResult.BindFailed b => new Outcome.Failed(new OpenFailure.BindFailed(b.VpnSuspected)),
            ConnectResult.Refused => new Outcome.Failed(new OpenFailure.Refused()),
            ConnectResult.Unreachable => new Outcome.Failed(new OpenFailure.Unreachable()),
            _ => new Outcome.Failed(new OpenFailure.Timeout()),
        };
    }

    /// <summary>入站连接的本地接口（按本地地址查找）；来源不是该接口子网内的私有地址时返回 null（2.9），调用方立即关闭。</summary>
    public InboundRoute? AcceptInbound(ITcpConn conn)
    {
        var ifaces = network.Interfaces.Value;
        var via = ifaces.FirstOrDefault(i => i.Address == conn.Local.Ip);
        return AddressRules.AcceptsInbound(conn.Remote.Ip, via, ifaces) ? new InboundRoute(via) : null;
    }
}
