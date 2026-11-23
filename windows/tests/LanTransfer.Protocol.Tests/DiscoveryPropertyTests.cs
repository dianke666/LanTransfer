using System.Buffers.Binary;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>Property 5、8、10、11、12 的 C# 实现，与 Kotlin 侧 DiscoveryPropertiesTest 对应。</summary>
public sealed class DiscoveryPropertyTests
{
    private sealed record Field(int Tag, byte[] Value);

    private static List<Field> SplitFields(byte[] b, int offset)
    {
        var output = new List<Field>();
        var p = offset;
        while (p < b.Length)
        {
            int tag = b[p];
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 1));
            output.Add(new Field(tag, b[(p + 5)..(p + 5 + len)]));
            p += 5 + len;
        }

        return output;
    }

    private static byte[] JoinFields(IEnumerable<Field> fields)
    {
        var w = new TlvWriter();
        foreach (var f in fields.OrderBy(f => f.Tag))
        {
            w.Bytes(f.Tag, f.Value);
        }

        return w.ToArray();
    }

    /// <summary>每种消息已定义的最大 tag（1..max 都是已定义字段；0 与 max 之后的 tag 都是未定义的）。</summary>
    private static int MaxDefinedTag(MessageType t) => t switch
    {
        MessageType.Heartbeat or MessageType.PairComplete => 0,
        MessageType.Text or MessageType.FileEnd or MessageType.FileAck => 3,
        MessageType.FileOffer or MessageType.FileOfferReply or MessageType.FileReadFailed or MessageType.TransferCancel
            or MessageType.ResumeState or MessageType.ResumePlan => 2,
        MessageType.FileChunk or MessageType.FileResult => 4,
        _ => 1,
    };

    /// <summary>待插入字段的原始候选：(tag 种子, 值)。</summary>
    private static readonly Gen<(int Tag, byte[] Value)[]> ExtraFieldSeeds =
        Gen.Choose(0, 6).SelectMany(n => (from t in Gen.Choose(0, 255) from v in Gens.BytesGen(12) select (t, v.ToArray())).ArrayOf(n));

    /// <summary>把种子换算为未定义的 tag：0 或大于 <paramref name="maxTag"/> 的值（超出 255 的丢弃），并去重。</summary>
    private static List<Field> UnknownTags((int Tag, byte[] Value)[] seeds, int maxTag) =>
        seeds.Select(s => new Field(s.Tag == 0 || s.Tag > maxTag ? s.Tag : maxTag + s.Tag, s.Value))
            .Where(f => f.Tag <= 255).DistinctBy(f => f.Tag).ToList();

    private static List<Field> WithUnknown(List<Field> fields, List<Field> extra) =>
        [.. fields.Concat(extra.Where(e => fields.All(f => f.Tag != e.Tag))).OrderBy(f => f.Tag)];

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>非法二维码文本：在合法文本上做各种破坏，或直接构造字段越界的 Struct。</summary>
    private static readonly Gen<string> InvalidQrText = Gen.OneOf(
        from p in Gens.QrPayloadGen()
        from k in Gen.Choose(0, 1000)
        let body = QrCodec.Encode(p)[QrCodec.Prefix.Length..]
        let i = Enumerable.Range(k % body.Length, body.Length - (k % body.Length)).Cast<int?>().FirstOrDefault(j => char.IsAsciiLetterUpper(body[j!.Value]))
                ?? body.IndexOf(body.FirstOrDefault(char.IsAsciiLetterUpper))
        select i < 0 || !char.IsAsciiLetterUpper(body[i]) ? "lt1:" : QrCodec.Prefix + body[..i] + char.ToLowerInvariant(body[i]) + body[(i + 1)..],
        from p in Gens.QrPayloadGen() from bad in Gen.Elements("=", "0", "1", "8", "9", "-", " ", "\u00C4") select QrCodec.Encode(p) + bad,
        from p in Gens.QrPayloadGen() from prefix in Gen.Elements("LT2:", "lt1:", "LT1", "", "LT:") select prefix + QrCodec.Encode(p)[QrCodec.Prefix.Length..],
        from p in Gens.QrPayloadGen()
        let body = QrCodec.Encode(p)[QrCodec.Prefix.Length..]
        let target = Enumerable.Range(0, body.Length).Select(d => body.Length - d).First(n => n % 8 is 1 or 3 or 6)
        select QrCodec.Prefix + body[..target],
        from p in Gens.QrPayloadGen()
        let body = QrCodec.Encode(p)[QrCodec.Prefix.Length..]
        select body.Length % 8 == 0 ? QrCodec.Prefix : QrCodec.Prefix + body[..^1] + Alphabet[Alphabet.IndexOf(body[^1], StringComparison.Ordinal) | 1],
        Gen.Choose(513, 600).Select(n => QrCodec.Prefix + new string('A', n - QrCodec.Prefix.Length)),
        from token in Gens.Bytes16Gen()
        from ipCount in Gen.Choose(0, 5)
        from portKind in Gen.Choose(0, 2)
        from nameKind in Gen.Choose(0, 2)
        select BadStruct(token, ipCount, portKind, nameKind));

    /// <summary>字段约束不满足：IP 数为 0 或 5、IP 重复、IP 列表长度不是 4 的倍数、端口为 0、名称 0 或 33 个码点。</summary>
    private static string BadStruct(Bytes16 token, int ipCount, int portKind, int nameKind)
    {
        var w = new TlvWriter();
        w.Bytes16(1, token);
        w.String(2, nameKind switch { 0 => "", 1 => new string('一', 33), _ => "ok" });
        var ips = ipCount is 0 or 5
            ? Enumerable.Range(0, 4 * ipCount).Select(i => (byte)((i / 4) + 1)).ToArray()
            : Enumerable.Repeat((byte)7, 4 * (ipCount + 1)).ToArray();
        w.Bytes(3, portKind == 2 ? [.. ips, 0] : ips);
        w.U16(4, portKind == 0 ? (ushort)0 : (ushort)42580);
        w.Bytes(5, new byte[32]);
        w.Bytes16(6, token);
        return QrCodec.Prefix + Base32.Encode(w.ToArray());
    }

    // Feature: lan-file-transfer, Property 5: 未知字段被忽略
    // Validates: Requirements 22.7, 23.9, 23.10
    [PbtProperty(1000)]
    public Property Property5_UnknownFieldsAreIgnored() =>
        Prop.ForAll(
            Arb.From(
                from m in Gens.ProtocolMessageGen()
                from a in Gens.AnnouncementGen()
                from q in Gens.QrPayloadGen()
                from seeds in ExtraFieldSeeds
                from which in Gen.Choose(0, 2)
                select (m, a, q, seeds, which)),
            x =>
            {
                switch (x.which)
                {
                    case 0:
                        var frame = FrameEncoder.EncodeOrThrow(x.m);
                        var payload = JoinFields(WithUnknown(SplitFields(frame, ProtocolConstants.FrameHeaderSize), UnknownTags(x.seeds, MaxDefinedTag(x.m.Type))));
                        var header = frame[..ProtocolConstants.FrameHeaderSize];
                        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(3), (uint)payload.Length);
                        var batch = new FrameDecoder().Feed([.. header, .. payload]);
                        Assert.Null(batch.Error);
                        Assert.Equal(new[] { x.m }, batch.Messages);
                        break;
                    case 1:
                        var bytes = AnnouncementCodec.Encode(x.a);
                        byte[] mutated = [.. bytes[..4], .. JoinFields(WithUnknown(SplitFields(bytes, 4), UnknownTags(x.seeds, 6)))];
                        if (mutated.Length <= ProtocolConstants.MaxAnnouncement)
                        {
                            Assert.Equal(x.a, AnnouncementCodec.Decode(mutated));
                        }

                        break;
                    default:
                        var raw = Base32.Decode(QrCodec.Encode(x.q)[QrCodec.Prefix.Length..])!;
                        var text = QrCodec.Prefix + Base32.Encode(JoinFields(WithUnknown(SplitFields(raw, 0), UnknownTags(x.seeds, 6))));
                        if (text.Length <= ProtocolConstants.MaxQrText)
                        {
                            Assert.Equal(QrDecodeResult.Ok(x.q), QrCodec.Decode(text));
                        }

                        break;
                }
            });

    // Feature: lan-file-transfer, Property 8: 发现公告往返与长度上限
    // Validates: Requirements 5.6, 23.1, 23.3
    [PbtProperty(1000)]
    public void Property8_AnnouncementRoundTripAndLimit(Announcement a)
    {
        var bytes = AnnouncementCodec.Encode(a);
        Assert.True(bytes.Length <= ProtocolConstants.MaxAnnouncement);
        Assert.Equal(a, AnnouncementCodec.Decode(bytes));
        Assert.Equal([1, 2, 3, 4, 5, 6], SplitFields(bytes, 4).Select(f => f.Tag));
    }

    // Feature: lan-file-transfer, Property 10: 二维码载荷往返
    // Validates: Requirements 8.3, 23.4, 23.5, 23.7
    [PbtProperty(1000)]
    public void Property10_QrPayloadRoundTrip(QrPayload p)
    {
        var text = QrCodec.Encode(p);
        Assert.True(text.Length <= ProtocolConstants.MaxQrText);
        Assert.StartsWith(QrCodec.Prefix, text, StringComparison.Ordinal);
        Assert.All(text[QrCodec.Prefix.Length..], c => Assert.Contains(c, Alphabet));
        Assert.Equal(QrDecodeResult.Ok(p), QrCodec.Decode(text));
    }

    // Feature: lan-file-transfer, Property 11: 非法二维码文本被整体拒绝
    // Validates: Requirements 8.8, 23.6
    [PbtProperty(1000)]
    public Property Property11_InvalidQrTextIsRejected() =>
        Prop.ForAll(Arb.From(InvalidQrText), text =>
        {
            var r = QrCodec.Decode(text);
            Assert.Null(r.Payload);
            Assert.NotNull(r.Error);
        });

    // Feature: lan-file-transfer, Property 12: 前导往返与版本协商
    // Validates: Requirements 24.4, 24.5, 24.6
    [PbtProperty(1000)]
    public Property Property12_PreambleRoundTripAndNegotiation() =>
        Prop.ForAll(
            Arb.From(from a in Gens.ProtocolVersionGen() from b in Gens.ProtocolVersionGen() from junk in Gens.BytesGen(8) select (a, b, junk)),
            x =>
            {
                var bytes = PreambleCodec.Encode(x.a);
                Assert.Equal(PreambleCodec.Size, bytes.Length);
                Assert.Equal(x.a, PreambleCodec.Decode(bytes));
                var n = PreambleCodec.Negotiate(x.a, x.b);
                if (x.a.Major == x.b.Major)
                {
                    Assert.Equal(new Negotiation.Compatible(new ProtocolVersion(x.a.Major, Math.Min(x.a.Minor, x.b.Minor))), n);
                    Assert.Equal(n, PreambleCodec.Negotiate(x.b, x.a));
                }
                else
                {
                    Assert.Equal(new Negotiation.Incompatible(x.a, x.b), n);
                }

                var j = x.junk.Span;
                ProtocolVersion? expected = j.Length == 6 && j[..4].SequenceEqual("LANT"u8) ? new ProtocolVersion(j[4], j[5]) : null;
                Assert.Equal(expected, PreambleCodec.Decode(j));
            });
}
