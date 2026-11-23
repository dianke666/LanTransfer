package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileResultStatus
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.PairCancel
import com.lantransfer.protocol.PairCancelReason
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairDecision
import com.lantransfer.protocol.PairDecisionCode
import com.lantransfer.protocol.PairNonce
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.PairReveal
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumeFileState
import com.lantransfer.protocol.ResumeFileStatus
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.VerificationCode
import com.lantransfer.testing.Pbt
import io.kotest.assertions.withClue
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContain
import io.kotest.matchers.collections.shouldContainAll
import io.kotest.matchers.ints.shouldBeLessThanOrEqual
import io.kotest.matchers.shouldBe
import io.kotest.matchers.shouldNotBe
import io.kotest.property.Arb
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlin.random.Random

// 归约器属性测试（Property 35–38）。每个用例由一个种子决定，失败时打印种子即可重放。

// ---- Property 35：发起方与接收方的双方模拟 ----

private sealed interface PairAct {
    data object Connect : PairAct

    data object ConnectFail : PairAct

    /** 向接收方投递 1 条消息（或在对方关闭后投递断开）。 */
    data object ToR : PairAct

    data object ToI : PairAct

    data object Accept : PairAct

    data object Reject : PairAct

    data object Cancel : PairAct

    data class Advance(val dt: Long) : PairAct

    /** 网络中断：在途消息全部丢失（含 PAIR_COMPLETE 丢失），双方都检测到断开。 */
    data object Break : PairAct

    /** 完成一方未完成的信任写入。 */
    data class Store(val initiator: Boolean, val ok: Boolean) : PairAct
}

private fun randomPairAct(r: Random): PairAct = when (r.nextInt(20)) {
    0 -> PairAct.Connect
    1 -> PairAct.ConnectFail
    2, 3, 4, 5 -> PairAct.ToR
    6, 7, 8, 9 -> PairAct.ToI
    10, 11 -> PairAct.Accept
    12 -> PairAct.Reject
    13 -> PairAct.Cancel
    14, 15 -> PairAct.Advance(listOf(r.nextLong(0, 3_000), r.nextLong(9_000, 16_000), r.nextLong(59_000, 61_000))[r.nextInt(3)])
    16 -> PairAct.Break
    else -> PairAct.Store(r.nextBoolean(), r.nextInt(4) != 0)
}

/** 三分之一完全随机；其余以成功流程为骨架（决定为接受、拒绝或缺省），并随机插入、替换动作。 */
private fun pairScript(r: Random): List<PairAct> {
    if (r.nextInt(3) == 0) return List(r.nextInt(0, 41)) { randomPairAct(r) }
    val decision: PairAct? = when (r.nextInt(20)) {
        in 0..11 -> PairAct.Accept
        in 12..16 -> PairAct.Reject
        else -> null
    }
    val happy = listOfNotNull(
        PairAct.Connect, PairAct.ToR, PairAct.ToI, PairAct.ToR, decision, PairAct.ToI,
        PairAct.Store(true, true), PairAct.ToR, PairAct.Store(false, true),
    )
    val out = mutableListOf<PairAct>()
    for (a in happy) {
        if (r.nextInt(100) < 15) out += randomPairAct(r)
        out += if (r.nextInt(100) < 8) randomPairAct(r) else a
    }
    return out
}

/**
 * 发起方 I 与接收方 R 之间的一条连接 + 双方的信任存储。消息按发送顺序投递；一方关闭后，对方收完已发出的消息再收到断开，
 * 之后发往关闭方的消息全部丢失。信任写入是异步的：SaveTrust / CommitTrust 进入待完成状态，由 Store 动作完成。
 */
private class PairSim(r: Random) {
    val iInfo = peerInfo(1)
    val rInfo = peerInfo(2)
    private val conn = ConnId(1)
    private val nI = r.bytes(32)
    private val nR = r.bytes(32)
    private var now = 1_000_000L

    var ini = PairingState()
    var rec = PairingState()

    private var connected = false
    private val toR = ArrayDeque<ProtocolMessage>()
    private val toI = ArrayDeque<ProtocolMessage>()
    private var iClosed = false
    private var rClosed = false
    private var broken = false
    private var iNotified = false
    private var rNotified = false

    var iTrusted = false
    var rTrusted = false
    var rStaged = false
    private var iSavePending = false
    private var rCommitPending = false

    private var requestAt: Long? = null
    private var receivedAt: Long? = null
    var rGotRequest = false

    private fun canSend() = connected && !broken && !iClosed && !rClosed

    fun start() = applyI(PairingEvent.StartPair(rInfo.deviceId, now))

