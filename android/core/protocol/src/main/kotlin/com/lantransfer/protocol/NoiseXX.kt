package com.lantransfer.protocol

/** 握手失败：解密失败、消息格式不对或 DH 结果无效（需求 10.6）。 */
class HandshakeAuthError(message: String) : Exception(message)

enum class Role { INITIATOR, RESPONDER }

/** Noise CipherState（规范第 6 节）。k 为 null 时 EncryptWithAd / DecryptWithAd 原样返回。 */
internal class CipherState(private var k: ByteArray? = null) {
    private var n = 0L

    val hasKey: Boolean get() = k != null

    fun encryptWithAd(ad: ByteArray, plain: ByteArray): ByteArray {
        val key = k ?: return plain
        return Primitives.aesGcmEncrypt(key, n, ad, plain).also { n++ }
    }

    fun decryptWithAd(ad: ByteArray, cipher: ByteArray): ByteArray {
        val key = k ?: return cipher
        val p = Primitives.aesGcmDecrypt(key, n, ad, cipher) ?: throw HandshakeAuthError("decryption failed")
        n++
        return p
    }

    fun key(): ByteArray = requireNotNull(k).copyOf()
}

/** Noise SymmetricState。 */
internal class SymmetricState(protocolName: String) {
    var h: ByteArray
        private set
    private var ck: ByteArray
    private val cs = CipherState()
    private var cipher = cs

    init {
        val name = protocolName.encodeToByteArray()
        h = if (name.size <= Primitives.HASH_LEN) name.copyOf(Primitives.HASH_LEN) else Primitives.sha256(name)
        ck = h.copyOf()
    }

    fun mixKey(ikm: ByteArray) {
        val (newCk, k) = Primitives.hkdf2(ck, ikm)
        ck = newCk
        cipher = CipherState(k)
    }

    fun mixHash(data: ByteArray) {
        h = Primitives.sha256(h, data)
    }

    fun encryptAndHash(plain: ByteArray): ByteArray = cipher.encryptWithAd(h, plain).also { mixHash(it) }

    fun decryptAndHash(ct: ByteArray): ByteArray {
        val p = cipher.decryptWithAd(h, ct)
        mixHash(ct)
        return p
    }

    fun split(): Pair<ByteArray, ByteArray> = Primitives.hkdf2(ck, ByteArray(0))
}

/**
 * Noise_XX_25519_AESGCM_SHA256 握手状态机（规范第 6 节，需求 10.2、10.6）：
 * msg1 → e；msg2 ← e, ee, s, es；msg3 → s, se。[writeMessage] / [readMessage] 处理不含 `len:u16` 封装的 noise_message。
 */
class NoiseXX(
    private val role: Role,
    private val localStatic: X25519KeyPair,
    prologue: ByteArray,
    private val ephemeral: EphemeralKeySource,
) {
    private val ss = SymmetricState(PROTOCOL_NAME)
    private var e: X25519KeyPair? = null
    private var re: ByteArray? = null
    private var rs: ByteArray? = null
    private var step = 0
    private var failed = false

    init {
        ss.mixHash(prologue)
    }

    /** 只供测试：在 `s` 中出示与私钥不对应的公钥，模拟冒充者（Property 14）。 */
    internal var advertisedStaticOverride: ByteArray? = null

    private fun advertisedStatic(): ByteArray = advertisedStaticOverride ?: localStatic.publicKey.toByteArray()

    /** 对方的静态公钥：发起方在读取 msg2 后可用，接收方在读取 msg3 后可用。 */
    val remoteStatic: ByteArray? get() = rs?.copyOf()

    /** 握手哈希 h（握手完成后可用于绑定会话）。 */
    val handshakeHash: ByteArray get() = ss.h.copyOf()

    val isComplete: Boolean get() = step == 3

    private fun dh(local: X25519KeyPair, remote: ByteArray): ByteArray =
        local.agree(remote) ?: throw HandshakeAuthError("invalid DH result")

    /** 写出下一条握手消息（本方必须是当前应发送的一方）。 */
    fun writeMessage(payload: ByteArray): ByteArray {
        check(!failed) { "handshake failed" }
        val out = java.io.ByteArrayOutputStream()
        when {
            step == 0 && role == Role.INITIATOR -> {
                val eph = ephemeral.next().also { e = it }
                out.write(eph.publicKey.toByteArray())
                ss.mixHash(eph.publicKey.toByteArray())
            }
            step == 1 && role == Role.RESPONDER -> {
                val eph = ephemeral.next().also { e = it }
                out.write(eph.publicKey.toByteArray())
                ss.mixHash(eph.publicKey.toByteArray())
                ss.mixKey(dh(eph, re!!)) // ee
                out.write(ss.encryptAndHash(advertisedStatic())) // s
                ss.mixKey(dh(localStatic, re!!)) // es
            }
            step == 2 && role == Role.INITIATOR -> {
                out.write(ss.encryptAndHash(advertisedStatic())) // s
                ss.mixKey(dh(localStatic, re!!)) // se
            }
            else -> throw IllegalStateException("not this side's turn to write (step $step, $role)")
        }
        out.write(ss.encryptAndHash(payload))
        step++
        return out.toByteArray()
    }

    /** 读取对方的握手消息并返回载荷；任何失败都抛出 [HandshakeAuthError]，之后不可再用。 */
    fun readMessage(message: ByteArray): ByteArray {
        check(!failed) { "handshake failed" }
        try {
            var p = 0
            fun take(n: Int): ByteArray {
                if (message.size - p < n) throw HandshakeAuthError("handshake message too short")
                return message.copyOfRange(p, p + n).also { p += n }
            }
            when {
                step == 0 && role == Role.RESPONDER -> {
                    re = take(32).also { ss.mixHash(it) }
                }
                step == 1 && role == Role.INITIATOR -> {
                    re = take(32).also { ss.mixHash(it) }
                    ss.mixKey(dh(e!!, re!!)) // ee
                    rs = ss.decryptAndHash(take(32 + Primitives.TAG_LEN)) // s
                    ss.mixKey(dh(e!!, rs!!)) // es
                }
                step == 2 && role == Role.RESPONDER -> {
                    rs = ss.decryptAndHash(take(32 + Primitives.TAG_LEN)) // s
                    ss.mixKey(dh(e!!, rs!!)) // se
                }
                else -> throw IllegalStateException("not this side's turn to read (step $step, $role)")
            }
            val payload = ss.decryptAndHash(message.copyOfRange(p, message.size))
            step++
            return payload
        } catch (ex: HandshakeAuthError) {
            failed = true
            throw ex
        }
    }

    /** 握手完成后得到（发送方向, 接收方向）的记录层密码。 */
    fun split(): Pair<RecordCipher, RecordCipher> {
        check(isComplete) { "handshake not complete" }
        val (k1, k2) = ss.split()
        return if (role == Role.INITIATOR) RecordCipher(k1) to RecordCipher(k2) else RecordCipher(k2) to RecordCipher(k1)
    }

    companion object {
        const val PROTOCOL_NAME = "Noise_XX_25519_AESGCM_SHA256"
        val PROLOGUE_PREFIX: ByteArray = "LanTransfer-Noise-v1".encodeToByteArray()

        /** prologue = "LanTransfer-Noise-v1" ‖ Preamble_I ‖ Preamble_R。 */
        fun prologue(preambleI: ByteArray, preambleR: ByteArray): ByteArray = PROLOGUE_PREFIX + preambleI + preambleR
    }
}
