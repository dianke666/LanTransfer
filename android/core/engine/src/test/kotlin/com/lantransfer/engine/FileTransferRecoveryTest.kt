@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileState
import com.lantransfer.domain.InterruptInfo
import com.lantransfer.domain.InterruptReason
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.Settings
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TempFileSelector
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TransferTask
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.TransferId
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldBeEmpty
import io.kotest.matchers.collections.shouldContainExactly
import io.kotest.matchers.nulls.shouldNotBeNull
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest

private const val BIG = 8_000_000

private fun sent(r: SendOfferResult): TransferId = r.shouldBeInstanceOf<SendOfferResult.Sent>().id

private fun tid(b: Int) = TransferId(Bytes16.of(ByteArray(16) { b.toByte() }))

/** 让传输进行到接收方写入第一个数据块后断开，双方进入“已中断”。 */
private suspend fun XferWorld.interruptedTransfer(size: Int = BIG): TransferId {
    val id = sent(a.engine.send(b.d.id, listOf(a.file("big", size))))
    b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
    cut()
    a.awaitState(id, TaskState.INTERRUPTED)
    b.awaitState(id, TaskState.INTERRUPTED)
    return id
}

class FileTransferRecoveryTest : FunSpec({
    context("pending_cancel（18.9）") {
        test("取消已中断的任务：本机立即 CANCELLED 并写入 pending_cancel；下次建立 Session 后 2 s 内对方收到取消") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = w.interruptedTransfer()
                w.a.engine.cancel(id)
                runCurrent()
                w.a.state(id) shouldBe TaskState.CANCELLED
                w.a.db.pendingCancels().map { it.transferId } shouldContainExactly listOf(id)
                w.b.state(id) shouldBe TaskState.INTERRUPTED
                val t0 = testScheduler.currentTime
                w.a.d.manager.open(Target.Device(w.b.d.id)).shouldBeInstanceOf<OpenResult.Ok>()
                w.b.awaitState(id, TaskState.PEER_CANCELLED)
                (testScheduler.currentTime - t0 <= 2_000) shouldBe true
                w.a.db.pendingCancels().shouldBeEmpty()
                moveTimeBy(5_000)
                w.b.storage.tempRefs.shouldBeEmpty()
            }
        }

        test("已有 Session 时取消已中断的任务：立即送达") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = w.interruptedTransfer()
                w.a.d.manager.open(Target.Device(w.b.d.id)).shouldBeInstanceOf<OpenResult.Ok>()
                runCurrent()
                w.a.engine.cancel(id)
                w.b.awaitState(id, TaskState.PEER_CANCELLED)
                w.a.db.pendingCancels().shouldBeEmpty()
            }
        }
    }

    context("E4：提交失败与接收目录") {
        test("提交失败：文件 WRITE_FAILED、任务 INTERRUPTED，临时文件保留；继续时 start = size，重新校验后提交") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                w.b.storage.commitFailure = { "被占用" }
                val ref = w.a.file("doc.pdf", 300_000)
                val id = sent(w.a.engine.send(w.b.d.id, listOf(ref)))
                w.b.awaitState(id, TaskState.INTERRUPTED)
                w.a.awaitState(id, TaskState.INTERRUPTED)
                w.b.task(id).interrupt shouldBe InterruptInfo(InterruptReason.WRITE_FAILED, "被占用")
                w.b.task(id).files[0].state shouldBe FileState.WRITE_FAILED
                w.b.storage.length(w.b.storage.tempRefs.single()) shouldBe 300_000L
                w.b.storage.commitFailure = { null }
                w.b.engine.resume(id) shouldBe ResumeResult.Started
                w.b.awaitState(id, TaskState.COMPLETED)
                w.a.awaitState(id, TaskState.COMPLETED)
                w.b.received()["doc.pdf"] shouldBe w.a.content(ref)
            }
        }

        test("接收目录在接受时固定（16.3）：之后修改设置不影响该任务，续传后仍写入原目录") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = w.interruptedTransfer()
                val other = DirRef.Path("/other")
                w.b.settings.update { Settings(receiveDirectory = other) }
                w.a.engine.resume(id) shouldBe ResumeResult.Started
                w.b.awaitState(id, TaskState.COMPLETED)
                w.b.received().keys shouldBe setOf("big")
                w.b.storage.filesIn(other).isEmpty() shouldBe true
                w.b.task(id).receiveDir shouldBe DirRef.Default
            }
        }
    }

    context("启动恢复与临时文件清理（17.7、17.9、M3.2）") {
        test("RUNNING / RESUMING → INTERRUPTED(APP_KILLED)，OFFERING / AWAITING_USER → CONNECTION_LOST，已中断的保留原因，终态不变") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                fun task(id: TransferId, state: TaskState, dir: Direction = Direction.SEND, interrupt: InterruptInfo? = null) = TransferTask(
                    id, w.b.d.id, "B", dir, state, interrupt, if (dir == Direction.RECEIVE) DirRef.Default else null, RecordTime(1, 480), 1,
                    listOf(TransferFile(id, 0, 0, "f", 10, "a/b", null, FileState.WAITING)),
                )
                val ids = (1..6).map { tid(it) }
                w.a.store.save(task(ids[0], TaskState.RUNNING))
                w.a.store.save(task(ids[1], TaskState.RESUMING, Direction.RECEIVE))
                w.a.store.save(task(ids[2], TaskState.OFFERING))
                w.a.store.save(task(ids[3], TaskState.AWAITING_USER, Direction.RECEIVE))
                w.a.store.save(task(ids[4], TaskState.INTERRUPTED, interrupt = InterruptInfo(InterruptReason.PEER_UNRESPONSIVE)))
                w.a.store.save(task(ids[5], TaskState.CANCELLED))
                w.a.engine.restoreOnStartup()
                w.a.store.get(ids[0])!!.interrupt shouldBe InterruptInfo(InterruptReason.APP_KILLED)
                w.a.store.get(ids[1])!!.state shouldBe TaskState.INTERRUPTED
                w.a.store.get(ids[2])!!.state shouldBe TaskState.CONNECTION_LOST
                w.a.store.get(ids[3])!!.state shouldBe TaskState.CONNECTION_LOST
                w.a.store.get(ids[4])!!.interrupt shouldBe InterruptInfo(InterruptReason.PEER_UNRESPONSIVE)
                w.a.store.get(ids[5])!!.state shouldBe TaskState.CANCELLED
                w.a.engine.unfinishedCount().value shouldBe 3
                w.a.state(ids[5]) shouldBe null // 已结束的任务不载入
            }
        }

        test("TempFileJanitor：不属于进行中任务且超过 24 h 未写入的临时文件被删除；恰好 24 h 保留；启动后每 1 h 运行一次") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val s = w.a.storage
                val now = w.clock.wallMs()
                val old = s.createTemp(DirRef.Default, tid(1), 0)
                val edge = s.createTemp(DirRef.Default, tid(2), 0)
                val fresh = s.createTemp(DirRef.Default, tid(3), 0)
                s.temp(old)!!.lastWriteUtcMs = now - TempFileSelector.MAX_AGE_MS - 1
                s.temp(edge)!!.lastWriteUtcMs = now - TempFileSelector.MAX_AGE_MS
                s.temp(fresh)!!.lastWriteUtcMs = now - 23 * 3_600_000L
                w.a.engine.restoreOnStartup()
                s.tempRefs shouldContainExactly listOf(edge, fresh)
                moveTimeBy(3_600_000L - 1)
                s.tempRefs shouldContainExactly listOf(edge, fresh)
                moveTimeBy(1)
                s.tempRefs shouldContainExactly listOf(fresh)
                moveTimeBy(3_600_000L)
                s.tempRefs.shouldBeEmpty()
            }
        }

        test("进行中任务的临时文件不被清理") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = sent(w.a.engine.send(w.b.d.id, listOf(w.a.file("big", BIG))))
                w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                val temp = w.b.storage.tempRefs.single()
                w.b.storage.temp(temp)!!.lastWriteUtcMs = 0
                w.b.engine.runJanitor()
                w.b.storage.tempRefs shouldContainExactly listOf(temp)
            }
        }

        test("“以 .txt 文件发送”的缓存：发送任务结束后删除；启动时删除不被未结束任务引用的缓存") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val ref = (w.a.cache.write("aa", "文字_1.txt", byteArrayOf(1, 2, 3)) as com.lantransfer.engine.ports.WriteResult.Ok).value
                val stale = (w.a.cache.write("bb", "文字_2.txt", byteArrayOf(4)) as com.lantransfer.engine.ports.WriteResult.Ok).value
                val id = sent(w.a.engine.send(w.b.d.id, listOf(ref)))
                w.a.awaitState(id, TaskState.COMPLETED)
                w.a.cache.owns(ref) shouldBe false
                w.a.cache.owns(stale) shouldBe true
                w.a.engine.restoreOnStartup()
                w.a.cache.list().shouldBeEmpty()
            }
        }
    }

    context("放弃与解除配对（20.5、20.10、Q7）") {
        test("abandonAll：等待确认的接收任务回复 REJECT，传输中的任务发送取消；之后 tasks 为空、未完成数为 0") {
            runTest {
                val w = XferWorld(this)
                runCurrent()
                val waiting = sent(w.a.engine.send(w.b.d.id, listOf(w.a.file("x", 10))))
                w.b.awaitState(waiting, TaskState.AWAITING_USER)
                w.b.d.trust.setAutoAccept(w.a.d.id, true)
                val running = sent(w.a.engine.send(w.b.d.id, listOf(w.a.file("big", BIG))))
                w.b.engine.tasks.first { l -> l.any { it.id == running && it.task.files[0].persisted > 0 } }
                w.b.engine.unfinishedCount().value shouldBe 2
                w.b.engine.abandonAll()
                w.b.engine.tasks.value.shouldBeEmpty()
                w.b.engine.unfinishedCount().value shouldBe 0
                w.b.store.get(waiting)!!.state shouldBe TaskState.REJECTED
                w.b.store.get(running)!!.state shouldBe TaskState.CANCELLED
                w.a.awaitState(waiting, TaskState.REJECTED)
                w.a.awaitState(running, TaskState.PEER_CANCELLED)
                moveTimeBy(5_000)
                w.b.storage.tempRefs.shouldBeEmpty()
            }
        }

        test("abandon 已中断的任务：CANCELLED 并写入 pending_cancel") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = w.interruptedTransfer()
                w.b.engine.abandon(id)
                w.b.state(id) shouldBe TaskState.CANCELLED
                w.b.db.pendingCancels().map { it.transferId } shouldContainExactly listOf(id)
            }
        }

        test("cancelPeer：对该对端的全部未结束任务按本机取消处理，对方收到 TRANSFER_CANCEL(UNPAIRED)") {
            runTest {
                val w = XferWorld(this)
                w.autoAccept()
                runCurrent()
                val id = sent(w.a.engine.send(w.b.d.id, listOf(w.a.file("big", BIG))))
                w.b.engine.tasks.first { l -> l.any { it.id == id && it.task.files[0].persisted > 0 } }
                w.a.engine.cancelPeer(w.b.d.id)
                w.a.state(id) shouldBe TaskState.CANCELLED
                w.b.awaitState(id, TaskState.PEER_CANCELLED)
                w.a.store.get(id).shouldNotBeNull().state shouldBe TaskState.CANCELLED
            }
        }
    }
})
