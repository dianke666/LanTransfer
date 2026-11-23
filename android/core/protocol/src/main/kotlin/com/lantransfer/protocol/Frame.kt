package com.lantransfer.protocol

/** [FrameEncoder.encode] 的结果。 */
sealed interface EncodeResult {
    /** 完整 Frame（帧头 + 载荷）。 */
    class Ok(val bytes: ByteArray) : EncodeResult

    /** 载荷超过 MAX_PAYLOAD，未输出任何字节（需求 22.12）。 */
    data class PayloadTooLarge(val length: Long) : EncodeResult
}

/** Frame 编码器（规范第 8 节）。 */
object FrameEncoder {
    /**
     * 编码一条消息。只输出 [version] 中已定义的字段；消息类型或枚举值在 [version] 中未定义时抛出 [IllegalArgumentException]
     * （属于调用方错误）；载荷超过 [maxPayload] 时返回 [EncodeResult.PayloadTooLarge]，不输出任何字节。
     */
    fun encode(msg: ProtocolMessage, version: ProtocolVersion = ProtocolVersion.CURRENT, maxPayload: Int = ProtocolConstants.MAX_PAYLOAD): EncodeResult {
        require(msg.type.definedIn(version)) { "${msg.type} is not defined in $version" }
        val payload = MessageCodec.encodePayload(msg)
        if (payload.size > maxPayload) return EncodeResult.PayloadTooLarge(payload.size.toLong())
        val out = ByteArray(ProtocolConstants.FRAME_HEADER_SIZE + payload.size)
        out[0] = (ProtocolConstants.FRAME_MAGIC ushr 8).toByte()
        out[1] = ProtocolConstants.FRAME_MAGIC.toByte()
        out[2] = msg.type.code.toByte()
        val len = payload.size
        out[3] = (len ushr 24).toByte()
        out[4] = (len ushr 16).toByte()
        out[5] = (len ushr 8).toByte()
        out[6] = len.toByte()
        payload.copyInto(out, ProtocolConstants.FRAME_HEADER_SIZE)
        return EncodeResult.Ok(out)
    }

    /** 编码成功时返回字节，否则抛出 [IllegalStateException]（用于确定不会超限的小消息）。 */
    fun encodeOrThrow(msg: ProtocolMessage, version: ProtocolVersion = ProtocolVersion.CURRENT): ByteArray =
        when (val r = encode(msg, version)) {
            is EncodeResult.Ok -> r.bytes
            is EncodeResult.PayloadTooLarge -> throw IllegalStateException("payload too large: ${r.length}")
        }
}

/** 解码错误（规范第 8 节）。 */
sealed interface DecodeError {
    data object BadMagic : DecodeError

    data class UnknownType(val type: Int) : DecodeError

    data class PayloadTooLarge(val length: Long) : DecodeError

    data class MalformedPayload(val type: Int, val detail: String) : DecodeError
}

/** 一次 [FrameDecoder.feed] 的结果：按到达顺序的完整消息，以及（若出错）错误。出错时错误之前的消息照常给出。 */
data class DecodeBatch(val messages: List<ProtocolMessage>, val error: DecodeError?)

/**
 * 流式 Frame 解码器（规范第 8 节，需求 22.3–22.6、22.10、22.13）。
 *
 * - 收齐 7 字节帧头后立即检查 magic、type、length，不等待载荷；
 * - 出错后不再接受输入：之后的 [feed] 忽略输入并返回空结果，[error] 保留第一次的错误；
 * - [bufferedBytes] 不超过 7 + [maxPayload]；[close] 丢弃未完整到达的残留字节。
 */
class FrameDecoder(
    private val version: ProtocolVersion = ProtocolVersion.CURRENT,
    private val maxPayload: Int = ProtocolConstants.MAX_PAYLOAD,
) {
    private val header = ByteArray(ProtocolConstants.FRAME_HEADER_SIZE)
    private var headerFill = 0
    private var type: MessageType? = null
    private var payload: ByteArray? = null
    private var payloadFill = 0
    private var closed = false

    /** 第一次出现的解码错误；未出错时为 null。 */
    var error: DecodeError? = null
        private set

    val bufferedBytes: Int get() = headerFill + payloadFill

    val isClosed: Boolean get() = closed

    fun feed(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size - offset): DecodeBatch {
        require(offset >= 0 && length >= 0 && offset + length <= bytes.size) { "range out of bounds" }
        if (error != null || closed) return DecodeBatch(emptyList(), null)
        val out = ArrayList<ProtocolMessage>()
        var i = offset
        val end = offset + length
        while (true) {
            if (payload == null) {
                if (i == end) break
                val take = minOf(ProtocolConstants.FRAME_HEADER_SIZE - headerFill, end - i)
                bytes.copyInto(header, headerFill, i, i + take)
                headerFill += take
                i += take
                if (headerFill < ProtocolConstants.FRAME_HEADER_SIZE) break
                val failure = startFrame()
                if (failure != null) return fail(out, failure)
            }
            val p = payload!!
            if (payloadFill < p.size) {
                if (i == end) break
                val take = minOf(p.size - payloadFill, end - i)
                bytes.copyInto(p, payloadFill, i, i + take)
                payloadFill += take
                i += take
                if (payloadFill < p.size) break
            }
            val t = type!!
            val msg = try {
                MessageCodec.decodePayload(t, p, 0, p.size, version)
            } catch (e: MalformedException) {
                return fail(out, DecodeError.MalformedPayload(t.code, e.detail))
            }
            out += msg
            reset()
        }
        return DecodeBatch(out, null)
    }

    /** 连接关闭时调用：丢弃未完整到达的残留字节，不输出任何消息。 */
    fun close() {
        reset()
        closed = true
    }

    private fun startFrame(): DecodeError? {
        val magic = ((header[0].toInt() and 0xFF) shl 8) or (header[1].toInt() and 0xFF)
        if (magic != ProtocolConstants.FRAME_MAGIC) return DecodeError.BadMagic
        val code = header[2].toInt() and 0xFF
        val t = MessageType.fromCode(code)?.takeIf { it.definedIn(version) } ?: return DecodeError.UnknownType(code)
        val len = TlvStruct.readU32(header, 3)
        if (len > maxPayload) return DecodeError.PayloadTooLarge(len)
        type = t
        payload = ByteArray(len.toInt())
        payloadFill = 0
        return null
    }

    private fun fail(out: List<ProtocolMessage>, e: DecodeError): DecodeBatch {
        error = e
        reset()
        return DecodeBatch(out, e)
    }

    private fun reset() {
        headerFill = 0
        type = null
        payload = null
        payloadFill = 0
    }
}
