package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.FileReadFailed
import com.lantransfer.protocol.FileResult
import com.lantransfer.protocol.FileResultStatus
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumePlan
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import java.time.LocalDateTime

/** 接收方对一个文件做完“flushSync → 校验 → 提交”后的结果（C4.5 FileReceiver）。 */
sealed interface VerifyOutcome {
    data class Committed(val finalName: String, val fullPath: String, val storedRef: StoredFileRef) : VerifyOutcome

    data object ChecksumMismatch : VerifyOutcome

    data class CommitFailed(val detail: String) : VerifyOutcome
}

sealed interface TransferEvent {
    // ---- 发送方 ----

    /** 收到 FILE_OFFER_REPLY（OFFERING）。 */
    data class OfferReplied(val decision: OfferDecision) : TransferEvent

    /** 源文件打开或读取失败（14.7）。 */
    data class SourceReadFailed(val index: Int) : TransferEvent

    /** 已发出 FILE_END（附带整个文件的 SHA‑256）。 */
    data class FileEndSent(val index: Int, val sha256: Bytes) : TransferEvent

    data class AckReceived(val index: Int, val persisted: Long) : TransferEvent

    data class ResultReceived(val index: Int, val status: FileResultStatus, val detail: String?) : TransferEvent

    // ---- 接收方 ----

    /** 用户点击“接收”（15.6）。[dirUsable] 为 false 表示接收目录无法创建或不可写（16.15）。 */
    data class UserAccepted(val dir: DirRef, val dirUsable: Boolean) : TransferEvent

    data object UserRejected : TransferEvent

    /** 已写入 FILE_CHUNK；[persisted] 为该文件临时文件的新长度。 */
    data class ChunkWritten(val index: Int, val persisted: Long, val localNow: LocalDateTime, val nowWall: Long) : TransferEvent

    data class EndReceived(val index: Int, val sha256: Bytes, val localNow: LocalDateTime) : TransferEvent

    data class VerifyDone(val index: Int, val outcome: VerifyOutcome) : TransferEvent

    data class WriteFailed(val index: Int, val detail: String) : TransferEvent

    /** 收到 FILE_READ_FAILED。 */
    data class PeerReadFailed(val index: Int) : TransferEvent

    // ---- 双方 ----

    /** Offer 计时到期：接收方 60 s、发送方保底 65 s（15.4）。 */
    data object OfferTimedOut : TransferEvent

    data class Disconnected(val reason: InterruptReason, val detail: String? = null) : TransferEvent

    /**
     * 本机取消或放弃（18.2、18.9、20.5、20.10、Q7）。[sessionActive] 表示与对方当前有 Active Session。
     * 接收方在 AWAITING_USER 时按拒绝处理（C4.5）。
     */
    data class LocalCancel(val sessionActive: Boolean, val reason: TransferCancelReason = TransferCancelReason.USER) : TransferEvent

    /** 收到 TRANSFER_CANCEL（含续传时对方回复 UNKNOWN_TRANSFER）。 */
    data class PeerCancelled(val reason: TransferCancelReason) : TransferEvent

    /** 用户点击“继续”，或收到对方的 RESUME_REQUEST / RESUME_STATE（17.4）。 */
    data object ResumeRequested : TransferEvent

    /** 10 s 内未建立 Session（17.10）。 */
    data class ResumeFailed(val reason: InterruptReason) : TransferEvent

    /** RESUME_PLAN 已确定：发送方由 ResumePlanner 生成，接收方从对方收到。 */
    data class ResumePlanned(val plan: List<ResumePlanEntry>) : TransferEvent

    /** 启动恢复（17.9）。 */
    data object Restored : TransferEvent
}

sealed interface TransferEffect {
    /** 在与对方的 Session 上发送（取消类消息用 P0）。 */
    data class Send(val msg: ProtocolMessage) : TransferEffect

    /** 发送方：从 [offset] 起读取并发送第 [index] 个文件（offset = size 时直接发 FILE_END）。 */
    data class StartFile(val index: Int, val offset: Long) : TransferEffect

    /** 发送方：停止读取与发送。 */
    data object StopSending : TransferEffect

