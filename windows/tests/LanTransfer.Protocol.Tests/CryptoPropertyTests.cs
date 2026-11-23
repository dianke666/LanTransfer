using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>Property 13–15 的 C# 实现，与 Kotlin 侧 CryptoPropertiesTest 对应。</summary>
public sealed class CryptoPropertyTests
{
    private static readonly Gen<byte[]> KeyGen = Gens.BytesOfSize(32).Select(b => b.ToArray());

    private static readonly byte[] Prologue = NoiseXX.Prologue(PreambleCodec.Encode(ProtocolVersion.V1_0), PreambleCodec.Encode(ProtocolVersion.V1_0));

    /// <summary>篡改方式：0 比特翻转、1 删除、2 重放、3 重排、4 插入其他 Session 的 record。返回篡改后的列表与第一条被篡改 record 的位置。</summary>
    private static (List<byte[]> Records, int FirstBad)? Tamper(List<byte[]> records, int kind, int at, int bit, byte[] foreign)
    {
        var k = at % records.Count;
        var output = records.ToList();
        switch (kind)
        {
            case 0:
                // 只翻转密文部分（长度前缀被改动的情况由删除、重排覆盖）
                var r = (byte[])output[k].Clone();
                var pos = 2 + ((bit >>> 3) % (r.Length - 2));
                r[pos] ^= (byte)(1 << (bit & 7));
                output[k] = r;
                return (output, k);
            case 1:
                if (records.Count < 2 || k == records.Count - 1)
                {
                    return null;
                }

                output.RemoveAt(k);
                return (output, k);
            case 2:
                output.Insert(k + 1, records[k]);
                return (output, k + 1);
            case 3:
                if (k + 1 >= records.Count)
                {
                    return null;
                }

                (output[k], output[k + 1]) = (records[k + 1], records[k]);
                return (output, k);
            default:
                output.Insert(k, foreign);
                return (output, k);
        }
    }

    /// <summary>运行一次完整握手；<paramref name="mutate"/> 可以篡改第 index 条握手消息。返回是否成功。</summary>
    private static bool Handshake(NoiseXX i, NoiseXX r, byte[] p1, byte[] p2, byte[] p3, Func<int, byte[], byte[]> mutate)
    {
        try
        {
            // msg1 的载荷没有加密，被篡改时这里不会报错；篡改会改变握手哈希，在之后的消息中被发现
            r.ReadMessage(mutate(0, i.WriteMessage(p1)));
            Assert.Equal(p2, i.ReadMessage(mutate(1, r.WriteMessage(p2))));
            Assert.Equal(p3, r.ReadMessage(mutate(2, i.WriteMessage(p3))));
            return true;
        }
        catch (HandshakeAuthException)
        {
            return false;
        }
    }

    // Feature: lan-file-transfer, Property 13: 记录层往返与完整性
    // Validates: Requirements 10.1, 10.5
    [PbtProperty(1000)]
    public Property Property13_RecordRoundTripAndIntegrity()
    {
        var pieces = Gen.Choose(1, 8).SelectMany(n => Gen.Choose(1, 300).SelectMany(len => Gens.BytesOfSize(len)).Select(b => b.ToArray()).ArrayOf(n));
        var splits = Gen.Choose(1, 6).SelectMany(n => Gen.Choose(1, 97).ArrayOf(n));
        var gen =
            from key in KeyGen
            from otherKey in KeyGen
            from plain in pieces
            from split in splits
            from kind in Gen.Choose(0, 4)
            from at in Gen.Choose(0, 1000)
            from bit in Gen.Choose(0, int.MaxValue)
            select (key, otherKey, plain, split, kind, at, bit);
        return Prop.ForAll(Arb.From(gen), x =>
        {
            var sender = new RecordCipher(x.key);
            var records = x.plain.Select(p => sender.Seal(p)).ToList();

            // 往返：任意切分输入，明文拼接等于原明文拼接
            var stream = records.SelectMany(r => r).ToArray();
            var reader = new RecordCipher(x.key).OpenStream();
            var got = new List<byte>();
            for (int pos = 0, s = 0; pos < stream.Length; s++)
            {
                var n = Math.Min(x.split[s % x.split.Length], stream.Length - pos);
                var res = reader.Feed(stream.AsSpan(pos, n));
                Assert.False(res.IntegrityError);
                got.AddRange(res.Chunks.SelectMany(c => c.ToArray()));
                pos += n;
            }

            Assert.Equal(x.plain.SelectMany(p => p), got);

            // 篡改：返回 IntegrityError，且不输出被篡改 record 及其之后的任何明文
            var foreign = new RecordCipher(x.otherKey).Seal([1, 2, 3]);
            if (Tamper(records, x.kind, x.at, x.bit, foreign) is { } t && !(x.kind == 4 && x.otherKey.AsSpan().SequenceEqual(x.key)))
            {
                var r2 = new RecordCipher(x.key).OpenStream();
                var output = new List<byte[]>();
                var error = false;
                foreach (var rec in t.Records)
                {
                    var res = r2.Feed(rec);
                    output.AddRange(res.Chunks.Select(c => c.ToArray()));
                    error |= res.IntegrityError;
                }

                Assert.True(error);
                Assert.Equal(x.plain.Take(t.FirstBad).Select(Convert.ToHexString), output.Select(Convert.ToHexString));
            }
        });
    }

