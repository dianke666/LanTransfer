using System.Text;
using LanTransfer.Protocol;

namespace LanTransfer.Domain;

/// <summary><see cref="TextRules.CheckOutgoing"/> 的结果，检查顺序为 Empty → HasUnpairedSurrogate → TooLong → Ok（11.2、12.6、11.9）。</summary>
public abstract record OutgoingCheck
{
    public sealed record Empty : OutgoingCheck;

    public sealed record HasUnpairedSurrogate : OutgoingCheck;

    public sealed record TooLong(long Utf8Bytes) : OutgoingCheck;

    public sealed record Ok(Bytes Utf8) : OutgoingCheck;
}

/// <summary><see cref="TextRules.Collapse"/> 的结果：完整显示，或只显示 [0, PrefixEnd) 并提供“展开”。</summary>
public abstract record Collapse
{
    public sealed record Full : Collapse;

    public sealed record Collapsed(int PrefixEnd) : Collapse;
}

/// <summary>文字规则（需求 11、12、21.6，设计 C3.3），与 Kotlin 侧 <c>TextRules</c> 对应。</summary>
public static class TextRules
{
    public const int CollapseLines = 20;
    public const int CollapseCodePoints = 1000;
    public const int PreviewCodePoints = 100;

    public static OutgoingCheck CheckOutgoing(string text)
    {
        if (text.Length == 0)
        {
            return new OutgoingCheck.Empty();
        }

        if (StrictUtf8.HasUnpairedSurrogate(text))
        {
            return new OutgoingCheck.HasUnpairedSurrogate();
        }

        var n = StrictUtf8.EncodedLength(text);
        return n > ProtocolConstants.MaxTextBytes ? new OutgoingCheck.TooLong(n) : new OutgoingCheck.Ok(Bytes.Of(StrictUtf8.Encode(text)));
    }

    /// <summary>每个 Unpaired_Surrogate → U+FFFD，其余码元不变（12.10）。</summary>
    public static string ReplaceUnpaired(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            sb.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }

        return sb.ToString();
    }

    /// <summary>LF 数 + 其后不是 LF 的 CR 数 + 1（11.10）。</summary>
    public static int LineCount(string text)
    {
        var n = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>码点数：代理对计 1 个，单独的代理码元也计 1 个（与 Java 的 codePointCount 一致）。</summary>
    internal static int CodePointCount(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            n++;
        }

        return n;
    }

    /// <summary>
    /// 行数超过 20 或码点数超过 1000 时折叠；折叠前缀为同时满足“不超过 20 行、不超过 1000 个码点、
    /// 结束于码点边界、不切开 CRLF”的最长前缀（设计 C3.3）。
    /// </summary>
    public static Collapse Collapse(string text)
    {
        if (LineCount(text) <= CollapseLines && CodePointCount(text) <= CollapseCodePoints)
        {
            return new Domain.Collapse.Full();
        }

        // 前缀的行数与码点数都随长度单调不减：找到最后一个满足两项上限的码点边界
        int best = 0, lines = 1, cps = 0, i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var width = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var newLines = lines + (c == '\n' && i > 0 && text[i - 1] == '\r' ? 0 : c is '\n' or '\r' ? 1 : 0);
            var newCps = cps + 1;
            if (newLines > CollapseLines || newCps > CollapseCodePoints)
            {
                break;
            }

            lines = newLines;
            cps = newCps;
            i += width;
            best = i;
        }

        // 不切开 CRLF：前缀以 CR 结尾而下一个是 LF 时，退到 CR 之前
        if (best > 0 && best < text.Length && text[best - 1] == '\r' && text[best] == '\n')
        {
            best--;
        }

        return new Domain.Collapse.Collapsed(best);
    }

    /// <summary>前 100 个码点，超出时以 "…" 结尾（21.6）。</summary>
    public static string NotificationPreview(string text)
    {
        int cps = 0, i = 0;
        while (i < text.Length && cps < PreviewCodePoints)
        {
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            cps++;
        }

        return i >= text.Length ? text : text[..i] + "…";
    }
}

/// <summary>
/// 显示文本与“显示码元 → 原文”映射（设计 C3.3），与 Kotlin 侧 <c>DisplayText</c> 对应。每个映射单元对应原文中的一段
/// （CRLF、代理对或单个码元）与显示中的一段；<see cref="OriginalRange"/> 先把选区扩展到完整的映射单元，再返回对应的原文 UTF‑16 范围。
/// </summary>
public sealed class DisplayText
{
    private readonly int[] _unitOfDisplay;
    private readonly int[] _unitOrigStart;
    private readonly int[] _unitOrigEnd;
    private readonly int _originalLength;

    internal DisplayText(string display, int[] unitOfDisplay, int[] unitOrigStart, int[] unitOrigEnd, int originalLength)
    {
        Display = display;
        _unitOfDisplay = unitOfDisplay;
        _unitOrigStart = unitOrigStart;
        _unitOrigEnd = unitOrigEnd;
        _originalLength = originalLength;
    }

    public string Display { get; }

    /// <summary>显示选区 [selStart, selEnd) 对应的原文范围 [Start, End)。</summary>
    public (int Start, int End) OriginalRange(int selStart, int selEnd)
    {
        if (selStart < 0 || selStart > selEnd || selEnd > Display.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(selStart), "selection out of range");
        }

        if (selStart == selEnd)
        {
            var p = selStart == Display.Length ? _originalLength : _unitOrigStart[_unitOfDisplay[selStart]];
            return (p, p);
        }

        return (_unitOrigStart[_unitOfDisplay[selStart]], _unitOrigEnd[_unitOfDisplay[selEnd - 1]]);
    }
}

/// <summary>文字显示映射（需求 12.8、13.3、13.4），与 Kotlin 侧 <c>TextDisplayMapper</c> 对应。</summary>
public static class TextDisplayMapper
{
    private const char C1Placeholder = '\u25AF'; // ▯

    public static DisplayText Map(string original)
    {
        var display = new StringBuilder(original.Length);
        var unitOfDisplay = new List<int>(original.Length);
        var starts = new List<int>();
        var ends = new List<int>();

        void Unit(int origStart, int origEnd, string shown)
        {
            var u = starts.Count;
            starts.Add(origStart);
            ends.Add(origEnd);
            display.Append(shown);
            for (var k = 0; k < shown.Length; k++)
            {
                unitOfDisplay.Add(u);
            }
        }

        var i = 0;
        while (i < original.Length)
        {
            var c = original[i];
            if (c == '\r' && i + 1 < original.Length && original[i + 1] == '\n')
            {
                Unit(i, i + 2, "\n");
                i += 2;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < original.Length && char.IsLowSurrogate(original[i + 1]))
            {
                Unit(i, i + 2, original.Substring(i, 2));
                i += 2;
            }
            else
            {
                var shown = c switch
                {
                    '\r' or '\n' => "\n",
                    '\t' => "\t",
                    <= '\u001F' => ((char)(0x2400 + c)).ToString(),
                    '\u007F' => "\u2421",
                    >= '\u0080' and <= '\u009F' => C1Placeholder.ToString(),
                    _ when char.IsSurrogate(c) => "\uFFFD",
                    _ => c.ToString(),
                };
                Unit(i, i + 1, shown);
                i++;
            }
        }

        return new DisplayText(display.ToString(), [.. unitOfDisplay], [.. starts], [.. ends], original.Length);
    }
}