    private fun applyI(e: PairingEvent) {
        val step = PairingReducer.reduce(ini, e)
        ini = step.state
        for (f in step.effects) {
            when (f) {
                is PairingEffect.Send -> if (canSend()) toR.addLast(f.msg)
                is PairingEffect.Close -> {
                    iClosed = true
                    toI.clear()
                }
                is PairingEffect.SaveTrust -> iSavePending = true
                // 之前的保存（无论是否已完成）都被撤销
                is PairingEffect.DeleteTrust -> {
                    iTrusted = false
                    iSavePending = false
                }
                is PairingEffect.StageTrust, is PairingEffect.CommitTrust, is PairingEffect.DiscardStagedTrust -> error("发起方不应暂存或提交信任：$f")
                else -> Unit
            }
        }
        if (requestAt == null && ini.session is PairingSession.AwaitingPeer) requestAt = now
    }

    private fun applyR(e: PairingEvent) {
        val step = PairingReducer.reduce(rec, e)
        rec = step.state
        val fromComplete = e is PairingEvent.MessageReceived && e.msg == PairComplete
        for (f in step.effects) {
            when (f) {
                is PairingEffect.Send -> if (canSend()) toI.addLast(f.msg)
                is PairingEffect.Close -> {
                    rClosed = true
                    toR.clear()
                }
                is PairingEffect.StageTrust -> rStaged = true
                is PairingEffect.CommitTrust -> {
                    withClue("接收方只在收到 PAIR_COMPLETE 后提交信任") { fromComplete shouldBe true }
                    rCommitPending = true
                }
                is PairingEffect.DiscardStagedTrust -> rStaged = false
                is PairingEffect.SaveTrust, is PairingEffect.DeleteTrust -> error("接收方不应直接保存或删除信任：$f")
                else -> Unit
            }
        }
    }

    private fun deliverToR() {
        if (!connected || rClosed) return
        val m = toR.removeFirstOrNull()
        when {
            m is PairRequest -> {
                rGotRequest = true
                receivedAt = now
                applyR(PairingEvent.PairRequestReceived(conn, iInfo, rInfo.publicKey, m.commitment, nR, now))
            }
            m != null -> applyR(PairingEvent.MessageReceived(conn, m, now))
            (iClosed || broken) && !rNotified -> {
                rNotified = true
                applyR(PairingEvent.Disconnected(conn, now))
            }
        }
    }

    private fun deliverToI() {
        if (!connected || iClosed) return
        val m = toI.removeFirstOrNull()
        when {
            m != null -> applyI(PairingEvent.MessageReceived(conn, m, now))
            (rClosed || broken) && !iNotified -> {
                iNotified = true
                applyI(PairingEvent.Disconnected(conn, now))
            }
        }
    }

    fun act(a: PairAct) {
        when (a) {
            PairAct.Connect -> if (ini.session is PairingSession.Connecting) {
                connected = true
                applyI(PairingEvent.Connected(conn, rInfo, iInfo.publicKey, nI, now))
            }
            PairAct.ConnectFail -> if (ini.session is PairingSession.Connecting) applyI(PairingEvent.ConnectFailed(now))
            PairAct.ToR -> deliverToR()
            PairAct.ToI -> deliverToI()
            PairAct.Accept -> applyR(PairingEvent.UserDecision(true, now))
            PairAct.Reject -> applyR(PairingEvent.UserDecision(false, now))
            PairAct.Cancel -> applyI(PairingEvent.UserCancel(now))
            is PairAct.Advance -> {
                now += a.dt
                applyI(PairingEvent.Tick(now))
                applyR(PairingEvent.Tick(now))
            }
            PairAct.Break -> if (connected && !broken) {
                broken = true
                toR.clear()
                toI.clear()
                if (!iClosed && !iNotified) {
                    iNotified = true
                    applyI(PairingEvent.Disconnected(conn, now))
                }
                if (!rClosed && !rNotified) {
                    rNotified = true
                    applyR(PairingEvent.Disconnected(conn, now))
                }
            }
            is PairAct.Store -> if (a.initiator) {
                if (iSavePending) {
                    iSavePending = false
                    if (a.ok) iTrusted = true
                    applyI(PairingEvent.TrustStored(rInfo.deviceId, a.ok))
                }
            } else if (rCommitPending) {
                rCommitPending = false
                if (a.ok) {
                    rTrusted = true
                    rStaged = false
                }
                applyR(PairingEvent.TrustStored(iInfo.deviceId, a.ok))
            }
        }
        checkDeadlines()
    }

    /** 时间只随 Advance 推进且每次都送 Tick，因此满 60 s 后不能再有未结束的请求（7.7）。 */
    private fun checkDeadlines() {
        val sent = requestAt
        if (sent != null && now >= sent + 60_000) {
            withClue("发起方在 PAIR_REQUEST 满 60 s 后仍在等待") {
                (ini.session is PairingSession.AwaitingPeer || ini.session is PairingSession.ShowingSas) shouldBe false
            }
        }
        val got = receivedAt
        if (got != null && now >= got + 60_000) {
            withClue("接收方在收到请求满 60 s 后仍在显示") { (rec.session is PairingSession.AwaitingUser) shouldBe false }
        }
    }

