using System.Buffers.Binary;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>Property 1–4、6、7 的 C# 实现，与 Kotlin 侧 FramePropertiesTest 对应。</summary>
public sealed class FramePropertyTests
{
    private static byte[] Encode(ProtocolMessage m, ProtocolVersion? v = null) => FrameEncoder.EncodeOrThrow(m, v);

    private static byte[] Concat(IEnumerable<ProtocolMessage> ms) => ms.SelectMany(m => Encode(m)).ToArray();

    /// <summary>切分方式：整段、逐字节，或随机长度（1–64 字节）循环使用。</summary>
    private static readonly Gen<int[]> SplitGen = Gen.Frequency(
        (1, Gen.Constant(new[] { int.MaxValue })),
        (1, Gen.Constant(new[] { 1 })),
        (4, Gen.Choose(1, 8).SelectMany(n => Gen.Choose(1, 64).ArrayOf(n))));

    private static List<DecodeBatch> FeedSplit(FrameDecoder d, byte[] bytes, int[] split)
    {
        var batches = new List<DecodeBatch>();
        var pos = 0;
        var k = 0;
        while (pos < bytes.Length)
        {
            var n = Math.Min(split[k % split.Length], bytes.Length - pos);
            batches.Add(d.Feed(bytes.AsSpan(pos, n)));
            Assert.True(d.BufferedBytes <= ProtocolConstants.FrameHeaderSize + ProtocolConstants.MaxPayload);
            pos += n;
            k++;
        }

        return batches;
    }

    private static Gen<ProtocolMessage[]> MessagesGen(int max) =>
        Gen.Choose(0, max).SelectMany(n => Gens.ProtocolMessageGen().ArrayOf(n));

