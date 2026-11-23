using System.Text;
using FsCheck;
using FsCheck.Fluent;

namespace LanTransfer.Testing;

/// <summary>
/// 共享生成器（设计“属性测试约定 → 共享生成器”），与 Kotlin 侧 <c>com.lantransfer.testing.Gens</c> 同名、分布相近。
/// 返回 <see cref="Arbitrary{T}"/> 的无参静态方法会被 FsCheck 注册（见 <see cref="PbtPropertyAttribute"/>）；
/// 返回 <see cref="Gen{T}"/> 的方法供组合使用。本文件是文本类生成器，文件名与地址类生成器见 DomainGens.cs。
/// </summary>
public static partial class Gens
{
    /// <summary>合法的 Unicode 标量值（U+0000–U+10FFFF，不含 U+D800–U+DFFF），偏向 ASCII、CJK 与增补平面。</summary>
    public static Gen<int> ScalarValue() => Gen.Frequency(
        (4, Gen.Choose(0x20, 0x7E)),
        (2, Gen.Choose(0x4E00, 0x9FFF)),
        (2, Gen.Choose(0x0000, 0xD7FF)),
        (1, Gen.Choose(0xE000, 0xFFFF)),
        (2, Gen.Choose(0x10000, 0x10FFFF)));

    /// <summary>需求 12.2–12.4 中某个字符类的一个代表片段。</summary>
    public static Gen<string> CharClassFragment() =>
        // 传数组：传 IReadOnlyList 时会匹配到 params T[] 重载，把整个列表当成一个元素
        Gen.Elements(TextCharClass.All.ToArray()).SelectMany(c => Gen.Elements(c.Samples.ToArray()));

    /// <summary>文本片段：字符类片段、随机标量值串或普通文字。</summary>
    public static Gen<string> TextFragment() => Gen.Frequency(
        (3, CharClassFragment()),
        (3, Gen.Choose(1, 8).SelectMany(n => ScalarValue().ArrayOf(n)).Select(cps => TextCharClass.Cps(cps))),
        (2, Gen.Elements("a", "Hello", "你好，世界", "123", "LanTransfer", "\u00E9t\u00E9")));

    /// <summary>
    /// 不含 Unpaired_Surrogate 的 Unicode 文本片段序列，偏向需求 12.2–12.4 的每个字符类；
    /// 首尾以一定概率出现空白或 U+FEFF（覆盖“首尾空白”和“位于开头的 U+FEFF”）。
    /// </summary>
    public static Gen<string[]> UnicodeTextFragments(int maxFragments = 24)
    {
        var edgeChoices = TextCharClass.EdgeWhitespace.Samples.Concat(TextCharClass.Bom.Samples).ToArray();
        var edge = Gen.Frequency((3, Gen.Constant(Array.Empty<string>())), (1, Gen.Elements(edgeChoices).Select(s => new[] { s })));
        return from head in edge
               from n in Gen.Choose(0, maxFragments)
               from body in TextFragment().ArrayOf(n)
               from tail in edge
               select head.Concat(body).Concat(tail).ToArray();
    }

    public static Arbitrary<UnicodeText> UnicodeText() =>
        Arb.From(UnicodeTextFragments().Select(f => new UnicodeText(f)), t => ShrinkFragments(t.Fragments).Select(f => new UnicodeText(f)));

    /// <summary>单独的高代理、单独的低代理，或次序颠倒的代理对（低在前、高在后）。</summary>
    public static Gen<string> LoneSurrogate() => Gen.Frequency(
        (2, Gen.Choose(0xD800, 0xDBFF).Select(c => ((char)c).ToString())),
        (2, Gen.Choose(0xDC00, 0xDFFF).Select(c => ((char)c).ToString())),
        (1, Gen.Choose(0xDC00, 0xDFFF).SelectMany(low => Gen.Choose(0xD800, 0xDBFF), (low, high) => new string([(char)low, (char)high]))));

    /// <summary>至少含 1 个 Unpaired_Surrogate 的 UTF-16 字符串片段序列（Property 17、24）。</summary>
    public static Gen<string[]> Utf16WithUnpairedSurrogateFragments() =>
        from n in Gen.Choose(0, 12)
        from fragments in TextFragment().ArrayOf(n)
        from k in Gen.Choose(1, 3)
        from lones in LoneSurrogate().ArrayOf(k)
        from positions in Gen.Choose(0, 64).ArrayOf(k)
        select InsertLones(fragments, lones, positions);