    private fun disconnectPending(): Boolean = connected &&
        ((iClosed || broken) && !rClosed && !rNotified || (rClosed || broken) && !iClosed && !iNotified)

    /** 排空：投递全部在途消息、完成全部存储操作，再推进时间，直到双方都没有进行中的配对。 */
    fun drain() {
        repeat(100) {
            var guard = 0
            while (guard++ < 50 && (toR.isNotEmpty() || toI.isNotEmpty() || disconnectPending())) {
                deliverToR()
                deliverToI()
            }
            act(PairAct.Store(true, true))
            act(PairAct.Store(false, true))
            if (ini.session == null && rec.session == null && !iSavePending && !rCommitPending) return
            act(PairAct.Advance(16_000))
        }
    }
}

// ---- Property 36：接收方自动拒绝 ----

private sealed interface RecvAct {
    data class Request(val k: Int) : RecvAct

    data class Reveal(val good: Boolean, val stale: Boolean) : RecvAct

    data class Decide(val accept: Boolean) : RecvAct

    data object Complete : RecvAct

    data class StoreDone(val ok: Boolean) : RecvAct

    data object Drop : RecvAct

    data object PeerCancel : RecvAct

    data class Advance(val dt: Long) : RecvAct
}

private fun randomRecvAct(r: Random): RecvAct = when (r.nextInt(19)) {
    0, 1, 2, 3 -> RecvAct.Request(r.nextInt(3))
    4, 5, 6 -> RecvAct.Reveal(r.nextInt(8) != 0, r.nextInt(8) == 0)
    7, 8, 9 -> RecvAct.Decide(r.nextInt(3) != 0)
    10, 11 -> RecvAct.Complete
    12, 13 -> RecvAct.StoreDone(r.nextInt(4) != 0)
    14 -> RecvAct.Drop
    15 -> RecvAct.PeerCancel
    else -> RecvAct.Advance(listOf(r.nextLong(0, 5_000), r.nextLong(14_000, 16_000), r.nextLong(59_000, 61_000))[r.nextInt(3)])
}

/** 参考模型：无 / 显示中（是否已揭示）/ 等待 PAIR_COMPLETE / 提交中。 */
private sealed interface RecvModel {
    data object Idle : RecvModel

    data class Showing(val k: Int, val conn: ConnId, val nI: Bytes, val revealed: Boolean, val deadline: Long) : RecvModel

    data class Completing(val k: Int, val conn: ConnId, val deadline: Long) : RecvModel

    data class Saving(val k: Int, val conn: ConnId) : RecvModel
}

private fun recvConn(m: RecvModel): ConnId = when (m) {
    RecvModel.Idle -> ConnId(0)
    is RecvModel.Showing -> m.conn
    is RecvModel.Completing -> m.conn
    is RecvModel.Saving -> m.conn
}

private fun recvPeerIndex(m: RecvModel): Int = when (m) {
    RecvModel.Idle -> 0
    is RecvModel.Showing -> m.k
    is RecvModel.Completing -> m.k
    is RecvModel.Saving -> m.k
}

private fun matches(s: PairingSession?, m: RecvModel, peers: List<PeerInfo>): Boolean = when (m) {
    RecvModel.Idle -> s == null
    is RecvModel.Showing -> s is PairingSession.AwaitingUser && s.peer == peers[m.k].deviceId && s.conn == m.conn &&
        (s.sas != null) == m.revealed && s.deadline == m.deadline
    is RecvModel.Completing -> s is PairingSession.AwaitingComplete && s.peer == peers[m.k].deviceId && s.conn == m.conn &&
        s.role == PairingRole.RECEIVER && s.deadline == m.deadline
    is RecvModel.Saving -> s is PairingSession.Saving && s.peer == peers[m.k].deviceId && s.conn == m.conn && s.role == PairingRole.RECEIVER
}

private fun finished(peer: PeerInfo, result: PairingResult) = PairingEffect.Finished(PairingDone(peer.deviceId, PairingRole.RECEIVER, result))

// ---- Property 37：文字发送 ----

private sealed interface TextAct {
    data class Send(val k: Int) : TextAct

    data class Retry(val k: Int) : TextAct

    data class Ack(val k: Int) : TextAct

    data class Corrupt(val k: Int) : TextAct

    data class Advance(val dt: Long) : TextAct

    data object Restart : TextAct
}

