using System.Buffers.Binary;
using System.Text;

namespace LanTransfer.Protocol;

/// <summary>
/// QR_Payload 的模型（规范第 4 节），与 Kotlin 侧 <c>QrPayload</c> 对应。<see cref="Ipv4List"/> 中每个元素是一个 IPv4 地址（大端），
/// 按优先级排列、互不相同、1–4 个；<see cref="Fingerprint"/> 为 SHA‑256(显示方身份公钥)，<see cref="Token"/> 为 Pairing_Token。
/// </summary>
public sealed record QrPayload(DeviceId DeviceId, string DisplayName, ValueList<uint> Ipv4List, ushort Port, Bytes Fingerprint, Bytes16 Token);

/// <summary>二维码文本解析失败的类别（只用于诊断；任何失败都不输出部分结果）。</summary>
public enum QrError
{
    TooLong,
    BadPrefix,
    BadBase32,
    Malformed,
    InvalidField,
}

/// <summary><see cref="QrCodec.Decode"/> 的结果：成功时 <see cref="Payload"/> 非 null，否则 <see cref="Error"/> 非 null。</summary>
public readonly record struct QrDecodeResult(QrPayload? Payload, QrError? Error)
{
    public static QrDecodeResult Ok(QrPayload p) => new(p, null);

    public static QrDecodeResult Fail(QrError e) => new(null, e);
}

/// <summary>RFC 4648 Base32（大写、无填充），严格解码（规范第 4 节）。</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(((b.Length * 8) + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var x in b)
        {
            buffer = (buffer << 8) | x;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }

            buffer &= 0xFF; // 只需保留未输出的低位
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return sb.ToString();
    }

    /// <summary>只接受 A–Z、2–7；长度对 8 取模必须属于 {0, 2, 4, 5, 7}；末尾多余比特必须为 0。否则返回 null。</summary>
    public static byte[]? Decode(string s)
    {
        if (s.Length % 8 is not (0 or 2 or 4 or 5 or 7))
        {
            return null;
        }

        var output = new byte[s.Length * 5 / 8];
        int buffer = 0, bits = 0, p = 0;
        foreach (var c in s)
        {
            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }

            buffer = ((buffer << 5) | v) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                output[p++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return bits > 0 && (buffer & ((1 << bits) - 1)) != 0 ? null : output;
    }
}

/// <summary>QR_Payload 文本编解码：<c>LT1:</c> ‖ Base32(QrStruct)（需求 8.3、23.4–23.8），与 Kotlin 侧 <c>QrCodec</c> 对应。</summary>
public static class QrCodec
{
    public const string Prefix = "LT1:";

    /// <summary>编码；前置条件：字段满足规范第 4 节的约束。</summary>
    public static string Encode(QrPayload p)
    {
        if (!ValidFields(p))
        {
            throw new ArgumentException("invalid QR payload fields", nameof(p));
        }

        var w = new TlvWriter();
        w.Bytes16(1, p.DeviceId.Value);
        w.String(2, p.DisplayName);
        var ips = new byte[4 * p.Ipv4List.Count];
        for (var i = 0; i < p.Ipv4List.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(ips.AsSpan(4 * i), p.Ipv4List[i]);
        }

        w.Bytes(3, ips);
        w.U16(4, p.Port);
        w.Bytes(5, p.Fingerprint.Span);
        w.Bytes16(6, p.Token);
        var text = Prefix + Base32.Encode(w.ToArray());
        return text.Length <= ProtocolConstants.MaxQrText ? text : throw new InvalidOperationException("QR text exceeds 512 bytes");
    }

    /// <summary>解析；整体成功或整体失败，失败时不输出任何部分结果（需求 23.6）。未知 tag 跳过。</summary>
    public static QrDecodeResult Decode(string text)
    {
        if (text.Length > ProtocolConstants.MaxQrText)
        {
            return QrDecodeResult.Fail(QrError.TooLong);
        }

        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return QrDecodeResult.Fail(QrError.BadPrefix);
        }

        if (Base32.Decode(text[Prefix.Length..]) is not { } raw)
        {
            return QrDecodeResult.Fail(QrError.BadBase32);
        }

        QrPayload payload;
        try
        {
            var s = new TlvStruct(raw, 0, raw.Length);
            var ipBytes = s.Bytes(3, "ipv4_list");
            if (ipBytes.Length % 4 != 0)
            {
                return QrDecodeResult.Fail(QrError.InvalidField);
            }

            var ips = Enumerable.Range(0, ipBytes.Length / 4).Select(i => BinaryPrimitives.ReadUInt32BigEndian(ipBytes.Span[(4 * i)..]));
            payload = new QrPayload(
                new DeviceId(s.Bytes16(1, "device_id")),
                s.String(2, "display_name"),
                new ValueList<uint>(ips.ToArray()),
                s.U16(4, "port"),
                s.BytesFixed(5, "fingerprint", 32),
                s.Bytes16(6, "token"));
        }
        catch (MalformedException)
        {
            return QrDecodeResult.Fail(QrError.Malformed);
        }

        return ValidFields(payload) ? QrDecodeResult.Ok(payload) : QrDecodeResult.Fail(QrError.InvalidField);
    }

    private static bool ValidFields(QrPayload p) =>
        AnnouncementCodec.NameOk(p.DisplayName) &&
        p.Ipv4List.Count is >= 1 and <= ProtocolConstants.MaxQrIps && p.Ipv4List.Distinct().Count() == p.Ipv4List.Count &&
        p.Port != 0 && p.Fingerprint.Length == 32;
}
