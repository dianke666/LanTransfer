package com.lantransfer.protocol

import org.bouncycastle.math.ec.rfc7748.X25519
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** X25519 密钥对（Device_Identity 的身份密钥对与 Noise 临时密钥对）。私钥按 RFC 7748 在运算时钳位。 */
class X25519KeyPair private constructor(private val priv: ByteArray, val publicKey: Bytes) {
    fun privateKeyBytes(): ByteArray = priv.copyOf()

    /** 与对方公钥做 DH；结果全零（对方公钥为小阶点）时返回 null。 */
    fun agree(remotePublic: ByteArray): ByteArray? {
        if (remotePublic.size != KEY_SIZE) return null
        val out = ByteArray(KEY_SIZE)
        return if (X25519.calculateAgreement(priv, 0, remotePublic, 0, out, 0)) out else null
    }

    override fun toString(): String = "X25519KeyPair(public=${publicKey.toHex()})"

    companion object {
        const val KEY_SIZE = 32

        fun fromPrivateKey(privateKey: ByteArray): X25519KeyPair {
            require(privateKey.size == KEY_SIZE) { "X25519 private key must be 32 bytes" }
            val pub = ByteArray(KEY_SIZE)
            X25519.scalarMultBase(privateKey, 0, pub, 0)
            return X25519KeyPair(privateKey.copyOf(), Bytes.of(pub))
        }

        fun generate(random: SecureRandom): X25519KeyPair {
            val k = ByteArray(KEY_SIZE)
            random.nextBytes(k)
            return fromPrivateKey(k)
        }
    }
}

/** Noise 临时密钥来源（端口）：生产环境用 CSPRNG，测试时注入固定密钥。 */
fun interface EphemeralKeySource {
    fun next(): X25519KeyPair

    companion object {
        fun secure(random: SecureRandom = SecureRandom()): EphemeralKeySource = EphemeralKeySource { X25519KeyPair.generate(random) }

        /** 依次返回给定的私钥（测试向量用）。 */
        fun fixed(vararg privateKeys: ByteArray): EphemeralKeySource {
            val queue = ArrayDeque(privateKeys.toList())
            return EphemeralKeySource { X25519KeyPair.fromPrivateKey(queue.removeFirst()) }
        }
    }
}

/** 平台密码学原语：SHA‑256、HMAC‑SHA256、AES‑256‑GCM（JCA）。 */
internal object Primitives {
    const val HASH_LEN = 32
    const val TAG_LEN = 16

    fun sha256(vararg parts: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        parts.forEach { md.update(it) }
        return md.digest()
    }

    fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        parts.forEach { mac.update(it) }
        return mac.doFinal()
    }

    /** Noise 的 HKDF：返回 2 个 32 字节输出。 */
    fun hkdf2(chainingKey: ByteArray, ikm: ByteArray): Pair<ByteArray, ByteArray> {
        val temp = hmac(chainingKey, ikm)
        val o1 = hmac(temp, byteArrayOf(1))
        val o2 = hmac(temp, o1, byteArrayOf(2))
        return o1 to o2
    }

    /** Noise AESGCM 的 nonce：4 字节 0 ‖ n（大端 u64）。 */
    fun nonce(n: Long): ByteArray {
        val b = ByteArray(12)
        for (i in 0 until 8) b[4 + i] = (n ushr (56 - 8 * i)).toByte()
        return b
    }

    fun aesGcmEncrypt(key: ByteArray, n: Long, ad: ByteArray, plain: ByteArray, off: Int = 0, len: Int = plain.size): ByteArray {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LEN * 8, nonce(n)))
        if (ad.isNotEmpty()) c.updateAAD(ad)
        return c.doFinal(plain, off, len)
    }

    /** 认证失败时返回 null。 */
    fun aesGcmDecrypt(key: ByteArray, n: Long, ad: ByteArray, cipher: ByteArray, off: Int = 0, len: Int = cipher.size): ByteArray? {
        if (len < TAG_LEN) return null
        return try {
            val c = Cipher.getInstance("AES/GCM/NoPadding")
            c.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LEN * 8, nonce(n)))
            if (ad.isNotEmpty()) c.updateAAD(ad)
            c.doFinal(cipher, off, len)
        } catch (_: AEADBadTagException) {
            null
        } catch (_: javax.crypto.BadPaddingException) {
            null
        }
    }
}

/** 公钥指纹 = SHA‑256(32 字节公钥)。 */
object Fingerprint {
    fun of(publicKey: ByteArray): ByteArray = Primitives.sha256(publicKey)
}

/** 配对验证码派生（规范第 10 节，需求 7.2、10.3）。 */
object VerificationCode {
    private val COMMIT_PREFIX = "LanTransfer-Commit-v1".encodeToByteArray()
    private val SAS_PREFIX = "LanTransfer-SAS-v1".encodeToByteArray()

    fun commitment(pkI: ByteArray, pkR: ByteArray, nI: ByteArray): ByteArray = Primitives.sha256(COMMIT_PREFIX, pkI, pkR, nI)

    /** 揭示的 [nI] 是否与承诺一致（常数时间比较）。 */
    fun verifyCommitment(commitment: ByteArray, pkI: ByteArray, pkR: ByteArray, nI: ByteArray): Boolean =
        MessageDigest.isEqual(commitment, commitment(pkI, pkR, nI))

    /** 6 位十进制数字，左侧补零。 */
    fun sas(pkI: ByteArray, pkR: ByteArray, nI: ByteArray, nR: ByteArray): String {
        val h = Primitives.sha256(SAS_PREFIX, pkI, pkR, nI, nR)
        var v = 0L
        for (i in 0 until 8) v = (v shl 8) or (h[i].toLong() and 0xFF)
        val code = java.lang.Long.remainderUnsigned(v, 1_000_000L)
        return code.toString().padStart(6, '0')
    }
}
