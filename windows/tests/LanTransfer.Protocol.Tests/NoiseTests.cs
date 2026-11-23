using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>NoiseXX、记录层、验证码的示例测试；含 Noise 官方测试向量（cacophony）中 Noise_XX_25519_AESGCM_SHA256 的用例。</summary>
public sealed class NoiseTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);

    [Fact]
    public void Official_vector_matches_byte_for_byte()
    {
        var prologue = H("4a6f686e2047616c74");
        var i = new NoiseXX(Role.Initiator, X25519KeyPair.FromPrivateKey(H("e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1")), prologue,
            EphemeralKeySource.Fixed(H("893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a")));
        var r = new NoiseXX(Role.Responder, X25519KeyPair.FromPrivateKey(H("4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893")), prologue,
            EphemeralKeySource.Fixed(H("bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b")));

        var m1 = i.WriteMessage(H("4c756477696720766f6e204d69736573"));
        Assert.Equal("ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c79444c756477696720766f6e204d69736573", Hex(m1));
        Assert.Equal("4c756477696720766f6e204d69736573", Hex(r.ReadMessage(m1)));
        var m2 = r.WriteMessage(H("4d757272617920526f746862617264"));
        Assert.Equal("95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f144808843757117acceb05bd7a45733bc22015c97a9d0cbaf41b80446d5988ff5127235d76b79eade70f473d6a4ef521fdcbeda5340d01e028ba793fc059f2724a83af05f12dda0448a7621a926b379a92477fd", Hex(m2));
        Assert.Equal("4d757272617920526f746862617264", Hex(i.ReadMessage(m2)));
        var m3 = i.WriteMessage(H("462e20412e20486179656b"));
        Assert.Equal("c90f1cf77eba4e50edb038991565e36c9758943a989229b6051244dc4fbecb6946744b401af2ee1a5881b65fbb87fd07cb6a328ececc9ce6ce84c399dc332d4fd521fa4bb7f467ce909395", Hex(m3));
        Assert.Equal("462e20412e20486179656b", Hex(r.ReadMessage(m3)));
        Assert.Equal("1b7aefb1125762aa21a252890d00af54519638b76437444538f9a52f21e2e0dc", Hex(i.HandshakeHash));
        Assert.Equal(Hex(i.HandshakeHash), Hex(r.HandshakeHash));

        var (iSend, iRecv) = i.Split();
        var (rSend, rRecv) = r.Split();
        var t4 = rSend.Seal(H("4361726c204d656e676572"));
        Assert.Equal("bc3fa77f6aca3e8466d7dc6bea10013e88a6a29add5132b461806c", Hex(t4.AsSpan(2)));
        Assert.Equal("4361726c204d656e676572", Assert.Single(iRecv.OpenStream().Feed(t4).Chunks).ToHex());
        var t5 = iSend.Seal(H("4a65616e2d426170746973746520536179"));
        Assert.Equal("250b01074cdfe0df2ecf8ccbf1737b15a2ddb5b52fd9a396604e9c793cee3b3bb9", Hex(t5.AsSpan(2)));
        Assert.Equal("4a65616e2d426170746973746520536179", Assert.Single(rRecv.OpenStream().Feed(t5).Chunks).ToHex());
    }

    [Fact]
    public void Remote_statics_are_learned_and_tampering_fails()
    {
        var si = X25519KeyPair.FromPrivateKey(Enumerable.Repeat((byte)1, 32).ToArray());
        var sr = X25519KeyPair.FromPrivateKey(Enumerable.Repeat((byte)2, 32).ToArray());
        var p = NoiseXX.Prologue(PreambleCodec.Encode(ProtocolVersion.V1_0), PreambleCodec.Encode(ProtocolVersion.V1_0));
        NoiseXX I(byte[] prologue) => new(Role.Initiator, si, prologue, EphemeralKeySource.Fixed(Enumerable.Repeat((byte)3, 32).ToArray()));
        NoiseXX R(byte[] prologue) => new(Role.Responder, sr, prologue, EphemeralKeySource.Fixed(Enumerable.Repeat((byte)4, 32).ToArray()));

        var i = I(p);
        var r = R(p);
        r.ReadMessage(i.WriteMessage([]));
        var m2 = r.WriteMessage("R"u8.ToArray());
        m2[40] ^= 1;
        Assert.Throws<HandshakeAuthException>(() => i.ReadMessage(m2));

        var i2 = I(p);
        var r2 = R([.. p, 0]);
        r2.ReadMessage(i2.WriteMessage([]));
        Assert.Throws<HandshakeAuthException>(() => i2.ReadMessage(r2.WriteMessage([])));

        var i3 = I(p);
        var r3 = R(p);
        r3.ReadMessage(i3.WriteMessage([]));
        i3.ReadMessage(r3.WriteMessage([]));
        Assert.Equal(sr.PublicKey.ToHex(), Hex(i3.RemoteStatic));
        r3.ReadMessage(i3.WriteMessage([]));
        Assert.Equal(si.PublicKey.ToHex(), Hex(r3.RemoteStatic));
        Assert.True(i3.IsComplete);
    }

    [Fact]
    public void Records_split_large_plaintext_and_reject_short_or_tampered_records()
    {
        var key = Enumerable.Repeat((byte)9, 32).ToArray();
        var plain = Enumerable.Range(0, 200_000).Select(x => (byte)x).ToArray();
        var sealedBytes = new RecordCipher(key).Seal(plain);
        var r = new RecordCipher(key).OpenStream().Feed(sealedBytes);
        Assert.False(r.IntegrityError);
        Assert.Equal(4, r.Chunks.Count);
        Assert.Equal(plain, r.Chunks.SelectMany(c => c.ToArray()));
        Assert.Empty(new RecordCipher(key).Seal([]));

        var d2 = new RecordCipher(key).OpenStream();
        Assert.True(d2.Feed([0, 16, .. new byte[16]]).IntegrityError);
        var after = d2.Feed(sealedBytes);
        Assert.True(after.IntegrityError);
        Assert.Empty(after.Chunks);
    }

    [Fact]
    public void Verification_code_and_fingerprint()
    {
        byte[] pkI = [.. Enumerable.Repeat((byte)1, 32)], pkR = [.. Enumerable.Repeat((byte)2, 32)];
        byte[] nI = [.. Enumerable.Repeat((byte)3, 32)], nR = [.. Enumerable.Repeat((byte)4, 32)];
        Assert.Equal(32, VerificationCode.Commitment(pkI, pkR, nI).Length);
        Assert.Matches("^[0-9]{6}$", VerificationCode.Sas(pkI, pkR, nI, nR));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hex(Fingerprint.Of([])));
    }
}
