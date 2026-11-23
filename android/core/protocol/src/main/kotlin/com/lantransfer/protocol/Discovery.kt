package com.lantransfer.protocol

/** Discovery_Announcement 的模型（规范第 3 节）。 */
data class Announcement(
    val deviceId: DeviceId,
    val displayName: String,
    val deviceType: DeviceType,
    val version: ProtocolVersion,
    val port: Int,
)

/** 名称是否为 1–32 个码点（不含 Unpaired_Surrogate 的前提下按码点计数）。 */
internal fun nameLengthOk(name: String): Boolean =
    name.codePointCount(0, name.length) in ProtocolConstants.MIN_NAME_CODE_POINTS..ProtocolConstants.MAX_NAME_CODE_POINTS

/** Discovery_Announcement 编解码（需求 5.6、23.1–23.3）。 */
object AnnouncementCodec {
    private val MAGIC = byteArrayOf(0x4C, 0x54, 0x44, 0x41) // "LTDA"

    /** 编码；前置条件：名称 1–32 个码点且不含 Unpaired_Surrogate，端口 1–65535。结果不超过 512 字节。 */
    fun encode(a: Announcement): ByteArray {
        require(!StrictUtf8.hasUnpairedSurrogate(a.displayName) && nameLengthOk(a.displayName)) { "display name must be 1..32 code points" }
        require(a.port in 1..0xFFFF) { "port must be 1..65535" }
        val w = TlvWriter()
        w.bytes16(1, a.deviceId.value)
        w.string(2, a.displayName)
        w.u8(3, a.deviceType.code)
        w.u8(4, a.version.major)
        w.u8(5, a.version.minor)
        w.u16(6, a.port)
        val out = MAGIC + w.toByteArray()
        check(out.size <= ProtocolConstants.MAX_ANNOUNCEMENT) { "announcement exceeds 512 bytes" }
        return out
    }

    /**
     * 解码；以下情况返回 null（丢弃，需求 23.2）：长度超过 512 字节、magic 不符、Struct 不合法或缺少字段、
     * 名称不是合法 UTF‑8 或码点数不在 1–32 之间、设备类型不在 {1, 2} 中、端口为 0。未知 tag 跳过。
     */
    fun decode(datagram: ByteArray): Announcement? {
        if (datagram.size > ProtocolConstants.MAX_ANNOUNCEMENT || datagram.size < MAGIC.size) return null
        for (i in MAGIC.indices) if (datagram[i] != MAGIC[i]) return null
        return try {
            val s = TlvStruct(datagram, MAGIC.size, datagram.size - MAGIC.size)
            val name = s.string(2, "display_name")
            val type = DeviceType.fromCode(s.u8(3, "device_type"))
            val port = s.u16(6, "port")
            if (!nameLengthOk(name) || type == null || port == 0) {
                null
            } else {
                Announcement(DeviceId(s.bytes16(1, "device_id")), name, type, ProtocolVersion(s.u8(4, "proto_major"), s.u8(5, "proto_minor")), port)
            }
        } catch (_: MalformedException) {
            null
        }
    }
}

/** 连接前导的版本协商结果（规范第 5 节）。 */
sealed interface Negotiation {
    data class Compatible(val version: ProtocolVersion) : Negotiation

    data class Incompatible(val local: ProtocolVersion, val remote: ProtocolVersion) : Negotiation
}

/** 连接前导 "LANT" ‖ major ‖ minor（需求 24.4–24.6）。 */
object PreambleCodec {
    const val SIZE = 6
    private val MAGIC = byteArrayOf(0x4C, 0x41, 0x4E, 0x54) // "LANT"

    fun encode(v: ProtocolVersion): ByteArray = MAGIC + byteArrayOf(v.major.toByte(), v.minor.toByte())

    /** 长度不是 6 字节或 magic 不符时返回 null。 */
    fun decode(b: ByteArray): ProtocolVersion? {
        if (b.size != SIZE) return null
        for (i in MAGIC.indices) if (b[i] != MAGIC[i]) return null
        return ProtocolVersion(b[4].toInt() and 0xFF, b[5].toInt() and 0xFF)
    }

    /** 主版本相同时协商为 (major, min(minor))，否则不兼容。 */
    fun negotiate(local: ProtocolVersion, remote: ProtocolVersion): Negotiation =
        if (local.major != remote.major) {
            Negotiation.Incompatible(local, remote)
        } else {
            Negotiation.Compatible(ProtocolVersion(local.major, minOf(local.minor, remote.minor)))
        }
}