    // Feature: lan-file-transfer, Property 14: Noise_XX 相互认证
    // Validates: Requirements 10.2, 10.6
    [PbtProperty(1000)]
    public Property Property14_NoiseXXMutualAuthentication()
    {
        var gen =
            from si in KeyGen
            from sr in KeyGen
            from ei in KeyGen
            from er in KeyGen
            from p1 in Gens.BytesGen(16)
            from p2 in Gens.BytesGen(16)
            from p3 in Gens.BytesGen(16)
            from scenario in Gen.Choose(0, 3)
            from seed in Gen.Choose(0, int.MaxValue)
            from fake in KeyGen
            select (si, sr, ei, er, p1: p1.ToArray(), p2: p2.ToArray(), p3: p3.ToArray(), scenario, seed, fake);
        return Prop.ForAll(Arb.From(gen), x =>
        {
            var kpI = X25519KeyPair.FromPrivateKey(x.si);
            var kpR = X25519KeyPair.FromPrivateKey(x.sr);
            NoiseXX Init(byte[] pro) => new(Role.Initiator, kpI, pro, EphemeralKeySource.Fixed(x.ei));
            NoiseXX Resp(byte[] pro) => new(Role.Responder, kpR, pro, EphemeralKeySource.Fixed(x.er));
            switch (x.scenario)
            {
                case 0:
                {
                    var i = Init(Prologue);
                    var r = Resp(Prologue);
                    Assert.True(Handshake(i, r, x.p1, x.p2, x.p3, (_, m) => m));
                    Assert.Equal(kpR.PublicKey, Bytes.Of(i.RemoteStatic));
                    Assert.Equal(kpI.PublicKey, Bytes.Of(r.RemoteStatic));
                    var (iSend, iRecv) = i.Split();
                    var (rSend, rRecv) = r.Split();
                    byte[] a = [.. x.p1, 1], b = [.. x.p2, 2];
                    Assert.Equal(a, Assert.Single(rRecv.OpenStream().Feed(iSend.Seal(a)).Chunks).ToArray());
                    Assert.Equal(b, Assert.Single(iRecv.OpenStream().Feed(rSend.Seal(b)).Chunks).ToArray());
                    break;
                }

                case 1:
                {
                    // 篡改任意一条握手消息的任意一个字节
                    var target = x.seed % 3;
                    Assert.False(Handshake(Init(Prologue), Resp(Prologue), x.p1, x.p2, x.p3, (idx, m) =>
                    {
                        if (idx != target)
                        {
                            return m;
                        }

                        var c = (byte[])m.Clone();
                        c[(x.seed >>> 2) % c.Length] ^= (byte)(1 + (x.seed & 0x7F));
                        return c;
                    }));
                    break;
                }

                case 2:
                {
                    // 一方出示与其私钥不对应的公钥
                    var i = Init(Prologue);
                    var r = Resp(Prologue);
                    var liar = x.seed % 2 == 0 ? i : r;
                    var real = ReferenceEquals(liar, i) ? kpI : kpR;
                    if (!real.PublicKey.Span.SequenceEqual(x.fake))
                    {
                        liar.AdvertisedStaticOverride = x.fake;
                        Assert.False(Handshake(i, r, x.p1, x.p2, x.p3, (_, m) => m));
                    }

                    break;
                }

                default:
                {
                    // 双方 prologue 不同（如前导版本被篡改）
                    var other = NoiseXX.Prologue(PreambleCodec.Encode(new ProtocolVersion(1, 1)), PreambleCodec.Encode(ProtocolVersion.V1_0));
                    Assert.False(Handshake(Init(Prologue), Resp(other), x.p1, x.p2, x.p3, (_, m) => m));
                    break;
                }
            }
        });
    }

    // Feature: lan-file-transfer, Property 15: 配对验证码派生
    // Validates: Requirements 7.2, 10.3
    [PbtProperty(1000)]
    public Property Property15_VerificationCodeDerivation() =>
        Prop.ForAll(
            Arb.From(from pkI in KeyGen from pkR in KeyGen from nI in KeyGen from nR in KeyGen from other in KeyGen select (pkI, pkR, nI, nR, other)),
            x =>
            {
                var sas = VerificationCode.Sas(x.pkI, x.pkR, x.nI, x.nR);
                Assert.Matches("^[0-9]{6}$", sas);
                Assert.Equal(sas, VerificationCode.Sas((byte[])x.pkI.Clone(), (byte[])x.pkR.Clone(), (byte[])x.nI.Clone(), (byte[])x.nR.Clone()));
                var c = VerificationCode.Commitment(x.pkI, x.pkR, x.nI);
                Assert.True(VerificationCode.VerifyCommitment(c, x.pkI, x.pkR, x.nI));
                Assert.Equal(x.other.AsSpan().SequenceEqual(x.nI), VerificationCode.VerifyCommitment(c, x.pkI, x.pkR, x.other));
            });
}