    /** 接收方：flushSync → 校验长度与哈希 → 文件名处理 → 提交。 */
    data class Verify(val index: Int, val sha256: Bytes) : TransferEffect

    /** 接收方：删除第 [index] 个文件的临时文件（18.3 要求 5 s 内完成）。 */
    data class DeleteTemp(val index: Int) : TransferEffect

    /** 写入 pending_cancel，下次与对方建立 Session 后 2 s 内发出（18.9）。 */
    data class AddPendingCancel(val reason: TransferCancelReason) : TransferEffect

    data class StartOfferTimer(val ms: Long) : TransferEffect

    data object CancelOfferTimer : TransferEffect

    /** 重新建立 Session 并交换 RESUME_STATE / RESUME_PLAN（10 s 预算）。 */
    data object StartResume : TransferEffect

    data object AbortResume : TransferEffect

    /** 对方的 FILE_* 消息违反顺序规则：关闭连接（22.6）。 */
    data object ProtocolViolation : TransferEffect
}

/**
 * 传输任务状态机（M3.2、M3.3，需求 14–18）：(task, event) → (task′, effects)。按 [TransferTask.direction] 区分发送方与接收方。
 * 同一任务内按 index 顺序逐个文件传输：“当前文件”是第一个未进入终态的文件。终态任务不再迁移。
 */
object TransferReducer {
    const val RECEIVER_OFFER_TIMEOUT_MS = 60_000L
    const val SENDER_OFFER_TIMEOUT_MS = 65_000L

    fun reduce(t: TransferTask, e: TransferEvent): Step<TransferTask, TransferEffect> {
        if (t.state.isTerminal) return Step(t, emptyList())
        return when (e) {
            is TransferEvent.OfferReplied -> onOfferReplied(t, e)
            is TransferEvent.SourceReadFailed -> senderOnly(t) { onSourceReadFailed(t, e) }
            is TransferEvent.FileEndSent -> senderOnly(t) { onFileEndSent(t, e) }
            is TransferEvent.AckReceived -> senderOnly(t) { onAck(t, e) }
            is TransferEvent.ResultReceived -> senderOnly(t) { onResult(t, e) }
            is TransferEvent.UserAccepted -> onUserAccepted(t, e)
            TransferEvent.UserRejected -> onUserRejected(t)
            is TransferEvent.ChunkWritten -> receiverOnly(t) { onChunk(t, e) }
            is TransferEvent.EndReceived -> receiverOnly(t) { onEnd(t, e) }
            is TransferEvent.VerifyDone -> receiverOnly(t) { onVerifyDone(t, e) }
            is TransferEvent.WriteFailed -> receiverOnly(t) { onWriteFailed(t, e) }
            is TransferEvent.PeerReadFailed -> receiverOnly(t) { onPeerReadFailed(t, e) }
            TransferEvent.OfferTimedOut -> onOfferTimedOut(t)
            is TransferEvent.Disconnected -> onDisconnected(t, e)
            is TransferEvent.LocalCancel -> onLocalCancel(t, e)
            is TransferEvent.PeerCancelled -> onPeerCancelled(t)
            TransferEvent.ResumeRequested ->
                if (t.state == TaskState.INTERRUPTED) Step(t.copy(state = TaskState.RESUMING, interrupt = null), listOf(TransferEffect.StartResume)) else none(t)
            is TransferEvent.ResumeFailed ->
                if (t.state == TaskState.RESUMING) Step(t.copy(state = TaskState.INTERRUPTED, interrupt = InterruptInfo(e.reason)), emptyList()) else none(t)
            is TransferEvent.ResumePlanned -> onResumePlanned(t, e)
            TransferEvent.Restored -> onRestored(t)
        }
    }

    private fun none(t: TransferTask) = Step<TransferTask, TransferEffect>(t, emptyList())

    private inline fun senderOnly(t: TransferTask, f: () -> Step<TransferTask, TransferEffect>) =
        if (t.direction == Direction.SEND && t.state == TaskState.RUNNING) f() else none(t)

    private inline fun receiverOnly(t: TransferTask, f: () -> Step<TransferTask, TransferEffect>) =
        if (t.direction == Direction.RECEIVE && t.state == TaskState.RUNNING) f() else none(t)

