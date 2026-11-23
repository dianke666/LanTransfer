package com.lantransfer.protocol

/** 协议常量（规范第 1 节）。 */
object ProtocolConstants {
    const val DEFAULT_PORT = 42580
    const val DISCOVERY_PORT = 42581
    const val MULTICAST_GROUP = "239.255.42.58"

    const val FRAME_MAGIC = 0x4C54
    const val FRAME_HEADER_SIZE = 7
    const val MAX_PAYLOAD = 2 * 1024 * 1024

    const val MAX_TEXT_BYTES = 1_048_576
    const val MAX_CHUNK = 65_536
    const val MAX_FILES_PER_OFFER = 1000
    const val MAX_FILE_SIZE = 1L shl 40 // 1 TiB
    const val MAX_MIME_BYTES = 255

    const val RECORD_MIN_CIPHERTEXT = 17
    const val RECORD_MAX_CIPHERTEXT = 65_535
    const val RECORD_MAX_PLAINTEXT = RECORD_MAX_CIPHERTEXT - 16

    const val MAX_ANNOUNCEMENT = 512
    const val MAX_QR_TEXT = 512
    const val MAX_QR_IPS = 4
    const val MIN_NAME_CODE_POINTS = 1
    const val MAX_NAME_CODE_POINTS = 32
}

/** Protocol_Version：主版本号.次版本号。 */
data class ProtocolVersion(val major: Int, val minor: Int) : Comparable<ProtocolVersion> {
    init {
        require(major in 0..255 && minor in 0..255) { "version components must be u8" }
    }

    override fun compareTo(other: ProtocolVersion): Int =
        if (major != other.major) major.compareTo(other.major) else minor.compareTo(other.minor)

    override fun toString(): String = "$major.$minor"

    companion object {
        val V1_0 = ProtocolVersion(1, 0)

        /** 本实现支持的最新版本。 */
        val CURRENT = V1_0
    }
}

/** 设备类型（公告、二维码不含此项，握手身份与公告中出现）。 */
enum class DeviceType(val code: Int) {
    ANDROID(1),
    WINDOWS(2),
    IOS(3),
    ;

    companion object {
        fun fromCode(code: Int): DeviceType? = entries.firstOrNull { it.code == code }
    }
}
