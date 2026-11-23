package com.lantransfer.testing

import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.QrPayload
import io.kotest.property.Arb
import io.kotest.property.arbitrary.arbitrary
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.enum
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map

// 公告、二维码与版本的生成器（Property 5、8、9、10–12）。C# 侧同名生成器在 LanTransfer.Testing/DiscoveryGens.cs。

/** 1–32 个码点、不含 Unpaired_Surrogate 的显示名称，偏向 32 个增补平面字符（最长编码）与中文。 */
fun Gens.displayName(): Arb<String> = Arb.choose(
    3 to Arb.list(scalarValue(), 1..32).map { cps(*it.toIntArray()) },
    1 to Arb.int(1..32).map { n -> cps(*IntArray(n) { 0x1F600 + (it % 0x40) }) },
    1 to Arb.element("小明的手机", "Pixel 9", "办公室电脑", "😀".repeat(32), "a", "名".repeat(32)),
)

fun Gens.deviceId(): Arb<DeviceId> = bytes16().map { DeviceId(it) }

/** 任意 u8.u8 版本。 */
fun Gens.protocolVersion(): Arb<ProtocolVersion> = Arb.bind(Arb.int(0..255), Arb.int(0..255)) { a, b -> ProtocolVersion(a, b) }

fun Gens.port(): Arb<Int> = Arb.choose(3 to Arb.element(42580, 42590, 42599, 1, 65535), 2 to Arb.int(1..65535))

fun Gens.announcement(): Arb<Announcement> =
    Arb.bind(deviceId(), displayName(), Arb.enum<DeviceType>(), protocolVersion(), port()) { id, name, type, v, port ->
        Announcement(id, name, type, v, port)
    }

/** 1–4 个互不相同的 IPv4 地址。 */
fun Gens.qrIpv4List(): Arb<List<Int>> = Arb.list(Arb.int(), 1..4).map { it.distinct() }

fun Gens.qrPayload(): Arb<QrPayload> =
    Arb.bind(deviceId(), displayName(), qrIpv4List(), port(), bytesOfSize(32), bytes16()) { id, name, ips, port, fp, token ->
        QrPayload(id, name, ips, port, fp, token)
    }

/** 违反需求 23.2 某一项约束的公告数据报（Property 9）；[kind] 与 C# 侧 `InvalidAnnouncement.Kind` 的取值一致。 */
class InvalidAnnouncement(val bytes: ByteArray, val kind: String) {
    override fun toString(): String = "InvalidAnnouncement(kind=$kind, bytes=${bytes.joinToString("") { "%02x".format(it) }})"

    companion object {
        /** 有效字段 + 未知 tag 填充，总长 513–600 字节。 */
        const val OVERSIZE = "oversize"
        const val BAD_MAGIC = "bad-magic"
        const val MISSING_FIELD = "missing-field"
        const val BAD_UTF8 = "bad-utf8"

        /** 名称为 0 个或 33–40 个码点。 */
        const val NAME_LENGTH = "name-length"
        const val BAD_DEVICE_TYPE = "bad-device-type"
        const val PORT_ZERO = "port-zero"

        /** 定长字段（device_id、device_type、版本、端口）的长度不对。 */
        const val BAD_FIELD_LENGTH = "bad-field-length"

        /** 有效数据报截断到更短的长度（含不足 4 字节的 magic）。 */
        const val TRUNCATED = "truncated"

        /** 相邻字段次序颠倒或同一 tag 重复出现。 */
        const val TAG_ORDER = "tag-order"
        val kinds = listOf(OVERSIZE, BAD_MAGIC, MISSING_FIELD, BAD_UTF8, NAME_LENGTH, BAD_DEVICE_TYPE, PORT_ZERO, BAD_FIELD_LENGTH, TRUNCATED, TAG_ORDER)
    }
}

private val announcementMagic = byteArrayOf(0x4C, 0x54, 0x44, 0x41) // "LTDA"

private fun tlvField(tag: Int, value: ByteArray): ByteArray {
    val n = value.size
    return byteArrayOf(tag.toByte(), (n ushr 24).toByte(), (n ushr 16).toByte(), (n ushr 8).toByte(), n.toByte()) + value
}

private fun announcementFields(a: Announcement): MutableList<Pair<Int, ByteArray>> = mutableListOf(
    1 to a.deviceId.value.toByteArray(),
    2 to a.displayName.encodeToByteArray(),
    3 to byteArrayOf(a.deviceType.code.toByte()),
    4 to byteArrayOf(a.version.major.toByte()),
    5 to byteArrayOf(a.version.minor.toByte()),
    6 to byteArrayOf((a.port ushr 8).toByte(), a.port.toByte()),
)

