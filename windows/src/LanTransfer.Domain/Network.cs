using LanTransfer.Protocol;

namespace LanTransfer.Domain;

/// <summary>IPv4 地址（大端），与 Kotlin 侧 <c>Ipv4</c> 对应。</summary>
public readonly record struct Ipv4(uint Bits)
{
    public static Ipv4 Of(byte a, byte b, byte c, byte d) => new(((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d);

    public override string ToString() => $"{Bits >> 24}.{(Bits >> 16) & 0xFF}.{(Bits >> 8) & 0xFF}.{Bits & 0xFF}";
}

/// <summary>本机接口类型；前缀长度相同时按 Wifi、Ethernet、Hotspot、Other 的顺序优先（C3.2）。</summary>
public enum IfaceKind
{
    Wifi,
    Hotspot,
    Ethernet,
    Other,
}

/// <summary>一个活动的本地网络接口。平台绑定句柄由平台适配层按 <see cref="Name"/> 另行维护。</summary>
public sealed record LocalInterface
{
    public LocalInterface(string name, IfaceKind kind, Ipv4 address, int prefixLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)prefixLength, 32u, nameof(prefixLength));
        Name = name;
        Kind = kind;
        Address = address;
        PrefixLength = prefixLength;
    }

    public string Name { get; }

    public IfaceKind Kind { get; }

    public Ipv4 Address { get; }

    public int PrefixLength { get; }

    /// <summary>定向广播地址。</summary>
    public Ipv4 Broadcast => new(Address.Bits | ~AddressRules.SubnetMask(PrefixLength));

    public bool Contains(Ipv4 ip)
    {
        var m = AddressRules.SubnetMask(PrefixLength);
        return (ip.Bits & m) == (Address.Bits & m);
    }
}

/// <summary><see cref="AddressRules.Classify"/> 的结果。</summary>
public abstract record Route
{
    public sealed record Self : Route;

    public sealed record Local(LocalInterface Iface) : Route;

    public sealed record NotLocal : Route;
}

/// <summary><see cref="AddressRules.ParsePort"/> 的结果。</summary>
public abstract record PortParse
{
    public sealed record Default(int Port) : PortParse;

    public sealed record Ok(int Port) : PortParse;

    public sealed record Invalid : PortParse;
}

/// <summary>地址规则（需求 2、6、8，设计 C3.2），与 Kotlin 侧 <c>AddressRules</c> 对应。不使用 IPAddress.Parse / TryParse，从不触发 DNS。</summary>
public static class AddressRules
{
    private static readonly IfaceKind[] KindOrder = [IfaceKind.Wifi, IfaceKind.Ethernet, IfaceKind.Hotspot, IfaceKind.Other];

    /// <summary>子网掩码（前缀长度 0–32）。</summary>
    public static uint SubnetMask(int prefixLength) => prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

    /// <summary>去除首尾 White_Space 后要求恰好 4 段 ASCII 十进制数，每段 0–255，除单个 "0" 外不以 0 开头。</summary>
    public static Ipv4? ParseIpv4(string input)
    {
        var parts = DisplayNameRules.TrimWhiteSpace(input).Split('.');
        if (parts.Length != 4)
        {
            return null;
        }

        uint bits = 0;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0'))
            {
                return null;
            }

            var v = int.Parse(p, System.Globalization.CultureInfo.InvariantCulture);
            if (v > 255)
            {
                return null;
            }

            bits = (bits << 8) | (uint)v;
        }

        return new Ipv4(bits);
    }

    /// <summary>去除首尾 White_Space 后：为空 → Default(42580)；只含 ASCII 数字且数值 1–65535 → Ok；其余 → Invalid。</summary>
    public static PortParse ParsePort(string input)
    {
        var s = DisplayNameRules.TrimWhiteSpace(input);
        if (s.Length == 0)
        {
            return new PortParse.Default(ProtocolConstants.DefaultPort);
        }

        if (!s.All(char.IsAsciiDigit))
        {
            return new PortParse.Invalid();
        }

        var digits = s.TrimStart('0');
        if (digits.Length > 5)
        {
            return new PortParse.Invalid();
        }

        var v = digits.Length == 0 ? 0 : int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        return v is >= 1 and <= 65535 ? new PortParse.Ok(v) : new PortParse.Invalid();
    }

    /// <summary>10/8、172.16/12、192.168/16、169.254/16。</summary>
    public static bool IsPrivate(Ipv4 ip)
    {
        var b = ip.Bits;
        return b >> 24 == 10 || b >> 20 == 0xAC1 || b >> 16 == 0xC0A8 || b >> 16 == 0xA9FE;
    }

    /// <summary>目标等于本机任一接口地址 → Self；是私有地址且落在接口子网内 → Local（最长前缀，同前缀按类型优先）；否则 NotLocal。</summary>
    public static Route Classify(Ipv4 target, IEnumerable<LocalInterface> ifaces)
    {
        var list = ifaces as IReadOnlyCollection<LocalInterface> ?? ifaces.ToList();
        if (list.Any(i => i.Address == target))
        {
            return new Route.Self();
        }

        if (!IsPrivate(target))
        {
            return new Route.NotLocal();
        }

        var best = list.Where(i => i.Contains(target))
            .OrderByDescending(i => i.PrefixLength)
            .ThenBy(i => Array.IndexOf(KindOrder, i.Kind))
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return best is null ? new Route.NotLocal() : new Route.Local(best);
    }

    /// <summary>来源是私有地址且落在接收接口 <paramref name="via"/> 的子网内；via 为 null 时任一接口子网包含来源即可。</summary>
    public static bool AcceptsInbound(Ipv4 src, LocalInterface? via, IEnumerable<LocalInterface> ifaces) =>
        IsPrivate(src) && (via is not null ? via.Contains(src) : ifaces.Any(i => i.Contains(src)));

    /// <summary>保持 QR 中的顺序，只保留 Classify 为 Local 的地址（8.5）。</summary>
    public static IReadOnlyList<(Ipv4 Ip, LocalInterface Iface)> QrCandidates(IEnumerable<Ipv4> ips, IEnumerable<LocalInterface> ifaces)
    {
        var list = ifaces.ToList();
        return ips.Select(ip => (ip, route: Classify(ip, list)))
            .Where(x => x.route is Route.Local)
            .Select(x => (x.ip, ((Route.Local)x.route).Iface))
            .ToList();
    }

    /// <summary>排序后的接口地址去重，最多 4 个（23.8）。</summary>
    public static IReadOnlyList<Ipv4> QrAddressList(IEnumerable<LocalInterface> sortedIfaces) =>
        sortedIfaces.Select(i => i.Address).Distinct().Take(ProtocolConstants.MaxQrIps).ToList();

    /// <summary>按 QR 中的顺序逐个尝试候选地址，在第一个成功处停止；只对 Local 地址调用 <paramref name="attempt"/>；全部失败时返回 null。</summary>
    public static async Task<TResult?> ConnectInQrOrderAsync<TResult>(
        IEnumerable<Ipv4> ips,
        IEnumerable<LocalInterface> ifaces,
        Func<Ipv4, LocalInterface, Task<TResult?>> attempt)
        where TResult : class
    {
        foreach (var (ip, iface) in QrCandidates(ips, ifaces))
        {
            if (await attempt(ip, iface).ConfigureAwait(false) is { } r)
            {
                return r;
            }
        }

        return null;
    }
}
