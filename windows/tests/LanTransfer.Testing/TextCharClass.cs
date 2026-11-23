using System.Text;

namespace LanTransfer.Testing;

/// <summary>
/// 需求 12.2–12.4 列出的特殊字符类，与 Kotlin 侧 <c>com.lantransfer.testing.TextCharClass</c> 一一对应。
/// <see cref="Label"/> 与 <c>protocol/test-vectors/text-messages.json</c> 中的 <c>class</c> 标签一致，供覆盖检查（任务 6.3）使用。
/// <see cref="Samples"/> 是该类的代表片段，全部由合法的 Unicode 标量值组成（不含 Unpaired_Surrogate）。
/// </summary>
public sealed record TextCharClass(string Label, IReadOnlyList<string> Samples)
{
    public static readonly TextCharClass Lf = new("lf", ["\n"]);
    public static readonly TextCharClass Cr = new("cr", ["\r"]);
    public static readonly TextCharClass Crlf = new("crlf", ["\r\n"]);
    public static readonly TextCharClass Tab = new("tab", ["\t"]);
    public static readonly TextCharClass ConsecutiveSpaces = new("consecutive-spaces", ["  ", "   ", "      "]);
    public static readonly TextCharClass EdgeWhitespace = new("edge-whitespace", [" ", "\t", "\n", "\r\n", "\u00A0", "\u3000"]);
    public static readonly TextCharClass Nbsp = new("nbsp", ["\u00A0"]);
    public static readonly TextCharClass IdeographicSpace = new("ideographic-space", ["\u3000"]);
    public static readonly TextCharClass Nul = new("nul", ["\0"]);
    public static readonly TextCharClass C0Control = new("c0-control", Range(0x01, 0x1F));
    public static readonly TextCharClass Del = new("del", ["\u007F"]);
    public static readonly TextCharClass C1Control = new("c1-control", Range(0x80, 0x9F));

    public static readonly TextCharClass ZwjSequence = new("zwj-sequence",
    [
        Cps(0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467), // 家庭
        Cps(0x1F3F3, 0xFE0F, 0x200D, 0x1F308), // 彩虹旗
        Cps(0x1F469, 0x1F3FD, 0x200D, 0x1F4BB), // 带肤色的程序员
    ]);

    public static readonly TextCharClass SkinToneModifier = new("skin-tone-modifier", [Cps(0x1F44D, 0x1F3FD), Cps(0x1F44B, 0x1F3FF), Cps(0x1F9D1, 0x1F3FB)]);
    public static readonly TextCharClass VariationSelector16 = new("variation-selector-16", [Cps(0x2764, 0xFE0F), Cps(0x263A, 0xFE0F), Cps(0x0031, 0xFE0F, 0x20E3)]);
    public static readonly TextCharClass ZeroWidth = new("zero-width", ["\u200B", "\u200C", "\u200D", "\u2060"]);
    public static readonly TextCharClass Bom = new("bom", ["\uFEFF"]);
    public static readonly TextCharClass CombiningMark = new("combining-mark", ["e\u0301", "a\u0308", "\u20DD", "\u0915\u094D\u0937", "\u1100\u1161\u11A8"]);
    public static readonly TextCharClass SupplementaryPlane = new("supplementary-plane", [Cps(0x10000), Cps(0x1F600), Cps(0x20000), Cps(0x2A6D6), Cps(0x1D11E)]);
    public static readonly TextCharClass PrivateUse = new("private-use", [Cps(0xE000), Cps(0xF8FF), Cps(0xF0000), Cps(0xFFFFD), Cps(0x100000), Cps(0x10FFFD)]);

    /// <summary>截至 Unicode 16.0 仍未分配的码点（希腊文区空位、第 5 平面）。</summary>
    public static readonly TextCharClass Unassigned = new("unassigned", [Cps(0x0378), Cps(0x0379), Cps(0x50000), Cps(0x8FFFD)]);

    public static readonly TextCharClass Noncharacter = new("noncharacter", [Cps(0xFFFE), Cps(0xFFFF), Cps(0xFDD0), Cps(0xFDEF), Cps(0x1FFFE), Cps(0x10FFFF)]);

    /// <summary>全部字符类，顺序与 Kotlin 侧的枚举顺序一致。</summary>
    public static readonly IReadOnlyList<TextCharClass> All =
    [
        Lf, Cr, Crlf, Tab, ConsecutiveSpaces, EdgeWhitespace, Nbsp, IdeographicSpace, Nul, C0Control, Del, C1Control,
        ZwjSequence, SkinToneModifier, VariationSelector16, ZeroWidth, Bom, CombiningMark, SupplementaryPlane, PrivateUse,
        Unassigned, Noncharacter,
    ];

    public static IReadOnlyList<string> Labels { get; } = All.Select(c => c.Label).ToArray();

    public static TextCharClass? ByLabel(string label) => All.FirstOrDefault(c => c.Label == label);

    /// <summary>若干 Unicode 码点拼成的字符串。</summary>
    public static string Cps(params int[] codePoints)
    {
        var sb = new StringBuilder();
        foreach (var cp in codePoints)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }

        return sb.ToString();
    }

    private static string[] Range(int first, int last) =>
        Enumerable.Range(first, last - first + 1).Select(cp => ((char)cp).ToString()).ToArray();
}
