@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.Heartbeat
import com.lantransfer.protocol.Intent
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.SessionClose
import com.lantransfer.protocol.SessionResult
import com.lantransfer.protocol.SessionResultCode
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TransferId
import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.protocolMessage
import io.kotest.assertions.withClue
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContainExactly
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.filter
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.channels.toList
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.random.Random

private fun TestScope.advanceTo(t: Long) {
    val dt = t - testScheduler.currentTime
    if (dt > 0) testScheduler.advanceTimeBy(dt)
    testScheduler.runCurrent()
}

private fun ack(n: Int) = TextAck(MessageId(Bytes16.of(ByteArray(16) { n.toByte() })))

private fun text(n: Int) = Text(MessageId(Bytes16.of(ByteArray(16) { n.toByte() })), 1_700_000_000_000, Bytes.of(byteArrayOf(n.toByte())))

class SessionPropertiesTest : FunSpec({
    test("Property 40: Pending 阶段的应用消息被拒") {
        // 前缀：白名单内、且不会改变状态的消息（SESSION_CLOSE 会关闭连接，接收方收到 PAIR_COMPLETE 会转为 Active）
        val allowed = Gens.protocolMessage().filter { it.type in Connection.PENDING_ALLOWED && it !is SessionClose && it != PairComplete }
        val forbidden = Gens.protocolMessage().filter { it.type !in Connection.PENDING_ALLOWED }
        checkAll(Pbt.iterations(100), Arb.list(allowed, 0..3), forbidden, Arb.list(Gens.protocolMessage(), 0..3)) { prefix, bad, tail ->
            runTest {
                val clock = FakeClock(testScheduler)
                val t = connectionUnderTest(backgroundScope, clock)
                (prefix + bad + tail).forEach { t.peer.send(it) }
                runCurrent()
                withClue("prefix=$prefix bad=$bad") {
                    t.peer.receive() shouldBe SessionResult(SessionResultCode.NOT_TRUSTED)
                    t.peer.receive().shouldBeNull()
                    t.conn.state.value shouldBe ConnState.Closed(CloseReason.PendingViolation)
                    t.conn.incoming.toList() shouldContainExactly prefix.filter { it != Heartbeat }
                }
            }
        }
    }

    test("Property 41: 信任判定表（全部组合）") {
        data class Case(val self: Boolean, val aTrustsB: Boolean, val aKeyOk: Boolean, val bTrustsA: Boolean, val bKeyOk: Boolean)
        val cases = buildList {
            add(Case(self = true, aTrustsB = false, aKeyOk = true, bTrustsA = false, bKeyOk = true))
            for (aT in listOf(false, true)) for (aK in if (aT) listOf(false, true) else listOf(true)) {
                for (bT in listOf(false, true)) for (bK in if (bT) listOf(false, true) else listOf(true)) add(Case(false, aT, aK, bT, bK))
            }
        }
        cases.size shouldBe 10
        for (c in cases) {
            runTest {
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val ifA = iface("wlan0", 192, 168, 1, 2)
                val ifB = iface("wlan0", 192, 168, 1, 3)
                val a = TestDevice("A", 1, ifA, net, clock, backgroundScope)
                val b = TestDevice("B", 2, ifB, net, clock, backgroundScope, identityOverride = if (c.self) a.identity else null)
                b.listen()
                val wrongForB = keyPairOf(90).publicKey
                val wrongForA = keyPairOf(91).publicKey
                if (c.aTrustsB) a.trusts(b, key = if (c.aKeyOk) b.publicKey else wrongForB)
                if (c.bTrustsA) b.trusts(a, key = if (c.bKeyOk) a.publicKey else wrongForA)
                val r = a.manager.open(Target.Address(ifB.address, b.port))
                runCurrent()
                val inbound = withTimeoutOrNull(1) { b.manager.pendingInbound.first() }
                withClue("$c → $r") {
                    when {
                        c.self -> r shouldBe OpenResult.Failed(OpenFailure.SelfConnection)
                        c.aTrustsB && !c.aKeyOk -> {
                            r shouldBe OpenResult.Failed(OpenFailure.KeyMismatch("B"))
                            a.trust.get(b.id)!!.publicKey shouldBe wrongForB
                        }
                        c.aTrustsB && c.bTrustsA && c.bKeyOk -> {
                            r.shouldBeInstanceOf<OpenResult.Ok>()
                            b.manager.session(a.id).shouldNotBeNull()
                        }
                        c.aTrustsB && c.bTrustsA -> {
                            r shouldBe OpenResult.Failed(OpenFailure.PeerKeyMismatch("B"))
                            b.trust.get(a.id)!!.publicKey shouldBe wrongForA
                        }
                        c.aTrustsB -> r shouldBe OpenResult.Failed(OpenFailure.NotTrusted)
                        else -> {
                            r.shouldBeInstanceOf<OpenResult.Pairing>().connection.intent shouldBe Intent.PAIR
                            inbound.shouldNotBeNull().intent shouldBe Intent.PAIR
                        }
                    }
                    if (r !is OpenResult.Pairing) inbound.shouldBeNull() // 不出现任何配对界面
                    if (r !is OpenResult.Ok) {
                        a.manager.sessions.value shouldBe emptyMap()
                        b.manager.sessions.value shouldBe emptyMap()
                    }
                }
            }
        }
    }

    test("Property 42: 每个对端恰好 1 个 Session") {
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val r = Random(seed)
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val n = r.nextInt(2, 5)
                // 设备 ID 的大小顺序与编号顺序随机无关
                val idBytes = (1..n).map { it * 10 }.shuffled(r)
                val devices = (0 until n).map { i ->
                    TestDevice("D$i", idBytes[i], iface("wlan0", 192, 168, 1, 10 + i), net, clock, backgroundScope).also { it.listen() }
                }
                for (x in devices) for (y in devices) if (x !== y) x.trusts(y)
                val opens = List(r.nextInt(1, 12)) {
                    val from = r.nextInt(n)
                    val to = (from + r.nextInt(1, n)) % n
                    Triple(from, to, r.nextLong(0, 4))
                }
                val results = opens.map { (from, to, wait) ->
                    async {
                        delay(wait)
                        devices[from].manager.open(Target.Device(devices[to].id))
                    }
                }.awaitAll()
                advanceTo(testScheduler.currentTime + 2_000)
                withClue("seed=$seed ids=$idBytes opens=$opens results=$results") {
                    val pairs = opens.map { (f, t, _) -> setOf(f, t) }.toSet()
                    for ((k, o) in opens.withIndex()) {
                        val (from, to, _) = o
                        val s = devices[from].manager.session(devices[to].id).shouldNotBeNull()
                        results[k] shouldBe OpenResult.Ok(s)
                    }
                    for (pair in pairs) {
                        val (x, y) = pair.toList().map { devices[it] }
                        val sx = x.manager.session(y.id).shouldNotBeNull()
                        val sy = y.manager.session(x.id).shouldNotBeNull()
                        sx.connection.local shouldBe sy.connection.remote // 两端是同一条 TCP 连接
                        sx.initiator shouldBe sy.initiator
                    }
                    for ((i, d) in devices.withIndex()) {
                        d.manager.sessions.value.size shouldBe pairs.count { i in it }
                    }
                    net.connections.count { it.isOpen } shouldBe pairs.size // 被放弃的连接都已关闭
                }
            }
        }
    }

    test("Property 43: 心跳与无响应判定") {
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val r = Random(seed)
                val horizon = 60_000L
                // 对方发帧的间隔：避开恰好 15 000 ms（与判定时刻同时发生时先后不确定）
                val peerTimes = ArrayList<Long>()
                var t = 0L
                while (true) {
                    t += if (r.nextInt(5) == 0) r.nextLong(15_001, 25_000) else r.nextLong(1, 14_999)
                    if (t > horizon) break
                    peerTimes += t
                }
                val localTimes = List(r.nextInt(0, 12)) { r.nextLong(0, horizon) }.sorted()
                val events = (peerTimes.map { it to true } + localTimes.map { it to false }).sortedBy { it.first }

                val clock = FakeClock(testScheduler)
                var rec: RecordingConn? = null
                val u = connectionUnderTest(backgroundScope, clock) { RecordingConn(it, clock).also { x -> rec = x } }
                u.conn.activate()
                for ((i, e) in events.withIndex()) {
                    advanceTo(e.first)
                    if (e.second) u.peer.send(if (i % 2 == 0) Heartbeat else ack(i)) else u.conn.enqueue(ack(i), Priority.CONTROL)
                    runCurrent()
                }
                advanceTo(horizon)

                // 参考模型：第一个达到 15 s 的无接收区间
                var last = 0L
                var expectedClose: Long? = null
                for (p in peerTimes) {
                    if (p - last >= 15_000) break
                    last = p
                }
                if (last + 15_000 <= horizon) expectedClose = last + 15_000
                withClue("seed=$seed peer=$peerTimes local=$localTimes writes=${rec!!.writeTimes}") {
                    val state = u.conn.state.value
                    if (expectedClose == null) {
                        state shouldBe ConnState.Active
                    } else {
                        state shouldBe ConnState.Closed(CloseReason.PeerUnresponsive)
                    }
                    val end = expectedClose ?: horizon
                    val writes = rec!!.writeTimes.zip(rec!!.writeLengths)
                    var prev = 0L
                    for ((time, len) in writes) {
                        (time - prev <= 5_000) shouldBe true
                        if (len == RecordingConn.HEARTBEAT_WIRE_LENGTH) (time - prev >= 5_000) shouldBe true
                        (time <= end) shouldBe true
                        prev = time
                    }
                    (end - prev <= 5_000) shouldBe true
                    if (expectedClose != null) {
                        // 关闭恰好发生在区间满 15 s 时：之后没有任何写出，且之前 1 ms 时仍未关闭由模型的时刻唯一确定
                        writes.all { it.first <= expectedClose } shouldBe true
                    }
                }
            }
        }
    }

    test("Property 44: 写循环优先级与公平性") {
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val r = Random(seed)
                val clock = FakeClock(testScheduler)
                var gate: GatedConn? = null
                val u = connectionUnderTest(backgroundScope, clock) { GatedConn(it).also { g -> gate = g } }
                u.conn.activate()

                // 参考模型
                val p0 = ArrayDeque<ProtocolMessage>()
                val p1 = ArrayDeque<ProtocolMessage>()
                val modelSources = ArrayList<ArrayDeque<ProtocolMessage>>()
                var rr = 0
                var inFlight = false
                val expected = ArrayList<ProtocolMessage>()
                fun pick(): ProtocolMessage? {
                    p0.removeFirstOrNull()?.let { return it }
                    p1.removeFirstOrNull()?.let { return it }
                    val n = modelSources.size
                    for (k in 0 until n) {
                        val i = (rr + k) % n
                        val m = modelSources[i].removeFirstOrNull()
                        if (m != null) {
                            rr = (i + 1) % n
                            return m
                        }
                    }
                    return null
                }
                fun settle() {
                    if (!inFlight) pick()?.let {
                        expected += it
                        inFlight = true
                    }
                }

                var counter = 0
                var sourceCount = 0
                repeat(r.nextInt(5, 40)) {
                    when (r.nextInt(5)) {
                        0 -> {
                            val m = ack(counter++ and 0x7F)
                            p0.addLast(m)
                            u.conn.enqueue(m, Priority.CONTROL)
                        }
                        1 -> {
                            val m = text(counter++ and 0x7F)
                            p1.addLast(m)
                            u.conn.enqueue(m, Priority.TEXT)
                        }
                        2 -> if (sourceCount < 4) {
                            val tid = TransferId(Bytes16.of(ByteArray(16) { sourceCount.toByte() }))
                            sourceCount++
                            val chunks = ArrayDeque((0 until r.nextInt(0, 7)).map { FileChunk(tid, 0u, it.toULong(), Bytes.of(byteArrayOf(it.toByte()))) })
                            modelSources += ArrayDeque(chunks)
                            u.conn.attachChunkSource { chunks.removeFirstOrNull() }
                        }
                        else -> {
                            val k = r.nextInt(1, 3)
                            repeat(k) {
                                if (inFlight) {
                                    gate!!.release()
                                    inFlight = false
                                    runCurrent()
                                    settle()
                                }
                            }
                        }
                    }
                    runCurrent()
                    settle()
                }
                // 放行剩余的全部写入
                while (inFlight) {
                    gate!!.release()
                    inFlight = false
                    runCurrent()
                    settle()
                }
                val actual = List(expected.size) { u.peer.receive() }
                withClue("seed=$seed") {
                    actual shouldContainExactly expected
                    // 同一文件的 offset 严格连续、没有丢失
                    val chunks = actual.filterIsInstance<FileChunk>().groupBy { it.transferId }
                    chunks.values.forEach { list -> list.map { it.offset.toInt() } shouldBe list.indices.toList() }
                }
            }
        }
    }
})