private fun randomTextAct(r: Random): TextAct = when (r.nextInt(14)) {
    0, 1, 2 -> TextAct.Send(r.nextInt(3))
    3, 4 -> TextAct.Retry(r.nextInt(3))
    5, 6, 7, 8 -> TextAct.Ack(r.nextInt(3))
    9 -> TextAct.Corrupt(r.nextInt(3))
    10, 11, 12 -> TextAct.Advance(listOf(r.nextLong(0, 3_000), r.nextLong(9_000, 11_000), r.nextLong(20_000, 100_000))[r.nextInt(3)])
    else -> TextAct.Restart
}

private data class TextModel(val status: TextStatus, val deadline: Long?)

// ---- Property 38：传输任务 ----

private val activeFileStates = setOf(FileState.TRANSFERRING, FileState.VERIFYING)
private val deadEndFileStates = setOf(FileState.READ_FAILED, FileState.CHECKSUM_FAILED)
private val pendingTaskStates = setOf(TaskState.RUNNING, TaskState.RESUMING, TaskState.OFFERING, TaskState.AWAITING_USER)
private val acceptedTaskStates = setOf(TaskState.RUNNING, TaskState.INTERRUPTED, TaskState.RESUMING)

private fun randomPlan(r: Random, t: TransferTask): List<ResumePlanEntry> {
    if (t.direction == Direction.SEND) {
        val state = t.files.map { f -> ResumeFileState(f.index.toUInt(), ResumeFileStatus.entries[r.nextInt(4)], r.nextLong(0, f.size + 2).toULong()) }
        val stats = t.files.map { f -> if (r.nextInt(5) == 0) SourceStat(f.size + 1, f.mtime) else SourceStat(f.size, f.mtime) }
        return ResumePlanner.plan(t, state, stats)
    }
    return t.files.map { f ->
        when (r.nextInt(3)) {
            0 -> ResumePlanEntry(f.index, ResumeAction.SKIP, 0)
            1 -> ResumePlanEntry(f.index, ResumeAction.CONTINUE, r.nextLong(0, f.size + 1))
            else -> ResumePlanEntry(f.index, ResumeAction.RESTART, 0, f.size + r.nextLong(0, 3), f.mtime)
        }
    }
}

private fun commonEvent(r: Random, t: TransferTask): TransferEvent = when (r.nextInt(14)) {
    0 -> TransferEvent.OfferTimedOut
    1, 2, 3 -> TransferEvent.Disconnected(InterruptReason.entries[r.nextInt(InterruptReason.entries.size)])
    4 -> TransferEvent.LocalCancel(r.nextBoolean(), if (r.nextInt(4) == 0) TransferCancelReason.UNPAIRED else TransferCancelReason.USER)
    5 -> TransferEvent.PeerCancelled(if (r.nextBoolean()) TransferCancelReason.USER else TransferCancelReason.UNKNOWN_TRANSFER)
    6, 7, 8 -> TransferEvent.ResumeRequested
    9 -> TransferEvent.ResumeFailed(InterruptReason.PEER_UNRESPONSIVE)
    10, 11, 12 -> TransferEvent.ResumePlanned(randomPlan(r, t))
    else -> TransferEvent.Restored
}

/** 按当前任务现场生成事件：文件序号 70% 取当前文件，其余随机（含不存在的序号）。 */
private fun randomTransferEvent(r: Random, t: TransferTask): TransferEvent {
    if (r.nextInt(100) < 8) return commonEvent(r, t)
    val cur = t.files.firstOrNull { !it.state.isTerminal }
    val i = if (cur != null && r.nextInt(10) < 7) cur.index else r.nextInt(t.files.size + 1)
    val f = t.files.firstOrNull { it.index == i }
    val size = f?.size ?: 0L
    val persisted = f?.persisted ?: 0L
    return if (t.direction == Direction.SEND) {
        when (r.nextInt(14)) {
            0, 1 -> TransferEvent.OfferReplied(if (r.nextInt(10) < 7) OfferDecision.ACCEPT else OfferDecision.entries[r.nextInt(OfferDecision.entries.size)])
            2 -> TransferEvent.SourceReadFailed(i)
            3, 4, 5 -> TransferEvent.FileEndSent(i, r.bytes(32))
            6, 7, 8 -> TransferEvent.AckReceived(i, r.nextLong(0, size + 1))
            else -> TransferEvent.ResultReceived(
                i,
                when (r.nextInt(10)) {
                    in 0..6 -> FileResultStatus.COMPLETED
                    7, 8 -> FileResultStatus.CHECKSUM_FAILED
                    else -> FileResultStatus.WRITE_FAILED
                },
                "磁盘已满",
            )
        }
    } else {
        when (r.nextInt(16)) {
            0 -> TransferEvent.UserAccepted(DirRef.Default, r.nextInt(10) != 0)
            1 -> TransferEvent.UserRejected
            2, 3, 4, 5, 6 -> {
                val np = if (r.nextInt(10) == 0) size + 1 else minOf(size, persisted + r.nextLong(1, 70_000))
                TransferEvent.ChunkWritten(i, maxOf(np, persisted), fixtureLocalNow, 1_700_000_000_000)
            }
            7, 8, 9 -> TransferEvent.EndReceived(i, r.bytes(32), fixtureLocalNow)
            10, 11, 12 -> TransferEvent.VerifyDone(
                i,
                when (r.nextInt(10)) {
                    in 0..6 -> VerifyOutcome.Committed("f$i.bin", "/sdcard/Download/LanTransfer/f$i.bin", StoredFileRef("content://media/$i"))
                    7, 8 -> VerifyOutcome.ChecksumMismatch
                    else -> VerifyOutcome.CommitFailed("重命名失败")
                },
            )
            13 -> TransferEvent.WriteFailed(i, "存储空间不足")
            else -> TransferEvent.PeerReadFailed(i)
        }
    }
}

