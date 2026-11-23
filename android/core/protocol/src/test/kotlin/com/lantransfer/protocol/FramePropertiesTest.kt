package com.lantransfer.protocol

import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.bytes
import com.lantransfer.testing.protocolMessage
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.ints.shouldBeLessThanOrEqual
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.byte
import io.kotest.property.arbitrary.byteArray
import io.kotest.property.arbitrary.choice
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.constant
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll

private fun encode(m: ProtocolMessage, v: ProtocolVersion = ProtocolVersion.CURRENT) = FrameEncoder.encodeOrThrow(m, v)

/** 切分方式：整段、逐字节，或随机长度（1–64 字节）循环使用。 */
private val splitArb: Arb<List<Int>> = Arb.choose(
    1 to Arb.constant(listOf(Int.MAX_VALUE)),
    1 to Arb.constant(listOf(1)),
    4 to Arb.list(Arb.int(1..64), 1..8),
)

/** 按 [split] 切分 [bytes] 依次喂给 [d]；每次喂入后检查缓冲上界，返回全部批次。 */
private fun feedSplit(d: FrameDecoder, bytes: ByteArray, split: List<Int>): List<DecodeBatch> {
    val batches = ArrayList<DecodeBatch>()
    var pos = 0
    var k = 0
    while (pos < bytes.size) {
        val n = minOf(split[k % split.size], bytes.size - pos)
        batches += d.feed(bytes, pos, n)
        d.bufferedBytes shouldBeLessThanOrEqual ProtocolConstants.FRAME_HEADER_SIZE + ProtocolConstants.MAX_PAYLOAD
        pos += n
        k++
    }
    return batches
}

private val versionArb: Arb<ProtocolVersion> = Arb.int(0..3).map { ProtocolVersion(1, it) }

/** 非法帧：前三类只有帧头（错误应在帧头到齐时返回），载荷格式错误类带完整载荷。 */
private class BadFrame(val bytes: ByteArray, val expected: (DecodeError) -> Boolean, val headerOnly: Boolean) {
    override fun toString() = "BadFrame(${Bytes.of(bytes).toHex()})"
}

private fun header(magic: Int, type: Int, length: Long) = byteArrayOf(
    (magic ushr 8).toByte(), magic.toByte(), type.toByte(),
    (length ushr 24).toByte(), (length ushr 16).toByte(), (length ushr 8).toByte(), length.toByte(),
)

private val definedCodes = MessageType.entries.map { it.code }.toSet()

/** 有必需字段的类型：空载荷、截断的字段头或越界字段都是 MalformedPayload。 */
private val typesWithFields = MessageType.entries.filter { it != MessageType.HEARTBEAT && it != MessageType.PAIR_COMPLETE }

private val badFrameArb: Arb<BadFrame> = Arb.choice(
    Arb.bind(Arb.int(0..0xFFFF), Arb.element(MessageType.entries), Arb.long(0L..100L)) { magic, t, len ->
        val m = if (magic == ProtocolConstants.FRAME_MAGIC) magic xor 1 else magic
        BadFrame(header(m, t.code, len), { it == DecodeError.BadMagic }, true)
    },
    Arb.bind(Arb.int(0..255), Arb.long(0L..100L)) { code, len ->
        val c = if (code in definedCodes) 0xFF else code
        BadFrame(header(ProtocolConstants.FRAME_MAGIC, c, len), { it == DecodeError.UnknownType(c) }, true)
    },
    Arb.bind(Arb.element(MessageType.entries), Arb.long((ProtocolConstants.MAX_PAYLOAD + 1L)..0xFFFF_FFFFL)) { t, len ->
        BadFrame(header(ProtocolConstants.FRAME_MAGIC, t.code, len), { it == DecodeError.PayloadTooLarge(len) }, true)
    },
    Arb.bind(Arb.element(typesWithFields), Arb.int(0..2), Arb.byteArray(Arb.int(1..4), Arb.byte())) { t, kind, junk ->
        val payload = when (kind) {
            0 -> ByteArray(0) // 缺少必需字段
            1 -> junk // 截断的字段头
            else -> byteArrayOf(1, 0, 0, 0, 9) // 字段长度越界
        }
        BadFrame(header(ProtocolConstants.FRAME_MAGIC, t.code, payload.size.toLong()) + payload, {
            it is DecodeError.MalformedPayload && it.type == t.code
        }, false)
    },
)

