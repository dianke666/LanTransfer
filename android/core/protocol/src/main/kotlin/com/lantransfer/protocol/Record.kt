package com.lantransfer.protocol

/**
 * 安全记录层的一个方向（规范第 7 节，需求 10.1、10.5）：
 * `Record := len:u16 ‖ AES‑256‑GCM(k, nonce = 0 ‖ n, ad = 空, plaintext)`，len 17–65535，n 从 0 开始逐条加 1。
 */
class RecordCipher internal constructor(private val key: ByteArray) {
    private var n = 0L // 按无符号解释
    private var reader: RecordReader? = null

    /**
     * 加密 [plain] 的 [off, off+len) 部分，按每条最多 65,519 字节拆成若干条 record 并依次拼接；len 为 0 时输出为空。
     * 计数器将要用尽时抛出 [IllegalStateException]（调用方应终止 Session）。
     */
    fun seal(plain: ByteArray, off: Int = 0, len: Int = plain.size - off): ByteArray {
        require(off >= 0 && len >= 0 && off + len <= plain.size) { "range out of bounds" }
        val out = java.io.ByteArrayOutputStream(len + (len / ProtocolConstants.RECORD_MAX_PLAINTEXT + 1) * 18)
        var p = off
        val end = off + len
        while (p < end) {
            val chunk = minOf(ProtocolConstants.RECORD_MAX_PLAINTEXT, end - p)
            check(n != LAST_NONCE) { "record counter exhausted" }
            val ct = Primitives.aesGcmEncrypt(key, n, ByteArray(0), plain, p, chunk)
            n++
            out.write(ct.size ushr 8)
            out.write(ct.size and 0xFF)
            out.write(ct)
            p += chunk
        }
        return out.toByteArray()
    }

    /** 接收方向的流式读取器（同一 RecordCipher 只能有一个）。 */
    fun openStream(): RecordReader {
        check(reader == null) { "stream already opened" }
        return RecordReader(key).also { reader = it }
    }

    companion object {
        /** Noise 保留 2⁶⁴ − 1，可用的最大计数器为 2⁶⁴ − 2。 */
        internal const val LAST_NONCE = -1L // 0xFFFF_FFFF_FFFF_FFFF
    }
}

/** [RecordReader.feed] 的结果。 */
sealed interface RecordFeedResult {
    /** 本次输入中完整到达的 record 解密后的明文（按顺序）。 */
    data class Plain(val chunks: List<Bytes>) : RecordFeedResult

    /** 完整性校验失败；[chunksBefore] 是同一次输入中错误之前已通过校验的明文。之后不再输出任何明文。 */
    data class IntegrityError(val chunksBefore: List<Bytes>) : RecordFeedResult
}

/** 把 TCP 字节流切成 record 并解密；出错后不再接受输入（需求 10.5）。 */
class RecordReader internal constructor(private val key: ByteArray) {
    private var n = 0L
    private val lenBuf = ByteArray(2)
    private var lenFill = 0
    private var body: ByteArray? = null
    private var bodyFill = 0
    private var failed = false

    val isFailed: Boolean get() = failed

    /** 缓存的未完成 record 字节数（≤ 2 + 65535）。 */
    val bufferedBytes: Int get() = lenFill + bodyFill

    fun feed(bytes: ByteArray, off: Int = 0, len: Int = bytes.size - off): RecordFeedResult {
        require(off >= 0 && len >= 0 && off + len <= bytes.size) { "range out of bounds" }
        if (failed) return RecordFeedResult.IntegrityError(emptyList())
        val out = ArrayList<Bytes>()
        var i = off
        val end = off + len
        while (i < end) {
            val b = body
            if (b == null) {
                val take = minOf(2 - lenFill, end - i)
                bytes.copyInto(lenBuf, lenFill, i, i + take)
                lenFill += take
                i += take
                if (lenFill < 2) break
                val recLen = ((lenBuf[0].toInt() and 0xFF) shl 8) or (lenBuf[1].toInt() and 0xFF)
                if (recLen < ProtocolConstants.RECORD_MIN_CIPHERTEXT) return fail(out)
                body = ByteArray(recLen)
                bodyFill = 0
                continue
            }
            val take = minOf(b.size - bodyFill, end - i)
            bytes.copyInto(b, bodyFill, i, i + take)
            bodyFill += take
            i += take
            if (bodyFill < b.size) break
            if (n == RecordCipher.LAST_NONCE) return fail(out)
            val plain = Primitives.aesGcmDecrypt(key, n, ByteArray(0), b) ?: return fail(out)
            n++
            out += Bytes.of(plain)
            body = null
            bodyFill = 0
            lenFill = 0
        }
        return RecordFeedResult.Plain(out)
    }

    private fun fail(out: List<Bytes>): RecordFeedResult {
        failed = true
        body = null
        bodyFill = 0
        lenFill = 0
        return RecordFeedResult.IntegrityError(out)
    }
}
