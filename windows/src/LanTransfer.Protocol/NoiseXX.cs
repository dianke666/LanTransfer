using System.Text;

namespace LanTransfer.Protocol;

/// <summary>握手失败：解密失败、消息格式不对或 DH 结果无效（需求 10.6）。</summary>
public sealed class HandshakeAuthException(string message) : Exception(message);

public enum Role
{
    Initiator,
    Responder,
}

/// <summary>Noise CipherState。k 为 null 时 EncryptWithAd / DecryptWithAd 原样返回。</summary>
internal sealed class CipherState(byte[]? k = null)
{
    private ulong _n;

    public byte[] EncryptWithAd(byte[] ad, byte[] plain)
    {
        if (k is null)
        {
            return plain;
        }

        var c = Primitives.AesGcmEncrypt(k, _n, ad, plain);
        _n++;
        return c;
    }

    public byte[] DecryptWithAd(byte[] ad, byte[] cipher)
    {
        if (k is null)
        {
            return cipher;
        }

        var p = Primitives.AesGcmDecrypt(k, _n, ad, cipher) ?? throw new HandshakeAuthException("decryption failed");
        _n++;
        return p;
    }
}

/// <summary>Noise SymmetricState。</summary>
internal sealed class SymmetricState
{
    private byte[] _ck;
    private CipherState _cipher = new();

    public SymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        H = name.Length <= Primitives.HashLen ? [.. name, .. new byte[Primitives.HashLen - name.Length]] : Primitives.Sha256(name);
        _ck = (byte[])H.Clone();
    }

    public byte[] H { get; private set; }

    public void MixKey(byte[] ikm)
    {
        var (ck, k) = Primitives.Hkdf2(_ck, ikm);
        _ck = ck;
        _cipher = new CipherState(k);
    }

    public void MixHash(byte[] data) => H = Primitives.Sha256(H, data);

    public byte[] EncryptAndHash(byte[] plain)
    {
        var c = _cipher.EncryptWithAd(H, plain);
        MixHash(c);
        return c;
    }

    public byte[] DecryptAndHash(byte[] ct)
    {
        var p = _cipher.DecryptWithAd(H, ct);
        MixHash(ct);
        return p;
    }

    public (byte[] K1, byte[] K2) Split() => Primitives.Hkdf2(_ck, []);
}

/// <summary>
/// Noise_XX_25519_AESGCM_SHA256 握手状态机（规范第 6 节，需求 10.2、10.6），与 Kotlin 侧 <c>NoiseXX</c> 对应：
/// msg1 → e；msg2 ← e, ee, s, es；msg3 → s, se。<see cref="WriteMessage"/> / <see cref="ReadMessage"/> 处理不含 <c>len:u16</c> 封装的 noise_message。
/// </summary>
public sealed class NoiseXX
{
    public const string ProtocolName = "Noise_XX_25519_AESGCM_SHA256";

    private static readonly byte[] ProloguePrefix = Encoding.ASCII.GetBytes("LanTransfer-Noise-v1");

    private readonly Role _role;
    private readonly X25519KeyPair _localStatic;
    private readonly IEphemeralKeySource _ephemeral;
    private readonly SymmetricState _ss = new(ProtocolName);
    private X25519KeyPair? _e;
    private byte[]? _re;
    private byte[]? _rs;
    private int _step;
    private bool _failed;

    public NoiseXX(Role role, X25519KeyPair localStatic, byte[] prologue, IEphemeralKeySource ephemeral)
    {
        _role = role;
        _localStatic = localStatic;
        _ephemeral = ephemeral;
        _ss.MixHash(prologue);
    }

    /// <summary>prologue = "LanTransfer-Noise-v1" ‖ Preamble_I ‖ Preamble_R。</summary>
    public static byte[] Prologue(ReadOnlySpan<byte> preambleI, ReadOnlySpan<byte> preambleR) => [.. ProloguePrefix, .. preambleI, .. preambleR];

    /// <summary>只供测试：在 <c>s</c> 中出示与私钥不对应的公钥，模拟冒充者（Property 14）。</summary>
    internal byte[]? AdvertisedStaticOverride { get; set; }

