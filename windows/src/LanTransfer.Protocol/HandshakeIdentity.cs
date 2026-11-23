namespace LanTransfer.Protocol;

/// <summary>握手 msg3 中的连接意图（规范第 6 节）。</summary>
public enum Intent : byte
{
    Session = 1,
    Pair = 2,
    QrPair = 3,
}

/// <summary>msg2 / msg3 的载荷。<see cref="Intent"/> 只在 msg3 中出现。显示名称由接收方按显示名称规则另行检查。</summary>
public sealed record HandshakeIdentity(DeviceId DeviceId, string DisplayName, DeviceType DeviceType, Intent? Intent = null);

/// <summary>HandshakeIdentity（Struct：1 device_id、2 display_name、3 device_type、4 intent）编解码，与 Kotlin 侧 <c>HandshakeIdentityCodec</c> 对应。</summary>
public static class HandshakeIdentityCodec
{
    public static byte[] Encode(HandshakeIdentity id)
    {
        var w = new TlvWriter();
        w.Bytes16(1, id.DeviceId.Value);
        w.String(2, id.DisplayName);
        w.U8(3, (byte)id.DeviceType);
        if (id.Intent is { } intent)
        {
            w.Enum(4, intent);
        }

        return w.ToArray();
    }

    /// <summary>解码；Struct 不合法、缺少字段、设备类型或 intent 未定义，或 intent 的出现与 <paramref name="expectIntent"/> 不符时返回 null。</summary>
    public static HandshakeIdentity? Decode(byte[] b, bool expectIntent, ProtocolVersion? version = null)
    {
        try
        {
            var s = new TlvStruct(b, 0, b.Length);
            var type = s.U8(3, "device_type");
            if (!Enum.IsDefined((DeviceType)type) || s.Has(4) != expectIntent)
            {
                return null;
            }

            return new HandshakeIdentity(
                new DeviceId(s.Bytes16(1, "device_id")),
                s.String(2, "display_name"),
                (DeviceType)type,
                expectIntent ? s.Enum<Intent>(4, "intent", version ?? ProtocolVersion.Current) : null);
        }
        catch (MalformedException)
        {
            return null;
        }
    }
}
