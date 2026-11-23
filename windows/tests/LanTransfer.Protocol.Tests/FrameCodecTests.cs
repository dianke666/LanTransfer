using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>Frame / TLV / StrictUtf8 的示例测试（属性测试见 Property 1–4、6、7），与 Kotlin 侧 FrameCodecTest 对应。</summary>
public sealed class FrameCodecTests
{
    private static byte[] Encode(ProtocolMessage m) => FrameEncoder.EncodeOrThrow(m);

    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Heartbeat_is_a_bare_header() => Assert.Equal("4c540300000000", Convert.ToHexStringLower(Encode(Heartbeat.Instance)));

    [Fact]
    public void SessionResult_layout() =>
        Assert.Equal("4c540100000006010000000103", Convert.ToHexStringLower(Encode(new SessionResult(SessionResultCode.Duplicate))));

    [Fact]
    public void Generator_covers_every_type_and_each_round_trips()
    {
        var messages = Gens.ProtocolMessageGen().Draw(3000);
        Assert.Equal(Enum.GetValues<MessageType>().Order(), messages.Select(m => m.Type).Distinct().Order());
        foreach (var m in messages)
        {
            var d = new FrameDecoder();
            var batch = d.Feed(Encode(m));
            Assert.Null(batch.Error);
            Assert.Equal(new[] { m }, batch.Messages);
            Assert.Equal(0, d.BufferedBytes);
        }
    }

    [Fact]
    public void Header_errors_are_reported_without_waiting_for_payload()
    {
        Assert.Null(new FrameDecoder().Feed(H("4c5501000000")).Error);
        Assert.IsType<DecodeError.BadMagic>(new FrameDecoder().Feed(H("4c550100000001")).Error);
        Assert.Equal(new DecodeError.UnknownType(0xFF), new FrameDecoder().Feed(H("4c54ff00000000")).Error);
        Assert.Equal(new DecodeError.PayloadTooLarge(0x200001), new FrameDecoder().Feed(H("4c540300200001")).Error);
    }

    [Fact]
    public void Messages_before_error_are_delivered_and_input_is_rejected_afterwards()
    {
        var d = new FrameDecoder();
        var batch = d.Feed([.. Encode(Heartbeat.Instance), .. H("4c54010000000101")]);
        Assert.Equal(new ProtocolMessage[] { Heartbeat.Instance }, batch.Messages);
        Assert.IsType<DecodeError.MalformedPayload>(batch.Error);
        Assert.Empty(d.Feed(Encode(Heartbeat.Instance)).Messages);
    }

    [Fact]
    public void Unknown_tags_are_skipped_and_decreasing_tags_are_malformed()
    {
        Assert.Equal(new ProtocolMessage[] { new SessionClose(SessionCloseReason.Normal) }, new FrameDecoder().Feed(H("4c54020000000c" + "010000000100" + "090000000107")).Messages);
        Assert.IsType<DecodeError.MalformedPayload>(new FrameDecoder().Feed(H("4c54020000000c" + "090000000107" + "010000000100")).Error);
    }

    [Fact]
    public void Undefined_enum_and_wrong_fixed_length_are_malformed()
    {
        Assert.IsType<DecodeError.MalformedPayload>(new FrameDecoder().Feed(H("4c54010000000601000000010a")).Error);
        Assert.IsType<DecodeError.MalformedPayload>(new FrameDecoder().Feed(H("4c54010000000701000000020000")).Error);
    }

    [Fact]
    public void Byte_by_byte_feeding_matches_and_close_discards_residue()
    {
        var frame = Encode(new TextAck(new MessageId(Bytes16.OfHex("00112233445566778899aabbccddeeff"))));
        var d = new FrameDecoder();
        var got = new List<ProtocolMessage>();
        for (var i = 0; i < frame.Length; i++)
        {
            got.AddRange(d.Feed(frame.AsSpan(i, 1)).Messages);
        }

        Assert.Equal(new FrameDecoder().Feed(frame).Messages, got);
        d.Feed(frame.AsSpan(0, 10));
        Assert.Equal(10, d.BufferedBytes);
        d.Close();
        Assert.Equal(0, d.BufferedBytes);
        Assert.Empty(d.Feed(frame).Messages);
    }

    [Fact]
    public void Oversized_payload_produces_no_bytes()
    {
        var big = new Text(new MessageId(Bytes16.Of(new byte[16])), 0, Bytes.Of(new byte[ProtocolConstants.MaxPayload]));
        var r = FrameEncoder.Encode(big);
        Assert.False(r.IsOk);
        Assert.NotNull(r.PayloadTooLarge);
    }

    [Fact]
    public void StrictUtf8_round_trips_and_rejects_invalid_sequences()
    {
        var s = "a\0\r\n" + TextCharClass.Cps(0x1F600, 0x10FFFF, 0xFFFE) + "中";
        Assert.Equal(s, StrictUtf8.Decode(StrictUtf8.Encode(s)));
        Assert.Equal("f0908080", Convert.ToHexStringLower(StrictUtf8.Encode(TextCharClass.Cps(0x10000))));
        foreach (var hex in new[] { "c080", "e08080", "eda080", "f4908080", "f5808080", "80", "e282", "ff" })
        {
            Assert.Null(StrictUtf8.Decode(H(hex)));
        }

        Assert.True(StrictUtf8.HasUnpairedSurrogate("\uD800"));
    }
}
