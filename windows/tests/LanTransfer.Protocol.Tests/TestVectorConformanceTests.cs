using System.Text.Json;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>跨端一致性（任务 6.3）：C# 实现逐条满足 protocol/test-vectors 中的期望（与 Kotlin 侧读取同一份 JSON），并检查向量覆盖。</summary>
public sealed class TestVectorConformanceTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    private static string ErrorName(DecodeError e) => e switch
    {
        DecodeError.BadMagic => "BAD_MAGIC",
        DecodeError.UnknownType => "UNKNOWN_TYPE",
        DecodeError.PayloadTooLarge => "PAYLOAD_TOO_LARGE",
        DecodeError.MalformedPayload => "MALFORMED_PAYLOAD",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    private static bool Has(JsonElement o, string key) => o.TryGetProperty(key, out _);

    [Fact]
    public void Manifest_lists_every_vector_file() =>
        Assert.Subset(TestVectors.Files().ToHashSet(), new HashSet<string>
        {
            "frames.json", "frames-invalid.json", "text-messages.json", "utf8-invalid.json", "announcements.json",
            "qr-payloads.json", "preamble.json", "noise-xx.json", "verification-code.json",
        });

    [Fact]
    public void Frames_encode_and_decode_and_cover_every_type()
    {
        var vectors = TestVectors.Vectors("frames.json");
        foreach (var v in vectors)
        {
            var model = VectorJson.Message(VectorJson.EnumOf<MessageType>(v.Str("type")), v.GetProperty("model"));
            var bytes = v.HexBytes("bytes");
            var canonical = Has(v, "canonical_bytes") ? v.HexBytes("canonical_bytes") : bytes;
            Assert.Equal(Hex(canonical), Hex(FrameEncoder.EncodeOrThrow(model)));
            var batch = new FrameDecoder().Feed(bytes);
            Assert.Null(batch.Error);
            Assert.Equal(new[] { model }, batch.Messages);
        }

        Assert.Equal(Enum.GetValues<MessageType>().Order(), vectors.Select(v => VectorJson.EnumOf<MessageType>(v.Str("type"))).Distinct().Order());
        Assert.Contains(vectors, v => Has(v, "canonical_bytes"));
    }

    [Fact]
    public void Invalid_frames_report_expected_errors_and_cover_every_kind()
    {
        var vectors = TestVectors.Vectors("frames-invalid.json");
        foreach (var v in vectors)
        {
            var batch = new FrameDecoder(VectorJson.ParseVersion(v.Str("version"))).Feed(v.HexBytes("bytes"));
            Assert.Equal(v.GetProperty("messages_before_error").GetInt32(), batch.Messages.Count);
            Assert.Equal(v.Str("error"), ErrorName(batch.Error!));
        }

        Assert.Equal(new[] { "BAD_MAGIC", "MALFORMED_PAYLOAD", "PAYLOAD_TOO_LARGE", "UNKNOWN_TYPE" }, vectors.Select(v => v.Str("error")).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Text_messages_round_trip_and_cover_every_char_class()
    {
        var doc = TestVectors.Load("text-messages.json");
        var id = new MessageId(Bytes16.OfHex(doc.Str("message_id")));
        var sentAt = long.Parse(doc.Str("sent_at"), System.Globalization.CultureInfo.InvariantCulture);
        var vectors = TestVectors.Vectors("text-messages.json");
        foreach (var v in vectors)
        {
            var utf8 = v.HexBytes("text_utf8");
            var s = StrictUtf8.Decode(utf8)!;
            Assert.Equal(Hex(utf8), Hex(StrictUtf8.Encode(s)));
            Assert.Equal(v.Str("frame"), Hex(FrameEncoder.EncodeOrThrow(new Text(id, sentAt, Bytes.Of(utf8)))));
            var got = Assert.IsType<Text>(Assert.Single(new FrameDecoder().Feed(v.HexBytes("frame")).Messages));
            Assert.Equal(s, StrictUtf8.Decode(got.Body.Span));
        }

        var labels = vectors.SelectMany(v => v.GetProperty("class").EnumerateArray().Select(e => e.GetString()!)).ToHashSet();
        Assert.Subset(labels, TextCharClass.Labels.ToHashSet());
    }

    [Fact]
    public void Invalid_utf8_texts_decode_as_frames_but_fail_strict_decoding()
    {
        var vectors = TestVectors.Vectors("utf8-invalid.json");
        foreach (var v in vectors)
        {
            Assert.Null(StrictUtf8.Decode(v.HexBytes("text_utf8")));
            var t = Assert.IsType<Text>(Assert.Single(new FrameDecoder().Feed(v.HexBytes("frame")).Messages));
            Assert.Equal(v.Str("text_utf8"), t.Body.ToHex());
            Assert.Equal("TEXT_CORRUPT", v.Str("expected"));
        }

        Assert.Equal(InvalidUtf8.Kinds.Order(StringComparer.Ordinal), vectors.Select(v => v.Str("kind")).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Announcements_round_trip_or_are_dropped()
    {
        var vectors = TestVectors.Vectors("announcements.json");
        foreach (var v in vectors)
        {
            if (Has(v, "error"))
            {
                Assert.Null(AnnouncementCodec.Decode(v.HexBytes("bytes")));
            }
            else
            {
                var a = VectorJson.Announcement(v.GetProperty("model"));
                Assert.Equal(v.Str("bytes"), Hex(AnnouncementCodec.Encode(a)));
                Assert.Equal(a, AnnouncementCodec.Decode(v.HexBytes("bytes")));
            }
        }

        Assert.Contains(vectors.Where(v => !Has(v, "error")).Select(v => VectorJson.Announcement(v.GetProperty("model")).DisplayName), n =>
            n.EnumerateRunes().Count() == 32 && n.Any(c => c is >= '\u4E00' and <= '\u9FFF') && n.EnumerateRunes().Any(r => r.Value >= 0x1F000));
    }

    [Fact]
    public void Qr_payloads_round_trip_or_are_rejected()
    {
        var vectors = TestVectors.Vectors("qr-payloads.json");
        foreach (var v in vectors)
        {
            if (Has(v, "error"))
            {
                Assert.Null(QrCodec.Decode(v.Str("text")).Payload);
            }
            else
            {
                var p = VectorJson.Qr(v.GetProperty("model"));
                Assert.Equal(v.Str("text"), QrCodec.Encode(p));
                Assert.Equal(QrDecodeResult.Ok(p), QrCodec.Decode(v.Str("text")));
            }
        }

        var valid = vectors.Where(v => !Has(v, "error")).Select(v => VectorJson.Qr(v.GetProperty("model"))).ToArray();
        Assert.Contains(valid, p => p.Ipv4List.Count >= 2);
        Assert.Contains(valid, p => p.DisplayName.EnumerateRunes().Count() == 32);
    }

    [Fact]
    public void Preamble_encode_decode_and_negotiate()
    {
        foreach (var v in TestVectors.Vectors("preamble.json"))
        {
            if (Has(v, "negotiated"))
            {
                var n = PreambleCodec.Negotiate(VectorJson.ParseVersion(v.Str("local")), VectorJson.ParseVersion(v.Str("remote")));
                var expected = v.Str("negotiated");
                if (expected == "INCOMPATIBLE")
                {
                    Assert.IsType<Negotiation.Incompatible>(n);
                }
                else
                {
                    Assert.Equal(new Negotiation.Compatible(VectorJson.ParseVersion(expected)), n);
                }
            }
            else if (Has(v, "error"))
            {
                Assert.Null(PreambleCodec.Decode(v.HexBytes("bytes")));
            }
            else
            {
                var ver = VectorJson.ParseVersion(v.Str("version"));
                Assert.Equal(v.Str("bytes"), Hex(PreambleCodec.Encode(ver)));
                Assert.Equal(ver, PreambleCodec.Decode(v.HexBytes("bytes")));
            }
        }
    }

    [Fact]
    public void Noise_official_vectors_and_transcripts_match_byte_for_byte()
    {
        var doc = TestVectors.Load("noise-xx.json");
        foreach (var v in doc.GetProperty("official").EnumerateArray())
        {
            var i = new NoiseXX(Role.Initiator, X25519KeyPair.FromPrivateKey(v.HexBytes("init_static")), v.HexBytes("init_prologue"), EphemeralKeySource.Fixed(v.HexBytes("init_ephemeral")));
            var r = new NoiseXX(Role.Responder, X25519KeyPair.FromPrivateKey(v.HexBytes("resp_static")), v.HexBytes("resp_prologue"), EphemeralKeySource.Fixed(v.HexBytes("resp_ephemeral")));
            var msgs = v.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(msgs[0].Str("ciphertext"), Hex(i.WriteMessage(msgs[0].HexBytes("payload"))));
            Assert.Equal(msgs[0].Str("payload"), Hex(r.ReadMessage(msgs[0].HexBytes("ciphertext"))));
            Assert.Equal(msgs[1].Str("ciphertext"), Hex(r.WriteMessage(msgs[1].HexBytes("payload"))));
            Assert.Equal(msgs[1].Str("payload"), Hex(i.ReadMessage(msgs[1].HexBytes("ciphertext"))));
            Assert.Equal(msgs[2].Str("ciphertext"), Hex(i.WriteMessage(msgs[2].HexBytes("payload"))));
            Assert.Equal(msgs[2].Str("payload"), Hex(r.ReadMessage(msgs[2].HexBytes("ciphertext"))));
            Assert.Equal(v.Str("handshake_hash"), Hex(i.HandshakeHash));
            var (iSend, iRecv) = i.Split();
            var (rSend, rRecv) = r.Split();
            var iIn = iRecv.OpenStream();
            var rIn = rRecv.OpenStream();
            for (var k = 3; k < msgs.Length; k++)
            {
                var fromResponder = (k - 3) % 2 == 0;
                var record = (fromResponder ? rSend : iSend).Seal(msgs[k].HexBytes("payload"));
                Assert.Equal(msgs[k].Str("ciphertext"), Hex(record.AsSpan(2)));
                Assert.Equal(msgs[k].Str("payload"), Assert.Single((fromResponder ? iIn : rIn).Feed(record).Chunks).ToHex());
            }
        }

        foreach (var v in doc.GetProperty("transcripts").EnumerateArray())
        {
            var prologue = NoiseXX.Prologue(v.HexBytes("preamble_i"), v.HexBytes("preamble_r"));
            Assert.Equal(v.Str("prologue"), Hex(prologue));
            var i = new NoiseXX(Role.Initiator, X25519KeyPair.FromPrivateKey(v.HexBytes("init_static")), prologue, EphemeralKeySource.Fixed(v.HexBytes("init_ephemeral")));
            var r = new NoiseXX(Role.Responder, X25519KeyPair.FromPrivateKey(v.HexBytes("resp_static")), prologue, EphemeralKeySource.Fixed(v.HexBytes("resp_ephemeral")));
            Assert.Equal(v.Str("init_static_public"), X25519KeyPair.FromPrivateKey(v.HexBytes("init_static")).PublicKey.ToHex());
            var m1 = v.GetProperty("msg1");
            var m2 = v.GetProperty("msg2");
            var m3 = v.GetProperty("msg3");
            Assert.Equal(m1.Str("message"), Hex(i.WriteMessage(m1.HexBytes("payload"))));
            Assert.Empty(r.ReadMessage(m1.HexBytes("message")));
            Assert.Equal(m2.Str("message"), Hex(r.WriteMessage(m2.HexBytes("payload"))));
            var id2 = HandshakeIdentityCodec.Decode(i.ReadMessage(m2.HexBytes("message")), expectIntent: false)!;
            Assert.Equal(m2.GetProperty("identity").Str("display_name"), id2.DisplayName);
            Assert.Equal(m3.Str("message"), Hex(i.WriteMessage(m3.HexBytes("payload"))));
            var id3 = HandshakeIdentityCodec.Decode(r.ReadMessage(m3.HexBytes("message")), expectIntent: true)!;
            Assert.Equal(m3.GetProperty("identity").Str("intent"), VectorJson.SpecName(id3.Intent!.Value));
            Assert.Equal(v.Str("handshake_hash"), Hex(i.HandshakeHash));
            Assert.Equal(v.Str("init_static_public"), Hex(r.RemoteStatic));
            Assert.Equal(v.Str("resp_static_public"), Hex(i.RemoteStatic));
            var (iSend, _) = i.Split();
            var (rSend, rRecv) = r.Split();
            var rIn = rRecv.OpenStream();
            foreach (var rec in v.GetProperty("records_i_to_r").EnumerateArray())
            {
                Assert.Equal(rec.Str("record"), Hex(iSend.Seal(rec.HexBytes("plaintext"))));
                Assert.Equal(rec.Str("plaintext"), Assert.Single(rIn.Feed(rec.HexBytes("record")).Chunks).ToHex());
            }

            foreach (var rec in v.GetProperty("records_r_to_i").EnumerateArray())
            {
                Assert.Equal(rec.Str("record"), Hex(rSend.Seal(rec.HexBytes("plaintext"))));
            }
        }
    }

    [Fact]
    public void Verification_codes_match()
    {
        foreach (var v in TestVectors.Vectors("verification-code.json"))
        {
            var pkI = v.HexBytes("pk_i");
            var pkR = v.HexBytes("pk_r");
            var nI = v.HexBytes("n_i");
            Assert.Equal(v.Str("commitment"), Hex(VerificationCode.Commitment(pkI, pkR, nI)));
            Assert.Equal(v.Str("sas"), VerificationCode.Sas(pkI, pkR, nI, v.HexBytes("n_r")));
        }
    }
}
