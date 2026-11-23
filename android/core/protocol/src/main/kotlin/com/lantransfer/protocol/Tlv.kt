package com.lantransfer.protocol

import java.io.ByteArrayOutputStream

/** 载荷不符合 Struct 规则（规范 2.2、2.3）。只在协议层内部使用，对外转换为 [DecodeError.MalformedPayload] 等结果。 */
internal class MalformedException(val detail: String) : Exception(detail)

/** 协议中的枚举：取值编码与引入版本。 */
interface ProtocolEnum {
    val code: Int
    val since: ProtocolVersion
}

/** [version] 中是否已定义该取值（同一主版本内，引入的次版本不晚于协商的次版本）。 */
fun ProtocolEnum.definedIn(version: ProtocolVersion): Boolean =
    since.major == version.major && since.minor <= version.minor

/** TLV Struct 编码器：字段必须按 tag 升序写入（规范 2.2 第 6 条）。 */
internal class TlvWriter {
    private val out = ByteArrayOutputStream()
    private var lastTag = -1

    private fun header(tag: Int, length: Int) {
        require(tag in 0..255 && tag > lastTag) { "TLV tags must be strictly increasing: $tag after $lastTag" }
        lastTag = tag
        out.write(tag)
        writeU32(length.toLong())
    }

    private fun writeU32(v: Long) {
        out.write((v ushr 24).toInt() and 0xFF)
        out.write((v ushr 16).toInt() and 0xFF)
        out.write((v ushr 8).toInt() and 0xFF)
        out.write(v.toInt() and 0xFF)
    }

    private fun writeU64(v: Long) {
        for (shift in 56 downTo 0 step 8) out.write((v ushr shift).toInt() and 0xFF)
    }

    fun u8(tag: Int, v: Int) {
        require(v in 0..0xFF) { "u8 out of range: $v" }
        header(tag, 1)
        out.write(v)
    }

    fun u16(tag: Int, v: Int) {
        require(v in 0..0xFFFF) { "u16 out of range: $v" }
        header(tag, 2)
        out.write(v ushr 8)
        out.write(v and 0xFF)
    }

    fun u32(tag: Int, v: UInt) {
        header(tag, 4)
        writeU32(v.toLong())
    }

    fun u64(tag: Int, v: ULong) {
        header(tag, 8)
        writeU64(v.toLong())
    }

    fun i64(tag: Int, v: Long) {
        header(tag, 8)
        writeU64(v)
    }

    fun enum(tag: Int, v: ProtocolEnum) = u8(tag, v.code)

    fun bytes(tag: Int, v: Bytes) {
        header(tag, v.size)
        out.write(v.toByteArray())
    }

    fun bytes(tag: Int, v: ByteArray) {
        header(tag, v.size)
        out.write(v)
    }

    fun bytes16(tag: Int, v: Bytes16) = bytes(tag, v.toByteArray())

    /** 前置条件：[v] 不含 Unpaired_Surrogate（StrictUtf8.encode 会拒绝）。 */
    fun string(tag: Int, v: String) = bytes(tag, StrictUtf8.encode(v))

    fun <T> list(tag: Int, items: List<T>, element: TlvWriter.(T) -> Unit) {
        val body = ByteArrayOutputStream()
        val count = items.size.toLong()
        body.write(u32Bytes(count))
        for (item in items) {
            val w = TlvWriter()
            w.element(item)
            val bytes = w.toByteArray()
            body.write(u32Bytes(bytes.size.toLong()))
            body.write(bytes)
        }
        bytes(tag, body.toByteArray())
    }

    fun toByteArray(): ByteArray = out.toByteArray()

    private fun u32Bytes(v: Long) = byteArrayOf((v ushr 24).toByte(), (v ushr 16).toByte(), (v ushr 8).toByte(), v.toByte())
}

/**
 * 已解析的 TLV Struct：构造时检查 tag 严格递增、每个字段不越界、字段恰好填满缓冲区（规范 2.2 第 1、5 条）。
 * 读取已知字段时检查类型长度；未知 tag 不会被读取，因而被跳过（第 4 条）。
 */
internal class TlvStruct(private val buf: ByteArray, offset: Int, length: Int) {
    private val starts = IntArray(256) { -1 }
    private val lengths = IntArray(256)