    public static Arbitrary<Utf16WithUnpairedSurrogate> Utf16WithUnpairedSurrogate() =>
        Arb.From(Utf16WithUnpairedSurrogateFragments().Select(f => new Utf16WithUnpairedSurrogate(f)));

    /// <summary>非法 UTF‑8 字节序列（Property 7、utf8-invalid.json）：合法前缀 + 1 段非法字节 + 合法后缀。</summary>
    public static Gen<InvalidUtf8> InvalidUtf8Gen() =>
        from head in UnicodeTextFragments(6)
        from core in InvalidUtf8Core()
        from tail in UnicodeTextFragments(6)
        select new InvalidUtf8(
            [.. Encoding.UTF8.GetBytes(string.Concat(head)), .. core.Bytes, .. Encoding.UTF8.GetBytes(string.Concat(tail))],
            core.Kind);

    public static Arbitrary<InvalidUtf8> InvalidUtf8() => Arb.From(InvalidUtf8Gen());

    private static Gen<InvalidUtf8> InvalidUtf8Core()
    {
        var c6 = Gen.Choose(0, 0x3F);

        // 截断的多字节序列：取 2–4 字节编码的前 1..n-1 个字节
        var truncated =
            from raw in Gen.Choose(0x80, 0x10FFFF)
            from keep in Gen.Choose(1, 3)
            let cp = raw is >= 0xD800 and <= 0xDFFF ? raw + 0x800 : raw
            let full = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp))
            select new InvalidUtf8(full[..Math.Min(keep, full.Length - 1)], Testing.InvalidUtf8.Truncated);

        // 非最短形式：C0/C1 开头的 2 字节、E0 80–9F 的 3 字节、F0 80–8F 的 4 字节
        var overlong = Gen.OneOf(
            from lead in Gen.Choose(0xC0, 0xC1) from b in c6 select Make(Testing.InvalidUtf8.Overlong, lead, Cont(b)),
            from b1 in Gen.Choose(0x80, 0x9F) from b2 in c6 select Make(Testing.InvalidUtf8.Overlong, 0xE0, b1, Cont(b2)),
            from b1 in Gen.Choose(0x80, 0x8F) from b2 in c6 from b3 in c6 select Make(Testing.InvalidUtf8.Overlong, 0xF0, b1, Cont(b2), Cont(b3)));

        // 编码 U+D800–U+DFFF 的 3 字节序列
        var surrogate = from b1 in Gen.Choose(0xA0, 0xBF) from b2 in c6 select Make(Testing.InvalidUtf8.Surrogate, 0xED, b1, Cont(b2));

        // 解码值大于 U+10FFFF：F4 90–BF xx xx，或 F5–F7 开头
        var aboveMax = Gen.OneOf(
            from b1 in Gen.Choose(0x90, 0xBF) from b2 in c6 from b3 in c6 select Make(Testing.InvalidUtf8.AboveMax, 0xF4, b1, Cont(b2), Cont(b3)),
            from lead in Gen.Choose(0xF5, 0xF7) from b1 in c6 from b2 in c6 from b3 in c6 select Make(Testing.InvalidUtf8.AboveMax, lead, Cont(b1), Cont(b2), Cont(b3)));

        // 任何位置都不合法的字节 F8–FF
        var invalidByte = Gen.Choose(0xF8, 0xFF).Select(b => Make(Testing.InvalidUtf8.InvalidByte, b));

        // 没有前导字节的续字节 80–BF
        var continuation =
            from n in Gen.Choose(1, 3)
            from bytes in Gen.Choose(0x80, 0xBF).ArrayOf(n)
            select Make(Testing.InvalidUtf8.UnexpectedContinuation, bytes);

        // 前导字节后跟非续字节
        var missing = from lead in Gen.Elements(0xC3, 0xE2, 0xF0) from ascii in Gen.Choose(0x20, 0x7E) select Make(Testing.InvalidUtf8.MissingContinuation, lead, ascii);

        return Gen.OneOf(truncated, overlong, surrogate, aboveMax, invalidByte, continuation, missing);
    }

    private static int Cont(int low6) => 0x80 | (low6 & 0x3F);

    private static InvalidUtf8 Make(string kind, params int[] bytes) => new(bytes.Select(b => (byte)b).ToArray(), kind);

    /// <summary>把孤立代理项插入片段序列；若前一段以高代理结尾、后一段以低代理开头，中间插入空格，避免意外拼成合法代理对。</summary>
    private static string[] InsertLones(string[] fragments, string[] lones, int[] positions)
    {
        var pieces = fragments.ToList();
        for (var i = 0; i < lones.Length; i++)
        {
            pieces.Insert(Math.Min(positions[i], pieces.Count), lones[i]);
        }

        var result = new List<string>();
        foreach (var p in pieces)
        {
            if (result.Count > 0 && result[^1].Length > 0 && char.IsHighSurrogate(result[^1][^1]) && p.Length > 0 && char.IsLowSurrogate(p[0]))
            {
                result.Add(" ");
            }

            result.Add(p);
        }

        return result.ToArray();
    }

    /// <summary>[lo, hi] 内近似均匀分布的 long（对应 Kotlin 的 <c>Arb.long(lo..hi)</c>）；前置条件：0 ≤ hi − lo &lt; long.MaxValue。</summary>
    public static Gen<long> Choose64(long lo, long hi) =>
        hi - lo < int.MaxValue
            ? Gen.Choose(0, (int)(hi - lo)).Select(d => lo + d)
            : AnyULong().Select(u => lo + (long)(u % (ulong)(hi - lo + 1)));

    /// <summary>取 <paramref name="count"/> 个样本（生成器自检用；用命名参数避免把 size 与样本数传反）。</summary>
    public static T[] Draw<T>(this Gen<T> gen, int count, int size = 30) => Gen.Sample(gen, size: size, numberOfSamples: count);

    /// <summary>缩减：先尝试空序列，再依次去掉每个片段。</summary>
    internal static IEnumerable<string[]> ShrinkFragments(IReadOnlyList<string> fragments)
    {
        if (fragments.Count == 0)
        {
            yield break;
        }

        yield return [];
        for (var i = 0; i < fragments.Count; i++)
        {
            yield return fragments.Where((_, j) => j != i).ToArray();
        }
    }

    /// <summary>字符串中是否含有 Unpaired_Surrogate。</summary>
    public static bool HasUnpairedSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把字符串转成便于阅读的转义形式（控制字符与代理项显示为 \uXXXX）。</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c < 0x20 || c is >= '\u007F' and <= '\u009F' || char.IsSurrogate(c) || c is '"' or '\\')
            {
                sb.Append(CultureInvariant($"\\u{(int)c:X4}"));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }

    private static string CultureInvariant(FormattableString s) => FormattableString.Invariant(s);
}

