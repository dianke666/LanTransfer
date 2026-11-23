@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.FileState
import com.lantransfer.domain.TaskState
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileAck
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.FileEnd
import com.lantransfer.protocol.FileEntry
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.FileResult
import com.lantransfer.protocol.FileResultStatus
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.TransferId
import com.lantransfer.testing.Pbt
import io.kotest.assertions.withClue
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import java.security.MessageDigest
import kotlin.random.Random

private fun sha(b: ByteArray): Bytes = Bytes.of(MessageDigest.getInstance("SHA-256").digest(b))

private fun sentId(r: SendOfferResult): TransferId = r.shouldBeInstanceOf<SendOfferResult.Sent>().id

private val terminalOrInterrupted = { s: TaskState? -> s != null && (s.isTerminal || s == TaskState.INTERRUPTED) }

// Property 46 在 ResumePropertyTest 中。
class TransferPropertiesTest : FunSpec({
    test("Property 45: 当且仅当校验通过才提交") {
        // 原始发送方按扰动方式发送数据块与 FILE_END：替换某块数据、末尾多发、少发最后一块、篡改哈希（可组合）
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val rnd = Random(seed)
                val w = XferWorld(this)
                val raw = TestDevice("R", 5, iface("wlan0", 192, 168, 1, 9), w.net, w.clock, backgroundScope)
                raw.trusts(w.b.d)
                w.b.d.trusts(raw)
                w.b.d.trust.setAutoAccept(raw.id, true)
                runCurrent()
                val size = if (rnd.nextInt(5) == 0) 0 else rnd.nextInt(1, 300_000)
                val content = rnd.nextBytes(size)
                val chunks = content.toList().chunked(65_536).map { it.toByteArray() }.toMutableList()
                val replace = chunks.isNotEmpty() && rnd.nextBoolean()
                val extra = rnd.nextInt(4) == 0
                val drop = chunks.isNotEmpty() && rnd.nextInt(4) == 0
                val tamper = rnd.nextInt(4) == 0
                if (replace) {
                    val k = rnd.nextInt(chunks.size)
                    chunks[k] = chunks[k].copyOf().also { it[rnd.nextInt(it.size)] = (it[0] + 1).toByte() }
                }
                if (drop) chunks.removeAt(chunks.size - 1)
                if (extra) chunks += rnd.nextBytes(rnd.nextInt(1, 1000))
                val sent = chunks.fold(ByteArray(0)) { acc, c -> acc + c }
                val endHash = if (tamper) sha(byteArrayOf(9) + sent) else sha(if (rnd.nextBoolean()) sent else content)
                val s = (raw.manager.open(Target.Device(w.b.d.id)) as OpenResult.Ok).session
                val id = TransferId(Bytes16.of(rnd.nextBytes(16)))
                s.send(FileOffer(id, listOf(FileEntry("f.bin", size.toULong(), "application/octet-stream"))))
                s.incoming.receive() shouldBe FileOfferReply(id, OfferDecision.ACCEPT)
                var off = 0L
                for (c in chunks) {
                    s.send(FileChunk(id, 0u, off.toULong(), Bytes.of(c)), Priority.CONTROL)
                    off += c.size
                }
                w.b.received().keys.shouldBeEmpty() // 提交之前不出现最终文件名
                s.send(FileEnd(id, 0u, endHash))
                var result: FileResult
                while (true) {
                    when (val m = s.incoming.receive()) {
                        is FileResult -> {
                            result = m
                            break
                        }
                        is FileAck -> Unit
                        else -> error("unexpected $m")
                    }
                }
                val expectCommit = sent.size == size && sha(sent) == endHash
                withClue("seed=$seed size=$size replace=$replace extra=$extra drop=$drop tamper=$tamper") {
                    if (expectCommit) {
                        result.status shouldBe FileResultStatus.COMPLETED
                        w.b.received()["f.bin"] shouldBe Bytes.of(sent)
                        w.b.task(id).files[0].state shouldBe FileState.COMPLETED
                    } else {
                        result.status shouldBe FileResultStatus.CHECKSUM_FAILED
                        w.b.received().keys.shouldBeEmpty()
                        runCurrent()
                        w.b.task(id).files[0].state shouldBe FileState.CHECKSUM_FAILED
                        w.b.storage.tempRefs.shouldBeEmpty()
                    }
                }
            }
        }
    }

    test("Property 47: 故障隔离") {
        // 对端 A、C 各向 B 并行发送 2 个任务；对其中一个任务注入取消 / 校验失败 / 写入失败，或断开 A–B 的 Session。
        // 其他任务（以及不属于被断开 Session 的任务）全部完成，内容一致，接收方看到的已传输字节数不回退。
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val rnd = Random(seed)
                val w = XferWorld(this)
                w.autoAccept()
                val c = XferDevice(TestDevice("C", 3, iface("wlan0", 192, 168, 1, 4), w.net, w.clock, backgroundScope), w.clock, backgroundScope, 3)
                c.d.trusts(w.b.d)
                w.b.d.trusts(c.d)
                w.b.d.trust.setAutoAccept(c.d.id, true)
                runCurrent()
                val senders = listOf(w.a, w.a, c, c)
                val refs = senders.mapIndexed { k, s -> listOf(s.file("t${k}a", rnd.nextInt(1, 600_000), rnd.nextLong()), s.file("t${k}b", rnd.nextInt(0, 200_000), rnd.nextLong())) }
                val fault = rnd.nextInt(4) // 0 取消、1 校验失败、2 写入失败、3 断开 A–B
                val victim = rnd.nextInt(4)
                val ids = arrayOfNulls<TransferId>(4)
                if (fault == 1) {
                    // 发出 Offer 后修改源文件，使接收方校验失败
                    ids[victim] = sentId(senders[victim].engine.send(w.b.d.id, refs[victim]))
                    senders[victim].sources.modify(refs[victim][0], Bytes.of(rnd.nextBytes(10)), 7)
                }
                if (fault == 2) {
                    w.b.storage.writeFailure = { ref, len -> if (ids[victim] != null && ref.value.contains(ids[victim]!!.value.toHex()) && len > 1000) "写入失败" else null }
                }
                for (k in 0 until 4) if (ids[k] == null) ids[k] = sentId(senders[k].engine.send(w.b.d.id, refs[k]))
                val monotonic = HashMap<TransferId, Long>()
                var regressed: String? = null
                backgroundScope.launch {
                    w.b.engine.tasks.collect { list ->
                        for (v in list) {
                            val now = v.task.files.sumOf { it.persisted }
                            val prev = monotonic[v.id] ?: 0
                            if (v.task.state == TaskState.RUNNING && now < prev) regressed = "${v.id} $prev -> $now"
                            monotonic[v.id] = maxOf(prev, now)
                        }
                    }
                }
                when (fault) {
                    0 -> senders[victim].engine.cancel(ids[victim]!!)
                    3 -> {
                        w.b.engine.tasks.first { l -> l.count { it.task.files.any { f -> f.persisted > 0 } } >= 1 }
                        w.net.connections.filter { it.clientHost == "A" }.forEach { it.reset() }
                    }
                }
                val affected = when (fault) {
                    3 -> setOf(0, 1)
                    else -> setOf(victim)
                }
                for (k in 0 until 4) {
                    if (k in affected) {
                        w.b.engine.tasks.first { l -> l.any { it.id == ids[k] && terminalOrInterrupted(it.task.state) } }
                        continue
                    }
                    w.b.awaitState(ids[k]!!, TaskState.COMPLETED)
                }
                withClue("seed=$seed fault=$fault victim=$victim") {
                    regressed shouldBe null
                    val got = w.b.received()
                    for (k in 0 until 4) {
                        if (k in affected) continue
                        senders[k].awaitState(ids[k]!!, TaskState.COMPLETED)
                        w.b.task(ids[k]!!).files.map { it.state } shouldBe listOf(FileState.COMPLETED, FileState.COMPLETED)
                        got["t${k}a"] shouldBe senders[k].content(refs[k][0])
                        got["t${k}b"] shouldBe senders[k].content(refs[k][1])
                    }
                }
            }
        }
    }

    test("Property 52: 清空记录放弃未完成任务") {
        // B 上有若干任务：等待确认（A 发来）、传输中（R 发来）、已中断（A 发来，A 随后在线或离线）、已完成；B 作为发送方的 OFFERING。
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val rnd = Random(seed)
                val w = XferWorld(this)
                val c = XferDevice(TestDevice("C", 3, iface("wlan0", 192, 168, 1, 4), w.net, w.clock, backgroundScope).also { it.listen() }, w.clock, backgroundScope, 3)
                c.d.trusts(w.b.d)
                w.b.d.trusts(c.d)
                w.a.d.listen()
                w.b.d.knows(w.a.d)
                runCurrent()
                // 传输中的任务由只发一块数据的原始发送方 R 产生，保证在放弃时仍在 RUNNING
                val raw = TestDevice("R", 5, iface("wlan0", 192, 168, 1, 9), w.net, w.clock, backgroundScope)
                raw.trusts(w.b.d)
                w.b.d.trusts(raw)
                w.b.d.trust.setAutoAccept(raw.id, true)
                val rawSession = (raw.manager.open(Target.Device(w.b.d.id)) as OpenResult.Ok).session
                val rawInbox = ArrayList<ProtocolMessage>()
                backgroundScope.launch { for (m in rawSession.incoming) rawInbox += m }
                // 0 等待确认 1 传输中 2 已中断 3 已完成 4 B 发出的 OFFERING；断开 A–B 的步骤（2）排在 A 发起的其他任务之前
                val kinds = (0 until rnd.nextInt(1, 6)).map { rnd.nextInt(5) }.sortedBy { listOf(3, 2, 0, 1, 4).indexOf(it) }
                val ids = ArrayList<Pair<Int, TransferId>>()
                for (k in kinds) {
                    when (k) {
                        0 -> {
                            w.b.d.trust.setAutoAccept(w.a.d.id, false)
                            val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("w${ids.size}", 10))))
                            w.b.awaitState(id, TaskState.AWAITING_USER)
                            ids += k to id
                        }
                        1 -> {
                            val id = TransferId(Bytes16.of(rnd.nextBytes(16)))
                            rawSession.send(FileOffer(id, listOf(FileEntry("r${ids.size}", 1_000_000u, "a/b"))))
                            runCurrent()
                            rawSession.send(FileChunk(id, 0u, 0u, Bytes.of(ByteArray(1000))))
                            w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                            ids += k to id
                        }
                        2 -> {
                            w.b.d.trust.setAutoAccept(w.a.d.id, true)
                            val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("i${ids.size}", 8_000_000))))
                            w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                            w.net.connections.filter { it.clientHost == "A" || it.serverHost == "A" }.forEach { it.reset() }
                            w.b.awaitState(id, TaskState.INTERRUPTED)
                            w.a.awaitState(id, TaskState.INTERRUPTED)
                            ids += k to id
                        }
                        3 -> {
                            w.b.d.trust.setAutoAccept(c.d.id, true)
                            val id = sentId(c.engine.send(w.b.d.id, listOf(c.file("d${ids.size}", 100))))
                            w.b.awaitState(id, TaskState.COMPLETED)
                            ids += k to id
                        }
                        else -> {
                            c.d.trust.setAutoAccept(w.b.d.id, false)
                            w.b.d.knows(c.d)
                            val id = sentId(w.b.engine.send(c.d.id, listOf(w.b.file("o${ids.size}", 10))))
                            c.awaitState(id, TaskState.AWAITING_USER)
                            ids += k to id
                        }
                    }
                }
                val unfinished = ids.count { it.first != 3 }
                val completedNames = w.b.received().keys.toSet()
                withClue("seed=$seed kinds=$kinds") {
                    w.b.engine.unfinishedCount().value shouldBe unfinished
                    w.b.engine.abandonAll()
                    w.b.history.clearAll()
                    runCurrent()
                    for ((k, id) in ids) {
                        w.b.store.get(id) shouldBe null // clearAll 之后不再有任何任务行
                        when (k) {
                            0 -> w.a.awaitState(id, TaskState.REJECTED)
                            1 -> rawInbox.any { it == TransferCancel(id, TransferCancelReason.USER) } shouldBe true
                            4 -> c.awaitState(id, TaskState.PEER_CANCELLED)
                            else -> Unit
                        }
                    }
                    // 已中断的任务：与对方没有 Session 时写入 pending_cancel，否则已直接送达
                    val interrupted = ids.filter { it.first == 2 }.map { it.second }
                    val pending = w.b.db.pendingCancels().map { it.transferId }.toSet()
                    for (id in interrupted) (id in pending || w.a.state(id) == TaskState.PEER_CANCELLED) shouldBe true
                    // 对方下次建立 Session 后 2 s 内收到取消
                    if (pending.isNotEmpty()) {
                        val t0 = testScheduler.currentTime
                        w.a.d.manager.open(Target.Device(w.b.d.id)).shouldBeInstanceOf<OpenResult.Ok>()
                        for (id in pending) w.a.awaitState(id, TaskState.PEER_CANCELLED)
                        (testScheduler.currentTime - t0 <= 2_000) shouldBe true
                    }
                    moveTimeBy(5_000)
                    w.b.storage.tempRefs.shouldBeEmpty()
                    w.b.received().keys shouldBe completedNames
                    (w.b.history.list() as ListResult.Ok<HistoryRecord>).items.shouldBeEmpty()
                    w.b.engine.tasks.value.shouldBeEmpty()
                    w.b.engine.restoreOnStartup()
                    w.b.engine.tasks.value.shouldBeEmpty()
                }
            }
        }
    }
})