    init {
        var pos = offset
        val end = offset + length
        var lastTag = -1
        while (pos < end) {
            if (end - pos < 5) throw MalformedException("truncated field header")
            val tag = buf[pos].toInt() and 0xFF
            val len = readU32(buf, pos + 1)
            if (len > (end - pos - 5).toLong()) throw MalformedException("field $tag exceeds buffer")
            if (tag <= lastTag) throw MalformedException("tag $tag not strictly increasing")
            lastTag = tag
            starts[tag] = pos + 5
            lengths[tag] = len.toInt()
            pos += 5 + len.toInt()
        }
    }

    fun has(tag: Int): Boolean = starts[tag] >= 0

    private fun startOf(tag: Int, name: String): Int {
        if (!has(tag)) throw MalformedException("missing field $tag ($name)")
        return starts[tag]
    }

    private fun fixed(tag: Int, name: String, size: Int): Int {
        val start = startOf(tag, name)
        if (lengths[tag] != size) throw MalformedException("field $tag ($name) must be $size bytes, got ${lengths[tag]}")
        return start
    }

    fun u8(tag: Int, name: String): Int = buf[fixed(tag, name, 1)].toInt() and 0xFF

    fun u16(tag: Int, name: String): Int {
        val p = fixed(tag, name, 2)
        return ((buf[p].toInt() and 0xFF) shl 8) or (buf[p + 1].toInt() and 0xFF)
    }

    fun u32(tag: Int, name: String): UInt = readU32(buf, fixed(tag, name, 4)).toUInt()

    fun u64(tag: Int, name: String): ULong = readU64(buf, fixed(tag, name, 8)).toULong()

    fun i64(tag: Int, name: String): Long = readU64(buf, fixed(tag, name, 8))

    fun optU64(tag: Int, name: String): ULong? = if (has(tag)) u64(tag, name) else null

    fun optI64(tag: Int, name: String): Long? = if (has(tag)) i64(tag, name) else null

    fun bytes(tag: Int, name: String): Bytes {
        val p = startOf(tag, name)
        return Bytes.of(buf, p, lengths[tag])
    }

    fun bytesFixed(tag: Int, name: String, size: Int): Bytes = Bytes.of(buf, fixed(tag, name, size), size)

    fun bytes16(tag: Int, name: String): Bytes16 = Bytes16.of(buf.copyOfRange(fixed(tag, name, 16), starts[tag] + 16))

    fun length(tag: Int): Int = lengths[tag]

    fun string(tag: Int, name: String): String {
        val p = startOf(tag, name)
        return StrictUtf8.decode(buf, p, lengths[tag]) ?: throw MalformedException("field $tag ($name) is not valid UTF-8")
    }

    fun optString(tag: Int, name: String): String? = if (has(tag)) string(tag, name) else null

    fun <E : ProtocolEnum> enum(tag: Int, name: String, values: List<E>, version: ProtocolVersion): E {
        val code = u8(tag, name)
        return values.firstOrNull { it.code == code && it.definedIn(version) }
            ?: throw MalformedException("field $tag ($name) has undefined value $code")
    }

    fun list(tag: Int, name: String): List<TlvStruct> {
        val p = startOf(tag, name)
        val end = p + lengths[tag]
        if (lengths[tag] < 4) throw MalformedException("list $tag ($name) truncated")
        val count = readU32(buf, p)
        var pos = p + 4
        val items = ArrayList<TlvStruct>()
        var i = 0L
        while (i < count) {
            if (end - pos < 4) throw MalformedException("list $tag ($name) element header truncated")
            val len = readU32(buf, pos)
            if (len > (end - pos - 4).toLong()) throw MalformedException("list $tag ($name) element exceeds buffer")
            items += TlvStruct(buf, pos + 4, len.toInt())
            pos += 4 + len.toInt()
            i++
        }
        if (pos != end) throw MalformedException("list $tag ($name) has trailing bytes")
        return items
    }

    companion object {
        fun readU32(b: ByteArray, p: Int): Long =
            ((b[p].toLong() and 0xFF) shl 24) or ((b[p + 1].toLong() and 0xFF) shl 16) or
                ((b[p + 2].toLong() and 0xFF) shl 8) or (b[p + 3].toLong() and 0xFF)

        fun readU64(b: ByteArray, p: Int): Long {
            var v = 0L
            for (k in 0 until 8) v = (v shl 8) or (b[p + k].toLong() and 0xFF)
            return v
        }
    }
}
