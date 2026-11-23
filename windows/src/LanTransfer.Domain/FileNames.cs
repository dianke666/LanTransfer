using System.Globalization;
using System.Text;

namespace LanTransfer.Domain;

/// <summary>接收端文件系统的命名约束（设计 C3.1）：长度上限 Android / iOS 为 255 个 UTF‑8 字节，Windows 为 255 个 UTF‑16 码元。</summary>
public enum FsProfile
{
    Android,
    Windows,
    Ios,
}

internal static class NameText
{
    public const int MaxLength = 255;

    private static readonly HashSet<string> Reserved =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(i => $"COM{i}"), .. Enumerable.Range(1, 9).Select(i => $"LPT{i}")];

    /// <summary>按 profile 计量的长度（前提：不含 Unpaired_Surrogate）。</summary>
    public static int Length(string s, FsProfile profile) => profile == FsProfile.Windows ? s.Length : Encoding.UTF8.GetByteCount(s);

    public static bool Fits(string s, FsProfile profile) => Length(s, profile) <= MaxLength;

    /// <summary>扩展名 = 最后一个 "." 及其后的部分；"." 为首字符或不存在时为空。</summary>
    public static int ExtensionStart(string s)
    {
        var i = s.LastIndexOf('.');
        return i <= 0 ? s.Length : i;
    }

    public static string DropLastCodePoint(string s)
    {
        if (s.Length == 0)
        {
            return s;
        }

        var n = s.Length >= 2 && char.IsLowSurrogate(s[^1]) && char.IsHighSurrogate(s[^2]) ? 2 : 1;
        return s[..^n];
    }

    public static int CodePoints(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }

            n++;
        }

        return n;
    }

    /// <summary>第一个 "." 之前的部分去除末尾空格后，不区分大小写地等于 Windows 保留名。</summary>
    public static bool IsReserved(string s)
    {
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        var head = (dot < 0 ? s : s[..dot]).TrimEnd(' ');
        // 保留名都是 ASCII；只做 ASCII 大小写折叠，保证两端结果一致
        return head.All(char.IsAscii) && Reserved.Contains(head.ToUpperInvariant());
    }
}

/// <summary>文件名处理（需求 16.4–16.10，设计 C3.1 的 S0–S2 与 L1–L4），与 Kotlin 侧 <c>FileNameSanitizer</c> 对应。</summary>
public static class FileNameSanitizer
{
    private const string Illegal = "\\/:*?\"<>|";

    /// <summary><paramref name="now"/> = 接收方开始接收该文件时的本地时间，只在需要回退名时使用（16.9）。</summary>
    public static string Sanitize(string raw, FsProfile profile, DateTime now)
    {
        // S0：每个 Unpaired_Surrogate 替换为 "_"
        var s0 = ReplaceUnpaired(raw);

        // S1：只保留最后一个 "/" 或 "\" 之后的部分；"." 或 ".." 视为空
        var cut = Math.Max(s0.LastIndexOf('/'), s0.LastIndexOf('\\'));
        var s1 = s0[(cut + 1)..];
        if (s1 is "." or "..")
        {
            s1 = string.Empty;
        }

        // S2：非法字符与控制字符替换为 "_"；记下扩展名 ext₀
        var sb = new StringBuilder(s1.Length);
        foreach (var c in s1)
        {
            sb.Append(Illegal.Contains(c, StringComparison.Ordinal) || c <= 0x1F || c == 0x7F ? '_' : c);
        }

        var s2 = sb.ToString();
        var ext0 = s2[NameText.ExtensionStart(s2)..];

        // L：循环执行 L1–L4 直到一整轮没有变化
        var name = s2;
        while (true)
        {
            var before = name;
            if (profile == FsProfile.Windows)
            {
                name = name.TrimEnd(' ', '.'); // L1
            }

            if (name is "" or "." or "..")
            {
                name = Fallback(now) + ext0; // L2
            }

            if (profile == FsProfile.Windows && NameText.IsReserved(name))
            {
                name = "_" + name; // L3
            }

            if (!NameText.Fits(name, profile))
            {
                name = ShortenOnce(name); // L4
            }

            if (name == before)
            {
                return name;
            }
        }
    }

    /// <summary>回退名 file_YYYYMMDD_HHMMSS。</summary>
    public static string Fallback(DateTime now) => "file_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>L4：删除主干末尾 1 个码点；主干只剩 1 个码点时，改为删除扩展名末尾 1 个码点。</summary>
    private static string ShortenOnce(string name)
    {
        var e = NameText.ExtensionStart(name);
        var stem = name[..e];
        var ext = name[e..];
        return NameText.CodePoints(stem) > 1 ? NameText.DropLastCodePoint(stem) + ext : stem + NameText.DropLastCodePoint(ext);
    }

    private static string ReplaceUnpaired(string s)
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

            sb.Append(char.IsSurrogate(c) ? '_' : c);
        }

        return sb.ToString();
    }
}

/// <summary>重名处理（需求 16.11），与 Kotlin 侧 <c>NameCollisionResolver</c> 对应。</summary>
public static class NameCollisionResolver
{
    /// <summary>返回 name 本身，或追加最小 " (n)" 后的名称；<paramref name="exists"/> 由 ReceiveStorage 提供（Windows 下不区分大小写）。</summary>
    public static string Resolve(string name, FsProfile profile, Func<string, bool> exists, int startAt = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startAt, 1);
        if (!exists(name))
        {
            return name;
        }

        var e = NameText.ExtensionStart(name);
        var stem = name[..e];
        var ext = name[e..];
        for (var n = startAt; ; n++)
        {
            var candidate = Candidate(stem, ext, n, profile);
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>主干′ + " (n)" + 扩展名：主干′ 是满足长度上限的最长主干前缀（至少 1 个码点），仍超限时按 16.8 截断扩展名。</summary>
    internal static string Candidate(string stem, string ext, int n, FsProfile profile)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $" ({n})");
        var s = stem;
        while (NameText.CodePoints(s) > 1 && !NameText.Fits(s + suffix + ext, profile))
        {
            s = NameText.DropLastCodePoint(s);
        }

        var x = ext;
        while (x.Length > 0 && !NameText.Fits(s + suffix + x, profile))
        {
            x = NameText.DropLastCodePoint(x);
        }

        return s + suffix + x;
    }
}