    /** 当前文件在 files 中的位置；全部为终态时为 -1。 */
    private fun currentPos(t: TransferTask): Int = t.files.indexOfFirst { !it.state.isTerminal }

    private fun posOf(t: TransferTask, index: Int): Int = t.files.indexOfFirst { it.index == index }

    private fun TransferTask.withFile(pos: Int, f: TransferFile) = copy(files = files.toMutableList().also { it[pos] = f })

    private fun interrupted(t: TransferTask, reason: InterruptReason, detail: String? = null) =
        t.copy(state = TaskState.INTERRUPTED, interrupt = InterruptInfo(reason, detail))

    /** 发送方：当前文件进入终态后开始下一个文件；没有剩余文件时任务完成。 */
    private fun startNextSender(t: TransferTask, effects: List<TransferEffect>): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0) return Step(t.copy(state = TaskState.COMPLETED, interrupt = null), effects)
        val f = t.files[pos]
        val next = t.withFile(pos, f.copy(state = FileState.TRANSFERRING))
        return Step(next, effects + TransferEffect.StartFile(f.index, f.persisted))
    }

    /** 接收方：当前文件进入终态后，没有剩余文件时任务完成；否则等待下一个文件的数据。 */
    private fun afterReceiverFile(t: TransferTask, effects: List<TransferEffect>): Step<TransferTask, TransferEffect> =
        if (currentPos(t) < 0) Step(t.copy(state = TaskState.COMPLETED, interrupt = null), effects) else Step(t, effects)

    // ---- Offer 阶段 ----

    private fun onOfferReplied(t: TransferTask, e: TransferEvent.OfferReplied): Step<TransferTask, TransferEffect> {
        if (t.direction != Direction.SEND || t.state != TaskState.OFFERING) return none(t)
        val end = when (e.decision) {
            OfferDecision.ACCEPT -> return startNextSender(t.copy(state = TaskState.RUNNING), listOf(TransferEffect.CancelOfferTimer))
            OfferDecision.REJECT -> TaskState.REJECTED
            OfferDecision.TIMEOUT -> TaskState.NO_RESPONSE
            OfferDecision.NO_SPACE -> TaskState.NO_SPACE
            OfferDecision.CANNOT_SAVE -> TaskState.CANNOT_SAVE
            OfferDecision.INVALID_OFFER -> TaskState.OFFER_INVALID
        }
        return Step(t.copy(state = end), listOf(TransferEffect.CancelOfferTimer))
    }

    private fun reply(t: TransferTask, d: OfferDecision) = TransferEffect.Send(FileOfferReply(t.transferId, d))

    private fun onUserAccepted(t: TransferTask, e: TransferEvent.UserAccepted): Step<TransferTask, TransferEffect> {
        if (t.direction != Direction.RECEIVE || t.state != TaskState.AWAITING_USER) return none(t)
        if (!e.dirUsable) return Step(t.copy(state = TaskState.CANNOT_SAVE), listOf(TransferEffect.CancelOfferTimer, reply(t, OfferDecision.CANNOT_SAVE)))
        val running = t.copy(state = TaskState.RUNNING, receiveDir = e.dir)
        return afterReceiverFile(running, listOf(TransferEffect.CancelOfferTimer, reply(t, OfferDecision.ACCEPT)))
    }

    private fun onUserRejected(t: TransferTask): Step<TransferTask, TransferEffect> =
        if (t.direction == Direction.RECEIVE && t.state == TaskState.AWAITING_USER) {
            Step(t.copy(state = TaskState.REJECTED), listOf(TransferEffect.CancelOfferTimer, reply(t, OfferDecision.REJECT)))
        } else {
            none(t)
        }

    private fun onOfferTimedOut(t: TransferTask): Step<TransferTask, TransferEffect> = when {
        t.direction == Direction.SEND && t.state == TaskState.OFFERING -> Step(t.copy(state = TaskState.NO_RESPONSE), emptyList())
        t.direction == Direction.RECEIVE && t.state == TaskState.AWAITING_USER -> Step(t.copy(state = TaskState.NO_RESPONSE), listOf(reply(t, OfferDecision.TIMEOUT)))
        else -> none(t)
    }

    // ---- 发送方 ----

    private fun onSourceReadFailed(t: TransferTask, e: TransferEvent.SourceReadFailed): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0 || t.files[pos].index != e.index) return none(t)
        val next = t.withFile(pos, t.files[pos].copy(state = FileState.READ_FAILED))
        return startNextSender(next, listOf(TransferEffect.Send(FileReadFailed(t.transferId, e.index.toUInt()))))
    }

    private fun onFileEndSent(t: TransferTask, e: TransferEvent.FileEndSent): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0 || t.files[pos].index != e.index || t.files[pos].state != FileState.TRANSFERRING) return none(t)
        return Step(t.withFile(pos, t.files[pos].copy(state = FileState.VERIFYING, sha256 = e.sha256)), emptyList())
    }

    private fun onAck(t: TransferTask, e: TransferEvent.AckReceived): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0 || t.files[pos].index != e.index) return none(t)
        val f = t.files[pos]
        val p = e.persisted.coerceIn(f.persisted, f.size)
        return if (p == f.persisted) none(t) else Step(t.withFile(pos, f.copy(persisted = p)), emptyList())
    }

    private fun onResult(t: TransferTask, e: TransferEvent.ResultReceived): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0 || t.files[pos].index != e.index) return none(t)
        val f = t.files[pos]
        return when (e.status) {
            FileResultStatus.COMPLETED -> startNextSender(t.withFile(pos, f.copy(state = FileState.COMPLETED, persisted = f.size)), emptyList())
            // 已接收字节数超过 size 时接收方可能在 FILE_END 之前就判定失败：停止当前文件，继续下一个
            FileResultStatus.CHECKSUM_FAILED -> startNextSender(
                t.withFile(pos, f.copy(state = FileState.CHECKSUM_FAILED)),
                if (f.state == FileState.TRANSFERRING) listOf(TransferEffect.StopSending) else emptyList(),
            )
            FileResultStatus.WRITE_FAILED -> Step(
                interrupted(t.withFile(pos, f.copy(state = FileState.WRITE_FAILED)), InterruptReason.WRITE_FAILED, e.detail),
                listOf(TransferEffect.StopSending),
            )
        }
    }

    // ---- 接收方 ----

    private fun violation(t: TransferTask) = Step<TransferTask, TransferEffect>(interrupted(t, InterruptReason.PROTOCOL_ERROR), listOf(TransferEffect.ProtocolViolation))

    /**
     * 对方消息针对的文件：是当前文件 → 位置；是已经终态的文件（例如判定 CHECKSUM_FAILED 后仍在途的数据）→ null 表示忽略；
     * 是更靠后的文件或不存在 → -1 表示违反顺序。
     */
    private fun targetPos(t: TransferTask, index: Int): Int? {
        val pos = posOf(t, index)
        if (pos < 0) return -1
        if (t.files[pos].state.isTerminal) return null
        return if (pos == currentPos(t)) pos else -1
    }

    private fun result(t: TransferTask, index: Int, status: FileResultStatus, detail: String? = null) =
        TransferEffect.Send(FileResult(t.transferId, index.toUInt(), status, detail))

    private fun onChunk(t: TransferTask, e: TransferEvent.ChunkWritten): Step<TransferTask, TransferEffect> {
        val pos = targetPos(t, e.index) ?: return none(t)
        if (pos < 0) return violation(t)
        val f = t.files[pos]
        if (f.state == FileState.VERIFYING) return violation(t)
        if (e.persisted > f.size) {
            // 已接收字节数超过 size：立即判定校验失败（17.2）
            val failed = t.withFile(pos, f.copy(state = FileState.CHECKSUM_FAILED))
            return afterReceiverFile(failed, listOf(TransferEffect.DeleteTemp(f.index), result(t, f.index, FileResultStatus.CHECKSUM_FAILED)))
        }
        val updated = f.copy(
            state = FileState.TRANSFERRING,
            persisted = maxOf(f.persisted, e.persisted),
            startedAt = f.startedAt ?: e.localNow,
            lastWriteAtUtcMs = e.nowWall,
        )
        return Step(t.withFile(pos, updated), emptyList())
    }

    private fun onEnd(t: TransferTask, e: TransferEvent.EndReceived): Step<TransferTask, TransferEffect> {
        val pos = targetPos(t, e.index) ?: return none(t)
        if (pos < 0) return violation(t)
        val f = t.files[pos]
        if (f.state == FileState.VERIFYING) return violation(t)
        // 0 字节文件（以及续传时 start = size 的文件）从 WAITING 直接进入 VERIFYING
        val updated = f.copy(state = FileState.VERIFYING, sha256 = e.sha256, startedAt = f.startedAt ?: e.localNow)
        return Step(t.withFile(pos, updated), listOf(TransferEffect.Verify(f.index, e.sha256)))
    }

    private fun onVerifyDone(t: TransferTask, e: TransferEvent.VerifyDone): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0 || t.files[pos].index != e.index || t.files[pos].state != FileState.VERIFYING) return none(t)
        val f = t.files[pos]
        return when (val o = e.outcome) {
            is VerifyOutcome.Committed -> afterReceiverFile(
                t.withFile(pos, f.copy(state = FileState.COMPLETED, persisted = f.size, finalName = o.finalName, fullPath = o.fullPath, storedRef = o.storedRef)),
                listOf(result(t, f.index, FileResultStatus.COMPLETED)),
            )
            VerifyOutcome.ChecksumMismatch -> afterReceiverFile(
                t.withFile(pos, f.copy(state = FileState.CHECKSUM_FAILED)),
                listOf(TransferEffect.DeleteTemp(f.index), result(t, f.index, FileResultStatus.CHECKSUM_FAILED)),
            )
            is VerifyOutcome.CommitFailed -> Step(
                interrupted(t.withFile(pos, f.copy(state = FileState.WRITE_FAILED)), InterruptReason.WRITE_FAILED, o.detail),
                listOf(result(t, f.index, FileResultStatus.WRITE_FAILED, o.detail)),
            )
        }
    }

    private fun onWriteFailed(t: TransferTask, e: TransferEvent.WriteFailed): Step<TransferTask, TransferEffect> {
        val pos = currentPos(t)
        if (pos < 0 || t.files[pos].index != e.index) return none(t)
        val f = t.files[pos]
        return Step(
            interrupted(t.withFile(pos, f.copy(state = FileState.WRITE_FAILED)), InterruptReason.WRITE_FAILED, e.detail),
            listOf(result(t, f.index, FileResultStatus.WRITE_FAILED, e.detail)),
        )
    }

    private fun onPeerReadFailed(t: TransferTask, e: TransferEvent.PeerReadFailed): Step<TransferTask, TransferEffect> {
        val pos = targetPos(t, e.index) ?: return none(t)
        if (pos < 0) return violation(t)
        val f = t.files[pos]
        return afterReceiverFile(t.withFile(pos, f.copy(state = FileState.READ_FAILED)), listOf(TransferEffect.DeleteTemp(f.index)))
    }

    // ---- 双方 ----

    private fun onDisconnected(t: TransferTask, e: TransferEvent.Disconnected): Step<TransferTask, TransferEffect> = when (t.state) {
        TaskState.OFFERING, TaskState.AWAITING_USER -> Step(t.copy(state = TaskState.CONNECTION_LOST), listOf(TransferEffect.CancelOfferTimer))
        TaskState.RUNNING -> Step(interrupted(t, e.reason, e.detail), if (t.direction == Direction.SEND) listOf(TransferEffect.StopSending) else emptyList())
        TaskState.RESUMING -> Step(interrupted(t, e.reason, e.detail), listOf(TransferEffect.AbortResume))
        else -> none(t)
    }

    /** 接收方：删除全部未完成文件的临时文件，保留已完成的文件（18.3）。 */
    private fun deleteUnfinishedTemps(t: TransferTask): List<TransferEffect> =
        if (t.direction == Direction.RECEIVE) t.files.filter { !it.state.isTerminal }.map { TransferEffect.DeleteTemp(it.index) } else emptyList()

    private fun cancelled(t: TransferTask, state: TaskState) = t.copy(state = state, interrupt = null)

    private fun onLocalCancel(t: TransferTask, e: TransferEvent.LocalCancel): Step<TransferTask, TransferEffect> {
        val notify = TransferEffect.Send(TransferCancel(t.transferId, e.reason))
        val stop = if (t.direction == Direction.SEND) listOf(TransferEffect.StopSending) else emptyList()
        return when (t.state) {
            TaskState.OFFERING -> Step(cancelled(t, TaskState.CANCELLED), listOf(TransferEffect.CancelOfferTimer, notify))
            // 接收方尚未响应：按拒绝处理（20.5、C4.5）
            TaskState.AWAITING_USER -> Step(cancelled(t, TaskState.REJECTED), listOf(TransferEffect.CancelOfferTimer, reply(t, OfferDecision.REJECT)))
            TaskState.RUNNING -> Step(cancelled(t, TaskState.CANCELLED), stop + notify + deleteUnfinishedTemps(t))
            TaskState.INTERRUPTED -> Step(cancelled(t, TaskState.CANCELLED), listOf<TransferEffect>(TransferEffect.AddPendingCancel(e.reason)) + deleteUnfinishedTemps(t))
            TaskState.RESUMING -> {
                val deliver = if (e.sessionActive) listOf(notify) else listOf(TransferEffect.AddPendingCancel(e.reason))
                Step(cancelled(t, TaskState.CANCELLED), listOf<TransferEffect>(TransferEffect.AbortResume) + stop + deliver + deleteUnfinishedTemps(t))
            }
            else -> none(t)
        }
    }

    private fun onPeerCancelled(t: TransferTask): Step<TransferTask, TransferEffect> {
        val stop = if (t.direction == Direction.SEND && t.state == TaskState.RUNNING) listOf(TransferEffect.StopSending) else emptyList()
        val extra = when (t.state) {
            TaskState.OFFERING, TaskState.AWAITING_USER -> listOf(TransferEffect.CancelOfferTimer)
            TaskState.RESUMING -> listOf(TransferEffect.AbortResume)
            else -> emptyList()
        }
        // Offer 阶段还没有临时文件
        val deletes = if (t.state == TaskState.OFFERING || t.state == TaskState.AWAITING_USER) emptyList() else deleteUnfinishedTemps(t)
        return Step(cancelled(t, TaskState.PEER_CANCELLED), extra + stop + deletes)
    }

    private fun onResumePlanned(t: TransferTask, e: TransferEvent.ResumePlanned): Step<TransferTask, TransferEffect> {
        if (t.state != TaskState.RESUMING) return none(t)
        val byIndex = e.plan.associateBy { it.index }
        val effects = mutableListOf<TransferEffect>()
        val files = t.files.map { f ->
            val p = byIndex[f.index] ?: return@map f
            when (p.action) {
                ResumeAction.SKIP -> if (f.state.isTerminal) f else f.copy(state = FileState.COMPLETED, persisted = f.size)
                ResumeAction.CONTINUE -> if (f.state.isTerminal) f else f.copy(state = FileState.WAITING, persisted = minOf(p.start, f.size))
                ResumeAction.RESTART -> {
                    if (t.direction == Direction.RECEIVE) effects += TransferEffect.DeleteTemp(f.index)
                    if (f.state.isTerminal) {
                        f
                    } else {
                        f.copy(state = FileState.WAITING, persisted = 0, size = p.size ?: f.size, mtime = if (p.size != null) p.mtime else f.mtime, sha256 = null)
                    }
                }
            }
        }
        val running = t.copy(state = TaskState.RUNNING, interrupt = null, files = files)
        return if (t.direction == Direction.SEND) {
            effects += TransferEffect.Send(ResumePlan(t.transferId, e.plan.map { it.toWire() }))
            startNextSender(running, effects)
        } else {
            afterReceiverFile(running, effects)
        }
    }

    private fun onRestored(t: TransferTask): Step<TransferTask, TransferEffect> = when (t.state) {
        TaskState.RUNNING, TaskState.RESUMING -> Step(interrupted(t, InterruptReason.APP_KILLED), emptyList())
        // 只有已被接受的任务才会进入“已中断”（C1.13）
        TaskState.OFFERING, TaskState.AWAITING_USER -> Step(t.copy(state = TaskState.CONNECTION_LOST), emptyList())
        else -> none(t)
    }
}
