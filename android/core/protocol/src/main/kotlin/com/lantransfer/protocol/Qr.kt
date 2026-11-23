package com.lantransfer.protocol

/**
 * QR_Payload 的模型（规范第 4 节）。[ipv4List] 中每个元素是一个 IPv4 地址（大端，按 Int 存放），按优先级排列、互不相同、1–4 个；
 * [fingerprint] 为 SHA‑256(显示方身份公钥)，[token] 为 Pairing_Token。
 */
data class QrPayload(
    val deviceId: DeviceId,
    val displayName: String,
    val ipv4List: List<Int>,
    val port: Int,
    val fingerprint: Bytes,
    val token: Bytes16,
)

/** 二维码文本解析失败的类别（只用于诊断；任何失败都不输出部分结果）。 */
enum class QrError { TOO_LONG, BAD_PREFIX, BAD_BASE32, MALFORMED, INVALID_FIELD }

sealed interface QrDecodeResult {
    data class Ok(val payload: QrPayload) : QrDecodeResult

    data class Error(val kind: QrError) : QrDecodeResult
}

/** RFC 4648 Base32（大写、无填充），严格解码（规范第 4 节）。 */
object Base32 {
    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"

    fun encode(b: ByteArray): String {
        val sb = StringBuilder((b.size * 8 + 4) / 5)
        var buffer = 0
        var bits = 0
        for (x in b) {
            buffer = (buffer shl 8) or (x.toInt() and 0xFF)
            bits += 8
            while (bits >= 5) {
                sb.append(ALPHABET[(buffer ushr (bits - 5)) and 0x1F])
                bits -= 5
            }
        }
        if (bits > 0) sb.append(ALPHABET[(buffer shl (5 - bits)) and 0x1F])
        return sb.toString()
    }

    /** 只接受 A–Z、2–7；长度对 8 取模必须属于 {0, 2, 4, 5, 7}；末尾多余比特必须为 0。否则返回 null。 */
    fun decode(s: String): ByteArray? {
        if (s.length % 8 !in setOf(0, 2, 4, 5, 7)) return null
        val out = ByteArray(s.length * 5 / 8)
        var buffer = 0
        var bits = 0
        var p = 0
        for (c in s) {
            val v = ALPHABET.indexOf(c)
            if (v < 0) return null
            buffer = ((buffer shl 5) or v) and 0xFFFF
            bits += 5
            if (bits >= 8) {
                out[p++] = (buffer ushr (bits - 8)).toByte()
                bits -= 8
            }
        }
        if (bits > 0 && (buffer and ((1 shl bits) - 1)) != 0) return null
        return out
    }
}

/** QR_Payload 文本编解码：`LT1:` ‖ Base32(QrStruct)（需求 8.3、23.4–23.8）。 */
object QrCodec {
    const val PREFIX = "LT1:"

    /** 编码；前置条件：字段满足规范第 4 节的约束。 */
    fun encode(p: QrPayload): String {
        require(validFields(p)) { "invalid QR payload fields" }
        val w = TlvWriter()
        w.bytes16(1, p.deviceId.value)
        w.string(2, p.displayName)
        val ips = ByteArray(4 * p.ipv4List.size)
        p.ipv4List.forEachIndexed { i, ip ->
            ips[4 * i] = (ip ushr 24).toByte()
            ips[4 * i + 1] = (ip ushr 16).toByte()
            ips[4 * i + 2] = (ip ushr 8).toByte()
            ips[4 * i + 3] = ip.toByte()
        }
        w.bytes(3, ips)
        w.u16(4, p.port)
        w.bytes(5, p.fingerprint)
        w.bytes16(6, p.token)
        val text = PREFIX + Base32.encode(w.toByteArray())
        check(text.length <= ProtocolConstants.MAX_QR_TEXT) { "QR text exceeds 512 bytes" }
        return text
    }

    /** 解析；整体成功或整体失败，失败时不输出任何部分结果（需求 23.6）。未知 tag 跳过。 */
    fun decode(text: String): QrDecodeResult {
        if (text.length > ProtocolConstants.MAX_QR_TEXT) return QrDecodeResult.Error(QrError.TOO_LONG)
        if (!text.startsWith(PREFIX)) return QrDecodeResult.Error(QrError.BAD_PREFIX)
        val raw = Base32.decode(text.substring(PREFIX.length)) ?: return QrDecodeResult.Error(QrError.BAD_BASE32)
        val payload = try {
            val s = TlvStruct(raw, 0, raw.size)
            val ipBytes = s.bytes(3, "ipv4_list").toByteArray()
            if (ipBytes.size % 4 != 0) return QrDecodeResult.Error(QrError.INVALID_FIELD)
            val ips = (0 until ipBytes.size / 4).map { i ->
                ((ipBytes[4 * i].toInt() and 0xFF) shl 24) or ((ipBytes[4 * i + 1].toInt() and 0xFF) shl 16) or
                    ((ipBytes[4 * i + 2].toInt() and 0xFF) shl 8) or (ipBytes[4 * i + 3].toInt() and 0xFF)
            }
            QrPayload(
                DeviceId(s.bytes16(1, "device_id")),
                s.string(2, "display_name"),
                ips,
                s.u16(4, "port"),
                s.bytesFixed(5, "fingerprint", 32),
                s.bytes16(6, "token"),
            )
        } catch (_: MalformedException) {
            return QrDecodeResult.Error(QrError.MALFORMED)
        }
        return if (validFields(payload)) QrDecodeResult.Ok(payload) else QrDecodeResult.Error(QrError.INVALID_FIELD)
    }

    private fun validFields(p: QrPayload): Boolean =
        !StrictUtf8.hasUnpairedSurrogate(p.displayName) && nameLengthOk(p.displayName) &&
            p.ipv4List.size in 1..ProtocolConstants.MAX_QR_IPS && p.ipv4List.toSet().size == p.ipv4List.size &&
            p.port in 1..0xFFFF && p.fingerprint.size == 32
}
