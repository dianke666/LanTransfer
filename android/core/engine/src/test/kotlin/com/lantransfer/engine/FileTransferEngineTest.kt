@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.FileState
import com.lantransfer.domain.InterruptReason
import com.lantransfer.domain.TaskState
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.FileEntry
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.TransferId
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.maps.shouldBeEmpty
import io.kotest.matchers.nulls.shouldBeNull
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private fun sentId(r: SendOfferResult): TransferId = r.shouldBeInstanceOf<SendOfferResult.Sent>().id

/** 足够大，使测试协程在传输进行中就能观察到第一个数据块（内存网络没有延迟）。 */
private const val BIG = 8_000_000

class FileTransferEngineTest : FunSpec({
    context("收发主流程（14、15、16）") {
        test("接收方确认后逐个文件传输（含 0 字节与跨多块的文件），两端完成且内容逐字节一致") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val refs = listOf(w.a.file("empty.bin", 0), w.a.file("small.txt", 1000), w.a.file("big.dat", 700_000))
                val id = sentId(w.a.engine.send(w.b.d.id, refs))
                w.a.state(id) shouldBe TaskState.OFFERING
                w.b.awaitState(id, TaskState.AWAITING_USER)
                w.b.received().shouldBeEmpty()
                w.b.engine.accept(id)
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.awaitState(id, TaskState.COMPLETED)
                val got = w.b.received()
                got.keys shouldBe setOf("empty.bin", "small.txt", "big.dat")
                for (r in refs) got[w.a.content(r).let { w.a.sources.entry(r)!!.name }] shouldBe w.a.content(r)
                w.a.task(id).files.map { it.state } shouldBe List(3) { FileState.COMPLETED }
                w.b.task(id).files.map { it.state } shouldBe List(3) { FileState.COMPLETED }
                w.a.task(id).files.map { it.persisted } shouldBe listOf(0L, 1000L, 700_000L)
                w.b.storage.tempRefs.shouldBeEmpty()
                w.a.view(id)!!.progress.percent shouldBe 100
                w.b.store.get(id).shouldNotBeNull().state shouldBe TaskState.COMPLETED
                w.a.engine.unfinishedCount().value shouldBe 0
            }
        }

        test("自动接收（15.2）：不出现确认，直接接收") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("x", 5000))))
                w.b.awaitState(id, TaskState.COMPLETED)
                w.b.received()["x"] shouldBe w.a.content(w.a.task(id).files[0].sourceRef!!)
            }
        }

        test("拒绝（15.3）：双方 REJECTED，不创建任何文件") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("x", 10))))
                w.b.awaitState(id, TaskState.AWAITING_USER)
                w.b.engine.reject(id)
                w.a.awaitState(id, TaskState.REJECTED)
                w.b.state(id) shouldBe TaskState.REJECTED
                w.b.received().shouldBeEmpty()
                w.b.storage.tempRefs.shouldBeEmpty()
            }
        }

        test("接收方 60 s 未响应：59 999 ms 仍在等待，60 000 ms 双方 NO_RESPONSE（15.4）") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("x", 10))))
                w.b.awaitState(id, TaskState.AWAITING_USER)
                val t0 = testScheduler.currentTime
                moveTimeTo(t0 + 59_999)
                w.b.state(id) shouldBe TaskState.AWAITING_USER
                moveTimeTo(t0 + 60_000)
                w.b.state(id) shouldBe TaskState.NO_RESPONSE
                w.a.awaitState(id, TaskState.NO_RESPONSE)
            }
        }

        test("发送方保底 65 s：对方始终不回复时 65 000 ms 结束为 NO_RESPONSE") {
            runTest {
                val w = XferWorld(this, bDispatch = false)
                runCurrent()
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("x", 10))))
                val t1 = testScheduler.currentTime
                moveTimeTo(t1 + 64_999)
                w.a.state(id) shouldBe TaskState.OFFERING
                moveTimeTo(t1 + 65_000)
                w.a.state(id) shouldBe TaskState.NO_RESPONSE
            }
        }

        test("预检：空间不足 → NO_SPACE，目录不可用 → CANNOT_SAVE（15.5、15.7）") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                w.b.storage.dir(DirRef.Default).freeSpace = 99
                val id1 = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("x", 100))))
                w.a.awaitState(id1, TaskState.NO_SPACE)
                w.b.state(id1) shouldBe TaskState.NO_SPACE
                w.b.storage.dir(DirRef.Default).freeSpace = null
                w.b.storage.dir(DirRef.Default).creatable = false
                val id2 = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("y", 100))))
                w.a.awaitState(id2, TaskState.CANNOT_SAVE)
                w.b.state(id2) shouldBe TaskState.CANNOT_SAVE
            }
        }

        test("超过 1000 个文件整批拒绝；1000 个可以（14.8）") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val many = (0 until 1001).map { w.a.file("f$it", 0) }
                w.a.engine.send(w.b.d.id, many) shouldBe SendOfferResult.TooMany
                val id = sentId(w.a.engine.send(w.b.d.id, many.take(1000)))
                w.b.awaitState(id, TaskState.COMPLETED)
                w.b.received().size shouldBe 1000
            }
        }

        test("文件夹与无法读取的源文件不发送 Offer（14.9）；无法建立 Session 时保留列表（14.10）") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val dir = com.lantransfer.domain.SourceRef("src:dir")
                w.a.sources.putDirectory(dir, "folder")
                w.a.engine.send(w.b.d.id, listOf(dir)) shouldBe SendOfferResult.Unreadable(listOf(dir))
                w.a.engine.send(deviceIdOf(9), listOf(w.a.file("x", 1))) shouldBe SendOfferResult.ConnectFailed(OpenFailure.Unreachable)
                w.a.engine.tasks.value.shouldBeEmpty()
            }
        }
    }

    context("取消、失败与中断（14.7、17.2、17.3、17.6、18.2、18.3、18.7）") {
        test("发送方在传输中取消：本机 CANCELLED，对方 PEER_CANCELLED 并删除未完成文件的临时文件") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("big", BIG))))
                w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                w.a.engine.cancel(id)
                w.a.awaitState(id, TaskState.CANCELLED)
                w.b.awaitState(id, TaskState.PEER_CANCELLED)
                moveTimeBy(100)
                w.b.storage.tempRefs.shouldBeEmpty()
                w.b.received().shouldBeEmpty()
            }
        }

        test("接收方取消：对方 PEER_CANCELLED，本机 5 s 内删除临时文件、保留已完成的文件") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(w.a.file("done", 10), w.a.file("big", BIG))))
                w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[1].persisted > 0 } }
                w.b.engine.cancel(id)
                w.b.awaitState(id, TaskState.CANCELLED)
                w.a.awaitState(id, TaskState.PEER_CANCELLED)
                moveTimeBy(5_000)
                w.b.storage.tempRefs.shouldBeEmpty()
                w.b.received().keys shouldBe setOf("done")
            }
        }

        test("源文件读取失败：该文件 READ_FAILED，继续下一个文件，任务完成（14.7）") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val bad = w.a.file("bad", 500_000)
                w.a.sources.entry(bad)!!.failAt = 200_000
                val good = w.a.file("good", 1000)
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(bad, good)))
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.awaitState(id, TaskState.COMPLETED)
                w.a.task(id).files.map { it.state } shouldBe listOf(FileState.READ_FAILED, FileState.COMPLETED)
                w.b.task(id).files.map { it.state } shouldBe listOf(FileState.READ_FAILED, FileState.COMPLETED)
                w.b.received().keys shouldBe setOf("good")
                w.b.storage.tempRefs.shouldBeEmpty()
            }
        }

        test("内容与 Offer 不符（源文件在发出 Offer 后变化）：CHECKSUM_FAILED，删除临时文件，继续下一个文件（17.2）") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val longer = w.a.file("longer", 1000)
                val shorter = w.a.file("shorter", 1000)
                val ok = w.a.file("ok", 10)
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(longer, shorter, ok)))
                w.a.sources.modify(longer, Bytes.of(ByteArray(1500) { 1 }), 1)
                w.a.sources.modify(shorter, Bytes.of(ByteArray(10) { 2 }), 1)
                w.b.awaitState(id, TaskState.AWAITING_USER)
                w.b.engine.accept(id)
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.awaitState(id, TaskState.COMPLETED)
                w.a.task(id).files.map { it.state } shouldBe listOf(FileState.CHECKSUM_FAILED, FileState.CHECKSUM_FAILED, FileState.COMPLETED)
                w.b.task(id).files.map { it.state } shouldBe listOf(FileState.CHECKSUM_FAILED, FileState.CHECKSUM_FAILED, FileState.COMPLETED)
                w.b.received().keys shouldBe setOf("ok")
                moveTimeBy(100)
                w.b.storage.tempRefs.shouldBeEmpty()
            }
        }

        test("连接断开：双方 INTERRUPTED 并保留临时文件；发送方点击继续后从接收方已写入处续传并完成（17.3、17.4）") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val ref = w.a.file("big", BIG)
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(ref)))
                w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                w.cut()
                w.a.awaitState(id, TaskState.INTERRUPTED)
                w.b.awaitState(id, TaskState.INTERRUPTED)
                w.a.task(id).interrupt!!.reason shouldBe InterruptReason.PEER_CLOSED
                val temp = w.b.storage.tempRefs.single()
                val partial = w.b.storage.length(temp)!!
                (partial > 0) shouldBe true
                w.a.engine.resume(id) shouldBe ResumeResult.Started
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.awaitState(id, TaskState.COMPLETED)
                w.b.received()["big"] shouldBe w.a.content(ref)
            }
        }

        test("接收方点击继续同样可以续传；10 s 内无法建立 Session 时保持已中断（17.10）") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val ref = w.a.file("big", BIG)
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(ref)))
                w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                w.cut()
                w.b.awaitState(id, TaskState.INTERRUPTED)
                w.a.awaitState(id, TaskState.INTERRUPTED)
                // B 不知道 A 的地址：无法连接
                w.b.engine.resume(id).shouldBeInstanceOf<ResumeResult.ConnectFailed>()
                w.b.state(id) shouldBe TaskState.INTERRUPTED
                w.b.storage.tempRefs.size shouldBe 1
                w.b.d.knows(w.a.d.also { it.listen() })
                w.b.engine.resume(id) shouldBe ResumeResult.Started
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.awaitState(id, TaskState.COMPLETED)
                w.b.received()["big"] shouldBe w.a.content(ref)
            }
        }

        test("接收方写入失败：双方 INTERRUPTED(WRITE_FAILED)、保留临时文件；恢复后继续完成（17.6）") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                var fail = true
                w.b.storage.writeFailure = { _, len -> if (fail && len >= 300_000) "磁盘已满" else null }
                val ref = w.a.file("big", 1_000_000)
                val id = sentId(w.a.engine.send(w.b.d.id, listOf(ref)))
                w.b.awaitState(id, TaskState.INTERRUPTED)
                w.a.awaitState(id, TaskState.INTERRUPTED)
                w.a.task(id).interrupt shouldBe com.lantransfer.domain.InterruptInfo(InterruptReason.WRITE_FAILED, "磁盘已满")
                w.b.task(id).files[0].state shouldBe FileState.WRITE_FAILED
                w.b.storage.tempRefs.size shouldBe 1
                fail = false
                w.a.engine.resume(id) shouldBe ResumeResult.Started
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.awaitState(id, TaskState.COMPLETED)
                w.b.received()["big"] shouldBe w.a.content(ref)
            }
        }
    }

    context("E3：未知任务与非法 Offer") {
        test("未知任务的 FILE_CHUNK 回复 TRANSFER_CANCEL(UNKNOWN_TRANSFER)；未知的 TRANSFER_CANCEL 不回复") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val raw = TestDevice("R", 5, iface("wlan0", 192, 168, 1, 9), w.net, w.clock, backgroundScope)
                raw.trusts(w.b.d)
                w.b.d.trusts(raw)
                val s = (raw.manager.open(Target.Device(w.b.d.id)) as OpenResult.Ok).session
                val tid = TransferId(Bytes16.of(ByteArray(16) { 7 }))
                s.send(TransferCancel(tid, TransferCancelReason.USER))
                s.send(FileChunk(tid, 0u, 0u, Bytes.of(byteArrayOf(1))))
                s.incoming.receive() shouldBe TransferCancel(tid, TransferCancelReason.UNKNOWN_TRANSFER)
                s.incoming.tryReceive().isFailure shouldBe true
            }
        }

        test("单个文件超过 1 TiB 的 Offer 回复 INVALID_OFFER，不创建任务；恰好 1 TiB 可以") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val raw = TestDevice("R", 5, iface("wlan0", 192, 168, 1, 9), w.net, w.clock, backgroundScope)
                raw.trusts(w.b.d)
                w.b.d.trusts(raw)
                val s = (raw.manager.open(Target.Device(w.b.d.id)) as OpenResult.Ok).session
                val big = TransferId(Bytes16.of(ByteArray(16) { 1 }))
                s.send(FileOffer(big, listOf(FileEntry("x", ProtocolConstants.MAX_FILE_SIZE.toULong() + 1u, "a/b"))))
                s.incoming.receive() shouldBe FileOfferReply(big, OfferDecision.INVALID_OFFER)
                w.b.view(big).shouldBeNull()
                w.b.store.get(big).shouldBeNull()
                val max = TransferId(Bytes16.of(ByteArray(16) { 2 }))
                s.send(FileOffer(max, listOf(FileEntry("x", ProtocolConstants.MAX_FILE_SIZE.toULong(), "a/b"))))
                runCurrent()
                w.b.state(max) shouldBe TaskState.AWAITING_USER
            }
        }
    }
})
