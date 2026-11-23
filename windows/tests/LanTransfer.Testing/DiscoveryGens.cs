using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

// 公告、二维码与版本的生成器（Property 5、8、10–12），与 Kotlin 侧 DiscoveryGens.kt 对应。
public static partial class Gens
{
    /// <summary>1–32 个码点、不含 Unpaired_Surrogate 的显示名称，偏向 32 个增补平面字符（最长编码）与中文。</summary>
    public static Gen<string> DisplayName() => Gen.Frequency(
        (3, Gen.Choose(1, 32).SelectMany(n => ScalarValue().ArrayOf(n)).Select(cps => TextCharClass.Cps(cps))),
        (1, Gen.Choose(1, 32).Select(n => TextCharClass.Cps(Enumerable.Range(0, n).Select(i => 0x1F600 + (i % 0x40)).ToArray()))),
        (1, Gen.Elements("小明的手机", "Pixel 9", "办公室电脑", string.Concat(Enumerable.Repeat("😀", 32)), "a", new string('名', 32))));

    public static Gen<DeviceId> DeviceIdGen() => Bytes16Gen().Select(b => new DeviceId(b));

    /// <summary>任意 u8.u8 版本。</summary>
    public static Gen<ProtocolVersion> ProtocolVersionGen() =>
        from a in Gen.Choose(0, 255) from b in Gen.Choose(0, 255) select new ProtocolVersion((byte)a, (byte)b);

    public static Gen<ushort> PortGen() => Gen.Frequency(
        (3, Gen.Elements<ushort>(42580, 42590, 42599, 1, 65535)),
        (2, Gen.Choose(1, 65535).Select(p => (ushort)p)));

    public static Gen<Announcement> AnnouncementGen() =>
        from id in DeviceIdGen()
        from name in DisplayName()
        from type in Gen.Elements(DeviceType.Android, DeviceType.Windows, DeviceType.Ios)
        from v in ProtocolVersionGen()
        from port in PortGen()
        select new Announcement(id, name, type, v, port);

    /// <summary>1–4 个互不相同的 IPv4 地址。</summary>
    public static Gen<ValueList<uint>> QrIpv4List() =>
        Gen.Choose(1, 4).SelectMany(n => AnyUInt().ArrayOf(n)).Select(a => new ValueList<uint>(a.Distinct()));

    public static Gen<QrPayload> QrPayloadGen() =>
        from id in DeviceIdGen()
        from name in DisplayName()
        from ips in QrIpv4List()
        from port in PortGen()
        from fp in BytesOfSize(32)
        from token in Bytes16Gen()
        select new QrPayload(id, name, ips, port, fp, token);

    public static Arbitrary<Announcement> Announcement() => Arb.From(AnnouncementGen());

    public static Arbitrary<QrPayload> QrPayload() => Arb.From(QrPayloadGen());

    /// <summary>
    /// 以 <paramref name="base"/> 中的一个有效公告为基础，只破坏其中一项约束（Property 9）；生成的数据报都不超过 512 字节
    /// （<see cref="InvalidAnnouncement.Oversize"/> 除外）。传入已在设备列表中的公告，可以检验“同一设备 ID 的非法数据报不刷新计时”。
    /// </summary>
    public static Gen<InvalidAnnouncement> InvalidAnnouncementGen(Gen<Announcement>? @base = null) =>
        from a in @base ?? AnnouncementGen()
        from kind in Gen.Elements(InvalidAnnouncement.Kinds.ToArray())
        from seed in Gen.Choose(0, int.MaxValue - 1)
        from badUtf8 in InvalidUtf8Gen()
        from nameCps in ScalarValue().ArrayOf(40)
        select MutateAnnouncement(a, kind, seed, badUtf8.Bytes, nameCps);

    private static byte[] TlvField(int tag, byte[] value) =>
        [(byte)tag, (byte)(value.Length >> 24), (byte)(value.Length >> 16), (byte)(value.Length >> 8), (byte)value.Length, .. value];

    private static List<(int Tag, byte[] Value)> AnnouncementFields(Announcement a) =>
    [
        (1, a.DeviceId.Value.ToArray()),
        (2, System.Text.Encoding.UTF8.GetBytes(a.DisplayName)),
        (3, [(byte)a.DeviceType]),
        (4, [a.Version.Major]),
        (5, [a.Version.Minor]),
        (6, [(byte)(a.Port >> 8), (byte)a.Port]),
    ];

    private static byte[] AssembleAnnouncement(List<(int Tag, byte[] Value)> fields) =>
        [.. "LTDA"u8, .. fields.SelectMany(f => TlvField(f.Tag, f.Value))];

