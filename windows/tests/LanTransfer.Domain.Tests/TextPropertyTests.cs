using System.Text;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 24–26 的 C# 实现与示例测试，与 Kotlin 侧 TextPropertiesTest 对应。</summary>
public sealed partial class TextPropertyTests
{
    // ---- 参考模型 ----

    [GeneratedRegex("\r\n|\r|\n")]
    private static partial Regex LineBreak();

    /// <summary>映射单元：CRLF、合法代理对或单个码元。</summary>
    [GeneratedRegex(@"\r\n|[\uD800-\uDBFF][\uDC00-\uDFFF]|[\s\S]")]
    private static partial Regex MappingUnit();

    /// <summary>码点：合法代理对或单个码元。</summary>
    [GeneratedRegex(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\s\S]")]
    private static partial Regex CodePoint();

    private static int RefLineCount(string t) => LineBreak().Count(t) + 1;

    private static List<(int Start, int End)> RefUnits(string t) =>
        MappingUnit().Matches(t).Select(m => (m.Index, m.Index + m.Length)).ToList();

    private static int RefCodePoints(string t) => CodePoint().Count(t);

    private static bool IsBreak(string u) => u is "\r\n" or "\r" or "\n";

    private static string RefShown(string u) => u switch
    {
        _ when IsBreak(u) => "\n",
        "\t" => "\t",
        [var c] when c < 0x20 => ((char)(0x2400 + c)).ToString(),
        "\u007F" => "\u2421",
        [var c] when c is >= '\u0080' and <= '\u009F' => "\u25AF",
        [var c] when char.IsSurrogate(c) => "\uFFFD",
        _ => u,
    };

    /// <summary>折叠前缀：在所有映射单元边界中，取行数 ≤ 20 且码点数 ≤ 1000 的最大者。</summary>
    private static Collapse RefCollapse(string t)
    {
        if (RefLineCount(t) <= 20 && RefCodePoints(t) <= 1000)
        {
            return new Collapse.Full();
        }

        int lines = 1, cps = 0;
        var ok = new List<int> { 0 };
        foreach (var (a, b) in RefUnits(t))
        {
            var u = t[a..b];
            if (IsBreak(u))
            {
                lines++;
            }

            cps += RefCodePoints(u);
            if (lines <= 20 && cps <= 1000)
            {
                ok.Add(b);
            }
        }

        return new Collapse.Collapsed(ok.Max());
    }

    /// <summary>位置 p 在码点边界上且不在 CRLF 中间。</summary>
    private static bool IsUnitBoundary(string t, int p) =>
        p == 0 || p == t.Length || (!(char.IsHighSurrogate(t[p - 1]) && char.IsLowSurrogate(t[p])) && !(t[p - 1] == '\r' && t[p] == '\n'));

    // ---- 生成器 ----

    private static readonly Gen<string> AnyText = Gen.Frequency(
        (4, Gens.UnicodeTextFragments().Select(f => string.Concat(f))),
        (3, Gens.Utf16WithUnpairedSurrogateFragments().Select(f => string.Concat(f))),
        (1, Gen.Elements("", " ", "\u3000", " \t\n", "\r\n", "\r", "\0", "\uFEFF")));

    private static readonly Gen<string> LineBreakGen = Gen.Elements("\n", "\r\n", "\r", "\r\r\n", "\n\r", "\r\n\n");

    /// <summary>行数在 20 上下的文本。</summary>
    private static readonly Gen<string> ManyLines =
        from n in Gen.Choose(15, 26)
        from lines in Gens.TextFragment().ArrayOf(n)
        from seps in LineBreakGen.ArrayOf(26)
        select string.Concat(lines.Select((l, k) => k < lines.Length - 1 ? l + seps[k] : l));

    /// <summary>码点数在 1000 上下、边界处跨着 CRLF / 代理对 / 组合字符的文本。</summary>
    private static readonly Gen<string> NearCodePointLimit =
        from n in Gen.Choose(995, 1001)
        from edge in Gen.Elements("\r\n", "😀", "\r", "\n", "\uD83D", "\uDE00", "e\u0301", "中")
        from rest in Gens.UnicodeTextFragments(3)
        select new string('a', n) + edge + string.Concat(rest);

    /// <summary>码点数在 100 上下（通知预览边界）。</summary>
    private static readonly Gen<string> NearPreviewLimit =
        from n in Gen.Choose(97, 101)
        from edge in Gen.Elements("😀", "a", "\r\n", "\uDBFF", "")
        select new string('中', n) + edge + "尾";

    private static readonly Gen<string> LongFragments =
        Gen.Choose(150, 320).SelectMany(n => Gens.TextFragment().ArrayOf(n)).Select(f => string.Concat(f));

    private static readonly Gen<string> TextForLayout = Gen.Frequency(
        (4, AnyText), (3, ManyLines), (2, NearCodePointLimit), (2, NearPreviewLimit), (1, LongFragments));

    /// <summary>UTF‑8 长度在 1 MiB 上下：前缀 ASCII + 多字节或单独代理项结尾。</summary>
    private static readonly Gen<string> NearByteLimit =
        from k in Gen.Choose(-3, 4)
        from tail in Gen.Elements("", "é", "中", "😀", "\uD800", "\uDC00")
        select new string('a', ProtocolConstants.MaxTextBytes - k) + tail;

    private static readonly Gen<string> OutgoingText = Gen.Frequency(
        (8, AnyText),
        (1, from w in Gen.Elements(" ", "\t", "\n", "\u3000", "\u00A0", "\r\n") from n in Gen.Choose(1, 20) select string.Concat(Enumerable.Repeat(w, n))),
        (1, NearByteLimit));

    // Feature: lan-file-transfer, Property 24: 发送前检查与代理项替换
    // Validates: Requirements 11.2, 11.3, 11.9, 12.6, 12.10, 13.7
    [PbtProperty(1000)]
    public Property Property24_OutgoingCheckAndSurrogateReplacement() =>
        Prop.ForAll(Arb.From(OutgoingText), t =>
        {
            OutgoingCheck expected;
            if (t.Length == 0)
            {
                expected = new OutgoingCheck.Empty();
            }
            else if (Gens.HasUnpairedSurrogate(t))
            {
                expected = new OutgoingCheck.HasUnpairedSurrogate();
            }
            else
            {
                var utf8 = Encoding.UTF8.GetBytes(t);
                expected = utf8.Length > ProtocolConstants.MaxTextBytes ? new OutgoingCheck.TooLong(utf8.Length) : new OutgoingCheck.Ok(Bytes.Of(utf8));
            }

            Assert.Equal(expected, TextRules.CheckOutgoing(t));
            var replaced = TextRules.ReplaceUnpaired(t);

            // EnumerateRunes 把每个单独的代理码元枚举为 U+FFFD
            Assert.Equal(string.Concat(t.EnumerateRunes().Select(r => r.ToString())), replaced);
            Assert.False(Gens.HasUnpairedSurrogate(replaced));
            if (!Gens.HasUnpairedSurrogate(t))
            {
                Assert.Equal(t, replaced);
            }
        });

    // Feature: lan-file-transfer, Property 25: 行数、折叠与通知预览
    // Validates: Requirements 11.10, 21.6
    [PbtProperty(1000)]
    public Property Property25_LineCountCollapseAndPreview() =>
        Prop.ForAll(Arb.From(TextForLayout), t =>
        {
            Assert.Equal(RefLineCount(t), TextRules.LineCount(t));
            var c = TextRules.Collapse(t);
            Assert.Equal(RefCollapse(t), c);
            if (c is Collapse.Collapsed collapsed)
            {
                var prefix = t[..collapsed.PrefixEnd];
                Assert.True(RefLineCount(prefix) <= 20);
                Assert.True(RefCodePoints(prefix) <= 1000);
                Assert.True(IsUnitBoundary(t, collapsed.PrefixEnd));
            }

            var cps = CodePoint().Matches(t);
            var preview = cps.Count <= 100 ? t : t[..(cps[99].Index + cps[99].Length)] + "…";
            Assert.Equal(preview, TextRules.NotificationPreview(t));
        });

    // Feature: lan-file-transfer, Property 26: 显示映射与选区还原原文
    // Validates: Requirements 12.8, 13.3, 13.4
    [PbtProperty(1000)]
    public Property Property26_DisplayMappingAndSelection() =>
        Prop.ForAll(Arb.From(from t in TextForLayout from seed in Gen.Choose(0, int.MaxValue - 1) select (t, seed)), x =>
        {
            var t = x.t;
            var units = RefUnits(t);
            var shown = units.Select(u => RefShown(t[u.Start..u.End])).ToList();
            var d = TextDisplayMapper.Map(t);
            Assert.Equal(string.Concat(shown), d.Display);
            Assert.Equal(RefLineCount(t) - 1, d.Display.Count(ch => ch == '\n'));

            // 每个显示码元所属的映射单元
            var unitAt = new int[d.Display.Length];
            var p = 0;
            for (var k = 0; k < shown.Count; k++)
            {
                for (var j = 0; j < shown[k].Length; j++)
                {
                    unitAt[p++] = k;
                }
            }

            (int, int) Expected(int a, int b) =>
                a == b && a == d.Display.Length ? (t.Length, t.Length)
                : a == b ? (units[unitAt[a]].Start, units[unitAt[a]].Start)
                : (units[unitAt[a]].Start, units[unitAt[b - 1]].End);

            void Check(int a, int b)
            {
                var got = d.OriginalRange(a, b);
                Assert.Equal(Expected(a, b), got);
                Assert.True(IsUnitBoundary(t, got.Start));
                Assert.True(IsUnitBoundary(t, got.End));
            }

            var n = d.Display.Length;
            if (n <= 40)
            {
                for (var a = 0; a <= n; a++)
                {
                    for (var b = a; b <= n; b++)
                    {
                        Check(a, b);
                    }
                }
            }
            else
            {
                var rnd = new Random(x.seed);
                for (var k = 0; k < 60; k++)
                {
                    var a = rnd.Next(n + 1);
                    Check(a, a + rnd.Next(n - a + 1));
                }
            }

            var all = d.OriginalRange(0, n);
            Assert.Equal(t, t[all.Start..all.End]);
        });

    [Fact]
    public void Examples_OutgoingCheckLineCountAndCollapse()
    {
        Assert.Equal(new OutgoingCheck.Empty(), TextRules.CheckOutgoing(string.Empty));
        Assert.IsType<OutgoingCheck.Ok>(TextRules.CheckOutgoing("  \n\t"));
        Assert.Equal(new OutgoingCheck.HasUnpairedSurrogate(), TextRules.CheckOutgoing("a\uD800"));
        Assert.IsType<OutgoingCheck.Ok>(TextRules.CheckOutgoing(new string('a', ProtocolConstants.MaxTextBytes)));
        Assert.Equal(new OutgoingCheck.TooLong(1_048_577), TextRules.CheckOutgoing(new string('a', ProtocolConstants.MaxTextBytes - 1) + "é"));
        Assert.Equal("x\uFFFD\uD83D\uDE00\uFFFD", TextRules.ReplaceUnpaired("x\uDC00\uD83D\uDE00\uD800"));
        Assert.Equal(1, TextRules.LineCount(string.Empty));
        Assert.Equal(4, TextRules.LineCount("a\r\nb\rc\nd"));
        Assert.Equal(4, TextRules.LineCount("\r\r\n\n"));
        Assert.Equal(new Collapse.Full(), TextRules.Collapse(string.Concat(Enumerable.Repeat("l\n", 19)) + "l"));
        Assert.Equal(new Collapse.Collapsed(39), TextRules.Collapse(string.Concat(Enumerable.Repeat("l\n", 20)) + "l"));
        Assert.Equal(new Collapse.Collapsed(999), TextRules.Collapse(new string('a', 999) + "\r\nb"));
        Assert.Equal(new Collapse.Collapsed(1001), TextRules.Collapse(new string('a', 999) + "😀b"));
        Assert.Equal(new Collapse.Full(), TextRules.Collapse(new string('a', 1000)));
        Assert.Equal(new string('中', 100), TextRules.NotificationPreview(new string('中', 100)));
        Assert.Equal(new string('中', 99) + "😀…", TextRules.NotificationPreview(new string('中', 99) + "😀😁"));
    }

    [Fact]
    public void Examples_DisplayMappingAndPartialCopy()
    {
        var d = TextDisplayMapper.Map("a\r\nb\0\u007F\u0085\t \uD83D\uDE00\r");
        Assert.Equal("a\nb\u2400\u2421\u25AF\t \uD83D\uDE00\n", d.Display);
        Assert.Equal((1, 3), d.OriginalRange(1, 2)); // "\n" ↔ CRLF
        Assert.Equal((9, 11), d.OriginalRange(9, 10)); // 选中代理对的一半 → 整个代理对
        Assert.Equal((0, 12), d.OriginalRange(0, d.Display.Length));
        Assert.Equal("x\uFFFD", TextDisplayMapper.Map("x\uDC00").Display);
    }
}
