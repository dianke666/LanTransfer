package com.lantransfer.protocol

import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.announcement
import com.lantransfer.testing.bytes
import com.lantransfer.testing.bytes16
import com.lantransfer.testing.cps
import com.lantransfer.testing.protocolMessage
import com.lantransfer.testing.protocolVersion
import com.lantransfer.testing.qrPayload
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.ints.shouldBeLessThanOrEqual
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.choice
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll

/** 一个 TLV 字段。 */
private data class Field(val tag: Int, val value: ByteArray)

private fun splitFields(b: ByteArray, offset: Int): List<Field> {
    val out = ArrayList<Field>()
    var p = offset
    while (p < b.size) {
        val tag = b[p].toInt() and 0xFF
        val len = TlvStruct.readU32(b, p + 1).toInt()
        out += Field(tag, b.copyOfRange(p + 5, p + 5 + len))
        p += 5 + len
    }
    return out
}

private fun joinFields(fields: List<Field>): ByteArray {
    val w = TlvWriter()
    fields.sortedBy { it.tag }.forEach { w.bytes(it.tag, it.value) }
    return w.toByteArray()
}

/** 每种消息已定义的最大 tag（1..max 都是已定义字段；0 与 max 之后的 tag 都是未定义的）。 */
private val maxDefinedTag: Map<MessageType, Int> = mapOf(
    MessageType.HEARTBEAT to 0, MessageType.PAIR_COMPLETE to 0,
    MessageType.TEXT to 3, MessageType.FILE_OFFER to 2, MessageType.FILE_OFFER_REPLY to 2, MessageType.FILE_CHUNK to 4,
    MessageType.FILE_END to 3, MessageType.FILE_ACK to 3, MessageType.FILE_RESULT to 4, MessageType.FILE_READ_FAILED to 2,
    MessageType.TRANSFER_CANCEL to 2, MessageType.RESUME_STATE to 2, MessageType.RESUME_PLAN to 2,
).withDefault { 1 }

/** 待插入字段的原始候选：(tag 种子, 值)。具体 tag 在知道目标结构后由 [unknownTags] 换算。 */
private val extraFieldSeeds: Arb<List<Pair<Int, ByteArray>>> =
    Arb.list(Arb.bind(Arb.int(0..255), Gens.bytes(12)) { t, v -> t to v.toByteArray() }, 0..6)

/** 把种子换算为未定义的 tag：0 或大于 [maxTag] 的值（超出 255 的丢弃），并去重。 */
private fun unknownTags(seeds: List<Pair<Int, ByteArray>>, maxTag: Int): List<Field> =
    seeds.mapNotNull { (t, v) ->
        val tag = if (t == 0 || t > maxTag) t else maxTag + t
        if (tag > 255) null else Field(tag, v)
    }.distinctBy { it.tag }

private fun withUnknown(fields: List<Field>, extra: List<Field>): List<Field> =
    (fields + extra.filter { e -> fields.none { it.tag == e.tag } }).sortedBy { it.tag }

/** 非法二维码文本：在合法文本上做各种破坏，或直接构造字段越界的 Struct。 */
private val invalidQrText: Arb<String> = Arb.choice(
    Arb.bind(Gens.qrPayload(), Arb.int(0..1000)) { p, k ->
        val t = QrCodec.encode(p)
        val body = t.substring(QrCodec.PREFIX.length)
        // 把某个字母改为小写（数字 2–7 没有小写形式，跳过）
        val i = ((k % body.length) until body.length).firstOrNull { body[it] in 'A'..'Z' } ?: body.indexOfFirst { it in 'A'..'Z' }
        if (i < 0) "lt1:" else QrCodec.PREFIX + body.substring(0, i) + body[i].lowercaseChar() + body.substring(i + 1)
    },
    Arb.bind(Gens.qrPayload(), Arb.element("=", "0", "1", "8", "9", "-", " ", "\u00C4")) { p, bad ->
        QrCodec.encode(p) + bad // 非法字符
    },
    Arb.bind(Gens.qrPayload(), Arb.element("LT2:", "lt1:", "LT1", "", "LT:")) { p, prefix ->
        prefix + QrCodec.encode(p).substring(QrCodec.PREFIX.length) // 前缀错误
    },
    Gens.qrPayload().map { p ->
        // 长度对 8 取模为 1、3、6
        val body = QrCodec.encode(p).substring(QrCodec.PREFIX.length)
        val target = generateSequence(body.length) { it - 1 }.first { it % 8 in setOf(1, 3, 6) }
        QrCodec.PREFIX + body.substring(0, target)
    },
    Gens.qrPayload().map { p ->
        // 末尾多余比特不为 0：把最后一个字符改为同一字节值下非零的填充比特
        val body = QrCodec.encode(p).substring(QrCodec.PREFIX.length)
        // 长度对 8 取模为 2、4、5、7 时，最后一个字符的低位是填充比特；为 0 时没有填充比特，改用空的 Base32 部分
        val alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
        val last = alphabet.indexOf(body.last())
        if (body.length % 8 == 0) QrCodec.PREFIX else QrCodec.PREFIX + body.dropLast(1) + alphabet[last or 1]
    },
    Arb.int(513..600).map { n -> QrCodec.PREFIX + "A".repeat(n - QrCodec.PREFIX.length) }, // 超过 512 字节
    Arb.bind(Gens.bytes16(), Arb.int(0..5), Arb.int(0..2), Arb.int(0..2)) { token, ipCount, portKind, nameKind ->
        // 字段约束不满足：IP 数为 0 或 5、IP 重复、端口为 0、名称 0 或 33 个码点
        val w = TlvWriter()
        w.bytes16(1, token)
        w.string(2, when (nameKind) { 0 -> ""; 1 -> cps(*IntArray(33) { 0x4E00 }); else -> "ok" })
        val ips = when {
            ipCount == 0 || ipCount == 5 -> ByteArray(4 * ipCount) { (it / 4 + 1).toByte() }
            else -> ByteArray(4 * (ipCount + 1)) { 7 } // 有重复
        }
        w.bytes(3, if (portKind == 2) ips.copyOf(ips.size + 1) else ips)
        w.u16(4, if (portKind == 0) 0 else 42580)
        w.bytes(5, ByteArray(32))
        w.bytes16(6, token)
        QrCodec.PREFIX + Base32.encode(w.toByteArray())
    },
)

class DiscoveryPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 5: 未知字段被忽略
    // Validates: Requirements 22.7, 23.9, 23.10
    test("Property 5") {
        checkAll(Pbt.iterations(1_000), Gens.protocolMessage(), Gens.announcement(), Gens.qrPayload(), extraFieldSeeds, Arb.int(0..2)) { m, a, q, seeds, which ->
            when (which) {
                0 -> {
                    val frame = FrameEncoder.encodeOrThrow(m)
                    val fields = splitFields(frame, ProtocolConstants.FRAME_HEADER_SIZE)
                    val extra = unknownTags(seeds, maxDefinedTag.getValue(m.type))
                    val payload = joinFields(withUnknown(fields, extra))
                    val mutated = frame.copyOf(ProtocolConstants.FRAME_HEADER_SIZE).also {
                        it[3] = (payload.size ushr 24).toByte(); it[4] = (payload.size ushr 16).toByte()
                        it[5] = (payload.size ushr 8).toByte(); it[6] = payload.size.toByte()
                    } + payload
                    val batch = FrameDecoder().feed(mutated)
                    batch.error shouldBe null
                    batch.messages shouldBe listOf(m)
                }
                1 -> {
                    val bytes = AnnouncementCodec.encode(a)
                    val extra = unknownTags(seeds, 6)
                    val mutated = bytes.copyOf(4) + joinFields(withUnknown(splitFields(bytes, 4), extra))
                    if (mutated.size <= ProtocolConstants.MAX_ANNOUNCEMENT) AnnouncementCodec.decode(mutated) shouldBe a
                }
                else -> {
                    val raw = Base32.decode(QrCodec.encode(q).substring(QrCodec.PREFIX.length))!!
                    val extra = unknownTags(seeds, 6)
                    val text = QrCodec.PREFIX + Base32.encode(joinFields(withUnknown(splitFields(raw, 0), extra)))
                    if (text.length <= ProtocolConstants.MAX_QR_TEXT) QrCodec.decode(text) shouldBe QrDecodeResult.Ok(q)
                }
            }
        }
    }

    // Feature: lan-file-transfer, Property 8: 发现公告往返与长度上限
    // Validates: Requirements 5.6, 23.1, 23.3
    test("Property 8") {
        checkAll(Pbt.iterations(1_000), Gens.announcement()) { a ->
            val bytes = AnnouncementCodec.encode(a)
            bytes.size shouldBeLessThanOrEqual ProtocolConstants.MAX_ANNOUNCEMENT
            AnnouncementCodec.decode(bytes) shouldBe a
            // 公告只含规范规定的 6 个字段
            splitFields(bytes, 4).map { it.tag } shouldBe listOf(1, 2, 3, 4, 5, 6)
        }
    }

    // Feature: lan-file-transfer, Property 10: 二维码载荷往返
    // Validates: Requirements 8.3, 23.4, 23.5, 23.7
    test("Property 10") {
        checkAll(Pbt.iterations(1_000), Gens.qrPayload()) { p ->
            val text = QrCodec.encode(p)
            text.length shouldBeLessThanOrEqual ProtocolConstants.MAX_QR_TEXT
            text.startsWith(QrCodec.PREFIX) shouldBe true
            text.substring(QrCodec.PREFIX.length).all { it in 'A'..'Z' || it in '2'..'7' } shouldBe true
            QrCodec.decode(text) shouldBe QrDecodeResult.Ok(p)
        }
    }

    // Feature: lan-file-transfer, Property 11: 非法二维码文本被整体拒绝
    // Validates: Requirements 8.8, 23.6
    test("Property 11") {
        checkAll(Pbt.iterations(1_000), invalidQrText) { text ->
            QrCodec.decode(text).shouldBeInstanceOf<QrDecodeResult.Error>()
        }
    }

    // Feature: lan-file-transfer, Property 12: 前导往返与版本协商
    // Validates: Requirements 24.4, 24.5, 24.6
    test("Property 12") {
        checkAll(Pbt.iterations(1_000), Gens.protocolVersion(), Gens.protocolVersion(), Gens.bytes(8)) { a, b, junk ->
            val bytes = PreambleCodec.encode(a)
            bytes.size shouldBe PreambleCodec.SIZE
            PreambleCodec.decode(bytes) shouldBe a
            val n = PreambleCodec.negotiate(a, b)
            n shouldBe PreambleCodec.negotiate(b, a).let { if (it is Negotiation.Incompatible) Negotiation.Incompatible(a, b) else it }
            if (a.major == b.major) {
                n shouldBe Negotiation.Compatible(ProtocolVersion(a.major, minOf(a.minor, b.minor)))
            } else {
                n shouldBe Negotiation.Incompatible(a, b)
            }
            val j = junk.toByteArray()
            val expected = if (j.size == 6 && j[0] == 0x4C.toByte() && j[1] == 0x41.toByte() && j[2] == 0x4E.toByte() && j[3] == 0x54.toByte()) {
                ProtocolVersion(j[4].toInt() and 0xFF, j[5].toInt() and 0xFF)
            } else {
                null
            }
            PreambleCodec.decode(j) shouldBe expected
        }
    }
})
