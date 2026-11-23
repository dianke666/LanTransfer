package com.lantransfer.protocol

import com.lantransfer.testing.Gens
import com.lantransfer.testing.cps
import com.lantransfer.testing.protocolMessage
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainExactly
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.RandomSource
import io.kotest.property.arbitrary.take

/** Frame / TLV / StrictUtf8 的示例测试（属性测试见 Property 1–4、6、7）。 */
class FrameCodecTest : FunSpec({
    fun encode(m: ProtocolMessage) = FrameEncoder.encodeOrThrow(m)

    test("HEARTBEAT 编码为 7 字节帧头、载荷为空") {
        Bytes.of(encode(Heartbeat)).toHex() shouldBe "4c540300000000"
    }

    test("SESSION_RESULT 的字节布局") {
        Bytes.of(encode(SessionResult(SessionResultCode.DUPLICATE))).toHex() shouldBe "4c5401000000060100000001" + "03"
    }

    test("生成器覆盖全部消息类型，且每种消息编码后再解码不变") {
        val rs = RandomSource.seeded(1L)
        val messages = Gens.protocolMessage().take(3_000, rs).toList()
        messages.map { it.type }.toSet() shouldBe MessageType.entries.toSet()
        messages.forEach { m ->
            val d = FrameDecoder()
            val batch = d.feed(encode(m))
            batch.error shouldBe null
            batch.messages shouldContainExactly listOf(m)
            d.bufferedBytes shouldBe 0
        }
    }

    test("帧头到齐即报错，不等待载荷") {
        FrameDecoder().feed(Bytes.ofHex("4c5501000000").toByteArray()).error shouldBe null
        FrameDecoder().feed(Bytes.ofHex("4c550100000001").toByteArray()).error shouldBe DecodeError.BadMagic
        FrameDecoder().feed(Bytes.ofHex("4c54ff00000000").toByteArray()).error shouldBe DecodeError.UnknownType(0xFF)
        FrameDecoder().feed(Bytes.ofHex("4c540300200001").toByteArray()).error shouldBe DecodeError.PayloadTooLarge(0x200001)
    }

    test("出错前的完整消息先交付，出错后不再接受输入") {
        val d = FrameDecoder()
        val batch = d.feed(encode(Heartbeat) + Bytes.ofHex("4c54010000000101").toByteArray())
        batch.messages shouldContainExactly listOf(Heartbeat)
        batch.error.shouldBeInstanceOf<DecodeError.MalformedPayload>()
        d.feed(encode(Heartbeat)).messages shouldBe emptyList()
    }

    test("未知 tag 被跳过，tag 不递增为 MalformedPayload") {
        // SESSION_CLOSE{1: NORMAL, 9: 未知}
        val withUnknown = Bytes.ofHex("4c54020000000c" + "010000000100" + "090000000107").toByteArray()
        FrameDecoder().feed(withUnknown).messages shouldContainExactly listOf(SessionClose(SessionCloseReason.NORMAL))
        val decreasing = Bytes.ofHex("4c54020000000c" + "090000000107" + "010000000100").toByteArray()
        FrameDecoder().feed(decreasing).error.shouldBeInstanceOf<DecodeError.MalformedPayload>()
    }

    test("未定义的枚举值与定长字段长度错误为 MalformedPayload") {
        FrameDecoder().feed(Bytes.ofHex("4c54010000000601000000010a").toByteArray()).error.shouldBeInstanceOf<DecodeError.MalformedPayload>()
        FrameDecoder().feed(Bytes.ofHex("4c54010000000701000000020000").toByteArray()).error.shouldBeInstanceOf<DecodeError.MalformedPayload>()
    }

    test("逐字节喂入与一次喂入结果相同，close 丢弃残留") {
        val frame = encode(TextAck(MessageId(Bytes16.ofHex("00112233445566778899aabbccddeeff"))))
        val d = FrameDecoder()
        val got = frame.indices.flatMap { d.feed(frame, it, 1).messages }
        got shouldBe FrameDecoder().feed(frame).messages
        d.feed(frame, 0, 10)
        d.bufferedBytes shouldBe 10
        d.close()
        d.bufferedBytes shouldBe 0
        d.feed(frame).messages shouldBe emptyList()
    }

    test("编码超限时不输出字节") {
        val big = Text(MessageId(Bytes16.of(ByteArray(16))), 0, Bytes.of(ByteArray(ProtocolConstants.MAX_PAYLOAD)))
        FrameEncoder.encode(big).shouldBeInstanceOf<EncodeResult.PayloadTooLarge>()
    }

    test("StrictUtf8 往返与非法序列") {
        val s = "a\u0000\r\n" + cps(0x1F600, 0x10FFFF, 0xFFFE) + "中"
        StrictUtf8.decode(StrictUtf8.encode(s)) shouldBe s
        StrictUtf8.encode(cps(0x10000)).let { Bytes.of(it).toHex() } shouldBe "f0908080"
        listOf("c080", "e08080", "eda080", "f4908080", "f5808080", "80", "e282", "ff").forEach {
            StrictUtf8.decode(Bytes.ofHex(it).toByteArray()) shouldBe null
        }
        StrictUtf8.hasUnpairedSurrogate("\uD800") shouldBe true
    }
})
