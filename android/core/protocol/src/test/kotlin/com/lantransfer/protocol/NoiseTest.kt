package com.lantransfer.protocol

import io.kotest.assertions.throwables.shouldThrow
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.matchers.string.shouldMatch
import io.kotest.matchers.types.shouldBeInstanceOf

private fun hex(s: String) = Bytes.ofHex(s).toByteArray()

private fun hexOf(b: ByteArray) = Bytes.of(b).toHex()

/** NoiseXX、记录层、验证码的示例测试；含 Noise 官方测试向量（cacophony）中 Noise_XX_25519_AESGCM_SHA256 的用例。 */
class NoiseTest : FunSpec({
    test("官方向量 Noise_XX_25519_AESGCM_SHA256：握手消息、握手哈希与传输消息逐字节一致") {
        val prologue = hex("4a6f686e2047616c74")
        val i = NoiseXX(
            Role.INITIATOR,
            X25519KeyPair.fromPrivateKey(hex("e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1")),
            prologue,
            EphemeralKeySource.fixed(hex("893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a")),
        )
        val r = NoiseXX(
            Role.RESPONDER,
            X25519KeyPair.fromPrivateKey(hex("4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893")),
            prologue,
            EphemeralKeySource.fixed(hex("bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b")),
        )
        val m1 = i.writeMessage(hex("4c756477696720766f6e204d69736573"))
        hexOf(m1) shouldBe "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c79444c756477696720766f6e204d69736573"
        hexOf(r.readMessage(m1)) shouldBe "4c756477696720766f6e204d69736573"
        val m2 = r.writeMessage(hex("4d757272617920526f746862617264"))
        hexOf(m2) shouldBe "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f144808843757117acceb05bd7a45733bc22015c97a9d0cbaf41b80446d5988ff5127235d76b79eade70f473d6a4ef521fdcbeda5340d01e028ba793fc059f2724a83af05f12dda0448a7621a926b379a92477fd"
        hexOf(i.readMessage(m2)) shouldBe "4d757272617920526f746862617264"
        val m3 = i.writeMessage(hex("462e20412e20486179656b"))
        hexOf(m3) shouldBe "c90f1cf77eba4e50edb038991565e36c9758943a989229b6051244dc4fbecb6946744b401af2ee1a5881b65fbb87fd07cb6a328ececc9ce6ce84c399dc332d4fd521fa4bb7f467ce909395"
        hexOf(r.readMessage(m3)) shouldBe "462e20412e20486179656b"
        hexOf(i.handshakeHash) shouldBe "1b7aefb1125762aa21a252890d00af54519638b76437444538f9a52f21e2e0dc"
        hexOf(r.handshakeHash) shouldBe hexOf(i.handshakeHash)

        val (iSend, iRecv) = i.split()
        val (rSend, rRecv) = r.split()
        // 传输消息：响应方先发（消息 4），发起方再发（消息 5）；记录层在密文前加 len:u16
        val t4 = rSend.seal(hex("4361726c204d656e676572"))
        hexOf(t4.copyOfRange(2, t4.size)) shouldBe "bc3fa77f6aca3e8466d7dc6bea10013e88a6a29add5132b461806c"
        val got4 = iRecv.openStream().feed(t4).shouldBeInstanceOf<RecordFeedResult.Plain>()
        got4.chunks.single().toHex() shouldBe "4361726c204d656e676572"
        val t5 = iSend.seal(hex("4a65616e2d426170746973746520536179"))
        hexOf(t5.copyOfRange(2, t5.size)) shouldBe "250b01074cdfe0df2ecf8ccbf1737b15a2ddb5b52fd9a396604e9c793cee3b3bb9"
        rRecv.openStream().feed(t5).shouldBeInstanceOf<RecordFeedResult.Plain>().chunks.single().toHex() shouldBe "4a65616e2d426170746973746520536179"
    }

    test("双方得到对方的静态公钥；篡改 msg2 导致握手失败") {
        val si = X25519KeyPair.fromPrivateKey(ByteArray(32) { 1 })
        val sr = X25519KeyPair.fromPrivateKey(ByteArray(32) { 2 })
        val p = NoiseXX.prologue(PreambleCodec.encode(ProtocolVersion.V1_0), PreambleCodec.encode(ProtocolVersion.V1_0))
        val i = NoiseXX(Role.INITIATOR, si, p, EphemeralKeySource.fixed(ByteArray(32) { 3 }))
        val r = NoiseXX(Role.RESPONDER, sr, p, EphemeralKeySource.fixed(ByteArray(32) { 4 }))
        r.readMessage(i.writeMessage(ByteArray(0)))
        val m2 = r.writeMessage("R".encodeToByteArray())
        val tampered = m2.copyOf().also { it[40] = (it[40].toInt() xor 1).toByte() }
        shouldThrow<HandshakeAuthError> { i.readMessage(tampered) }

        // 不同的 prologue（例如前导被篡改）同样失败
        val i2 = NoiseXX(Role.INITIATOR, si, p, EphemeralKeySource.fixed(ByteArray(32) { 3 }))
        val r2 = NoiseXX(Role.RESPONDER, sr, p + byteArrayOf(0), EphemeralKeySource.fixed(ByteArray(32) { 4 }))
        r2.readMessage(i2.writeMessage(ByteArray(0)))
        shouldThrow<HandshakeAuthError> { i2.readMessage(r2.writeMessage(ByteArray(0))) }

        val i3 = NoiseXX(Role.INITIATOR, si, p, EphemeralKeySource.fixed(ByteArray(32) { 3 }))
        val r3 = NoiseXX(Role.RESPONDER, sr, p, EphemeralKeySource.fixed(ByteArray(32) { 4 }))
        r3.readMessage(i3.writeMessage(ByteArray(0)))
        i3.readMessage(r3.writeMessage(ByteArray(0)))
        i3.remoteStatic!!.let(::hexOf) shouldBe sr.publicKey.toHex()
        r3.readMessage(i3.writeMessage(ByteArray(0)))
        r3.remoteStatic!!.let(::hexOf) shouldBe si.publicKey.toHex()
        i3.isComplete shouldBe true
    }

    test("记录层：大明文拆成多条 record，长度不足 17 或被篡改时为 IntegrityError 且之后不再输出") {
        val c = RecordCipher(ByteArray(32) { 9 })
        val d = RecordCipher(ByteArray(32) { 9 }).openStream()
        val plain = ByteArray(200_000) { it.toByte() }
        val sealed = c.seal(plain)
        val chunks = (d.feed(sealed) as RecordFeedResult.Plain).chunks
        chunks.size shouldBe 4
        chunks.fold(ByteArray(0)) { a, b -> a + b.toByteArray() }.contentEquals(plain) shouldBe true
        c.seal(ByteArray(0)).size shouldBe 0

        val d2 = RecordCipher(ByteArray(32) { 9 }).openStream()
        d2.feed(byteArrayOf(0, 16) + ByteArray(16)).shouldBeInstanceOf<RecordFeedResult.IntegrityError>()
        d2.feed(sealed).shouldBeInstanceOf<RecordFeedResult.IntegrityError>().chunksBefore shouldBe emptyList()
    }

    test("验证码：承诺与 SAS 确定、6 位补零；指纹为 SHA-256") {
        val pkI = ByteArray(32) { 1 }
        val pkR = ByteArray(32) { 2 }
        val nI = ByteArray(32) { 3 }
        val nR = ByteArray(32) { 4 }
        VerificationCode.commitment(pkI, pkR, nI).size shouldBe 32
        VerificationCode.sas(pkI, pkR, nI, nR) shouldMatch Regex("[0-9]{6}")
        VerificationCode.sas(pkI, pkR, nI, nR) shouldBe VerificationCode.sas(pkI.copyOf(), pkR.copyOf(), nI.copyOf(), nR.copyOf())
        hexOf(Fingerprint.of(ByteArray(0))) shouldBe "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    }
})
