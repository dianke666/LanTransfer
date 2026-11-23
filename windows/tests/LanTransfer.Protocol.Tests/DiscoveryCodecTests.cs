using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>公告、二维码、前导的示例测试（属性测试见 Property 5、8、10–12），与 Kotlin 侧 DiscoveryCodecTest 对应。</summary>
public sealed class DiscoveryCodecTests
{
    private static readonly DeviceId Id = new(Bytes16.OfHex("00112233445566778899aabbccddeeff"));

    private static byte[] Raw(string name, int type, int port)
    {
        var w = new TlvWriter();
        w.Bytes16(1, Id.Value);
        w.String(2, name);
        w.U8(3, (byte)type);
        w.U8(4, 1);
        w.U8(5, 0);
        w.U16(6, (ushort)port);
        return [.. "LTDA"u8, .. w.ToArray()];
    }

    [Fact]
    public void Announcement_with_32_emoji_round_trips_within_512_bytes()
    {
        var a = new Announcement(Id, string.Concat(Enumerable.Repeat("😀", 32)), DeviceType.Android, ProtocolVersion.V1_0, 42580);
        var bytes = AnnouncementCodec.Encode(a);
        Assert.True(bytes.Length <= 512);
        Assert.Equal("4c544441", Convert.ToHexStringLower(bytes.AsSpan(0, 4)));
        Assert.Equal(a, AnnouncementCodec.Decode(bytes));
    }

    [Fact]
    public void Invalid_announcements_are_dropped()
    {
        var good = AnnouncementCodec.Encode(new Announcement(Id, "Pixel", DeviceType.Windows, ProtocolVersion.V1_0, 1));
        Assert.Null(AnnouncementCodec.Decode(good[..^1]));
        Assert.Null(AnnouncementCodec.Decode([.. "LTDB"u8, .. good.AsSpan(4)]));
        Assert.Null(AnnouncementCodec.Decode([.. good, .. new byte[600]]));
        Assert.Null(AnnouncementCodec.Decode(Raw("a", 1, 0)));
        Assert.Null(AnnouncementCodec.Decode(Raw("a", 4, 1)));
        Assert.Equal(DeviceType.Ios, AnnouncementCodec.Decode(Raw("a", 3, 1))!.DeviceType);
        Assert.Null(AnnouncementCodec.Decode(Raw(new string('一', 33), 1, 1)));
        Assert.Null(AnnouncementCodec.Decode(Raw("", 1, 1)));
        Assert.Equal((ushort)1, AnnouncementCodec.Decode(Raw("a", 1, 1))!.Port);
    }

    [Fact]
    public void Qr_with_four_ips_and_32_char_name_round_trips()
    {
        var p = new QrPayload(
            Id, new string('名', 32), new ValueList<uint>([0xC0A80114, 0x0A000005, 0xAC100001, 0xC0A82B01]),
            42580, Bytes.Of(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()), Bytes16.OfHex("ffeeddccbbaa99887766554433221100"));
        var text = QrCodec.Encode(p);
        Assert.True(text.Length <= 512);
        Assert.Equal(QrDecodeResult.Ok(p), QrCodec.Decode(text));
    }

    [Fact]
    public void Base32_is_canonical_and_strict()
    {
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.Equal("foobar"u8.ToArray(), Base32.Decode("MZXW6YTBOI"));
        Assert.Equal("", Base32.Encode([]));
        Assert.Null(Base32.Decode("MZXW6YTBOJ"));
        Assert.Null(Base32.Decode("MZXW6YTBO"));
        Assert.Null(Base32.Decode("mzxw6ytboi"));
        Assert.Null(Base32.Decode("MZXW6YTBOI======"));
    }

    [Fact]
    public void Qr_error_kinds()
    {
        Assert.Equal(QrError.BadPrefix, QrCodec.Decode("LT2:AAAA").Error);
        Assert.Equal(QrError.TooLong, QrCodec.Decode("LT1:" + new string('A', 600)).Error);
        Assert.Equal(QrError.BadBase32, QrCodec.Decode("LT1:aaaa").Error);
        Assert.Equal(QrError.Malformed, QrCodec.Decode("LT1:").Error);
    }

    [Fact]
    public void Preamble_and_negotiation()
    {
        Assert.Equal("4c414e540100", Convert.ToHexStringLower(PreambleCodec.Encode(ProtocolVersion.V1_0)));
        Assert.Null(PreambleCodec.Decode(Convert.FromHexString("4c414e550100")));
        Assert.Equal(new Negotiation.Compatible(new ProtocolVersion(1, 0)), PreambleCodec.Negotiate(new ProtocolVersion(1, 2), new ProtocolVersion(1, 0)));
        Assert.IsType<Negotiation.Incompatible>(PreambleCodec.Negotiate(new ProtocolVersion(1, 0), new ProtocolVersion(2, 0)));
    }

    [Fact]
    public void Generators_produce_encodable_values()
    {
        Assert.All(Gens.AnnouncementGen().Draw(300), a => Assert.Equal(a, AnnouncementCodec.Decode(AnnouncementCodec.Encode(a))));
    }
}
