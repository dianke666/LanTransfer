package com.lantransfer.protocol

import com.lantransfer.testing.InvalidUtf8
import com.lantransfer.testing.TestVectors
import com.lantransfer.testing.TextCharClass
import com.lantransfer.testing.VectorJson
import com.lantransfer.testing.array
import com.lantransfer.testing.string
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainAll
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

private fun hex(b: ByteArray) = Bytes.of(b).toHex()

private fun JsonObject.hexBytes(key: String) = Bytes.ofHex(string(key)).toByteArray()

private fun errorName(e: DecodeError) = when (e) {
    DecodeError.BadMagic -> "BAD_MAGIC"
    is DecodeError.UnknownType -> "UNKNOWN_TYPE"
    is DecodeError.PayloadTooLarge -> "PAYLOAD_TOO_LARGE"
    is DecodeError.MalformedPayload -> "MALFORMED_PAYLOAD"
}

/** 跨端一致性（任务 6.2）：Kotlin 实现逐条满足 protocol/test-vectors 中的期望，并检查向量覆盖（需求 24.2、24.3、24.7、24.8）。 */
class TestVectorConformanceTest : FunSpec({
    test("manifest 列出全部向量文件") {
        TestVectors.files() shouldContainAll listOf(
            "frames.json", "frames-invalid.json", "text-messages.json", "utf8-invalid.json", "announcements.json",
            "qr-payloads.json", "preamble.json", "noise-xx.json", "verification-code.json",
        )
    }

    test("frames.json：encode(model) == bytes（或 canonical_bytes），decode(bytes) == model；覆盖每种消息类型") {
        val vectors = TestVectors.vectors("frames.json")
        vectors.forEach { v ->
            val type = MessageType.valueOf(v.string("type"))
            val model = VectorJson.message(type, v.getValue("model").jsonObject)
            val bytes = v.hexBytes("bytes")
            val canonical = if (v.containsKey("canonical_bytes")) v.hexBytes("canonical_bytes") else bytes
            hex(FrameEncoder.encodeOrThrow(model)) shouldBe hex(canonical)
            val batch = FrameDecoder().feed(bytes)
            batch.error shouldBe null
            batch.messages shouldBe listOf(model)
        }
        vectors.map { MessageType.valueOf(it.string("type")) }.toSet() shouldBe MessageType.entries.toSet()
        vectors.any { it.containsKey("canonical_bytes") } shouldBe true
    }

    test("frames-invalid.json：先交付错误之前的消息，再返回期望的错误类型；覆盖每种 DecodeError") {
        val vectors = TestVectors.vectors("frames-invalid.json")
        vectors.forEach { v ->
            val batch = FrameDecoder(VectorJson.parseVersion(v.string("version"))).feed(v.hexBytes("bytes"))
            batch.messages.size shouldBe v.getValue("messages_before_error").jsonPrimitive.int
            errorName(batch.error!!) shouldBe v.string("error")
        }
        vectors.map { it.string("error") }.toSet() shouldBe setOf("BAD_MAGIC", "UNKNOWN_TYPE", "PAYLOAD_TOO_LARGE", "MALFORMED_PAYLOAD")
    }

    test("text-messages.json：逐码点无损往返；覆盖每个字符类") {
        val doc = TestVectors.load("text-messages.json")
        val id = MessageId(Bytes16.ofHex(doc.string("message_id")))
        val sentAt = doc.string("sent_at").toLong()
        val vectors = TestVectors.vectors("text-messages.json")
        vectors.forEach { v ->
            val utf8 = v.hexBytes("text_utf8")
            val s = StrictUtf8.decode(utf8)!!
            hex(StrictUtf8.encode(s)) shouldBe hex(utf8)
            hex(FrameEncoder.encodeOrThrow(Text(id, sentAt, Bytes.of(utf8)))) shouldBe v.string("frame")
            val got = FrameDecoder().feed(v.hexBytes("frame")).messages.single() as Text
            StrictUtf8.decode(got.text.toByteArray()) shouldBe s
        }
        val labels = vectors.flatMap { v -> v.array("class").map { it.jsonPrimitive.content } }.toSet()
        labels shouldContainAll TextCharClass.labels
    }

    test("utf8-invalid.json：能作为 TEXT 解码，但严格 UTF-8 解码失败；覆盖每种非法类别") {
        val vectors = TestVectors.vectors("utf8-invalid.json")
        vectors.forEach { v ->
            StrictUtf8.decode(v.hexBytes("text_utf8")) shouldBe null
            val t = FrameDecoder().feed(v.hexBytes("frame")).messages.single().shouldBeInstanceOf<Text>()
            hex(t.text.toByteArray()) shouldBe v.string("text_utf8")
            v.string("expected") shouldBe "TEXT_CORRUPT"
        }
        vectors.map { it.string("kind") }.toSet() shouldBe InvalidUtf8.kinds.toSet()
    }

    test("announcements.json：有效样例往返，非法样例丢弃；含 32 个码点、中文与表情的名称") {
        val vectors = TestVectors.vectors("announcements.json")
        vectors.forEach { v ->
            if (v.containsKey("error")) {
                AnnouncementCodec.decode(v.hexBytes("bytes")) shouldBe null
            } else {
                val a = VectorJson.announcement(v.getValue("model").jsonObject)
                hex(AnnouncementCodec.encode(a)) shouldBe v.string("bytes")
                AnnouncementCodec.decode(v.hexBytes("bytes")) shouldBe a
            }
        }
        vectors.filter { !it.containsKey("error") }.map { VectorJson.announcement(it.getValue("model").jsonObject).displayName }.any { n ->
            n.codePointCount(0, n.length) == 32 && n.any { it in '\u4E00'..'\u9FFF' } && n.codePoints().anyMatch { it >= 0x1F000 }
        } shouldBe true
    }

    test("qr-payloads.json：有效样例往返，非法样例整体失败；含 ≥ 2 个 IP 与 32 个码点名称的样例") {
        val vectors = TestVectors.vectors("qr-payloads.json")
        vectors.forEach { v ->
            if (v.containsKey("error")) {
                QrCodec.decode(v.string("text")).shouldBeInstanceOf<QrDecodeResult.Error>()
            } else {
                val p = VectorJson.qr(v.getValue("model").jsonObject)
                QrCodec.encode(p) shouldBe v.string("text")
                QrCodec.decode(v.string("text")) shouldBe QrDecodeResult.Ok(p)
            }
        }
        val valid = vectors.filter { !it.containsKey("error") }.map { VectorJson.qr(it.getValue("model").jsonObject) }
        valid.any { it.ipv4List.size >= 2 } shouldBe true
        valid.any { it.displayName.codePointCount(0, it.displayName.length) == 32 } shouldBe true
    }

    test("preamble.json：编码、解码与协商") {
        TestVectors.vectors("preamble.json").forEach { v ->
            when {
                v.containsKey("negotiated") -> {
                    val n = PreambleCodec.negotiate(VectorJson.parseVersion(v.string("local")), VectorJson.parseVersion(v.string("remote")))
                    val expected = v.string("negotiated")
                    if (expected == "INCOMPATIBLE") n.shouldBeInstanceOf<Negotiation.Incompatible>()
                    else n shouldBe Negotiation.Compatible(VectorJson.parseVersion(expected))
                }
                v.containsKey("error") -> PreambleCodec.decode(v.hexBytes("bytes")) shouldBe null
                else -> {
                    val ver = VectorJson.parseVersion(v.string("version"))
                    hex(PreambleCodec.encode(ver)) shouldBe v.string("bytes")
                    PreambleCodec.decode(v.hexBytes("bytes")) shouldBe ver
                }
            }
        }
    }

    test("noise-xx.json：官方向量与本协议转录逐字节一致") {
        val doc = TestVectors.load("noise-xx.json")
        doc.array("official").map { it.jsonObject }.forEach { v ->
            val i = NoiseXX(Role.INITIATOR, X25519KeyPair.fromPrivateKey(v.hexBytes("init_static")), v.hexBytes("init_prologue"), EphemeralKeySource.fixed(v.hexBytes("init_ephemeral")))
            val r = NoiseXX(Role.RESPONDER, X25519KeyPair.fromPrivateKey(v.hexBytes("resp_static")), v.hexBytes("resp_prologue"), EphemeralKeySource.fixed(v.hexBytes("resp_ephemeral")))
            val msgs = v.array("messages").map { it.jsonObject }
            hex(i.writeMessage(msgs[0].hexBytes("payload"))) shouldBe msgs[0].string("ciphertext")
            hex(r.readMessage(msgs[0].hexBytes("ciphertext"))) shouldBe msgs[0].string("payload")
            hex(r.writeMessage(msgs[1].hexBytes("payload"))) shouldBe msgs[1].string("ciphertext")
            hex(i.readMessage(msgs[1].hexBytes("ciphertext"))) shouldBe msgs[1].string("payload")
            hex(i.writeMessage(msgs[2].hexBytes("payload"))) shouldBe msgs[2].string("ciphertext")
            hex(r.readMessage(msgs[2].hexBytes("ciphertext"))) shouldBe msgs[2].string("payload")
            hex(i.handshakeHash) shouldBe v.string("handshake_hash")
            val (iSend, iRecv) = i.split()
            val (rSend, rRecv) = r.split()
            val iIn = iRecv.openStream()
            val rIn = rRecv.openStream()
            msgs.drop(3).forEachIndexed { k, m ->
                val record = if (k % 2 == 0) rSend.seal(m.hexBytes("payload")) else iSend.seal(m.hexBytes("payload"))
                hex(record.copyOfRange(2, record.size)) shouldBe m.string("ciphertext")
                val got = (if (k % 2 == 0) iIn else rIn).feed(record) as RecordFeedResult.Plain
                got.chunks.single().toHex() shouldBe m.string("payload")
            }
        }
        doc.array("transcripts").map { it.jsonObject }.forEach { v ->
            val prologue = NoiseXX.prologue(v.hexBytes("preamble_i"), v.hexBytes("preamble_r"))
            hex(prologue) shouldBe v.string("prologue")
            val i = NoiseXX(Role.INITIATOR, X25519KeyPair.fromPrivateKey(v.hexBytes("init_static")), prologue, EphemeralKeySource.fixed(v.hexBytes("init_ephemeral")))
            val r = NoiseXX(Role.RESPONDER, X25519KeyPair.fromPrivateKey(v.hexBytes("resp_static")), prologue, EphemeralKeySource.fixed(v.hexBytes("resp_ephemeral")))
            X25519KeyPair.fromPrivateKey(v.hexBytes("init_static")).publicKey.toHex() shouldBe v.string("init_static_public")
            val m1 = v.getValue("msg1").jsonObject
            val m2 = v.getValue("msg2").jsonObject
            val m3 = v.getValue("msg3").jsonObject
            hex(i.writeMessage(m1.hexBytes("payload"))) shouldBe m1.string("message")
            r.readMessage(m1.hexBytes("message")).size shouldBe 0
            hex(r.writeMessage(m2.hexBytes("payload"))) shouldBe m2.string("message")
            val id2 = HandshakeIdentityCodec.decode(i.readMessage(m2.hexBytes("message")), expectIntent = false)!!
            id2.displayName shouldBe m2.getValue("identity").jsonObject.string("display_name")
            hex(i.writeMessage(m3.hexBytes("payload"))) shouldBe m3.string("message")
            val id3 = HandshakeIdentityCodec.decode(r.readMessage(m3.hexBytes("message")), expectIntent = true)!!
            id3.intent!!.name shouldBe m3.getValue("identity").jsonObject.string("intent")
            hex(i.handshakeHash) shouldBe v.string("handshake_hash")
            hex(r.remoteStatic!!) shouldBe v.string("init_static_public")
            hex(i.remoteStatic!!) shouldBe v.string("resp_static_public")
            val (iSend, _) = i.split()
            val (rSend, rRecv) = r.split()
            val rIn = rRecv.openStream()
            v.array("records_i_to_r").map { it.jsonObject }.forEach { rec ->
                hex(iSend.seal(rec.hexBytes("plaintext"))) shouldBe rec.string("record")
                (rIn.feed(rec.hexBytes("record")) as RecordFeedResult.Plain).chunks.single().toHex() shouldBe rec.string("plaintext")
            }
            v.array("records_r_to_i").map { it.jsonObject }.forEach { rec ->
                hex(rSend.seal(rec.hexBytes("plaintext"))) shouldBe rec.string("record")
            }
        }
    }

    test("verification-code.json：commitment 与 SAS") {
        TestVectors.vectors("verification-code.json").forEach { v ->
            val (pkI, pkR, nI, nR) = listOf("pk_i", "pk_r", "n_i", "n_r").map { v.hexBytes(it) }
            hex(VerificationCode.commitment(pkI, pkR, nI)) shouldBe v.string("commitment")
            VerificationCode.sas(pkI, pkR, nI, nR) shouldBe v.string("sas")
        }
    }
})
