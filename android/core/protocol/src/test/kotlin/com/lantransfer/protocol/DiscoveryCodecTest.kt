package com.lantransfer.protocol

import com.lantransfer.testing.cps
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.ints.shouldBeLessThanOrEqual
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf

/** 公告、二维码、前导的示例测试（属性测试见 Property 5、8、10–12）。 */
class DiscoveryCodecTest : FunSpec({
    val id = DeviceId(Bytes16.ofHex("00112233445566778899aabbccddeeff"))

    test("32 个表情组成的名称：公告往返且不超过 512 字节") {
        val a = Announcement(id, "😀".repeat(32), DeviceType.ANDROID, ProtocolVersion.V1_0, 42580)
        val bytes = AnnouncementCodec.encode(a)
        bytes.size shouldBeLessThanOrEqual 512
        Bytes.of(bytes.copyOf(4)).toHex() shouldBe "4c544441"
        AnnouncementCodec.decode(bytes) shouldBe a
    }

    test("非法公告被丢弃") {
        val good = AnnouncementCodec.encode(Announcement(id, "Pixel", DeviceType.WINDOWS, ProtocolVersion.V1_0, 1))
        AnnouncementCodec.decode(good.copyOf(good.size - 1)) shouldBe null // 截断
        AnnouncementCodec.decode(byteArrayOf(0x4C, 0x54, 0x44, 0x42) + good.copyOfRange(4, good.size)) shouldBe null // magic
        AnnouncementCodec.decode(good + ByteArray(600)) shouldBe null // 超过 512 字节
        // 端口为 0、设备类型 4、名称 33 个码点（设备类型 3 已预留给 iOS）
        fun raw(name: String, type: Int, port: Int): ByteArray {
            val w = TlvWriter()
            w.bytes16(1, id.value); w.string(2, name); w.u8(3, type); w.u8(4, 1); w.u8(5, 0); w.u16(6, port)
            return byteArrayOf(0x4C, 0x54, 0x44, 0x41) + w.toByteArray()
        }
        AnnouncementCodec.decode(raw("a", 1, 0)) shouldBe null
        AnnouncementCodec.decode(raw("a", 4, 1)) shouldBe null
        AnnouncementCodec.decode(raw("a", 3, 1))!!.deviceType shouldBe DeviceType.IOS
        AnnouncementCodec.decode(raw(cps(*IntArray(33) { 0x4E00 }), 1, 1)) shouldBe null
        AnnouncementCodec.decode(raw("", 1, 1)) shouldBe null
        AnnouncementCodec.decode(raw("a", 1, 1))!!.port shouldBe 1
    }

    test("4 个 IP、32 字符名称的二维码往返，字符集为 Base32") {
        val p = QrPayload(
            id, "名".repeat(32),
            listOf(0xC0A80114.toInt(), 0x0A000005, 0xAC100001.toInt(), 0xC0A82B01.toInt()),
            42580, Bytes.of(ByteArray(32) { it.toByte() }), Bytes16.ofHex("ffeeddccbbaa99887766554433221100"),
        )
        val text = QrCodec.encode(p)
        text.length shouldBeLessThanOrEqual 512
        QrCodec.decode(text) shouldBe QrDecodeResult.Ok(p)
    }

    test("Base32 规范编码与严格解码") {
        Base32.encode("foobar".encodeToByteArray()) shouldBe "MZXW6YTBOI"
        Base32.decode("MZXW6YTBOI")!!.decodeToString() shouldBe "foobar"
        Base32.encode(ByteArray(0)) shouldBe ""
        Base32.decode("MZXW6YTBOJ") shouldBe null // 末尾比特不为 0
        Base32.decode("MZXW6YTBO") shouldBe null // 长度对 8 取模为 1
        Base32.decode("mzxw6ytboi") shouldBe null
        Base32.decode("MZXW6YTBOI======") shouldBe null
    }

    test("二维码解析错误类别") {
        QrCodec.decode("LT2:AAAA").shouldBeInstanceOf<QrDecodeResult.Error>().kind shouldBe QrError.BAD_PREFIX
        QrCodec.decode("LT1:" + "A".repeat(600)).shouldBeInstanceOf<QrDecodeResult.Error>().kind shouldBe QrError.TOO_LONG
        QrCodec.decode("LT1:aaaa").shouldBeInstanceOf<QrDecodeResult.Error>().kind shouldBe QrError.BAD_BASE32
        QrCodec.decode("LT1:").shouldBeInstanceOf<QrDecodeResult.Error>().kind shouldBe QrError.MALFORMED
    }

    test("前导与版本协商") {
        Bytes.of(PreambleCodec.encode(ProtocolVersion.V1_0)).toHex() shouldBe "4c414e540100"
        PreambleCodec.decode(Bytes.ofHex("4c414e550100").toByteArray()) shouldBe null
        PreambleCodec.negotiate(ProtocolVersion(1, 2), ProtocolVersion(1, 0)) shouldBe Negotiation.Compatible(ProtocolVersion(1, 0))
        PreambleCodec.negotiate(ProtocolVersion(1, 0), ProtocolVersion(2, 0)).shouldBeInstanceOf<Negotiation.Incompatible>()
    }
})