private fun checkTransferStep(before: TransferTask, e: TransferEvent, step: Step<TransferTask, TransferEffect>) {
    val after = step.state
    if (before.state.isTerminal) {
        withClue("终态任务不再迁移") {
            after shouldBe before
            step.effects shouldBe emptyList()
        }
        return
    }
    val files = after.files
    files.size shouldBe before.files.size
    withClue("同一任务至多 1 个文件处于 TRANSFERRING 或 VERIFYING") {
        files.count { it.state in activeFileStates } shouldBeLessThanOrEqual 1
    }
    val cur = files.indexOfFirst { !it.state.isTerminal }
    if (cur >= 0) {
        withClue("当前文件之后的文件尚未开始") { files.drop(cur + 1).all { it.state == FileState.WAITING || it.state.isTerminal } shouldBe true }
    }
    (after.interrupt != null) shouldBe (after.state == TaskState.INTERRUPTED)
    if (after.state == TaskState.COMPLETED) cur shouldBe -1
    if (e == TransferEvent.Restored) {
        withClue("启动恢复后不存在进行中或等待响应的任务") { (after.state in pendingTaskStates) shouldBe false }
    }
    if ((e is TransferEvent.LocalCancel || e is TransferEvent.PeerCancelled) && before.state in acceptedTaskStates) {
        withClue("取消时删除全部未完成文件的临时文件、保留已完成的文件") {
            files shouldBe before.files
            val deletes = step.effects.filterIsInstance<TransferEffect.DeleteTemp>().map { it.index }.toSet()
            val expected = if (before.direction == Direction.RECEIVE) before.files.filter { !it.state.isTerminal }.map { it.index }.toSet() else emptySet()
            deletes shouldBe expected
        }
    }
    val newlyDead = files.indices.any { !before.files[it].state.isTerminal && files[it].state in deadEndFileStates }
    if (newlyDead) {
        withClue("读取失败或校验失败后继续下一个文件") {
            (after.state == TaskState.RUNNING || after.state == TaskState.COMPLETED) shouldBe true
            if (after.direction == Direction.SEND && after.state == TaskState.RUNNING) {
                files[cur].state shouldBe FileState.TRANSFERRING
                step.effects shouldContain TransferEffect.StartFile(files[cur].index, files[cur].persisted)
            }
        }
    }
    for (sf in step.effects.filterIsInstance<TransferEffect.StartFile>()) {
        withClue("只开始当前文件") {
            cur shouldNotBe -1
            files[cur].index shouldBe sf.index
            files[cur].state shouldBe FileState.TRANSFERRING
        }
    }
}

class ReducerPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 35: 配对结局与信任写入一致
    // Validates: Requirements 7.4, 7.6, 7.7, 7.10, 7.12
    test("Property 35") {
        val seen = mutableSetOf<PairingResult>()
        checkAll(Pbt.iterations(1_000), Arb.long()) { seed ->
            val r = Random(seed)
            val sim = PairSim(r)
            withClue("seed=$seed") {
                sim.start()
                for (a in pairScript(r)) sim.act(a)
                sim.drain()
                sim.ini.session shouldBe null
                sim.rec.session shouldBe null
                val iResult = sim.ini.last?.result
                val rResult = sim.rec.last?.result
                iResult shouldNotBe null
                if (sim.rGotRequest) rResult shouldNotBe null
                withClue("发起方 $iResult") { sim.iTrusted shouldBe (iResult == PairingResult.SUCCESS) }
                withClue("接收方 $rResult") { sim.rTrusted shouldBe (rResult == PairingResult.SUCCESS) }
                sim.rStaged shouldBe false
                val negative = setOf(PairingResult.REJECTED, PairingResult.CANCELLED, PairingResult.TIMEOUT)
                if (iResult in negative || rResult in negative) (sim.iTrusted || sim.rTrusted) shouldBe false
                if (iResult != null) seen += iResult
            }
        }
        seen shouldContainAll listOf(
            PairingResult.SUCCESS, PairingResult.REJECTED, PairingResult.TIMEOUT,
            PairingResult.CANCELLED, PairingResult.DISCONNECTED, PairingResult.CONNECT_FAILED,
        )
    }

    // Feature: lan-file-transfer, Property 36: 配对请求自动拒绝
    // Validates: Requirements 7.8, 7.9
    test("Property 36") {
        val local = peerInfo(2)
        val peers = List(3) { peerInfo(10 + it) }
        checkAll(Pbt.iterations(1_000), Arb.long()) { seed ->
            val r = Random(seed)
            val nR = r.bytes(32)
            var s = PairingState()
            var model: RecvModel = RecvModel.Idle
            val rejectedAt = HashMap<Int, Long>()
            var now = 0L
            var nextConn = 100L
            repeat(r.nextInt(1, 61)) {
                val a = randomRecvAct(r)
                val before = model
                val curConn = recvConn(before)
                val curPeer = peers[recvPeerIndex(before)]
                val event: PairingEvent
                val expected: List<PairingEffect>
                when (a) {
                    is RecvAct.Request -> {
                        val conn = ConnId(nextConn++)
                        val info = peers[a.k]
                        val nI = r.bytes(32)
                        val c = Bytes.of(VerificationCode.commitment(info.publicKey.toByteArray(), local.publicKey.toByteArray(), nI.toByteArray()))
                        event = PairingEvent.PairRequestReceived(conn, info, local.publicKey, c, nR, now)
                        val rejected = rejectedAt[a.k]
                        expected = if (rejected != null && now - rejected < 60_000) {
                            listOf(
                                PairingEffect.Send(conn, PairDecision(PairDecisionCode.REJECTED)),
                                PairingEffect.Close(conn),
                                PairingEffect.AutoRejected(info.deviceId, PairingResult.AUTO_REJECTED),
                            )
                        } else if (before != RecvModel.Idle) {
                            listOf(
                                PairingEffect.Send(conn, PairDecision(PairDecisionCode.BUSY)),
                                PairingEffect.Close(conn),
                                PairingEffect.AutoRejected(info.deviceId, PairingResult.BUSY),
                            )
                        } else {
                            model = RecvModel.Showing(a.k, conn, nI, false, now + 60_000)
                            listOf(PairingEffect.Send(conn, PairNonce(nR)), PairingEffect.ShowRequest(info), PairingEffect.ArmTimer(now + 60_000))
                        }
                    }
                    is RecvAct.Reveal -> {
                        val sh = before as? RecvModel.Showing
                        val conn = if (a.stale) ConnId(1) else curConn
                        val nonce = if (a.good && sh != null) sh.nI else r.bytes(32)
                        event = PairingEvent.MessageReceived(conn, PairReveal(nonce), now)
                        expected = if (a.stale || sh == null || sh.revealed) {
                            emptyList()
                        } else if (a.good) {
                            model = sh.copy(revealed = true)
                            val code = VerificationCode.sas(peers[sh.k].publicKey.toByteArray(), local.publicKey.toByteArray(), sh.nI.toByteArray(), nR.toByteArray())
                            listOf(PairingEffect.ShowSas(code))
                        } else {
                            model = RecvModel.Idle
                            listOf(PairingEffect.DismissRequest, PairingEffect.Close(conn), finished(peers[sh.k], PairingResult.CODE_MISMATCH))
                        }
                    }
                    is RecvAct.Decide -> {
                        event = PairingEvent.UserDecision(a.accept, now)
                        val sh = before as? RecvModel.Showing
                        expected = if (sh == null || !sh.revealed) {
                            emptyList()
                        } else if (a.accept) {
                            model = RecvModel.Completing(sh.k, sh.conn, now + 15_000)
                            listOf(
                                PairingEffect.StageTrust(peers[sh.k]),
                                PairingEffect.Send(sh.conn, PairDecision(PairDecisionCode.ACCEPTED)),
                                PairingEffect.ArmTimer(now + 15_000),
                            )
                        } else {
                            model = RecvModel.Idle
                            rejectedAt[sh.k] = now
                            listOf(
                                PairingEffect.Send(sh.conn, PairDecision(PairDecisionCode.REJECTED)),
                                PairingEffect.Close(sh.conn),
                                finished(peers[sh.k], PairingResult.REJECTED),
                            )
                        }
                    }
                    RecvAct.Complete -> {
                        event = PairingEvent.MessageReceived(curConn, PairComplete, now)
                        val c = before as? RecvModel.Completing
                        expected = if (c == null) {
                            emptyList()
                        } else {
                            model = RecvModel.Saving(c.k, c.conn)
                            listOf(PairingEffect.CommitTrust(peers[c.k].deviceId))
                        }
                    }
                    is RecvAct.StoreDone -> {
                        event = PairingEvent.TrustStored(curPeer.deviceId, a.ok)
                        val sv = before as? RecvModel.Saving
                        expected = if (sv == null) {
                            emptyList()
                        } else {
                            model = RecvModel.Idle
                            if (a.ok) {
                                listOf(PairingEffect.PromoteSession(sv.conn), finished(peers[sv.k], PairingResult.SUCCESS))
                            } else {
                                listOf(PairingEffect.DiscardStagedTrust(peers[sv.k].deviceId), PairingEffect.Close(sv.conn), finished(peers[sv.k], PairingResult.STORE_FAILED))
                            }
                        }
                    }
                    RecvAct.Drop -> {
                        event = PairingEvent.Disconnected(curConn, now)
                        expected = when (before) {
                            is RecvModel.Showing -> {
                                model = RecvModel.Idle
                                listOf(PairingEffect.DismissRequest, finished(peers[before.k], PairingResult.DISCONNECTED))
                            }
                            is RecvModel.Completing -> {
                                model = RecvModel.Idle
                                listOf(PairingEffect.DiscardStagedTrust(peers[before.k].deviceId), finished(peers[before.k], PairingResult.DISCONNECTED))
                            }
                            else -> emptyList()
                        }
                    }
                    RecvAct.PeerCancel -> {
                        event = PairingEvent.MessageReceived(curConn, PairCancel(PairCancelReason.USER), now)
                        expected = when (before) {
                            is RecvModel.Showing -> {
                                model = RecvModel.Idle
                                listOf(PairingEffect.DismissRequest, PairingEffect.Close(before.conn), finished(peers[before.k], PairingResult.CANCELLED))
                            }
                            is RecvModel.Completing -> {
                                model = RecvModel.Idle
                                listOf(PairingEffect.DiscardStagedTrust(peers[before.k].deviceId), PairingEffect.Close(before.conn), finished(peers[before.k], PairingResult.CANCELLED))
                            }
                            else -> emptyList()
                        }
                    }
                    is RecvAct.Advance -> {
                        now += a.dt
                        event = PairingEvent.Tick(now)
                        expected = when (before) {
                            is RecvModel.Showing -> if (now >= before.deadline) {
                                model = RecvModel.Idle
                                listOf(
                                    PairingEffect.Send(before.conn, PairDecision(PairDecisionCode.TIMEOUT)),
                                    PairingEffect.DismissRequest,
                                    PairingEffect.Close(before.conn),
                                    finished(peers[before.k], PairingResult.TIMEOUT),
                                )
                            } else {
                                emptyList()
                            }
                            is RecvModel.Completing -> if (now >= before.deadline) {
                                model = RecvModel.Idle
                                listOf(PairingEffect.DiscardStagedTrust(peers[before.k].deviceId), PairingEffect.Close(before.conn), finished(peers[before.k], PairingResult.DISCONNECTED))
                            } else {
                                emptyList()
                            }
                            else -> emptyList()
                        }
                    }
                }
                val step = PairingReducer.reduce(s, event)
                s = step.state
                withClue("seed=$seed act=$a model=$before") {
                    step.effects shouldBe expected
                    matches(s.session, model, peers) shouldBe true
                    // 只有空闲时才会显示新请求，所以任一时刻显示中的请求至多 1 个
                    step.effects.any { it is PairingEffect.ShowRequest } shouldBe (before == RecvModel.Idle && model is RecvModel.Showing)
                }
            }
        }
    }

    // Feature: lan-file-transfer, Property 37: 文字发送状态迁移
    // Validates: Requirements 11.5, 11.6, 11.7, 12.12
    test("Property 37") {
        val keys = List(3) { k -> TextKey(deviceIdOf(20 + k % 2), MessageId(Bytes16.of(ByteArray(16) { b -> (k * 16 + b).toByte() }))) }
        val texts = listOf("a", "你好\n世界", "😀 \u0000").map { Bytes.of(it.encodeToByteArray()) }
        val sentAt = List(3) { k -> 1_700_000_000_000 + k }
        val received = TextRecord(
            99, deviceIdOf(30), "对方", MessageId(Bytes16.of(ByteArray(16) { 0x77 })), Direction.RECEIVE,
            texts[0], 0, RecordTime(0, 480), TextStatus.RECEIVED,
        )
        fun transmit(k: Int) = TextEffect.Transmit(keys[k].peer, Text(keys[k].messageId, sentAt[k], texts[k]))
        checkAll(Pbt.iterations(1_000), Arb.long()) { seed ->
            val r = Random(seed)
            var s = TextState()
            val model = HashMap<Int, TextModel>()
            var now = 0L
            repeat(r.nextInt(1, 60)) {
                val a = randomTextAct(r)
                val expected = mutableListOf<TextEffect>()
                val step = when (a) {
                    is TextAct.Send -> {
                        if (model[a.k] == null) {
                            model[a.k] = TextModel(TextStatus.SENDING, now + 10_000)
                            expected += transmit(a.k)
                            expected += TextEffect.ArmTimer(now + 10_000)
                        }
                        TextReducer.reduce(s, TextEvent.Send(keys[a.k], texts[a.k], sentAt[a.k], now))
                    }
                    is TextAct.Retry -> {
                        if (model[a.k]?.status == TextStatus.FAILED) {
                            model[a.k] = TextModel(TextStatus.SENDING, now + 10_000)
                            expected += TextEffect.SaveStatus(keys[a.k], TextStatus.SENDING)
                            expected += transmit(a.k)
                            expected += TextEffect.ArmTimer(now + 10_000)
                        }
                        TextReducer.reduce(s, TextEvent.Retry(keys[a.k], now))
                    }
                    is TextAct.Ack -> {
                        val st = model[a.k]?.status
                        if (st == TextStatus.SENDING || st == TextStatus.FAILED) {
                            model[a.k] = TextModel(TextStatus.DELIVERED, null)
                            expected += TextEffect.SaveStatus(keys[a.k], TextStatus.DELIVERED)
                        }
                        TextReducer.reduce(s, TextEvent.AckReceived(keys[a.k]))
                    }
                    is TextAct.Corrupt -> {
                        if (model[a.k]?.status == TextStatus.SENDING) {
                            model[a.k] = TextModel(TextStatus.FAILED, null)
                            expected += TextEffect.SaveStatus(keys[a.k], TextStatus.FAILED)
                        }
                        TextReducer.reduce(s, TextEvent.CorruptReceived(keys[a.k]))
                    }
                    is TextAct.Advance -> {
                        now += a.dt
                        for ((k, m) in model.toList()) {
                            val d = m.deadline
                            if (m.status == TextStatus.SENDING && d != null && now >= d) {
                                model[k] = TextModel(TextStatus.FAILED, null)
                                expected += TextEffect.SaveStatus(keys[k], TextStatus.FAILED)
                            }
                        }
                        TextReducer.reduce(s, TextEvent.Tick(now))
                    }
                    TextAct.Restart -> {
                        val records = model.map { (k, m) ->
                            TextRecord(k.toLong(), keys[k].peer, "对方", keys[k].messageId, Direction.SEND, texts[k], sentAt[k], RecordTime(0, 480), m.status)
                        } + received
                        for ((k, m) in model.toList()) {
                            if (m.status == TextStatus.SENDING) expected += TextEffect.SaveStatus(keys[k], TextStatus.FAILED)
                            model[k] = TextModel(if (m.status == TextStatus.SENDING) TextStatus.FAILED else m.status, null)
                        }
                        TextReducer.reduce(TextState(), TextEvent.Restored(records))
                    }
                }
                s = step.state
                withClue("seed=$seed act=$a") {
                    step.effects.toSet() shouldBe expected.toSet()
                    step.effects.size shouldBe expected.size
                    s.outgoing.size shouldBe model.size
                    for (k in 0..2) {
                        val o = s.outgoing[keys[k]]
                        val m = model[k]
                        o?.status shouldBe m?.status
                        o?.deadline shouldBe m?.deadline
                        o?.showRetry shouldBe m?.let { it.status == TextStatus.FAILED }
                        if (o != null) {
                            o.key shouldBe keys[k]
                            o.text shouldBe texts[k]
                            o.sentAtUtcMs shouldBe sentAt[k]
                        }
                    }
                }
            }
        }
    }

    // Feature: lan-file-transfer, Property 38: 传输任务归约不变量
    // Validates: Requirements 14.6, 14.7, 17.2, 17.9, 18.3
    test("Property 38") {
        val seenTask = mutableSetOf<TaskState>()
        val seenFile = mutableSetOf<FileState>()
        checkAll(Pbt.iterations(1_000), Arb.long()) { seed ->
            val r = Random(seed)
            val direction = if (r.nextBoolean()) Direction.SEND else Direction.RECEIVE
            val sizes = List(r.nextInt(1, 6)) { listOf(0L, 1L, 100L, 70_000L, r.nextLong(0, 200_000))[r.nextInt(5)] }
            val initial = when {
                direction == Direction.SEND -> TaskState.OFFERING
                r.nextInt(5) == 0 -> TaskState.RUNNING
                else -> TaskState.AWAITING_USER
            }
            var t = transferTask(direction, initial, sizes)
            seenTask += t.state
            repeat(r.nextInt(1, 80)) {
                val e = randomTransferEvent(r, t)
                val step = TransferReducer.reduce(t, e)
                withClue("seed=$seed event=$e before=${t.state} files=${t.files.map { it.state }}") { checkTransferStep(t, e, step) }
                t = step.state
                seenTask += t.state
                t.files.forEach { seenFile += it.state }
            }
        }
        seenTask shouldContainAll TaskState.entries
        seenFile shouldContainAll FileState.entries
    }
})