    private static byte[] Header(int magic, int type, long length)
    {
        var h = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)magic);
        h[2] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(3), (uint)length);
        return h;
    }

    /// <summary>非法帧：前三类只有帧头（错误应在帧头到齐时返回），载荷格式错误类带完整载荷。</summary>
    private sealed record BadFrame(byte[] Bytes, Func<DecodeError, bool> Expected, bool HeaderOnly)
    {
        public override string ToString() => $"BadFrame({Convert.ToHexStringLower(Bytes)})";
    }

    private static readonly MessageType[] AllTypes = Enum.GetValues<MessageType>();

    private static readonly MessageType[] TypesWithFields = AllTypes.Where(t => t is not MessageType.Heartbeat and not MessageType.PairComplete).ToArray();

    private static readonly Gen<BadFrame> BadFrameGen = Gen.OneOf(
        from magic in Gen.Choose(0, 0xFFFF)
        from t in Gen.Elements(AllTypes)
        from len in Gen.Choose(0, 100)
        let m = magic == ProtocolConstants.FrameMagic ? magic ^ 1 : magic
        select new BadFrame(Header(m, (int)t, len), e => e is DecodeError.BadMagic, true),
        from code in Gen.Choose(0, 255)
        from len in Gen.Choose(0, 100)
        let c = Enum.IsDefined((MessageType)code) ? 0xFF : code
        select new BadFrame(Header(ProtocolConstants.FrameMagic, c, len), e => e == new DecodeError.UnknownType(c), true),
        from t in Gen.Elements(AllTypes)
        from extra in Gen.Choose(1, int.MaxValue)
        let len = ProtocolConstants.MaxPayload + (long)(extra % (uint.MaxValue - ProtocolConstants.MaxPayload))
        select new BadFrame(Header(ProtocolConstants.FrameMagic, (int)t, len), e => e == new DecodeError.PayloadTooLarge(len), true),
        from t in Gen.Elements(TypesWithFields)
        from kind in Gen.Choose(0, 2)
        from n in Gen.Choose(1, 4)
        from junk in Gen.Choose(0, 255).Select(b => (byte)b).ArrayOf(n)
        let payload = kind switch { 0 => Array.Empty<byte>(), 1 => junk, _ => new byte[] { 1, 0, 0, 0, 9 } }
        select new BadFrame([.. Header(ProtocolConstants.FrameMagic, (int)t, payload.Length), .. payload], e => e is DecodeError.MalformedPayload mp && mp.Type == (int)t, false));

    /// <summary>参考校验器：按 Unicode 标准表 3-7 逐条判断（与被测实现分开编写）。</summary>
    private static bool ReferenceWellFormed(byte[] b)
    {
        bool In(int i, int lo, int hi) => i < b.Length && b[i] >= lo && b[i] <= hi;
        var i = 0;
        while (i < b.Length)
        {
            int c = b[i];
            int n;
            if (c <= 0x7F) n = 1;
            else if (c is >= 0xC2 and <= 0xDF && In(i + 1, 0x80, 0xBF)) n = 2;
            else if (c == 0xE0 && In(i + 1, 0xA0, 0xBF) && In(i + 2, 0x80, 0xBF)) n = 3;
            else if (c is (>= 0xE1 and <= 0xEC) or 0xEE or 0xEF && In(i + 1, 0x80, 0xBF) && In(i + 2, 0x80, 0xBF)) n = 3;
            else if (c == 0xED && In(i + 1, 0x80, 0x9F) && In(i + 2, 0x80, 0xBF)) n = 3;
            else if (c == 0xF0 && In(i + 1, 0x90, 0xBF) && In(i + 2, 0x80, 0xBF) && In(i + 3, 0x80, 0xBF)) n = 4;
            else if (c is >= 0xF1 and <= 0xF3 && In(i + 1, 0x80, 0xBF) && In(i + 2, 0x80, 0xBF) && In(i + 3, 0x80, 0xBF)) n = 4;
            else if (c == 0xF4 && In(i + 1, 0x80, 0x8F) && In(i + 2, 0x80, 0xBF) && In(i + 3, 0x80, 0xBF)) n = 4;
            else return false;
            i += n;
        }

        return true;
    }

    // Feature: lan-file-transfer, Property 1: Frame 编解码往返与规范编码
    // Validates: Requirements 22.1, 22.2, 22.8, 22.9, 14.5
    [PbtProperty(1000)]
    public Property Property1_FrameRoundTripAndCanonicalEncoding() =>
        Prop.ForAll(
            Arb.From(from m in Gens.ProtocolMessageGen() from minor in Gen.Choose(0, 3) select (m, v: new ProtocolVersion(1, (byte)minor))),
            x =>
            {
                var bytes = Encode(x.m, x.v);
                Assert.Equal((uint)(bytes.Length - ProtocolConstants.FrameHeaderSize), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(3)));
                var batch = new FrameDecoder(x.v).Feed(bytes);
                Assert.Null(batch.Error);
                Assert.Equal(new[] { x.m }, batch.Messages);
                Assert.Equal(bytes, Encode(batch.Messages[0], x.v));
            });

    // Feature: lan-file-transfer, Property 2: 流切分不变与缓冲上界
    // Validates: Requirements 22.3, 22.4, 22.10, 22.13
    [PbtProperty(1000)]
    public Property Property2_SplitInvarianceAndBufferBound() =>
        Prop.ForAll(
            Arb.From(from ms in MessagesGen(8) from split in SplitGen from cut in Gen.Choose(0, int.MaxValue) select (ms, split, cut)),
            x =>
            {
                var stream = Concat(x.ms);
                var d = new FrameDecoder();
                Assert.Equal(x.ms, FeedSplit(d, stream, x.split).SelectMany(b => b.Messages));
                Assert.Equal(0, d.BufferedBytes);

                // 在任意位置截断后 close：只输出完整到达的帧，残留字节不构成消息
                if (stream.Length > 0)
                {
                    var cut = x.cut % stream.Length;
                    var d2 = new FrameDecoder();
                    var before = FeedSplit(d2, stream[..cut], x.split).SelectMany(b => b.Messages).ToArray();
                    Assert.Equal(x.ms.Take(before.Length), before);
                    d2.Close();
                    Assert.Equal(0, d2.BufferedBytes);
                    Assert.Empty(d2.Feed(stream.AsSpan(cut)).Messages);
                }
            });

    // Feature: lan-file-transfer, Property 3: 解码错误定位
    // Validates: Requirements 22.5, 22.6
    [PbtProperty(1000)]
    public Property Property3_DecodeErrorLocation() =>
        Prop.ForAll(
            Arb.From(from ms in MessagesGen(5) from bad in BadFrameGen from split in SplitGen from tail in Gens.BytesGen(16) select (ms, bad, split, tail)),
            x =>
            {
                var prefix = Concat(x.ms);
                byte[] stream = [.. prefix, .. x.bad.Bytes, .. x.tail.Span];
                var d = new FrameDecoder();
                var batches = FeedSplit(d, stream, x.split);
                Assert.Equal(x.ms, batches.SelectMany(b => b.Messages));
                var error = Assert.Single(batches.Select(b => b.Error).OfType<DecodeError>());
                Assert.True(x.bad.Expected(error), $"unexpected {error}");
                Assert.Empty(d.Feed(Encode(Heartbeat.Instance)).Messages);

                // 前三类错误在帧头到齐时立即返回，不需要任何载荷字节
                if (x.bad.HeaderOnly)
                {
                    var b = new FrameDecoder().Feed([.. prefix, .. x.bad.Bytes.AsSpan(0, ProtocolConstants.FrameHeaderSize)]);
                    Assert.Equal(x.ms, b.Messages);
                    Assert.True(b.Error is not null && x.bad.Expected(b.Error));
                }
            });

    // Feature: lan-file-transfer, Property 4: 编码超限
    // Validates: Requirements 22.11, 22.12
    [PbtProperty(1000)]
    public Property Property4_EncodeLimit()
    {
        // 真实上限：1,048,576 字节的 TEXT 编码成功；载荷恰好超过 MAX_PAYLOAD 的 TEXT 被拒绝
        var id = new MessageId(Bytes16.Of(new byte[16]));
        Assert.True(FrameEncoder.Encode(new Text(id, 0, Bytes.Of(new byte[ProtocolConstants.MaxTextBytes]))).IsOk);
        var overhead = FrameEncoder.EncodeOrThrow(new Text(id, 0, Bytes.Empty)).Length - ProtocolConstants.FrameHeaderSize;
        Assert.True(FrameEncoder.Encode(new Text(id, 0, Bytes.Of(new byte[ProtocolConstants.MaxPayload - overhead]))).IsOk);
        var tooLarge = FrameEncoder.Encode(new Text(id, 0, Bytes.Of(new byte[ProtocolConstants.MaxPayload - overhead + 1])));
        Assert.Null(tooLarge.Bytes);
        Assert.Equal((long?)(ProtocolConstants.MaxPayload + 1), tooLarge.PayloadTooLarge);

        // 用较小的上限覆盖“超限 ⇔ 返回 PayloadTooLarge 且不输出字节”的判定
        return Prop.ForAll(
            Arb.From(from m in Gens.ProtocolMessageGen() from limit in Gen.Choose(0, 2048) select (m, limit)),
            x =>
            {
                var payloadSize = Encode(x.m).Length - ProtocolConstants.FrameHeaderSize;
                var r = FrameEncoder.Encode(x.m, null, x.limit);
                if (payloadSize <= x.limit)
                {
                    Assert.Equal(payloadSize + ProtocolConstants.FrameHeaderSize, r.Bytes!.Length);
                }
                else
                {
                    Assert.Null(r.Bytes);
                    Assert.Equal((long?)payloadSize, r.PayloadTooLarge);
                }
            });
    }

    // Feature: lan-file-transfer, Property 6: 文字无损往返
    // Validates: Requirements 12.1, 12.2, 12.3, 12.4, 12.5, 12.9
    [PbtProperty(1000)]
    public Property Property6_TextLosslessRoundTrip()
    {
        var id = new MessageId(Bytes16.OfHex("0102030405060708090a0b0c0d0e0f10"));
        var texts = Gen.Frequency(
            (20, Gens.UnicodeTextFragments().Select(f => string.Concat(f))),
            (1, from f in Gens.UnicodeTextFragments(4) from n in Gen.Choose(1, 2000) select string.Concat(Enumerable.Repeat(string.Concat(f), n))),
            (1, Gen.Elements(new string('a', ProtocolConstants.MaxTextBytes), new string('\u4E2D', ProtocolConstants.MaxTextBytes / 3) + "a")));
        return Prop.ForAll(Arb.From(texts), s =>
        {
            var utf8 = StrictUtf8.Encode(s);
            Assert.Equal(new UTF8Encoding(false).GetBytes(s), utf8);
            var frame = Encode(new Text(id, 1_735_693_805_000L, Bytes.Of(utf8)));
            var text = Assert.IsType<Text>(Assert.Single(new FrameDecoder().Feed(frame).Messages));
            Assert.Equal(s, StrictUtf8.Decode(text.Body.Span));
        });
    }

    // Feature: lan-file-transfer, Property 7: 严格 UTF‑8 解码
    // Validates: Requirements 12.7
    [PbtProperty(1000)]
    public Property Property7_StrictUtf8Decoding()
    {
        var anyByte = Gen.Choose(0, 255).Select(b => (byte)b);
        var input = Gen.Frequency(
            (3, Gens.InvalidUtf8Gen().Select(x => x.Bytes)),
            (2, Gens.UnicodeTextFragments().Select(f => StrictUtf8.Encode(string.Concat(f)))),
            (2, Gen.Choose(0, 12).SelectMany(n => anyByte.ArrayOf(n))),
            (1, Gen.Choose(1, 4).SelectMany(n => Gen.Choose(0x80, 0xFF).Select(b => (byte)b).ArrayOf(n))));
        return Prop.ForAll(Arb.From(input), b =>
        {
            var decoded = StrictUtf8.Decode(b);
            Assert.Equal(ReferenceWellFormed(b), decoded is not null);
            if (decoded is not null)
            {
                Assert.Equal(b, StrictUtf8.Encode(decoded));
            }
        });
    }
}
