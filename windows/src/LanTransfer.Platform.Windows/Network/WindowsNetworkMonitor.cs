using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Platform.Windows.Network;

/// <summary>
/// Windows 网络接口监视器（设计 C7.1、Architecture）：监听 NetworkAddressChanged 并定时轮询（常态 2 s，存在 Session 时 1 s）。
/// 只选取状态为 Up、非 Loopback / Tunnel、带私有 IPv4 的接口，按“默认网关 → Wi‑Fi/以太网 → 非虚拟网卡 → 虚拟网卡”排序。
/// </summary>
public sealed class WindowsNetworkMonitor : INetworkMonitor, IDisposable
{
    private static readonly string[] VirtualMarkers = ["vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "vbox", "tap-", "tun", "tailscale", "zerotier"];

    private readonly ObservableState<ValueList<LocalInterface>> _interfaces;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _fastPolling;

    public WindowsNetworkMonitor()
    {
        _interfaces = new ObservableState<ValueList<LocalInterface>>(Snapshot());
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        _ = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
    }

    public IObservableState<ValueList<LocalInterface>> Interfaces => _interfaces;

    public void SetFastPolling(bool enabled) => _fastPolling = enabled;

    public void RefreshNow() => _interfaces.Value = Snapshot();

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    private void OnAddressChanged(object? sender, EventArgs e) => RefreshNow();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RefreshNow();

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_fastPolling ? 1_000 : 2_000, ct).ConfigureAwait(false);
                RefreshNow();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static ValueList<LocalInterface> Snapshot()
    {
        try
        {
            var raw = new List<(LocalInterface Iface, bool HasGateway, bool IsVirtual, int KindRank)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties props;
                try
                {
                    props = ni.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                var isVirtual = IsVirtualAdapter(ni.Name, ni.Description);
                var kind = ClassifyKind(ni.NetworkInterfaceType, ni.Name, ni.Description, isVirtual);
                var kindRank = kind switch
                {
                    IfaceKind.Wifi => 0,
                    IfaceKind.Ethernet => 1,
                    IfaceKind.Hotspot => 2,
                    _ => 3,
                };

                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var b = uni.Address.GetAddressBytes();
                    var ip = Ipv4.Of(b[0], b[1], b[2], b[3]);
                    if (!AddressRules.IsPrivate(ip))
                    {
                        continue;
                    }

                    var prefix = uni.PrefixLength is >= 1 and <= 32 ? uni.PrefixLength : 24;
                    raw.Add((new LocalInterface(ni.Name, kind, ip, prefix), hasGateway, isVirtual, kindRank));
                }
            }

            var ordered = raw
                .OrderBy(x => x.IsVirtual ? 1 : 0)
                .ThenBy(x => x.HasGateway ? 0 : 1)
                .ThenBy(x => x.KindRank)
                .ThenBy(x => x.Iface.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Iface.Address.Bits)
                .Select(x => x.Iface)
                .Distinct()
                .ToList();

            return new ValueList<LocalInterface>(ordered);
        }
        catch
        {
            return ValueList<LocalInterface>.Empty;
        }
    }

    private static bool IsVirtualAdapter(string name, string description)
    {
        foreach (var m in VirtualMarkers)
        {
            if (name.Contains(m, StringComparison.OrdinalIgnoreCase) || description.Contains(m, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IfaceKind ClassifyKind(NetworkInterfaceType type, string name, string description, bool isVirtual)
    {
        if (isVirtual)
        {
            return IfaceKind.Other;
        }

        if (type == NetworkInterfaceType.Wireless80211 || name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) || description.Contains("wireless", StringComparison.OrdinalIgnoreCase))
        {
            return IfaceKind.Wifi;
        }

        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT)
        {
            return IfaceKind.Ethernet;
        }

        return IfaceKind.Other;
    }
}
