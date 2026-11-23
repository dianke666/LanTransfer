package com.lantransfer.protocol

/**
 * 不可变字节串，按内容比较相等（协议中 `Bytes` / `Bytes[N]` 字段的模型类型）。
 * 构造时复制输入，[toByteArray] 返回副本，因此外部无法修改其内容。
 */
class Bytes private constructor(private val data: ByteArray) {
    val size: Int get() = data.size

    operator fun get(index: Int): Byte = data[index]

    fun toByteArray(): ByteArray = data.copyOf()

    /** 写入 [dest] 的 [offset] 处，不复制中间数组。 */
    fun copyInto(dest: ByteArray, offset: Int) {
        data.copyInto(dest, offset)
    }

    fun toHex(): String = hex(data, 0, data.size)

    override fun equals(other: Any?): Boolean = other is Bytes && data.contentEquals(other.data)

    override fun hashCode(): Int = data.contentHashCode()

    override fun toString(): String = "Bytes(${toHex()})"

    companion object {
        val EMPTY = Bytes(ByteArray(0))

        fun of(bytes: ByteArray): Bytes = Bytes(bytes.copyOf())

        fun of(bytes: ByteArray, offset: Int, length: Int): Bytes = Bytes(bytes.copyOfRange(offset, offset + length))

        fun ofHex(text: String): Bytes = Bytes(parseHex(text))

        internal fun hex(bytes: ByteArray, offset: Int, length: Int): String {
            val digits = "0123456789abcdef"
            val sb = StringBuilder(length * 2)
            for (i in offset until offset + length) {
                val b = bytes[i].toInt() and 0xFF
                sb.append(digits[b ushr 4]).append(digits[b and 0x0F])
            }
            return sb.toString()
        }

        internal fun parseHex(text: String): ByteArray {
            require(text.length % 2 == 0) { "hex length must be even" }
            return ByteArray(text.length / 2) { i ->
                val hi = Character.digit(text[2 * i], 16)
                val lo = Character.digit(text[2 * i + 1], 16)
                require(hi >= 0 && lo >= 0) { "invalid hex digit" }
                ((hi shl 4) or lo).toByte()
            }
        }
    }
}

/** 恰好 16 字节的不可变字节串（设备 ID、消息 ID、任务 ID、Pairing_Token 的底层类型）。 */
class Bytes16 private constructor(private val data: ByteArray) {
    fun toByteArray(): ByteArray = data.copyOf()

    fun toBytes(): Bytes = Bytes.of(data)

    fun toHex(): String = Bytes.hex(data, 0, SIZE)

    /** 按无符号字节逐个比较（同时互连时按发起方设备 ID 去重使用）。 */
    fun compareUnsigned(other: Bytes16): Int {
        for (i in 0 until SIZE) {
            val c = (data[i].toInt() and 0xFF) - (other.data[i].toInt() and 0xFF)
            if (c != 0) return c
        }
        return 0
    }

    override fun equals(other: Any?): Boolean = other is Bytes16 && data.contentEquals(other.data)

    override fun hashCode(): Int = data.contentHashCode()

    override fun toString(): String = toHex()

    companion object {
        const val SIZE = 16

        fun of(bytes: ByteArray): Bytes16 {
            require(bytes.size == SIZE) { "Bytes16 requires exactly 16 bytes, got ${bytes.size}" }
            return Bytes16(bytes.copyOf())
        }

        fun of(bytes: Bytes): Bytes16 = of(bytes.toByteArray())

        fun ofHex(text: String): Bytes16 = of(Bytes.parseHex(text))
    }
}

/** 设备 ID（Device_Identity 的 16 字节标识）。 */
@JvmInline
value class DeviceId(val value: Bytes16) {
    override fun toString(): String = "DeviceId(${value.toHex()})"
}

/** 文字消息 ID。 */
@JvmInline
value class MessageId(val value: Bytes16) {
    override fun toString(): String = "MessageId(${value.toHex()})"
}

/** 文件传输任务 ID。 */
@JvmInline
value class TransferId(val value: Bytes16) {
    override fun toString(): String = "TransferId(${value.toHex()})"
}