    private byte[] AdvertisedStatic() => AdvertisedStaticOverride ?? _localStatic.PublicKey.ToArray();

    /// <summary>对方的静态公钥：发起方在读取 msg2 后可用，接收方在读取 msg3 后可用。</summary>
    public byte[]? RemoteStatic => (byte[]?)_rs?.Clone();

    public byte[] HandshakeHash => (byte[])_ss.H.Clone();

    public bool IsComplete => _step == 3;

    private static byte[] Dh(X25519KeyPair local, byte[] remote) => local.Agree(remote) ?? throw new HandshakeAuthException("invalid DH result");

    /// <summary>写出下一条握手消息（本方必须是当前应发送的一方）。</summary>
    public byte[] WriteMessage(byte[] payload)
    {
        if (_failed)
        {
            throw new InvalidOperationException("handshake failed");
        }

        var output = new MemoryStream();
        switch (_step, _role)
        {
            case (0, Role.Initiator):
            {
                var e = _e = _ephemeral.Next();
                output.Write(e.PublicKey.Span);
                _ss.MixHash(e.PublicKey.ToArray());
                break;
            }

            case (1, Role.Responder):
            {
                var e = _e = _ephemeral.Next();
                output.Write(e.PublicKey.Span);
                _ss.MixHash(e.PublicKey.ToArray());
                _ss.MixKey(Dh(e, _re!)); // ee
                output.Write(_ss.EncryptAndHash(AdvertisedStatic())); // s
                _ss.MixKey(Dh(_localStatic, _re!)); // es
                break;
            }

            case (2, Role.Initiator):
                output.Write(_ss.EncryptAndHash(AdvertisedStatic())); // s
                _ss.MixKey(Dh(_localStatic, _re!)); // se
                break;

            default:
                throw new InvalidOperationException($"not this side's turn to write (step {_step}, {_role})");
        }

        output.Write(_ss.EncryptAndHash(payload));
        _step++;
        return output.ToArray();
    }

    /// <summary>读取对方的握手消息并返回载荷；任何失败都抛出 <see cref="HandshakeAuthException"/>，之后不可再用。</summary>
    public byte[] ReadMessage(byte[] message)
    {
        if (_failed)
        {
            throw new InvalidOperationException("handshake failed");
        }

        try
        {
            var p = 0;
            byte[] Take(int n)
            {
                if (message.Length - p < n)
                {
                    throw new HandshakeAuthException("handshake message too short");
                }

                var r = message[p..(p + n)];
                p += n;
                return r;
            }

            switch (_step, _role)
            {
                case (0, Role.Responder):
                    _re = Take(32);
                    _ss.MixHash(_re);
                    break;
                case (1, Role.Initiator):
                    _re = Take(32);
                    _ss.MixHash(_re);
                    _ss.MixKey(Dh(_e!, _re)); // ee
                    _rs = _ss.DecryptAndHash(Take(32 + Primitives.TagLen)); // s
                    _ss.MixKey(Dh(_e!, _rs)); // es
                    break;
                case (2, Role.Responder):
                    _rs = _ss.DecryptAndHash(Take(32 + Primitives.TagLen)); // s
                    _ss.MixKey(Dh(_e!, _rs)); // se
                    break;
                default:
                    throw new InvalidOperationException($"not this side's turn to read (step {_step}, {_role})");
            }

            var payload = _ss.DecryptAndHash(message[p..]);
            _step++;
            return payload;
        }
        catch (HandshakeAuthException)
        {
            _failed = true;
            throw;
        }
    }

    /// <summary>握手完成后得到（发送方向, 接收方向）的记录层密码。</summary>
    public (RecordCipher Send, RecordCipher Receive) Split()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException("handshake not complete");
        }

        var (k1, k2) = _ss.Split();
        return _role == Role.Initiator ? (new RecordCipher(k1), new RecordCipher(k2)) : (new RecordCipher(k2), new RecordCipher(k1));
    }
}