private fun assemble(fields: List<Pair<Int, ByteArray>>): ByteArray =
    fields.fold(announcementMagic.copyOf()) { acc, (tag, value) -> acc + tlvField(tag, value) }

/**
 * 以 [base] 中的一个有效公告为基础，只破坏其中一项约束；生成的数据报都不超过 512 字节（[InvalidAnnouncement.OVERSIZE] 除外）。
 * 传入已在设备列表中的公告，可以检验“同一设备 ID 的非法数据报不刷新计时”。
 */
fun Gens.invalidAnnouncement(base: Arb<Announcement> = announcement()): Arb<InvalidAnnouncement> = arbitrary { rs ->
    val r = rs.random
    val fields = announcementFields(base.bind())
    when (val kind = InvalidAnnouncement.kinds[r.nextInt(InvalidAnnouncement.kinds.size)]) {
        InvalidAnnouncement.OVERSIZE -> {
            val base = assemble(fields).size
            val pad = ProtocolConstants.MAX_ANNOUNCEMENT + 1 - base - 5 + r.nextInt(0, 88)
            fields += 0x7F to ByteArray(pad) { (it * 31).toByte() }
            InvalidAnnouncement(assemble(fields), kind)
        }
        InvalidAnnouncement.BAD_MAGIC -> {
            val b = assemble(fields)
            val i = r.nextInt(4)
            b[i] = (b[i].toInt() xor r.nextInt(1, 256)).toByte()
            InvalidAnnouncement(b, kind)
        }
        InvalidAnnouncement.MISSING_FIELD -> {
            fields.removeAt(r.nextInt(fields.size))
            InvalidAnnouncement(assemble(fields), kind)
        }
        InvalidAnnouncement.BAD_UTF8 -> {
            val bad = invalidUtf8().bind().bytes
            fields[1] = 2 to bad
            val b = assemble(fields)
            if (b.size <= ProtocolConstants.MAX_ANNOUNCEMENT) {
                InvalidAnnouncement(b, kind)
            } else {
                // 前后缀过长时只保留一段非法字节
                fields[1] = 2 to byteArrayOf(0xC0.toByte(), 0x80.toByte())
                InvalidAnnouncement(assemble(fields), kind)
            }
        }
        InvalidAnnouncement.NAME_LENGTH -> {
            val n = if (r.nextInt(3) == 0) 0 else r.nextInt(33, 41)
            val name = cps(*IntArray(n) { scalarValue().bind() })
            fields[1] = 2 to name.encodeToByteArray()
            InvalidAnnouncement(assemble(fields), kind)
        }
        InvalidAnnouncement.BAD_DEVICE_TYPE -> {
            val code = if (r.nextBoolean()) 0 else r.nextInt(4, 256)
            fields[2] = 3 to byteArrayOf(code.toByte())
            InvalidAnnouncement(assemble(fields), kind)
        }
        InvalidAnnouncement.PORT_ZERO -> {
            fields[5] = 6 to byteArrayOf(0, 0)
            InvalidAnnouncement(assemble(fields), kind)
        }
        InvalidAnnouncement.BAD_FIELD_LENGTH -> {
            val i = listOf(0, 2, 3, 4, 5)[r.nextInt(5)]
            val (tag, value) = fields[i]
            val delta = if (value.size > 1 && r.nextBoolean()) -r.nextInt(1, value.size) else r.nextInt(1, 4)
            val changed = if (delta < 0) value.copyOf(value.size + delta) else value + ByteArray(delta) { 0x5A }
            fields[i] = tag to (if (r.nextInt(4) == 0) ByteArray(0) else changed)
            InvalidAnnouncement(assemble(fields), kind)
        }
        InvalidAnnouncement.TRUNCATED -> {
            val b = assemble(fields)
            InvalidAnnouncement(b.copyOf(r.nextInt(b.size)), kind)
        }
        else -> { // TAG_ORDER
            val i = r.nextInt(fields.size - 1)
            if (r.nextBoolean()) {
                val t = fields[i]
                fields[i] = fields[i + 1]
                fields[i + 1] = t
            } else {
                fields.add(i + 1, fields[i])
            }
            InvalidAnnouncement(assemble(fields), kind)
        }
    }
}
