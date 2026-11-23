using System.Globalization;
using FsCheck;
using FsCheck.Fluent;

namespace LanTransfer.Testing;

// 领域层共享生成器：文件名、IPv4 与接口集合、本地时间。与 Kotlin 侧 core/domain testFixtures 中的 DomainGens.kt 对应。
public static partial class Gens
{
    private static readonly string[] ReservedStems =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(i => $"COM{i}"), .. Enumerable.Range(1, 9).Select(i => $"LPT{i}")];

    private static readonly string[] Extensions =
        ["", ".txt", ".TXT", ".tar.gz", ".jpg", ".pdf", ".中文", ".", ". txt", ".a.b.c", ".longextension"];

    private static readonly string[] IllegalChars =
        ["<", ">", ":", "\"", "|", "?", "*", "\\", "/", "\0", "\u0001", "\u001F", "\u007F"];

    /// <summary>
    /// 未经处理的文件名（Property 17、18）：路径成分、<c>.</c> / <c>..</c>、保留名的大小写与空格变体、多重扩展名、
    /// 末尾空格和点、多字节超长名称、非法字符、空串，以及含 Unpaired_Surrogate 的名称。
    /// </summary>
    public static Gen<string> RawFileNameGen()
    {
        var stem = Gen.Frequency(
            (3, Gen.Elements("report", "照片", "a", "My File", "数据_2025", "😀表情", "xxxxxxxxxx")),
            (2, UnicodeTextFragments(4).Select(f => string.Concat(f) is { Length: > 0 } s ? s : "f")));
        var plain = from s in stem from e in Gen.Elements(Extensions) select s + e;

        // 保留名：随机大小写（con、Con、cOn……），后跟空格或点，再跟扩展名
        var reserved =
            from r in Gen.Elements(ReservedStems)
            from flips in Gen.Elements(true, false).ArrayOf(r.Length)
            from pad in Gen.Elements("", " ", "  ", ".")
            from e in Gen.Elements(Extensions)
            select new string(r.Select((c, i) => flips[i] ? char.ToLowerInvariant(c) : c).ToArray()) + pad + e;

        var pathLike = from p in Gen.Elements("a/", "a\\", "../", "..\\", "/", "C:\\", "dir/sub/", "./", "a/../") from n in plain select p + n;
        var dots = Gen.Elements(".", "..", "...", " . ", ". .", " ", "   ");
        var trailing = from n in plain from t in Gen.Elements(" ", ".", "..", " .", ". ", "\u3000", "\t") select n + t;
        var longName =
            from unit in Gen.Elements("中", "😀", "a", "é", "\u0915\u094D")
            from count in Gen.Choose(40, 300)
            from e in Gen.Elements(Extensions)
            select string.Concat(Enumerable.Repeat(unit, count)) + e;
        var illegal =
            from n in plain
            from k in Gen.Choose(1, 3)
            from cs in Gen.Elements(IllegalChars).ArrayOf(k)
            from at in Gen.Choose(0, 20)
            let i = Math.Min(at, n.Length)
            select n[..i] + string.Concat(cs) + n[i..];
        var unpaired = from f in Utf16WithUnpairedSurrogateFragments() from e in Gen.Elements(Extensions) select string.Concat(f) + e;

        return Gen.Frequency(
            (4, plain), (2, reserved), (2, pathLike), (1, dots), (2, trailing), (1, longName), (2, illegal), (1, Gen.Constant(string.Empty)), (1, unpaired));
    }

    public static Arbitrary<RawFileName> RawFileName() => Arb.From(RawFileNameGen().Select(s => new RawFileName(s)));

    /// <summary>由 4 个八位组构成 IPv4 地址（大端）。</summary>
    public static uint Ipv4Of(int a, int b, int c, int d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d;

    /// <summary>IPv4 地址的点分十进制形式。</summary>
    public static string ToDottedQuad(uint address) =>
        string.Create(CultureInfo.InvariantCulture, $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}");

    /// <summary>IPv4 地址：各私有地址段、私有段的边界外侧、公网、回环、链路本地、组播、广播与 0.0.0.0。</summary>
    public static Gen<uint> Ipv4Gen()
    {
        var octet = Gen.Choose(0, 255);
        return Gen.Frequency(
            (3, from b in octet from c in octet from d in octet select Ipv4Of(10, b, c, d)),
            (3, from b in Gen.Choose(16, 31) from c in octet from d in octet select Ipv4Of(172, b, c, d)),
            (3, from c in octet from d in octet select Ipv4Of(192, 168, c, d)),
            (2, from a in octet from b in octet from c in octet from d in octet select Ipv4Of(a, b, c, d)),
            (2, Gen.Elements(
                Ipv4Of(127, 0, 0, 1), Ipv4Of(0, 0, 0, 0), Ipv4Of(255, 255, 255, 255), Ipv4Of(169, 254, 1, 1), Ipv4Of(224, 0, 0, 1),
                Ipv4Of(239, 255, 42, 58), Ipv4Of(100, 64, 0, 1), Ipv4Of(8, 8, 8, 8), Ipv4Of(9, 255, 255, 255), Ipv4Of(11, 0, 0, 0),
                Ipv4Of(172, 15, 255, 255), Ipv4Of(172, 32, 0, 0), Ipv4Of(192, 167, 255, 255), Ipv4Of(192, 169, 0, 0))));
    }

    public static Arbitrary<Ipv4Sample> Ipv4() => Arb.From(Ipv4Gen().Select(a => new Ipv4Sample(a)));

    /// <summary>前缀长度 8–32，偏向常见值。</summary>
    public static Gen<int> PrefixLengthGen() => Gen.Frequency((3, Gen.Elements(24, 16, 8, 30, 31, 32)), (2, Gen.Choose(8, 32)));

    public static Arbitrary<PrefixLength> PrefixLength() => Arb.From(PrefixLengthGen().Select(p => new PrefixLength(p)));

    /// <summary>子网掩码（前缀长度 0–32）。</summary>
    public static uint SubnetMask(int prefixLength) => prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

    /// <summary><paramref name="address"/>/<paramref name="prefixLength"/> 子网内的任意地址（含网络地址与广播地址）。</summary>
    public static Gen<uint> Ipv4InSubnet(uint address, int prefixLength)
    {
        var mask = SubnetMask(prefixLength);
        var octet = Gen.Choose(0, 255);
        return from a in octet from b in octet from c in octet from d in octet
               select (address & mask) | (Ipv4Of(a, b, c, d) & ~mask);
    }

    /// <summary>本机接口集合（0–4 个）：私有或公网地址、/8–/32 前缀，并有一定概率与已生成的接口处于重叠的子网。</summary>
    public static Gen<IReadOnlyList<InterfaceSpec>> InterfaceSetGen() =>
        Gen.Choose(0, 4).SelectMany(count => AppendInterfaces([], count));

    private static Gen<IReadOnlyList<InterfaceSpec>> AppendInterfaces(IReadOnlyList<InterfaceSpec> acc, int remaining)
    {
        if (remaining == 0)
        {
            return Gen.Constant(acc);
        }

        var address = acc.Count == 0
            ? Ipv4Gen()
            : Gen.Frequency(
                (2, Ipv4Gen()),
                (1, from other in Gen.Elements(acc.ToArray()) from p in PrefixLengthGen() from a in Ipv4InSubnet(other.Address, Math.Min(other.PrefixLength, p)) select a));
        var next =
            from kind in Gen.Elements(InterfaceSpec.Kinds.ToArray())
            from prefix in PrefixLengthGen()
            from a in address
            select new InterfaceSpec($"if{acc.Count}", kind, a, prefix);
        return next.SelectMany(i => AppendInterfaces([.. acc, i], remaining - 1));
    }

    public static Arbitrary<InterfaceSet> InterfaceSet() => Arb.From(InterfaceSetGen().Select(s => new InterfaceSet(s)));

    /// <summary>2000-01-01 至 2099-12-31 之间、精确到秒的本地时间（文件名回退名 file_YYYYMMDD_HHMMSS）。</summary>
    public static Gen<DateTime> LocalDateTimeGen()
    {
        var start = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var span = (int)((new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Unspecified) - start).TotalSeconds / 2);
        // Gen.Choose 只接受 int，用两段之和覆盖约 100 年的秒数
        return from a in Gen.Choose(0, span) from b in Gen.Choose(0, span) select start.AddSeconds((double)a + b);
    }

    public static Arbitrary<DateTime> LocalDateTime() => Arb.From(LocalDateTimeGen());
}

