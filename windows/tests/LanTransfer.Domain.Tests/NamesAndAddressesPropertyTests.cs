using System.Numerics;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 20–23 的 C# 实现与示例测试，与 Kotlin 侧 NamesAndAddressesPropertiesTest 对应。</summary>
public sealed partial class NamesAndAddressesPropertyTests
{
    private static readonly HashSet<int> WhiteSpaceTable =
        [.. Enumerable.Range(0x09, 5), 0x20, 0x85, 0xA0, 0x1680, .. Enumerable.Range(0x2000, 11), 0x2028, 0x2029, 0x202F, 0x205F, 0x3000];

    private static string RefTrim(string s)
    {
        var a = 0;
        var b = s.Length;
        while (a < b && WhiteSpaceTable.Contains(s[a]))
        {
            a++;
        }

        while (b > a && WhiteSpaceTable.Contains(s[b - 1]))
        {
            b--;
        }

        return s[a..b];
    }

    private static string RefRemove(string s)
    {
        var keep = Enumerable.Repeat(true, s.Length).ToArray();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(s[i]))
            {
                keep[i] = false;
            }
        }

        return new string(s.Where((_, k) => keep[k]).ToArray());
    }

    [GeneratedRegex("^(0|[1-9][0-9]{0,2})(\\.(0|[1-9][0-9]{0,2})){3}$")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex DigitsPattern();

    private static uint? RefIpv4(string s)
    {
        var t = RefTrim(s);
        if (!Ipv4Pattern().IsMatch(t))
        {
            return null;
        }

        var parts = t.Split('.').Select(int.Parse).ToArray();
        return parts.Any(p => p > 255) ? null : parts.Aggregate(0u, (acc, p) => (acc << 8) | (uint)p);
    }

    private static PortParse RefPort(string s)
    {
        var t = RefTrim(s);
        if (t.Length == 0)
        {
            return new PortParse.Default(ProtocolConstants.DefaultPort);
        }

        if (!DigitsPattern().IsMatch(t))
        {
            return new PortParse.Invalid();
        }

        var v = BigInteger.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        return v >= 1 && v <= 65535 ? new PortParse.Ok((int)v) : new PortParse.Invalid();
    }

    private static readonly IfaceKind[] KindRank = [IfaceKind.Wifi, IfaceKind.Ethernet, IfaceKind.Hotspot, IfaceKind.Other];

    private static bool InSubnet(Ipv4 t, LocalInterface i) => ((t.Bits ^ i.Address.Bits) & AddressRules.SubnetMask(i.PrefixLength)) == 0;

    private static Route RefClassify(Ipv4 t, IReadOnlyList<LocalInterface> ifaces)
    {
        if (ifaces.Any(i => i.Address == t))
        {
            return new Route.Self();
        }

        if (!AddressRules.IsPrivate(t))
        {
            return new Route.NotLocal();
        }

        var inSubnet = ifaces.Where(i => InSubnet(t, i)).ToList();
        if (inSubnet.Count == 0)
        {
            return new Route.NotLocal();
        }

        var longest = inSubnet.Max(i => i.PrefixLength);
        var best = inSubnet.Where(i => i.PrefixLength == longest).OrderBy(i => Array.IndexOf(KindRank, i.Kind)).ThenBy(i => i.Name, StringComparer.Ordinal).First();
        return new Route.Local(best);
    }

    private static List<LocalInterface> ToIfaces(IReadOnlyList<InterfaceSpec> specs) =>
        specs.Select(s => new LocalInterface(s.Name, VectorJson.EnumOf<IfaceKind>(s.Kind), new Ipv4(s.Address), s.PrefixLength)).ToList();

    /// <summary>名称输入：偏向 Unpaired_Surrogate、首尾空白、控制字符和长度边界。</summary>
    private static readonly Gen<string> NameInput = Gen.Frequency(
        (3, Gens.UnicodeTextFragments(6).Select(f => string.Concat(f))),
        (2, Gens.Utf16WithUnpairedSurrogateFragments().Select(f => string.Concat(f))),
        (2, from pre in Gen.Elements(" ", "\u3000", "\u00A0", "\t", "\u2028", "\u001C", "") from n in Gen.Choose(0, 40) from post in Gen.Elements(" ", "\u205F", "\n", "")
            select pre + new string('名', n) + post),
        (1, from s in Gens.UnicodeTextFragments(3) from c in Gen.Elements("\0", "\u001F", "\u007F", "\u0085", "\u009F") select "a" + c + string.Concat(s)));

    private static readonly Gen<string> AddressText = Gen.OneOf(
        Gens.Ipv4Gen().Select(Gens.ToDottedQuad),
        from a in Gens.Ipv4Gen() from w in Gen.Elements(" ", "\u3000", "\t", "") select w + Gens.ToDottedQuad(a) + w,
        Gen.Elements("1", "0x1.2.3.4", "1.2.3", "1.2.3.4.5", "256.1.1.1", "01.2.3.4", "1.2.3.04", "１.２.３.４", "١.٢.٣.٤", "1..2.3", "", " ", "a.b.c.d", "1.2.3.4 5", "-1.2.3.4"),
        Gens.UnicodeTextFragments(3).Select(f => string.Concat(f)));

    private static readonly Gen<string> PortText = Gen.OneOf(
        Gen.Choose(0, 70000).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        from p in Gen.Choose(1, 65535) from z in Gen.Elements("0", "00", "") select z + p.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Gen.Elements("", " ", "\u3000", "１２３", "٤٢٥٨٠", "42580 ", " 42580", "4258O", "+1", "-1", "99999999999999999999", "0"));

    private static Gen<Ipv4> TargetFor(IReadOnlyList<InterfaceSpec> specs) =>
        (specs.Count == 0
            ? Gens.Ipv4Gen()
            : Gen.OneOf(Gens.Ipv4Gen(), Gen.Elements(specs.ToArray()).SelectMany(s => Gens.Ipv4InSubnet(s.Address, s.PrefixLength)), Gen.Elements(specs.Select(s => s.Address).ToArray())))
        .Select(b => new Ipv4(b));

    // Feature: lan-file-transfer, Property 20: 显示名称规则
    // Validates: Requirements 4.3, 4.4, 4.5, 4.8, 4.9
    [PbtProperty(1000)]
    public Property Property20_DisplayNameRules() =>
        Prop.ForAll(Arb.From(from s in NameInput from t in Gen.Elements(DeviceType.Android, DeviceType.Windows, DeviceType.Ios) select (s, t)), x =>
        {
            Assert.Equal(RefRemove(x.s), DisplayNameRules.RemoveUnpairedSurrogates(x.s));
            Assert.Equal(RefTrim(x.s), DisplayNameRules.TrimWhiteSpace(x.s));
            var c = RefTrim(RefRemove(x.s));
            var n = c.EnumerateRunes().Count();
            NameCheck expected = c.Length == 0 ? new NameCheck.Empty()
                : n > 32 ? new NameCheck.TooLong()
                : c.Any(ch => ch <= 0x1F || ch is >= '\u007F' and <= '\u009F') ? new NameCheck.HasControl()
                : new NameCheck.Ok(c, Gens.HasUnpairedSurrogate(x.s));
            var got = DisplayNameRules.ValidateUserInput(x.s);
            Assert.Equal(expected, got);
            if (got is NameCheck.Ok ok)
            {
                Assert.False(Gens.HasUnpairedSurrogate(ok.Cleaned));
            }

            var head = string.Concat(c.EnumerateRunes().Take(32).Select(r => r.ToString()));
            var d = DisplayNameRules.DefaultName(x.s, x.t);
            var expectedDefault = x.t switch
            {
                DeviceType.Android => "Android 设备",
                DeviceType.Windows => "Windows 电脑",
                DeviceType.Ios => "iOS 设备",
                _ => "Windows 电脑",
            };
            Assert.Equal(head.Length > 0 ? head : expectedDefault, d);
            Assert.False(Gens.HasUnpairedSurrogate(d));
        });

    // Feature: lan-file-transfer, Property 21: IPv4 与端口解析
    // Validates: Requirements 6.3, 6.4
    [PbtProperty(1000)]
    public Property Property21_Ipv4AndPortParsing() =>
        Prop.ForAll(Arb.From(from a in AddressText from p in PortText from ip in Gens.Ipv4Gen() select (a, p, ip)), x =>
        {
            Assert.Equal(RefIpv4(x.a), AddressRules.ParseIpv4(x.a)?.Bits);
            Assert.Equal(new Ipv4(x.ip), AddressRules.ParseIpv4(new Ipv4(x.ip).ToString()));
            Assert.Equal(RefPort(x.p), AddressRules.ParsePort(x.p));
        });

    // Feature: lan-file-transfer, Property 22: 本地地址判定
    // Validates: Requirements 2.1, 2.5, 2.9, 5.5, 6.9
    [PbtProperty(1000)]
    public Property Property22_LocalAddressClassification() =>
        Prop.ForAll(
            Arb.From(Gens.InterfaceSetGen().SelectMany(specs =>
            {
                var ifaces = ToIfaces(specs);
                return from t in TargetFor(specs) from via in Gen.Elements(new LocalInterface?[] { null }.Concat(ifaces).ToArray()) select (t, via, ifaces);
            })),
            x =>
            {
                Assert.Equal(RefClassify(x.t, x.ifaces), AddressRules.Classify(x.t, x.ifaces));
                var expected = AddressRules.IsPrivate(x.t) && (x.via is not null ? InSubnet(x.t, x.via) : x.ifaces.Any(i => InSubnet(x.t, i)));
                Assert.Equal(expected, AddressRules.AcceptsInbound(x.t, x.via, x.ifaces));
            });

    // Feature: lan-file-transfer, Property 23: 二维码地址列表与候选地址顺序
    // Validates: Requirements 2.5, 8.5, 8.12, 23.8
    [PbtProperty(1000)]
    public Property Property23_QrAddressListAndCandidateOrder() =>
        Prop.ForAll(
            Arb.From(Gens.InterfaceSetGen().SelectMany(specs =>
            {
                var ifaces = ToIfaces(specs);
                return from ips in Gen.Choose(1, 4).SelectMany(n => TargetFor(specs).ArrayOf(n))
                       from outcomes in Gen.Elements(true, false).ArrayOf(4)
                       select (ifaces, ips: ips.Distinct().ToList(), outcomes);
            })),
            x =>
            {
                Assert.Equal(x.ifaces.Select(i => i.Address).Distinct().Take(4), AddressRules.QrAddressList(x.ifaces));
                var attempted = new List<Ipv4>();
                var result = AddressRules.ConnectInQrOrderAsync(x.ips, x.ifaces, (ip, _) =>
                {
                    attempted.Add(ip);
                    return Task.FromResult(x.outcomes[x.ips.IndexOf(ip)] ? "ok:" + ip : null);
                }).GetAwaiter().GetResult();
                var local = x.ips.Where(ip => RefClassify(ip, x.ifaces) is Route.Local).ToList();
                var firstOk = local.FindIndex(ip => x.outcomes[x.ips.IndexOf(ip)]);
                if (firstOk < 0)
                {
                    Assert.Null(result);
                    Assert.Equal(local, attempted);
                }
                else
                {
                    Assert.Equal("ok:" + local[firstOk], result);
                    Assert.Equal(local.Take(firstOk + 1), attempted);
                }
            });

    [Fact]
    public void Examples()
    {
        Assert.Equal("Pixel 9", DisplayNameRules.DefaultName("  Pixel 9  ", DeviceType.Android));
        Assert.Equal("Windows 电脑", DisplayNameRules.DefaultName("\u3000", DeviceType.Windows));
        Assert.Equal("Android 设备", DisplayNameRules.DefaultName(null, DeviceType.Android));
        Assert.Equal("iOS 设备", DisplayNameRules.DefaultName(null, DeviceType.Ios));
        Assert.Equal(new NameCheck.Ok("ab", true), DisplayNameRules.ValidateUserInput(" a\uD800b "));
        Assert.Equal(new NameCheck.HasControl(), DisplayNameRules.ValidateUserInput("a\u0085b"));
        Assert.Equal(new NameCheck.Empty(), DisplayNameRules.ValidateUserInput("\u0085"));
        Assert.Equal(new NameCheck.TooLong(), DisplayNameRules.ValidateUserInput(new string('名', 33)));
        Assert.Equal(Ipv4.Of(192, 168, 1, 20), AddressRules.ParseIpv4(" 192.168.1.20 "));
        Assert.Null(AddressRules.ParseIpv4("192.168.01.20"));
        Assert.Equal(new PortParse.Default(42580), AddressRules.ParsePort(""));
        Assert.Equal(new PortParse.Ok(80), AddressRules.ParsePort("00080"));
        Assert.Equal(new PortParse.Invalid(), AddressRules.ParsePort("65536"));
        var wifi = new LocalInterface("wlan0", IfaceKind.Wifi, Ipv4.Of(192, 168, 1, 5), 24);
        var hotspot = new LocalInterface("ap0", IfaceKind.Hotspot, Ipv4.Of(192, 168, 1, 1), 24);
        Assert.Equal(new Route.Local(wifi), AddressRules.Classify(Ipv4.Of(192, 168, 1, 9), [hotspot, wifi]));
        Assert.Equal(new Route.Self(), AddressRules.Classify(Ipv4.Of(192, 168, 1, 5), [wifi]));
        Assert.Equal(new Route.NotLocal(), AddressRules.Classify(Ipv4.Of(8, 8, 8, 8), [wifi]));
        Assert.Equal(Ipv4.Of(192, 168, 1, 255), wifi.Broadcast);
    }
}
