using System.Text;
using LanTransfer.Protocol;

namespace LanTransfer.Domain;

/// <summary><see cref="DisplayNameRules.ValidateUserInput"/> 的结果（需求 4.4、4.5、4.8）。</summary>
public abstract record NameCheck
{
    /// <summary><see cref="Cleaned"/> 为清理后名称；<see cref="RemovedAny"/> 表示原输入中含有至少 1 个 Unpaired_Surrogate。</summary>
    public sealed record Ok(string Cleaned, bool RemovedAny) : NameCheck;

    public sealed record Empty : NameCheck;

    public sealed record TooLong : NameCheck;

    public sealed record HasControl : NameCheck;
}

/// <summary>设备显示名称规则（需求 4，设计 C3.2），与 Kotlin 侧 <c>DisplayNameRules</c> 对应。</summary>
public static class DisplayNameRules
{
    public const string DefaultAndroidName = "Android 设备";
    public const string DefaultWindowsName = "Windows 电脑";
    public const string DefaultIosName = "iOS 设备";

    /// <summary>Unicode White_Space 属性的码点（显式列出，不用 char.IsWhiteSpace）。</summary>
    public static bool IsWhiteSpace(char c) => c is (>= '\u0009' and <= '\u000D') or '\u0020' or '\u0085' or '\u00A0' or '\u1680'
        or (>= '\u2000' and <= '\u200A') or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000';

    /// <summary>删除全部 Unpaired_Surrogate，其余码元顺序不变（4.3、4.4）。</summary>
    public static string RemoveUnpairedSurrogates(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }

            if (!char.IsSurrogate(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>只去除首尾属于 White_Space 表的码点（White_Space 全部在 BMP 内）。</summary>
    public static string TrimWhiteSpace(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsWhiteSpace(s[start]))
        {
            start++;
        }

        while (end > start && IsWhiteSpace(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary>清理后名称 = TrimWhiteSpace(RemoveUnpairedSurrogates(s))。</summary>
    public static string Cleaned(string s) => TrimWhiteSpace(RemoveUnpairedSurrogates(s));

    private static bool IsControl(char c) => c <= '\u001F' || c is >= '\u007F' and <= '\u009F';

    /// <summary>型号或计算机名称 → 移除 → 去空白 → 取前 32 个码点；为空时用该设备类型的默认名称（4.3）。</summary>
    public static string DefaultName(string? modelOrComputerName, DeviceType type)
    {
        var c = Cleaned(modelOrComputerName ?? string.Empty);
        var head = string.Concat(c.EnumerateRunes().Take(ProtocolConstants.MaxNameCodePoints).Select(r => r.ToString()));
        return head.Length > 0 ? head : type switch
        {
            DeviceType.Android => DefaultAndroidName,
            DeviceType.Windows => DefaultWindowsName,
            DeviceType.Ios => DefaultIosName,
            _ => DefaultWindowsName,
        };
    }

    /// <summary>基于清理后名称判定：Ok(cleaned, removedAny) | Empty | TooLong | HasControl。</summary>
    public static NameCheck ValidateUserInput(string input)
    {
        var c = Cleaned(input);
        if (c.Length == 0)
        {
            return new NameCheck.Empty();
        }

        if (c.EnumerateRunes().Count() > ProtocolConstants.MaxNameCodePoints)
        {
            return new NameCheck.TooLong();
        }

        if (c.Any(IsControl))
        {
            return new NameCheck.HasControl();
        }

        return new NameCheck.Ok(c, RemoveUnpairedSurrogates(input).Length != input.Length);
    }

    /// <summary>从线上收到的名称只检查码点数 1–32（23.2）；控制字符原样显示。</summary>
    public static bool IsAcceptableRemote(string name) =>
        name.EnumerateRunes().Count() is >= ProtocolConstants.MinNameCodePoints and <= ProtocolConstants.MaxNameCodePoints;
}
