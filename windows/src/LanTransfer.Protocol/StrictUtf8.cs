using System.Text;

namespace LanTransfer.Protocol;

/// <summary>严格 UTF‑8 编解码（规范 2.1、2.4），与 Kotlin 侧 <c>StrictUtf8</c> 对应；按 Unicode 标准表 3‑7 自行实现。</summary>
public static class StrictUtf8
{
    /// <summary>编码为标准 UTF‑8。前置条件：<paramref name="s"/> 不含 Unpaired_Surrogate，否则抛出 <see cref="ArgumentException"/>。</summary>
    public static byte[] Encode(string s)
    {
        var output = new byte[EncodedLength(s)];
        var p = 0;
        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }

            if (cp < 0x80)
            {
                output[p++] = (byte)cp;
            }
            else if (cp < 0x800)
            {
                output[p++] = (byte)(0xC0 | (cp >> 6));
                output[p++] = (byte)(0x80 | (cp & 0x3F));
            }
            else if (cp < 0x10000)
            {
                output[p++] = (byte)(0xE0 | (cp >> 12));
                output[p++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                output[p++] = (byte)(0x80 | (cp & 0x3F));
            }
            else
            {
                output[p++] = (byte)(0xF0 | (cp >> 18));
                output[p++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                output[p++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                output[p++] = (byte)(0x80 | (cp & 0x3F));
            }
        }

        return output;
    }

    /// <summary>UTF‑8 编码长度；含 Unpaired_Surrogate 时抛出 <see cref="ArgumentException"/>。</summary>
    public static int EncodedLength(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 0x80)
            {
                n += 1;
            }
            else if (c < 0x800)
            {
                n += 2;
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    throw new ArgumentException($"unpaired surrogate at index {i}", nameof(s));
                }

                n += 4;
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new ArgumentException($"unpaired surrogate at index {i}", nameof(s));
            }
            else
            {
                n += 3;
            }
        }

        return n;
    }

    /// <summary>严格解码；不合法（截断、非最短形式、代理项、&gt; U+10FFFF、多余续字节）时返回 null。</summary>
    public static string? Decode(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length);
        var i = 0;
        while (i < b.Length)
        {
            int b0 = b[i];
            if (b0 < 0x80)
            {
                sb.Append((char)b0);
                i++;
                continue;
            }

            // Unicode 标准表 3-7：按首字节确定长度与第二字节的合法范围
            int need, lo, hi;
            switch (b0)
            {
                case >= 0xC2 and <= 0xDF: need = 1; lo = 0x80; hi = 0xBF; break;
                case 0xE0: need = 2; lo = 0xA0; hi = 0xBF; break;
                case (>= 0xE1 and <= 0xEC) or 0xEE or 0xEF: need = 2; lo = 0x80; hi = 0xBF; break;
                case 0xED: need = 2; lo = 0x80; hi = 0x9F; break;
                case 0xF0: need = 3; lo = 0x90; hi = 0xBF; break;
                case >= 0xF1 and <= 0xF3: need = 3; lo = 0x80; hi = 0xBF; break;
                case 0xF4: need = 3; lo = 0x80; hi = 0x8F; break;
                default: return null;
            }

            if (b.Length - i - 1 < need)
            {
                return null; // 截断
            }

            int b1 = b[i + 1];
            if (b1 < lo || b1 > hi)
            {
                return null;
            }

            var cp = ((b0 & (0x3F >> need)) << 6) | (b1 & 0x3F);
            for (var k = 2; k <= need; k++)
            {
                int bk = b[i + k];
                if ((bk & 0xC0) != 0x80)
                {
                    return null;
                }

                cp = (cp << 6) | (bk & 0x3F);
            }

            sb.Append(char.ConvertFromUtf32(cp));
            i += need + 1;
        }

        return sb.ToString();
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
}