/** 参考校验器：按 Unicode 标准表 3-7 逐条判断（与被测实现分开编写）。 */
private fun referenceWellFormed(b: ByteArray): Boolean {
    fun u(i: Int) = b[i].toInt() and 0xFF
    fun inRange(i: Int, lo: Int, hi: Int) = i < b.size && u(i) in lo..hi
    var i = 0
    while (i < b.size) {
        val c = u(i)
        val n = when {
            c <= 0x7F -> 1
            c in 0xC2..0xDF && inRange(i + 1, 0x80, 0xBF) -> 2
            c == 0xE0 && inRange(i + 1, 0xA0, 0xBF) && inRange(i + 2, 0x80, 0xBF) -> 3
            (c in 0xE1..0xEC || c in 0xEE..0xEF) && inRange(i + 1, 0x80, 0xBF) && inRange(i + 2, 0x80, 0xBF) -> 3
            c == 0xED && inRange(i + 1, 0x80, 0x9F) && inRange(i + 2, 0x80, 0xBF) -> 3
            c == 0xF0 && inRange(i + 1, 0x90, 0xBF) && inRange(i + 2, 0x80, 0xBF) && inRange(i + 3, 0x80, 0xBF) -> 4
            c in 0xF1..0xF3 && inRange(i + 1, 0x80, 0xBF) && inRange(i + 2, 0x80, 0xBF) && inRange(i + 3, 0x80, 0xBF) -> 4
            c == 0xF4 && inRange(i + 1, 0x80, 0x8F) && inRange(i + 2, 0x80, 0xBF) && inRange(i + 3, 0x80, 0xBF) -> 4
            else -> return false
        }
        i += n
    }
    return true
}

/** Property 6 用的文本：偏向各字符类的短文本，偶尔为接近或恰好 1,048,576 字节的长文本。 */
private val textForRoundTrip: Arb<String> = Arb.choose(
    20 to Gens.unicodeText(),
    1 to Arb.bind(Gens.unicodeText(4), Arb.int(1..2000)) { s, n -> s.repeat(n) },
    1 to Arb.element("a".repeat(ProtocolConstants.MAX_TEXT_BYTES), "\u4E2D".repeat(ProtocolConstants.MAX_TEXT_BYTES / 3) + "a"),
)

class FramePropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 1: Frame 编解码往返与规范编码
    // Validates: Requirements 22.1, 22.2, 22.8, 22.9, 14.5
    test("Property 1") {
        checkAll(Pbt.iterations(1_000), Gens.protocolMessage(), versionArb) { m, v ->
            val bytes = encode(m, v)
            val length = TlvStruct.readU32(bytes, 3)
            length shouldBe (bytes.size - ProtocolConstants.FRAME_HEADER_SIZE).toLong()
            val batch = FrameDecoder(v).feed(bytes)
            batch.error shouldBe null
            batch.messages shouldBe listOf(m)
            Bytes.of(encode(batch.messages.single(), v)) shouldBe Bytes.of(bytes)
        }
    }

    // Feature: lan-file-transfer, Property 2: 流切分不变与缓冲上界
    // Validates: Requirements 22.3, 22.4, 22.10, 22.13
    test("Property 2") {
        checkAll(Pbt.iterations(1_000), Arb.list(Gens.protocolMessage(), 0..8), splitArb, Arb.int(0..Int.MAX_VALUE)) { ms, split, cutSeed ->
            val stream = ms.fold(ByteArray(0)) { acc, m -> acc + encode(m) }
            val d = FrameDecoder()
            val got = feedSplit(d, stream, split).flatMap { it.messages }
            got shouldBe ms
            d.bufferedBytes shouldBe 0

            // 在任意位置截断后 close：只输出完整到达的帧，残留字节不构成消息
            if (stream.isNotEmpty()) {
                val cut = cutSeed % stream.size
                val d2 = FrameDecoder()
                val before = feedSplit(d2, stream.copyOf(cut), split).flatMap { it.messages }
                ms.take(before.size) shouldBe before
                d2.close()
                d2.bufferedBytes shouldBe 0
                d2.feed(stream, cut, stream.size - cut).messages.shouldBeEmpty()
            }
        }
    }

    // Feature: lan-file-transfer, Property 3: 解码错误定位
    // Validates: Requirements 22.5, 22.6
    test("Property 3") {
        checkAll(Pbt.iterations(1_000), Arb.list(Gens.protocolMessage(), 0..5), badFrameArb, splitArb, Gens.bytes(16)) { ms, bad, split, tail ->
            val prefix = ms.fold(ByteArray(0)) { acc, m -> acc + encode(m) }
            val stream = prefix + bad.bytes + tail.toByteArray()
            val d = FrameDecoder()
            val batches = feedSplit(d, stream, split)
            batches.flatMap { it.messages } shouldBe ms
            val errors = batches.mapNotNull { it.error }
            errors.size shouldBe 1
            bad.expected(errors.single()) shouldBe true
            d.feed(encode(Heartbeat)).messages.shouldBeEmpty()

            // 前三类错误在帧头到齐时立即返回，不需要任何载荷字节
            if (bad.headerOnly) {
                val d2 = FrameDecoder()
                val b = d2.feed(prefix + bad.bytes.copyOf(ProtocolConstants.FRAME_HEADER_SIZE))
                b.messages shouldBe ms
                bad.expected(b.error!!) shouldBe true
            }
        }
    }

    // Feature: lan-file-transfer, Property 4: 编码超限
    // Validates: Requirements 22.11, 22.12
    test("Property 4") {
        // 用较小的上限覆盖“超限 ⇔ 返回 PayloadTooLarge 且不输出字节”的判定
        checkAll(Pbt.iterations(1_000), Gens.protocolMessage(), Arb.int(0..2_048)) { m, limit ->
            val payloadSize = (FrameEncoder.encodeOrThrow(m).size - ProtocolConstants.FRAME_HEADER_SIZE)
            when (val r = FrameEncoder.encode(m, ProtocolVersion.CURRENT, limit)) {
                is EncodeResult.Ok -> {
                    (payloadSize <= limit) shouldBe true
                    r.bytes.size shouldBe payloadSize + ProtocolConstants.FRAME_HEADER_SIZE
                }
                is EncodeResult.PayloadTooLarge -> {
                    (payloadSize > limit) shouldBe true
                    r.length shouldBe payloadSize.toLong()
                }
            }
        }
        // 真实上限：1,048,576 字节的 TEXT 编码成功；载荷恰好超过 MAX_PAYLOAD 的 TEXT 被拒绝
        val id = MessageId(Bytes16.of(ByteArray(16)))
        FrameEncoder.encode(Text(id, 0, Bytes.of(ByteArray(ProtocolConstants.MAX_TEXT_BYTES)))).shouldBeInstanceOf<EncodeResult.Ok>()
        val overhead = MessageCodec.encodePayload(Text(id, 0, Bytes.EMPTY)).size
        FrameEncoder.encode(Text(id, 0, Bytes.of(ByteArray(ProtocolConstants.MAX_PAYLOAD - overhead)))).shouldBeInstanceOf<EncodeResult.Ok>()
        FrameEncoder.encode(Text(id, 0, Bytes.of(ByteArray(ProtocolConstants.MAX_PAYLOAD - overhead + 1))))
            .shouldBeInstanceOf<EncodeResult.PayloadTooLarge>()
    }

    // Feature: lan-file-transfer, Property 6: 文字无损往返
    // Validates: Requirements 12.1, 12.2, 12.3, 12.4, 12.5, 12.9
    test("Property 6") {
        val id = MessageId(Bytes16.ofHex("0102030405060708090a0b0c0d0e0f10"))
        checkAll(Pbt.iterations(1_000), textForRoundTrip) { s ->
            val utf8 = StrictUtf8.encode(s)
            Bytes.of(utf8) shouldBe Bytes.of(s.toByteArray(Charsets.UTF_8))
            val frame = encode(Text(id, 1_735_693_805_000L, Bytes.of(utf8)))
            val text = FrameDecoder().feed(frame).messages.single() as Text
            StrictUtf8.decode(text.text.toByteArray()) shouldBe s
        }
    }

    // Feature: lan-file-transfer, Property 7: 严格 UTF‑8 解码
    // Validates: Requirements 12.7
    test("Property 7") {
        val input: Arb<ByteArray> = Arb.choose(
            3 to Gens.invalidUtf8().map { it.bytes },
            2 to Gens.unicodeText().map { StrictUtf8.encode(it) },
            2 to Arb.byteArray(Arb.int(0..12), Arb.byte()),
            1 to Arb.byteArray(Arb.int(1..4), Arb.element((0x80..0xFF).map { it.toByte() })),
        )
        checkAll(Pbt.iterations(1_000), input) { b ->
            val decoded = StrictUtf8.decode(b)
            (decoded != null) shouldBe referenceWellFormed(b)
            if (decoded != null) Bytes.of(StrictUtf8.encode(decoded)) shouldBe Bytes.of(b)
        }
    }
})
