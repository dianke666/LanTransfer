namespace LanTransfer.Protocol;

/// <summary>Discovery_Announcement 的模型（规范第 3 节）。</summary>
public sealed record Announcement(DeviceId DeviceId, string DisplayName, DeviceType DeviceType, ProtocolVersion Version, ushort Port);

/// <summary>Discovery_Announcement 编解码（需求 5.6、23.1–23.3），与 Kotlin 侧 <c>AnnouncementCodec</c> 对应。</summary>
public static class AnnouncementCodec
{
    private static ReadOnlySpan<byte> Magic => "LTDA"u8;

    /// <summary>名称是否为 1–32 个码点、不含 Unpaired_Surrogate。</summary>
    internal static bool NameOk(string name) =>
        !StrictUtf8.HasUnpairedSurrogate(name) &&
        name.EnumerateRunes().Count() is >= ProtocolConstants.MinNameCodePoints and <= ProtocolConstants.MaxNameCodePoints;

    /// <summary>编码；前置条件：名称 1–32 个码点且不含 Unpaired_Surrogate，端口 1–65535。结果不超过 512 字节。</summary>
    public static byte[] Encode(Announcement a)
    {
        if (!NameOk(a.DisplayName) || a.Port == 0)
        {
            throw new ArgumentException("display name must be 1..32 code points and port must be 1..65535", nameof(a));
        }

        var w = new TlvWriter();
        w.Bytes16(1, a.DeviceId.Value);
        w.String(2, a.DisplayName);
        w.U8(3, (byte)a.DeviceType);
        w.U8(4, a.Version.Major);
        w.U8(5, a.Version.Minor);
        w.U16(6, a.Port);
        byte[] output = [.. Magic, .. w.ToArray()];
        return output.Length <= ProtocolConstants.MaxAnnouncement ? output : throw new InvalidOperationException("announcement exceeds 512 bytes");
    }

    /// <summary>
    /// 解码；以下情况返回 null（丢弃，需求 23.2）：长度超过 512 字节、magic 不符、Struct 不合法或缺少字段、
    /// 名称不是合法 UTF‑8 或码点数不在 1–32 之间、设备类型不在 {1, 2} 中、端口为 0。未知 tag 跳过。
    /// </summary>
    public static Announcement? Decode(byte[] datagram)
    {
        if (datagram.Length > ProtocolConstants.MaxAnnouncement || datagram.Length < Magic.Length || !datagram.AsSpan(0, 4).SequenceEqual(Magic))
        {
            return null;
        }

        try
        {
            var s = new TlvStruct(datagram, 4, datagram.Length - 4);
            var name = s.String(2, "display_name");
            var type = s.U8(3, "device_type");
            var port = s.U16(6, "port");
            if (!NameOk(name) || !Enum.IsDefined((DeviceType)type) || port == 0)
            {
                return null;
            }

            return new Announcement(new DeviceId(s.Bytes16(1, "device_id")), name, (DeviceType)type, new ProtocolVersion(s.U8(4, "proto_major"), s.U8(5, "proto_minor")), port);
        }
        catch (MalformedException)
        {
            return null;
        }
    }
}

/// <summary>连接前导的版本协商结果（规范第 5 节）。</summary>
public abstract record Negotiation
{
    public sealed record Compatible(ProtocolVersion Version) : Negotiation;

    public sealed record Incompatible(ProtocolVersion Local, ProtocolVersion Remote) : Negotiation;
}

/// <summary>连接前导 "LANT" ‖ major ‖ minor（需求 24.4–24.6），与 Kotlin 侧 <c>PreambleCodec</c> 对应。</summary>
public static class PreambleCodec
{
    public const int Size = 6;

    private static ReadOnlySpan<byte> Magic => "LANT"u8;

    public static byte[] Encode(ProtocolVersion v) => [.. Magic, v.Major, v.Minor];

    /// <summary>长度不是 6 字节或 magic 不符时返回 null。</summary>
    public static ProtocolVersion? Decode(ReadOnlySpan<byte> b) =>
        b.Length == Size && b[..4].SequenceEqual(Magic) ? new ProtocolVersion(b[4], b[5]) : null;

    /// <summary>主版本相同时协商为 (major, min(minor))，否则不兼容。</summary>
    public static Negotiation Negotiate(ProtocolVersion local, ProtocolVersion remote) =>
        local.Major != remote.Major
            ? new Negotiation.Incompatible(local, remote)
            : new Negotiation.Compatible(new ProtocolVersion(local.Major, Math.Min(local.Minor, remote.Minor)));
}