/// <summary>未经处理的文件名。</summary>
public sealed record RawFileName(string Value)
{
    public override string ToString() => $"RawFileName({Gens.Escape(Value)})";
}

/// <summary>生成器给出的 IPv4 地址（大端）。</summary>
public sealed record Ipv4Sample(uint Value)
{
    public override string ToString() => Gens.ToDottedQuad(Value);
}

/// <summary>生成器给出的前缀长度（8–32）。</summary>
public sealed record PrefixLength(int Value);

/// <summary>生成器使用的本机接口描述；<see cref="Kind"/> 取 WIFI / HOTSPOT / ETHERNET / OTHER，与领域层 IfaceKind 的名称一致。</summary>
public sealed record InterfaceSpec(string Name, string Kind, uint Address, int PrefixLength)
{
    public static IReadOnlyList<string> Kinds { get; } = ["WIFI", "HOTSPOT", "ETHERNET", "OTHER"];

    public override string ToString() => $"InterfaceSpec({Name}, {Kind}, {Gens.ToDottedQuad(Address)}/{PrefixLength.ToString(CultureInfo.InvariantCulture)})";
}

/// <summary>本机接口集合。</summary>
public sealed record InterfaceSet(IReadOnlyList<InterfaceSpec> Interfaces)
{
    public override string ToString() => $"InterfaceSet[{string.Join(", ", Interfaces)}]";
}
