package com.lantransfer.protocol

import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.bytes
import com.lantransfer.testing.bytesOfSize
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.matchers.string.shouldMatch
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.byte
import io.kotest.property.arbitrary.byteArray
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.checkAll

private val keyArb = Gens.bytesOfSize(32)

/** 篡改方式：0 比特翻转、1 删除、2 重放、3 重排、4 插入其他 Session 的 record。 */
private fun tamper(records: List<ByteArray>, kind: Int, at: Int, bit: Int, foreign: ByteArray): Pair<List<ByteArray>, Int>? {
    val k = at % records.size
    val out = records.toMutableList()
    return when (kind) {
        0 -> {
            // 只翻转密文部分（长度前缀被改动的情况由删除、重排覆盖）
            val r = out[k].copyOf()
            val pos = 2 + (bit ushr 3) % (r.size - 2)
            r[pos] = (r[pos].toInt() xor (1 shl (bit and 7))).toByte()
            out[k] = r
            out to k
        }
        1 -> if (records.size < 2 || k == records.size - 1) null else { out.removeAt(k); out to k }
        2 -> { out.add(k + 1, records[k]); out to (k + 1) }
        3 -> if (k + 1 >= records.size) null else { out[k] = records[k + 1]; out[k + 1] = records[k]; out to k }
        else -> { out.add(k, foreign); out to k }
    }
}

private class Outcome(val ok: Boolean, val i: NoiseXX, val r: NoiseXX)

/** 运行一次完整握手；[mutate] 可以篡改第 index 条握手消息。 */
private fun handshake(i: NoiseXX, r: NoiseXX, p1: ByteArray, p2: ByteArray, p3: ByteArray, mutate: (Int, ByteArray) -> ByteArray): Outcome =
    try {
        // msg1 的载荷没有加密，被篡改时这里不会报错；篡改会改变握手哈希，在之后的消息中被发现
        r.readMessage(mutate(0, i.writeMessage(p1)))
        val m2 = mutate(1, r.writeMessage(p2))
        i.readMessage(m2).contentEquals(p2) shouldBe true
        val m3 = mutate(2, i.writeMessage(p3))
        r.readMessage(m3).contentEquals(p3) shouldBe true
        Outcome(true, i, r)
    } catch (_: HandshakeAuthError) {
        Outcome(false, i, r)
    }

class CryptoPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 13: 记录层往返与完整性
    // Validates: Requirements 10.1, 10.5
    test("Property 13") {
        val pieces = Arb.list(Arb.byteArray(Arb.int(1..300), Arb.byte()), 1..8)
        val splits = Arb.list(Arb.int(1..97), 1..6)
        checkAll(Pbt.iterations(1_000), Arb.bind(keyArb, keyArb) { a, b -> a to b }, pieces, splits, Arb.bind(Arb.int(0..4), Arb.int(0..1000), Arb.int(0..Int.MAX_VALUE)) { a, b, c -> Triple(a, b, c) }) { (key, otherKey), plain, split, (kind, at, bit) ->
            val k = key.toByteArray()
            val sender = RecordCipher(k)
            val records = plain.map { sender.seal(it) }

            // 往返：任意切分输入，明文拼接等于原明文拼接
            val stream = records.fold(ByteArray(0)) { a, b -> a + b }
            val reader = RecordCipher(k).openStream()
            val got = ArrayList<Bytes>()
            var pos = 0
            var s = 0
            while (pos < stream.size) {
                val n = minOf(split[s++ % split.size], stream.size - pos)
                got += reader.feed(stream, pos, n).shouldBeInstanceOf<RecordFeedResult.Plain>().chunks
                pos += n
            }
            got.fold(ByteArray(0)) { a, b -> a + b.toByteArray() }.contentEquals(plain.fold(ByteArray(0)) { a, b -> a + b }) shouldBe true

            // 篡改：返回 IntegrityError，且不输出被篡改 record 及其之后的任何明文
            val foreign = RecordCipher(otherKey.toByteArray()).seal(byteArrayOf(1, 2, 3))
            val sameKey = otherKey.toByteArray().contentEquals(k)
            val tampered = tamper(records, kind, at, bit, foreign)
            if (tampered != null && !(kind == 4 && sameKey)) {
                val (list, firstBad) = tampered
                val r2 = RecordCipher(k).openStream()
                val out = ArrayList<Bytes>()
                var error = false
                for (rec in list) {
                    when (val res = r2.feed(rec)) {
                        is RecordFeedResult.Plain -> out += res.chunks
                        is RecordFeedResult.IntegrityError -> { out += res.chunksBefore; error = true }
                    }
                }
                error shouldBe true
                out.map { it.toByteArray().toList() } shouldBe plain.take(firstBad).map { it.toList() }
            }
        }
    }

    // Feature: lan-file-transfer, Property 14: Noise_XX 相互认证
    // Validates: Requirements 10.2, 10.6
    test("Property 14") {
        val keys = Arb.bind(keyArb, keyArb, keyArb, keyArb) { a, b, c, d -> listOf(a, b, c, d).map { it.toByteArray() } }
        val payloads = Arb.bind(Gens.bytes(16), Gens.bytes(16), Gens.bytes(16)) { a, b, c -> Triple(a.toByteArray(), b.toByteArray(), c.toByteArray()) }
        checkAll(Pbt.iterations(1_000), keys, payloads, Arb.int(0..3), Arb.int(0..Int.MAX_VALUE), keyArb) { (si, sr, ei, er), (p1, p2, p3), scenario, seed, fake ->
            val prologue = NoiseXX.prologue(PreambleCodec.encode(ProtocolVersion.V1_0), PreambleCodec.encode(ProtocolVersion.V1_0))
            val kpI = X25519KeyPair.fromPrivateKey(si)
            val kpR = X25519KeyPair.fromPrivateKey(sr)
            fun init(pro: ByteArray) = NoiseXX(Role.INITIATOR, kpI, pro, EphemeralKeySource.fixed(ei))
            fun resp(pro: ByteArray) = NoiseXX(Role.RESPONDER, kpR, pro, EphemeralKeySource.fixed(er))
            when (scenario) {
                0 -> {
                    val o = handshake(init(prologue), resp(prologue), p1, p2, p3) { _, m -> m }
                    o.ok shouldBe true
                    Bytes.of(o.i.remoteStatic!!) shouldBe kpR.publicKey
                    Bytes.of(o.r.remoteStatic!!) shouldBe kpI.publicKey
                    val (iSend, iRecv) = o.i.split()
                    val (rSend, rRecv) = o.r.split()
                    (rRecv.openStream().feed(iSend.seal(p1 + 1)) as RecordFeedResult.Plain).chunks.single().toByteArray().contentEquals(p1 + 1) shouldBe true
                    (iRecv.openStream().feed(rSend.seal(p2 + 2)) as RecordFeedResult.Plain).chunks.single().toByteArray().contentEquals(p2 + 2) shouldBe true
                }
                1 -> {
                    // 篡改任意一条握手消息的任意一个字节
                    val target = seed % 3
                    val o = handshake(init(prologue), resp(prologue), p1, p2, p3) { idx, m ->
                        if (idx != target) m else m.copyOf().also { val at = (seed ushr 2) % m.size; it[at] = (it[at].toInt() xor (1 + (seed and 0x7F))).toByte() }
                    }
                    o.ok shouldBe false
                }
                2 -> {
                    // 一方出示与其私钥不对应的公钥
                    val i = init(prologue)
                    val r = resp(prologue)
                    val liar = if (seed % 2 == 0) i else r
                    val real = if (liar === i) kpI else kpR
                    if (!fake.toByteArray().contentEquals(real.publicKey.toByteArray())) {
                        liar.advertisedStaticOverride = fake.toByteArray()
                        handshake(i, r, p1, p2, p3) { _, m -> m }.ok shouldBe false
                    }
                }
                else -> {
                    // 双方 prologue 不同（如前导版本被篡改）
                    val other = NoiseXX.prologue(PreambleCodec.encode(ProtocolVersion(1, 1)), PreambleCodec.encode(ProtocolVersion.V1_0))
                    handshake(init(prologue), resp(other), p1, p2, p3) { _, m -> m }.ok shouldBe false
                }
            }
        }
    }

    // Feature: lan-file-transfer, Property 15: 配对验证码派生
    // Validates: Requirements 7.2, 10.3
    test("Property 15") {
        checkAll(Pbt.iterations(1_000), Arb.bind(keyArb, keyArb, keyArb, keyArb) { a, b, c, d -> listOf(a, b, c, d).map { it.toByteArray() } }, keyArb) { (pkI, pkR, nI, nR), other ->
            val sas = VerificationCode.sas(pkI, pkR, nI, nR)
            sas shouldMatch Regex("[0-9]{6}")
            // 双方按“发起方在前”各自计算（使用各自持有的副本）
            VerificationCode.sas(pkI.copyOf(), pkR.copyOf(), nI.copyOf(), nR.copyOf()) shouldBe sas
            val c = VerificationCode.commitment(pkI, pkR, nI)
            VerificationCode.verifyCommitment(c, pkI, pkR, nI) shouldBe true
            val revealed = other.toByteArray()
            VerificationCode.verifyCommitment(c, pkI, pkR, revealed) shouldBe revealed.contentEquals(nI)
        }
    }
})