/// <summary>不含 Unpaired_Surrogate 的 Unicode 文本（由片段拼成，缩减时按片段删除）。</summary>
public sealed record UnicodeText(IReadOnlyList<string> Fragments)
{
    public string Value => string.Concat(Fragments);

    public override string ToString() => $"UnicodeText({Gens.Escape(Value)})";
}

/// <summary>至少含 1 个 Unpaired_Surrogate 的 UTF-16 字符串。</summary>
public sealed record Utf16WithUnpairedSurrogate(IReadOnlyList<string> Fragments)
{
    public string Value => string.Concat(Fragments);

    public override string ToString() => $"Utf16WithUnpairedSurrogate({Gens.Escape(Value)})";
}

/// <summary>非法 UTF‑8 样例；<see cref="Kind"/> 的取值与 Kotlin 侧 <c>InvalidUtf8</c> 的常量一致。</summary>
public sealed record InvalidUtf8(byte[] Bytes, string Kind)
{
    public const string Truncated = "truncated";
    public const string Overlong = "overlong";
    public const string Surrogate = "surrogate";
    public const string AboveMax = "above-max";
    public const string InvalidByte = "invalid-byte";
    public const string UnexpectedContinuation = "unexpected-continuation";
    public const string MissingContinuation = "missing-continuation";

    public static IReadOnlyList<string> Kinds { get; } =
        [Truncated, Overlong, Surrogate, AboveMax, InvalidByte, UnexpectedContinuation, MissingContinuation];

    public override string ToString() => $"InvalidUtf8(kind={Kind}, bytes={Convert.ToHexStringLower(Bytes)})";
}
