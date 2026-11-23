using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace LanTransfer.Protocol;

/// <summary>X25519 密钥对（Device_Identity 的身份密钥对与 Noise 临时密钥对），与 Kotlin 侧 <c>X25519KeyPair</c> 对应。</summary>
public sealed class X25519KeyPair
{
    public const int KeySize = 32;

    private readonly byte[] _private;

    private X25519KeyPair(byte[] priv, Bytes pub)
    {
        _private = priv;
        PublicKey = pub;
    }

    public Bytes PublicKey { get; }

    public byte[] PrivateKeyBytes() => (byte[])_private.Clone();

    public static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize)
        {
            throw new ArgumentException("X25519 private key must be 32 bytes", nameof(privateKey));
        }

        var priv = privateKey.ToArray();
        var pub = new byte[KeySize];
        X25519.ScalarMultBase(priv, 0, pub, 0);
        return new X25519KeyPair(priv, Bytes.Of(pub));
    }

    public static X25519KeyPair Generate() => FromPrivateKey(RandomNumberGenerator.GetBytes(KeySize));

    /// <summary>与对方公钥做 DH；结果全零（对方公钥为小阶点）时返回 null。</summary>
    public byte[]? Agree(ReadOnlySpan<byte> remotePublic)
    {
        if (remotePublic.Length != KeySize)
        {
            return null;
        }

        var output = new byte[KeySize];
        return X25519.CalculateAgreement(_private, 0, remotePublic.ToArray(), 0, output, 0) ? output : null;
    }

    public override string ToString() => $"X25519KeyPair(public={PublicKey.ToHex()})";
}

/// <summary>Noise 临时密钥来源（端口）：生产环境用 CSPRNG，测试时注入固定密钥。</summary>
public interface IEphemeralKeySource
{
    X25519KeyPair Next();
}

public static class EphemeralKeySource
{
    private sealed class SecureSource : IEphemeralKeySource
    {
        public X25519KeyPair Next() => X25519KeyPair.Generate();
    }

    private sealed class FixedSource(IEnumerable<byte[]> keys) : IEphemeralKeySource
    {
        private readonly Queue<byte[]> _keys = new(keys);

        public X25519KeyPair Next() => X25519KeyPair.FromPrivateKey(_keys.Dequeue());
    }

    public static IEphemeralKeySource Secure { get; } = new SecureSource();

    /// <summary>依次返回给定的私钥（测试向量用）。</summary>
    public static IEphemeralKeySource Fixed(params byte[][] privateKeys) => new FixedSource(privateKeys);
}

/// <summary>平台密码学原语：SHA‑256、HMAC‑SHA256、AES‑256‑GCM（CNG）。</summary>
internal static class Primitives
{
    public const int HashLen = 32;
    public const int TagLen = 16;

    public static byte[] Sha256(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts)
        {
            h.AppendData(p);
        }

        return h.GetHashAndReset();
    }

    public static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        foreach (var p in parts)
        {
            h.AppendData(p);
        }

        return h.GetHashAndReset();
    }

    /// <summary>Noise 的 HKDF：返回 2 个 32 字节输出。</summary>
    public static (byte[] First, byte[] Second) Hkdf2(byte[] chainingKey, byte[] ikm)
    {
        var temp = Hmac(chainingKey, ikm);
        var o1 = Hmac(temp, [1]);
        var o2 = Hmac(temp, o1, [2]);
        return (o1, o2);
    }

    /// <summary>Noise AESGCM 的 nonce：4 字节 0 ‖ n（大端 u64）。</summary>
    public static byte[] Nonce(ulong n)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(4), n);
        return b;
    }

    /// <summary>返回 ciphertext ‖ tag。</summary>
    public static byte[] AesGcmEncrypt(byte[] key, ulong n, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plain)
    {
        var output = new byte[plain.Length + TagLen];
        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(Nonce(n), plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), ad);
        return output;
    }

    /// <summary>认证失败时返回 null。</summary>
    public static byte[]? AesGcmDecrypt(byte[] key, ulong n, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> cipher)
    {
        if (cipher.Length < TagLen)
        {
            return null;
        }

        var plain = new byte[cipher.Length - TagLen];
        using var aes = new AesGcm(key, TagLen);
        try
        {
            aes.Decrypt(Nonce(n), cipher[..plain.Length], cipher[plain.Length..], plain, ad);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }
}

/// <summary>公钥指纹 = SHA‑256(32 字节公钥)。</summary>
public static class Fingerprint
{
    public static byte[] Of(ReadOnlySpan<byte> publicKey) => SHA256.HashData(publicKey);
}

/// <summary>配对验证码派生（规范第 10 节，需求 7.2、10.3），与 Kotlin 侧 <c>VerificationCode</c> 对应。</summary>
public static class VerificationCode
{
    private static readonly byte[] CommitPrefix = Encoding.ASCII.GetBytes("LanTransfer-Commit-v1");
    private static readonly byte[] SasPrefix = Encoding.ASCII.GetBytes("LanTransfer-SAS-v1");

    public static byte[] Commitment(byte[] pkI, byte[] pkR, byte[] nI) => Primitives.Sha256(CommitPrefix, pkI, pkR, nI);

    /// <summary>揭示的 <paramref name="nI"/> 是否与承诺一致（常数时间比较）。</summary>
    public static bool VerifyCommitment(byte[] commitment, byte[] pkI, byte[] pkR, byte[] nI) =>
        CryptographicOperations.FixedTimeEquals(commitment, Commitment(pkI, pkR, nI));

    /// <summary>6 位十进制数字，左侧补零。</summary>
    public static string Sas(byte[] pkI, byte[] pkR, byte[] nI, byte[] nR)
    {
        var h = Primitives.Sha256(SasPrefix, pkI, pkR, nI, nR);
        var v = BinaryPrimitives.ReadUInt64BigEndian(h) % 1_000_000UL;
        return v.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }
}