    private static InvalidAnnouncement MutateAnnouncement(Announcement a, string kind, int seed, byte[] badUtf8, int[] nameCps)
    {
        var r = new Random(seed);
        var fields = AnnouncementFields(a);
        switch (kind)
        {
            case InvalidAnnouncement.Oversize:
            {
                var pad = ProtocolConstants.MaxAnnouncement + 1 - AssembleAnnouncement(fields).Length - 5 + r.Next(0, 88);
                fields.Add((0x7F, Enumerable.Range(0, pad).Select(i => (byte)(i * 31)).ToArray()));
                break;
            }

            case InvalidAnnouncement.BadMagic:
            {
                var b = AssembleAnnouncement(fields);
                b[r.Next(4)] ^= (byte)r.Next(1, 256);
                return new InvalidAnnouncement(b, kind);
            }

            case InvalidAnnouncement.MissingField:
                fields.RemoveAt(r.Next(fields.Count));
                break;
            case InvalidAnnouncement.BadUtf8:
                fields[1] = (2, badUtf8);
                if (AssembleAnnouncement(fields).Length > ProtocolConstants.MaxAnnouncement)
                {
                    // 前后缀过长时只保留一段非法字节
                    fields[1] = (2, [0xC0, 0x80]);
                }

                break;
            case InvalidAnnouncement.NameLength:
            {
                var n = r.Next(3) == 0 ? 0 : r.Next(33, 41);
                fields[1] = (2, System.Text.Encoding.UTF8.GetBytes(TextCharClass.Cps(nameCps[..n])));
                break;
            }

            case InvalidAnnouncement.BadDeviceType:
                fields[2] = (3, [(byte)(r.Next(2) == 0 ? 0 : r.Next(4, 256))]);
                break;
            case InvalidAnnouncement.PortZero:
                fields[5] = (6, [0, 0]);
                break;
            case InvalidAnnouncement.BadFieldLength:
            {
                int[] fixedSize = [0, 2, 3, 4, 5];
                var i = fixedSize[r.Next(fixedSize.Length)];
                var (tag, value) = fields[i];
                var delta = value.Length > 1 && r.Next(2) == 0 ? -r.Next(1, value.Length) : r.Next(1, 4);
                byte[] changed = delta < 0 ? value[..(value.Length + delta)] : [.. value, .. Enumerable.Repeat((byte)0x5A, delta)];
                fields[i] = (tag, r.Next(4) == 0 ? [] : changed);
                break;
            }

            case InvalidAnnouncement.Truncated:
            {
                var b = AssembleAnnouncement(fields);
                return new InvalidAnnouncement(b[..r.Next(b.Length)], kind);
            }

            default: // TagOrder
            {
                var i = r.Next(fields.Count - 1);
                if (r.Next(2) == 0)
                {
                    (fields[i], fields[i + 1]) = (fields[i + 1], fields[i]);
                }
                else
                {
                    fields.Insert(i + 1, fields[i]);
                }

                break;
            }
        }

        return new InvalidAnnouncement(AssembleAnnouncement(fields), kind);
    }
}

/// <summary>违反需求 23.2 某一项约束的公告数据报（Property 9）；<see cref="Kind"/> 的取值与 Kotlin 侧 <c>InvalidAnnouncement</c> 的常量一致。</summary>
public sealed record InvalidAnnouncement(byte[] Bytes, string Kind)
{
    /// <summary>有效字段 + 未知 tag 填充，总长 513–600 字节。</summary>
    public const string Oversize = "oversize";
    public const string BadMagic = "bad-magic";
    public const string MissingField = "missing-field";
    public const string BadUtf8 = "bad-utf8";

    /// <summary>名称为 0 个或 33–40 个码点。</summary>
    public const string NameLength = "name-length";
    public const string BadDeviceType = "bad-device-type";
    public const string PortZero = "port-zero";

    /// <summary>定长字段（device_id、device_type、版本、端口）的长度不对。</summary>
    public const string BadFieldLength = "bad-field-length";

    /// <summary>有效数据报截断到更短的长度（含不足 4 字节的 magic）。</summary>
    public const string Truncated = "truncated";

    /// <summary>相邻字段次序颠倒或同一 tag 重复出现。</summary>
    public const string TagOrder = "tag-order";

    public static IReadOnlyList<string> Kinds { get; } =
        [Oversize, BadMagic, MissingField, BadUtf8, NameLength, BadDeviceType, PortZero, BadFieldLength, Truncated, TagOrder];

    public override string ToString() => $"InvalidAnnouncement(kind={Kind}, bytes={Convert.ToHexStringLower(Bytes)})";
}
