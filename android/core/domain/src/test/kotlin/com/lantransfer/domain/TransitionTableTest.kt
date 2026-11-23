package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.FileReadFailed
import com.lantransfer.protocol.FileResult
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
import com.lantransfer.protocol.QrPairRequest
import com.lantransfer.protocol.QrPairResult
import com.lantransfer.protocol.QrPairResultCode
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumeFilePlan
import com.lantransfer.protocol.ResumeFileState
import com.lantransfer.protocol.ResumeFileStatus
import com.lantransfer.protocol.ResumePlan
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.VerificationCode
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.collections.shouldContain
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf

// 状态迁移表驱动测试（任务 12.7）：M3.1–M3.4 中每条迁移至少 1 个示例。

// ---- 配对流程的固定数据 ----

private val pfI = peerInfo(1)
private val pfR = peerInfo(2)
private val pfConn = ConnId(7)
private val pfNI = bytes32(40)
private val pfNR = bytes32(80)
private val pfCommit = Bytes.of(VerificationCode.commitment(pfI.publicKey.toByteArray(), pfR.publicKey.toByteArray(), pfNI.toByteArray()))
private val pfSas = VerificationCode.sas(pfI.publicKey.toByteArray(), pfR.publicKey.toByteArray(), pfNI.toByteArray(), pfNR.toByteArray())

private fun pr(s: PairingState, e: PairingEvent) = PairingReducer.reduce(s, e)

private fun fin(peer: PeerInfo, role: PairingRole, result: PairingResult) = PairingEffect.Finished(PairingDone(peer.deviceId, role, result))

private fun msg(m: com.lantransfer.protocol.ProtocolMessage, now: Long) = PairingEvent.MessageReceived(pfConn, m, now)

/** 发起方：t = 1 000 ms 时已发出 PAIR_REQUEST，截止 61 000 ms。 */
private fun initiatorAwaiting(): PairingState {
    val connecting = pr(PairingState(), PairingEvent.StartPair(pfR.deviceId, 0)).state
    return pr(connecting, PairingEvent.Connected(pfConn, pfR, pfI.publicKey, pfNI, 1_000)).state
}

private fun initiatorSas() = pr(initiatorAwaiting(), msg(PairNonce(pfNR), 2_000)).state

private fun initiatorSaving() = pr(initiatorSas(), msg(PairDecision(PairDecisionCode.ACCEPTED), 3_000)).state

/** 接收方：t = 1 000 ms 时收到 PAIR_REQUEST，截止 61 000 ms。 */
private fun receiverShowing() = pr(PairingState(), PairingEvent.PairRequestReceived(pfConn, pfI, pfR.publicKey, pfCommit, pfNR, 1_000)).state

private fun receiverRevealed() = pr(receiverShowing(), msg(PairReveal(pfNI), 2_000)).state

/** 接收方：t = 5 000 ms 时点击“接受”，等待 PAIR_COMPLETE 至 20 000 ms。 */
private fun receiverCompleting() = pr(receiverRevealed(), PairingEvent.UserDecision(true, 5_000)).state

private fun receiverSaving() = pr(receiverCompleting(), msg(PairComplete, 6_000)).state

// ---- 传输任务的辅助函数 ----

private val tid = fixtureTransferId
private val sha = bytes32(3)

private fun tr(t: TransferTask, e: TransferEvent) = TransferReducer.reduce(t, e)

private fun runningSender(sizes: List<Long>) = tr(transferTask(Direction.SEND, TaskState.OFFERING, sizes), TransferEvent.OfferReplied(OfferDecision.ACCEPT)).state

private fun runningReceiver(sizes: List<Long>) = transferTask(Direction.RECEIVE, TaskState.RUNNING, sizes)

private fun fileResult(i: Int, s: FileResultStatus, detail: String? = null) = TransferEffect.Send(FileResult(tid, i.toUInt(), s, detail))

private fun offerReply(d: OfferDecision) = TransferEffect.Send(FileOfferReply(tid, d))

private fun cancelMsg(reason: TransferCancelReason = TransferCancelReason.USER) = TransferEffect.Send(TransferCancel(tid, reason))

private fun chunk(i: Int, persisted: Long) = TransferEvent.ChunkWritten(i, persisted, fixtureLocalNow, 1_700_000_000_000)

private fun end(i: Int) = TransferEvent.EndReceived(i, sha, fixtureLocalNow)

private fun committed(name: String) = VerifyOutcome.Committed(name, "D:\\接收\\$name", StoredFileRef("D:\\接收\\$name"))

class TransitionTableTest : FunSpec({
    context("M3.1 TextStatus") {
        val key = TextKey(deviceIdOf(1), MessageId(Bytes16.of(ByteArray(16) { 3 })))
        val body = Bytes.of("你好".encodeToByteArray())
        val sentAt = 1_700_000_000_000L
        val transmit = TextEffect.Transmit(key.peer, Text(key.messageId, sentAt, body))
        val sending = TextReducer.reduce(TextState(), TextEvent.Send(key, body, sentAt, 0)).state
        val failed = TextReducer.reduce(sending, TextEvent.Tick(10_000)).state
        val delivered = TextReducer.reduce(sending, TextEvent.AckReceived(key)).state
        val record = TextRecord(1, key.peer, "对方", key.messageId, Direction.SEND, body, sentAt, RecordTime(sentAt, 480), TextStatus.SENDING)
        fun item(s: TextState) = s.outgoing.getValue(key)

        test("— + 开始发送 → SENDING，从点击起计时 10 s（11.11）") {
            val st = TextReducer.reduce(TextState(), TextEvent.Send(key, body, sentAt, 0))
            st.effects shouldBe listOf(transmit, TextEffect.ArmTimer(10_000))
            item(st.state).status shouldBe TextStatus.SENDING
            item(st.state).deadline shouldBe 10_000L
            item(st.state).showRetry shouldBe false
        }

        test("SENDING + TEXT_ACK → DELIVERED（11.5）") {
            val st = TextReducer.reduce(sending, TextEvent.AckReceived(key))
            st.effects shouldBe listOf(TextEffect.SaveStatus(key, TextStatus.DELIVERED))
            item(st.state).status shouldBe TextStatus.DELIVERED
        }

        test("SENDING + 10 s 到期 → FAILED，显示“重试”（11.6）") {
            TextReducer.reduce(sending, TextEvent.Tick(9_999)) shouldBe Step(sending, emptyList())
            TextReducer.reduce(sending, TextEvent.Tick(10_000)).effects shouldBe listOf(TextEffect.SaveStatus(key, TextStatus.FAILED))
            item(failed).status shouldBe TextStatus.FAILED
            item(failed).deadline shouldBe null
            item(failed).showRetry shouldBe true
        }

        test("SENDING + TEXT_CORRUPT → FAILED（12.12）") {
            val st = TextReducer.reduce(sending, TextEvent.CorruptReceived(key))
            st.effects shouldBe listOf(TextEffect.SaveStatus(key, TextStatus.FAILED))
            item(st.state).showRetry shouldBe true
        }

        test("FAILED + 重试 → SENDING，重新计时 10 s，消息 ID 和文本不变（11.7）") {
            val st = TextReducer.reduce(failed, TextEvent.Retry(key, 20_000))
            st.effects shouldBe listOf(TextEffect.SaveStatus(key, TextStatus.SENDING), transmit, TextEffect.ArmTimer(30_000))
            item(st.state).status shouldBe TextStatus.SENDING
            item(st.state).deadline shouldBe 30_000L
        }

        test("FAILED + 迟到的 TEXT_ACK → DELIVERED（11.5）") {
            val st = TextReducer.reduce(failed, TextEvent.AckReceived(key))
            st.effects shouldBe listOf(TextEffect.SaveStatus(key, TextStatus.DELIVERED))
            item(st.state).status shouldBe TextStatus.DELIVERED
        }

        test("SENDING + 应用重启（计时已丢失）→ FAILED（11.6）") {
            val st = TextReducer.reduce(TextState(), TextEvent.Restored(listOf(record)))
            st.effects shouldBe listOf(TextEffect.SaveStatus(key, TextStatus.FAILED))
            item(st.state).status shouldBe TextStatus.FAILED
            item(st.state).showRetry shouldBe true
            val kept = TextReducer.reduce(TextState(), TextEvent.Restored(listOf(record.copy(status = TextStatus.DELIVERED))))
            kept.effects shouldBe emptyList()
            item(kept.state).status shouldBe TextStatus.DELIVERED
        }

        test("DELIVERED、RECEIVED + 任何事件 → 不变") {
            val events = listOf(
                TextEvent.AckReceived(key), TextEvent.CorruptReceived(key), TextEvent.Retry(key, 50_000),
                TextEvent.Tick(1_000_000), TextEvent.Send(key, body, sentAt, 60_000),
            )
            for (e in events) TextReducer.reduce(delivered, e) shouldBe Step(delivered, emptyList())
            // 接收方记录（RECEIVED）不进入发送状态机，收到新消息时的 RECEIVED 由 TextService 写入（11.4）
            val receivedRecord = record.copy(direction = Direction.RECEIVE, status = TextStatus.RECEIVED)
            TextReducer.reduce(TextState(), TextEvent.Restored(listOf(receivedRecord))) shouldBe Step(TextState(), emptyList())
        }

        test("记录删除后，迟到的应答不再影响任何记录（20.4、20.5）") {
            val forgotten = TextReducer.reduce(sending, TextEvent.Forget(setOf(key))).state
            forgotten.outgoing shouldBe emptyMap()
            TextReducer.reduce(forgotten, TextEvent.AckReceived(key)) shouldBe Step(forgotten, emptyList())
            TextReducer.reduce(forgotten, TextEvent.Retry(key, 1)) shouldBe Step(forgotten, emptyList())
        }
    }

    context("M3.2 TaskState") {
        val offering = transferTask(Direction.SEND, TaskState.OFFERING, listOf(100, 200))
        val awaiting = transferTask(Direction.RECEIVE, TaskState.AWAITING_USER, listOf(100, 200))

        test("OFFERING + ACCEPT → RUNNING，开始第 1 个文件（15.6、14.6）") {
            val st = tr(offering, TransferEvent.OfferReplied(OfferDecision.ACCEPT))
            st.state.state shouldBe TaskState.RUNNING
            st.state.files.map { it.state } shouldBe listOf(FileState.TRANSFERRING, FileState.WAITING)
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer, TransferEffect.StartFile(0, 0))
        }

        test("OFFERING + 拒绝、超时、空间不足、无法保存、无效 → Offer 阶段终态（15.3–15.7、C1.9）") {
            val table = listOf(
                OfferDecision.REJECT to TaskState.REJECTED,
                OfferDecision.TIMEOUT to TaskState.NO_RESPONSE,
                OfferDecision.NO_SPACE to TaskState.NO_SPACE,
                OfferDecision.CANNOT_SAVE to TaskState.CANNOT_SAVE,
                OfferDecision.INVALID_OFFER to TaskState.OFFER_INVALID,
            )
            for ((decision, endState) in table) {
                val st = tr(offering, TransferEvent.OfferReplied(decision))
                st.state.state shouldBe endState
                st.effects shouldBe listOf(TransferEffect.CancelOfferTimer)
            }
        }

        test("OFFERING + 发送方 65 s 保底计时到期 → NO_RESPONSE（15.4）") {
            tr(offering, TransferEvent.OfferTimedOut) shouldBe Step(offering.copy(state = TaskState.NO_RESPONSE), emptyList())
        }

        test("OFFERING + 断开 → CONNECTION_LOST（15.8）") {
            val st = tr(offering, TransferEvent.Disconnected(InterruptReason.PEER_CLOSED))
            st.state.state shouldBe TaskState.CONNECTION_LOST
            st.state.interrupt shouldBe null
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer)
        }

        test("OFFERING + 本机取消 → CANCELLED（18.2）") {
            val st = tr(offering, TransferEvent.LocalCancel(true))
            st.state.state shouldBe TaskState.CANCELLED
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer, cancelMsg())
        }

        test("AWAITING_USER + 接收 → RUNNING，固定接收目录（15.6、16.3）") {
            val dir = DirRef.Path("D:\\接收")
            val st = tr(awaiting, TransferEvent.UserAccepted(dir, true))
            st.state.state shouldBe TaskState.RUNNING
            st.state.receiveDir shouldBe dir
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer, offerReply(OfferDecision.ACCEPT))
        }

        test("AWAITING_USER + 接收时目录不可用 → CANNOT_SAVE（16.15）") {
            val st = tr(awaiting, TransferEvent.UserAccepted(DirRef.Default, false))
            st.state.state shouldBe TaskState.CANNOT_SAVE
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer, offerReply(OfferDecision.CANNOT_SAVE))
        }

        test("AWAITING_USER + 拒绝 → REJECTED（15.3）") {
            val st = tr(awaiting, TransferEvent.UserRejected)
            st.state.state shouldBe TaskState.REJECTED
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer, offerReply(OfferDecision.REJECT))
        }

        test("AWAITING_USER + 60 s 未响应 → NO_RESPONSE（15.4）") {
            val st = tr(awaiting, TransferEvent.OfferTimedOut)
            st.state.state shouldBe TaskState.NO_RESPONSE
            st.effects shouldBe listOf(offerReply(OfferDecision.TIMEOUT))
        }

        test("AWAITING_USER + 断开 → CONNECTION_LOST（15.8）") {
            val st = tr(awaiting, TransferEvent.Disconnected(InterruptReason.LOCAL_NETWORK_LOST))
            st.state.state shouldBe TaskState.CONNECTION_LOST
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer)
        }

        test("AWAITING_USER + 发送方取消 → PEER_CANCELLED（15.8）") {
            val st = tr(awaiting, TransferEvent.PeerCancelled(TransferCancelReason.USER))
            st.state.state shouldBe TaskState.PEER_CANCELLED
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer)
        }

        test("AWAITING_USER + 放弃（清空记录）→ REJECTED（20.5）") {
            val st = tr(awaiting, TransferEvent.LocalCancel(true))
            st.state.state shouldBe TaskState.REJECTED
            st.effects shouldBe listOf(TransferEffect.CancelOfferTimer, offerReply(OfferDecision.REJECT))
        }

        test("RUNNING + 全部文件进入终态 → COMPLETED（18.4）") {
            var sender = runningSender(listOf(100))
            sender = tr(sender, TransferEvent.FileEndSent(0, sha)).state
            sender = tr(sender, TransferEvent.AckReceived(0, 100)).state
            val st = tr(sender, TransferEvent.ResultReceived(0, FileResultStatus.COMPLETED, null))
            st.state.state shouldBe TaskState.COMPLETED
            st.state.files.single().state shouldBe FileState.COMPLETED
            st.effects shouldBe emptyList()

            var receiver = runningReceiver(listOf(10))
            receiver = tr(receiver, chunk(0, 10)).state
            receiver = tr(receiver, end(0)).state
            val done = tr(receiver, TransferEvent.VerifyDone(0, committed("a.txt")))
            done.state.state shouldBe TaskState.COMPLETED
            done.effects shouldBe listOf(fileResult(0, FileResultStatus.COMPLETED))
        }

        test("RUNNING + 断开 → INTERRUPTED（17.3、18.6、18.8）") {
            val st = tr(runningSender(listOf(100)), TransferEvent.Disconnected(InterruptReason.PEER_UNRESPONSIVE))
            st.state.state shouldBe TaskState.INTERRUPTED
            st.state.interrupt shouldBe InterruptInfo(InterruptReason.PEER_UNRESPONSIVE)
            st.effects shouldBe listOf(TransferEffect.StopSending)
            tr(runningReceiver(listOf(100)), TransferEvent.Disconnected(InterruptReason.PROTOCOL_ERROR)).state.interrupt shouldBe
                InterruptInfo(InterruptReason.PROTOCOL_ERROR)
        }

        test("RUNNING + 写入失败 → INTERRUPTED，附带原因（17.6）") {
            val sender = tr(runningSender(listOf(100)), TransferEvent.ResultReceived(0, FileResultStatus.WRITE_FAILED, "存储空间不足"))
            sender.state.state shouldBe TaskState.INTERRUPTED
            sender.state.interrupt shouldBe InterruptInfo(InterruptReason.WRITE_FAILED, "存储空间不足")
            sender.state.files.single().state shouldBe FileState.WRITE_FAILED
            sender.effects shouldBe listOf(TransferEffect.StopSending)

            val receiving = tr(runningReceiver(listOf(100)), chunk(0, 40)).state
            val receiver = tr(receiving, TransferEvent.WriteFailed(0, "存储空间不足"))
            receiver.state.interrupt shouldBe InterruptInfo(InterruptReason.WRITE_FAILED, "存储空间不足")
            receiver.effects shouldBe listOf(fileResult(0, FileResultStatus.WRITE_FAILED, "存储空间不足"))
        }

        test("INTERRUPTED + 继续 → RESUMING；RESUMING + 10 s 内未建立 Session → INTERRUPTED（17.4、17.10）") {
            val interrupted = transferTask(Direction.SEND, TaskState.INTERRUPTED, listOf(100))
            val resuming = tr(interrupted, TransferEvent.ResumeRequested)
            resuming.state.state shouldBe TaskState.RESUMING
            resuming.state.interrupt shouldBe null
            resuming.effects shouldBe listOf(TransferEffect.StartResume)
            val failedResume = tr(resuming.state, TransferEvent.ResumeFailed(InterruptReason.PEER_UNRESPONSIVE))
            failedResume.state.state shouldBe TaskState.INTERRUPTED
            failedResume.state.interrupt shouldBe InterruptInfo(InterruptReason.PEER_UNRESPONSIVE)
            failedResume.effects shouldBe emptyList()
        }

        test("RESUMING + RESUME_PLAN 交换完成 → RUNNING（17.4）") {
            val resuming = tr(transferTask(Direction.SEND, TaskState.INTERRUPTED, listOf(100)), TransferEvent.ResumeRequested).state
            val st = tr(resuming, TransferEvent.ResumePlanned(listOf(ResumePlanEntry(0, ResumeAction.CONTINUE, 40))))
            st.state.state shouldBe TaskState.RUNNING
            st.state.files.single().state shouldBe FileState.TRANSFERRING
            st.state.files.single().persisted shouldBe 40L
            st.effects shouldBe listOf(
                TransferEffect.Send(ResumePlan(tid, listOf(ResumeFilePlan(0u, ResumeAction.CONTINUE, 40uL)))),
                TransferEffect.StartFile(0, 40),
            )
        }

        test("RUNNING + 本机取消 → CANCELLED，删除未完成文件的临时文件、保留已完成的文件（18.2、18.3）") {
            tr(runningSender(listOf(100)), TransferEvent.LocalCancel(true)).effects shouldBe listOf(TransferEffect.StopSending, cancelMsg())

            var receiver = runningReceiver(listOf(10, 20, 30))
            receiver = tr(receiver, chunk(0, 10)).state
            receiver = tr(receiver, end(0)).state
            receiver = tr(receiver, TransferEvent.VerifyDone(0, committed("a.txt"))).state
            receiver = tr(receiver, chunk(1, 5)).state
            val st = tr(receiver, TransferEvent.LocalCancel(true))
            st.state.state shouldBe TaskState.CANCELLED
            st.state.files shouldBe receiver.files
            st.state.files[0].state shouldBe FileState.COMPLETED
            st.effects shouldBe listOf(cancelMsg(), TransferEffect.DeleteTemp(1), TransferEffect.DeleteTemp(2))
        }

        test("INTERRUPTED + 本机取消 → CANCELLED，写入 pending_cancel（18.9）") {
            val st = tr(transferTask(Direction.RECEIVE, TaskState.INTERRUPTED, listOf(100, 200)), TransferEvent.LocalCancel(false))
            st.state.state shouldBe TaskState.CANCELLED
            st.state.interrupt shouldBe null
            st.effects shouldBe listOf(
                TransferEffect.AddPendingCancel(TransferCancelReason.USER),
                TransferEffect.DeleteTemp(0),
                TransferEffect.DeleteTemp(1),
            )
        }

        test("RUNNING + TRANSFER_CANCEL → PEER_CANCELLED（18.7）") {
            tr(runningSender(listOf(100)), TransferEvent.PeerCancelled(TransferCancelReason.USER)).let {
                it.state.state shouldBe TaskState.PEER_CANCELLED
                it.effects shouldBe listOf(TransferEffect.StopSending)
            }
            tr(runningReceiver(listOf(100)), TransferEvent.PeerCancelled(TransferCancelReason.USER)).effects shouldBe listOf(TransferEffect.DeleteTemp(0))
        }

        test("RESUMING + 对方回复 UNKNOWN_TRANSFER → PEER_CANCELLED（C1.13）") {
            val resuming = tr(transferTask(Direction.SEND, TaskState.INTERRUPTED, listOf(100)), TransferEvent.ResumeRequested).state
            val st = tr(resuming, TransferEvent.PeerCancelled(TransferCancelReason.UNKNOWN_TRANSFER))
            st.state.state shouldBe TaskState.PEER_CANCELLED
            st.effects shouldBe listOf(TransferEffect.AbortResume)
        }

        test("RESUMING + 本机取消 → CANCELLED：已有 Session 时直接通知，否则写入 pending_cancel（C4.5）") {
            val resuming = tr(transferTask(Direction.SEND, TaskState.INTERRUPTED, listOf(100)), TransferEvent.ResumeRequested).state
            tr(resuming, TransferEvent.LocalCancel(true)).effects shouldBe listOf(TransferEffect.AbortResume, TransferEffect.StopSending, cancelMsg())
            tr(resuming, TransferEvent.LocalCancel(false)).effects shouldBe
                listOf(TransferEffect.AbortResume, TransferEffect.StopSending, TransferEffect.AddPendingCancel(TransferCancelReason.USER))
        }

        test("解除配对：未结束的任务按本机取消处理并发送 TRANSFER_CANCEL(UNPAIRED)（Q7）") {
            val st = tr(runningSender(listOf(100)), TransferEvent.LocalCancel(true, TransferCancelReason.UNPAIRED))
            st.state.state shouldBe TaskState.CANCELLED
            st.effects shouldBe listOf(TransferEffect.StopSending, cancelMsg(TransferCancelReason.UNPAIRED))
        }

        test("启动恢复（17.9）") {
            tr(runningSender(listOf(100)), TransferEvent.Restored).state.let {
                it.state shouldBe TaskState.INTERRUPTED
                it.interrupt shouldBe InterruptInfo(InterruptReason.APP_KILLED)
            }
            val resuming = tr(transferTask(Direction.RECEIVE, TaskState.INTERRUPTED, listOf(100)), TransferEvent.ResumeRequested).state
            tr(resuming, TransferEvent.Restored).state.interrupt shouldBe InterruptInfo(InterruptReason.APP_KILLED)
            tr(offering, TransferEvent.Restored).state.state shouldBe TaskState.CONNECTION_LOST
            tr(awaiting, TransferEvent.Restored).state.state shouldBe TaskState.CONNECTION_LOST
            val interrupted = transferTask(Direction.SEND, TaskState.INTERRUPTED, listOf(100))
            tr(interrupted, TransferEvent.Restored) shouldBe Step(interrupted, emptyList()) // 保留原来的中断原因
            val cancelled = transferTask(Direction.SEND, TaskState.CANCELLED, listOf(100))
            tr(cancelled, TransferEvent.Restored) shouldBe Step(cancelled, emptyList()) // 被放弃的任务不会恢复为“已中断”
        }

        test("终态任务不再迁移") {
            val events = listOf(
                TransferEvent.LocalCancel(true), TransferEvent.PeerCancelled(TransferCancelReason.USER), TransferEvent.ResumeRequested,
                TransferEvent.Restored, TransferEvent.Disconnected(InterruptReason.PEER_CLOSED), TransferEvent.OfferReplied(OfferDecision.ACCEPT),
            )
            for (state in TaskState.entries.filter { it.isTerminal }) {
                for (direction in Direction.entries) {
                    val task = transferTask(direction, state, listOf(100))
                    for (e in events) tr(task, e) shouldBe Step(task, emptyList())
                }
            }
        }
    }

    context("M3.3 FileState") {
        val receiving = runningReceiver(listOf(100, 50))

        test("WAITING → TRANSFERRING：开始接收，记录最初的开始时刻（14.6、16.9）") {
            val st = tr(receiving, TransferEvent.ChunkWritten(0, 40, fixtureLocalNow, 111))
            val f = st.state.files[0]
            f.state shouldBe FileState.TRANSFERRING
            f.persisted shouldBe 40L
            f.startedAt shouldBe fixtureLocalNow
            f.lastWriteAtUtcMs shouldBe 111L
            st.effects shouldBe emptyList()
            val later = tr(st.state, TransferEvent.ChunkWritten(0, 80, fixtureLocalNow.plusMinutes(5), 222)).state.files[0]
            later.startedAt shouldBe fixtureLocalNow
            later.lastWriteAtUtcMs shouldBe 222L
        }

        test("WAITING → READ_FAILED：对方报告读取失败，删除临时文件并继续（14.7）") {
            val st = tr(receiving, TransferEvent.PeerReadFailed(0))
            st.state.state shouldBe TaskState.RUNNING
            st.state.files.map { it.state } shouldBe listOf(FileState.READ_FAILED, FileState.WAITING)
            st.effects shouldBe listOf(TransferEffect.DeleteTemp(0))
        }

        test("TRANSFERRING → READ_FAILED：发送方读取失败，通知对方并开始下一个文件（14.7）") {
            val st = tr(runningSender(listOf(100, 50)), TransferEvent.SourceReadFailed(0))
            st.state.files.map { it.state } shouldBe listOf(FileState.READ_FAILED, FileState.TRANSFERRING)
            st.effects shouldBe listOf(TransferEffect.Send(FileReadFailed(tid, 0u)), TransferEffect.StartFile(1, 0))
        }

        test("TRANSFERRING → VERIFYING：FILE_END（17.1）") {
            val full = tr(receiving, chunk(0, 100)).state
            val st = tr(full, end(0))
            st.state.files[0].state shouldBe FileState.VERIFYING
            st.state.files[0].sha256 shouldBe sha
            st.effects shouldBe listOf(TransferEffect.Verify(0, sha))

            val sent = tr(runningSender(listOf(100)), TransferEvent.FileEndSent(0, sha))
            sent.state.files.single().state shouldBe FileState.VERIFYING
            sent.effects shouldBe emptyList()
        }

        test("0 字节文件：WAITING → VERIFYING（C1.13）") {
            val st = tr(runningReceiver(listOf(0)), end(0))
            st.state.files.single().state shouldBe FileState.VERIFYING
            st.effects shouldBe listOf(TransferEffect.Verify(0, sha))
        }

        test("TRANSFERRING → CHECKSUM_FAILED：已接收字节数超过 size（17.2）") {
            val partial = tr(receiving, chunk(0, 40)).state
            val st = tr(partial, chunk(0, 101))
            st.state.state shouldBe TaskState.RUNNING
            st.state.files.map { it.state } shouldBe listOf(FileState.CHECKSUM_FAILED, FileState.WAITING)
            st.effects shouldBe listOf(TransferEffect.DeleteTemp(0), fileResult(0, FileResultStatus.CHECKSUM_FAILED))
            // 校验失败之后仍在途的数据被忽略
            tr(st.state, chunk(0, 50)) shouldBe Step(st.state, emptyList())
        }

        test("VERIFYING → COMPLETED：校验通过并提交（17.1、19.8）") {
            val verifying = tr(tr(receiving, chunk(0, 100)).state, end(0)).state
            val st = tr(verifying, TransferEvent.VerifyDone(0, committed("a.txt")))
            val f = st.state.files[0]
            f.state shouldBe FileState.COMPLETED
            f.finalName shouldBe "a.txt"
            f.fullPath shouldBe "D:\\接收\\a.txt"
            f.storedRef shouldBe StoredFileRef("D:\\接收\\a.txt")
            st.state.state shouldBe TaskState.RUNNING
            st.effects shouldBe listOf(fileResult(0, FileResultStatus.COMPLETED))
        }

        test("VERIFYING → CHECKSUM_FAILED：哈希不一致，继续其余文件（17.2）") {
            val verifying = tr(tr(receiving, chunk(0, 100)).state, end(0)).state
            val st = tr(verifying, TransferEvent.VerifyDone(0, VerifyOutcome.ChecksumMismatch))
            st.state.files.map { it.state } shouldBe listOf(FileState.CHECKSUM_FAILED, FileState.WAITING)
            st.state.state shouldBe TaskState.RUNNING
            st.effects shouldBe listOf(TransferEffect.DeleteTemp(0), fileResult(0, FileResultStatus.CHECKSUM_FAILED))

            val sender = tr(tr(runningSender(listOf(100, 50)), TransferEvent.FileEndSent(0, sha)).state, TransferEvent.ResultReceived(0, FileResultStatus.CHECKSUM_FAILED, null))
            sender.state.files.map { it.state } shouldBe listOf(FileState.CHECKSUM_FAILED, FileState.TRANSFERRING)
            sender.effects shouldBe listOf(TransferEffect.StartFile(1, 0))
        }

        test("VERIFYING → WRITE_FAILED：提交失败，任务中断（M3.3、17.6）") {
            val verifying = tr(tr(receiving, chunk(0, 100)).state, end(0)).state
            val st = tr(verifying, TransferEvent.VerifyDone(0, VerifyOutcome.CommitFailed("重命名失败")))
            st.state.files[0].state shouldBe FileState.WRITE_FAILED
            st.state.state shouldBe TaskState.INTERRUPTED
            st.state.interrupt shouldBe InterruptInfo(InterruptReason.WRITE_FAILED, "重命名失败")
            st.effects shouldBe listOf(fileResult(0, FileResultStatus.WRITE_FAILED, "重命名失败"))
        }

        test("WRITE_FAILED → TRANSFERRING：继续时 start = size，重发 FILE_END（M3.3、17.4）") {
            var t = runningSender(listOf(100))
            t = tr(t, TransferEvent.FileEndSent(0, sha)).state
            t = tr(t, TransferEvent.ResultReceived(0, FileResultStatus.WRITE_FAILED, "重命名失败")).state
            t = tr(t, TransferEvent.ResumeRequested).state
            val plan = ResumePlanner.plan(t, listOf(ResumeFileState(0u, ResumeFileStatus.PARTIAL, 100uL)), listOf(SourceStat(100, t.files[0].mtime)))
            plan shouldBe listOf(ResumePlanEntry(0, ResumeAction.CONTINUE, 100))
            val st = tr(t, TransferEvent.ResumePlanned(plan))
            st.state.files.single().state shouldBe FileState.TRANSFERRING
            st.state.files.single().persisted shouldBe 100L
            st.effects shouldContain TransferEffect.StartFile(0, 100)
        }

        test("续传 RESTART：源文件变化时丢弃临时文件、从第 0 字节重新传输（17.5）") {
            var t = tr(receiving, chunk(0, 40)).state
            t = tr(t, TransferEvent.Disconnected(InterruptReason.PEER_CLOSED)).state
            t = tr(t, TransferEvent.ResumeRequested).state
            val st = tr(t, TransferEvent.ResumePlanned(listOf(ResumePlanEntry(0, ResumeAction.RESTART, 0, 200, 9), ResumePlanEntry(1, ResumeAction.CONTINUE, 0))))
            val f = st.state.files[0]
            f.state shouldBe FileState.WAITING
            f.persisted shouldBe 0L
            f.size shouldBe 200L
            f.mtime shouldBe 9L
            st.state.state shouldBe TaskState.RUNNING
            st.effects shouldBe listOf(TransferEffect.DeleteTemp(0))
        }

        test("发送方的确认进度只增不减，且不超过 size（C1.13）") {
            val t = runningSender(listOf(100))
            val acked = tr(t, TransferEvent.AckReceived(0, 50)).state
            acked.files[0].persisted shouldBe 50L
            tr(acked, TransferEvent.AckReceived(0, 30)) shouldBe Step(acked, emptyList())
            tr(acked, TransferEvent.AckReceived(0, 500)).state.files[0].persisted shouldBe 100L
        }

        test("数据不按文件顺序到达 → 协议错误，任务中断（C1.13、22.6）") {
            val three = runningReceiver(listOf(100, 50, 70))
            for (e in listOf(chunk(1, 10), end(2), TransferEvent.PeerReadFailed(5))) {
                val st = tr(three, e)
                st.state.state shouldBe TaskState.INTERRUPTED
                st.state.interrupt shouldBe InterruptInfo(InterruptReason.PROTOCOL_ERROR)
                st.state.files shouldBe three.files
                st.effects shouldBe listOf(TransferEffect.ProtocolViolation)
            }
        }
    }

    context("M3.4 PairingState") {
        test("发起方：Idle → Connecting → AwaitingPeer → ShowingSas → 保存信任 → Done(SUCCESS)（7.1、7.2、7.4）") {
            var st = pr(PairingState(), PairingEvent.StartPair(pfR.deviceId, 0))
            st.state.session shouldBe PairingSession.Connecting(pfR.deviceId, null, 10_000)
            st.effects shouldBe listOf(PairingEffect.ArmTimer(10_000))
            st = pr(st.state, PairingEvent.Connected(pfConn, pfR, pfI.publicKey, pfNI, 1_000))
            st.state.session shouldBe PairingSession.AwaitingPeer(pfR.deviceId, pfConn, pfR, pfI.publicKey, pfNI, 61_000)
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, PairRequest(pfCommit)), PairingEffect.ArmTimer(61_000))
            st = pr(st.state, msg(PairNonce(pfNR), 2_000))
            st.state.session shouldBe PairingSession.ShowingSas(pfR.deviceId, pfConn, pfR, pfSas, 61_000)
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, PairReveal(pfNI)), PairingEffect.ShowSas(pfSas))
            st = pr(st.state, msg(PairDecision(PairDecisionCode.ACCEPTED), 3_000))
            st.state.session shouldBe PairingSession.Saving(pfR.deviceId, pfConn, pfR, PairingRole.INITIATOR, null)
            st.effects shouldBe listOf(PairingEffect.SaveTrust(pfR))
            st = pr(st.state, PairingEvent.TrustStored(pfR.deviceId, true))
            st.state.session shouldBe null
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, PairComplete), PairingEffect.PromoteSession(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.SUCCESS))
        }

        test("接收方：Idle → AwaitingUser → 显示 SAS → 接受（暂存）→ PAIR_COMPLETE 后提交 → Done(SUCCESS)（7.2–7.4）") {
            var st = pr(PairingState(), PairingEvent.PairRequestReceived(pfConn, pfI, pfR.publicKey, pfCommit, pfNR, 1_000))
            st.state.session shouldBe PairingSession.AwaitingUser(pfI.deviceId, pfConn, pfI, pfR.publicKey, pfCommit, pfNR, null, 61_000)
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, PairNonce(pfNR)), PairingEffect.ShowRequest(pfI), PairingEffect.ArmTimer(61_000))
            // 显示验证码之前不能接受
            pr(st.state, PairingEvent.UserDecision(true, 1_500)) shouldBe Step(st.state, emptyList())
            st = pr(st.state, msg(PairReveal(pfNI), 2_000))
            st.effects shouldBe listOf(PairingEffect.ShowSas(pfSas)) // 与发起方显示的验证码相同
            st = pr(st.state, PairingEvent.UserDecision(true, 5_000))
            st.state.session shouldBe PairingSession.AwaitingComplete(pfI.deviceId, pfConn, pfI, PairingRole.RECEIVER, null, 20_000)
            st.effects shouldBe listOf(
                PairingEffect.StageTrust(pfI),
                PairingEffect.Send(pfConn, PairDecision(PairDecisionCode.ACCEPTED)),
                PairingEffect.ArmTimer(20_000),
            )
            st = pr(st.state, msg(PairComplete, 6_000))
            st.state.session shouldBe PairingSession.Saving(pfI.deviceId, pfConn, pfI, PairingRole.RECEIVER, null)
            st.effects shouldBe listOf(PairingEffect.CommitTrust(pfI.deviceId))
            st = pr(st.state, PairingEvent.TrustStored(pfI.deviceId, true))
            st.state.session shouldBe null
            st.effects shouldBe listOf(PairingEffect.PromoteSession(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.SUCCESS))
        }

        test("扫码：扫描方 Connecting → 等待令牌校验 → 保存信任 → Done(SUCCESS)；令牌无效 → TOKEN_INVALID（8.4、8.9）") {
            val token = Bytes16.of(ByteArray(16) { 9 })
            var st = pr(PairingState(), PairingEvent.StartQrPair(pfR.deviceId, token, 0))
            st.state.session shouldBe PairingSession.Connecting(pfR.deviceId, token, 20_000)
            st.effects shouldBe listOf(PairingEffect.ArmTimer(20_000))
            st = pr(st.state, PairingEvent.Connected(pfConn, pfR, pfI.publicKey, pfNI, 4_000))
            st.state.session shouldBe PairingSession.AwaitingQrResult(pfR.deviceId, pfConn, pfR, 19_000)
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, QrPairRequest(token)), PairingEffect.ArmTimer(19_000))
            val awaitingResult = st.state
            pr(awaitingResult, msg(QrPairResult(QrPairResultCode.TOKEN_INVALID), 5_000)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.QR_SCANNER, PairingResult.TOKEN_INVALID))
            st = pr(awaitingResult, msg(QrPairResult(QrPairResultCode.OK), 5_000))
            st.state.session shouldBe PairingSession.Saving(pfR.deviceId, pfConn, pfR, PairingRole.QR_SCANNER, null)
            st.effects shouldBe listOf(PairingEffect.SaveTrust(pfR))
            st = pr(st.state, PairingEvent.TrustStored(pfR.deviceId, true))
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, PairComplete), PairingEffect.PromoteSession(pfConn), fin(pfR, PairingRole.QR_SCANNER, PairingResult.SUCCESS))
        }

        test("扫码：显示方令牌通过 → 暂存 → PAIR_COMPLETE 后提交并作废令牌；中断时归还令牌（8.4、8.6）") {
            val registry = PairingTokenRegistry { n -> ByteArray(n) { 1 } }
            val token = registry.issue(0)
            val lease = (registry.check(token.value.toByteArray(), 1) as TokenCheck.Ok).lease
            var st = pr(PairingState(), PairingEvent.QrRequestChecked(pfConn, pfI, lease, 1_000))
            st.state.session shouldBe PairingSession.AwaitingComplete(pfI.deviceId, pfConn, pfI, PairingRole.QR_DISPLAY, lease, 16_000)
            st.effects shouldBe listOf(PairingEffect.Send(pfConn, QrPairResult(QrPairResultCode.OK)), PairingEffect.StageTrust(pfI), PairingEffect.ArmTimer(16_000))
            val staged = st.state
            pr(staged, PairingEvent.Disconnected(pfConn, 2_000)).effects shouldBe
                listOf(PairingEffect.DiscardStagedTrust(pfI.deviceId), PairingEffect.ReleaseToken(lease), fin(pfI, PairingRole.QR_DISPLAY, PairingResult.DISCONNECTED))
            st = pr(staged, msg(PairComplete, 2_000))
            st.effects shouldBe listOf(PairingEffect.CommitTrust(pfI.deviceId))
            st = pr(st.state, PairingEvent.TrustStored(pfI.deviceId, true))
            st.effects shouldBe listOf(PairingEffect.MarkTokenUsed(lease), PairingEffect.PromoteSession(pfConn), fin(pfI, PairingRole.QR_DISPLAY, PairingResult.SUCCESS))

            // 令牌无效：回复 TOKEN_INVALID 并关闭，不进入配对（8.9）
            pr(PairingState(), PairingEvent.QrRequestChecked(pfConn, pfI, null, 1_000)) shouldBe
                Step(PairingState(), listOf(PairingEffect.Send(pfConn, QrPairResult(QrPairResultCode.TOKEN_INVALID)), PairingEffect.Close(pfConn)))
        }

        test("拒绝 → REJECTED；60 s 冷却期内同一设备的请求自动拒绝且不显示界面（7.6、7.8）") {
            val st = pr(receiverRevealed(), PairingEvent.UserDecision(false, 10_000))
            st.state.session shouldBe null
            st.state.rejectedAt shouldBe mapOf(pfI.deviceId to 10_000L)
            st.effects shouldBe listOf(
                PairingEffect.Send(pfConn, PairDecision(PairDecisionCode.REJECTED)),
                PairingEffect.Close(pfConn),
                fin(pfI, PairingRole.RECEIVER, PairingResult.REJECTED),
            )
            val conn2 = ConnId(8)
            pr(st.state, PairingEvent.PairRequestReceived(conn2, pfI, pfR.publicKey, pfCommit, pfNR, 69_999)).effects shouldBe listOf(
                PairingEffect.Send(conn2, PairDecision(PairDecisionCode.REJECTED)),
                PairingEffect.Close(conn2),
                PairingEffect.AutoRejected(pfI.deviceId, PairingResult.AUTO_REJECTED),
            )
            pr(st.state, PairingEvent.PairRequestReceived(conn2, pfI, pfR.publicKey, pfCommit, pfNR, 70_000)).state.session
                .shouldBeInstanceOf<PairingSession.AwaitingUser>()
            // 发起方：拒绝与自动拒绝都提示“对方拒绝了配对请求”
            pr(initiatorSas(), msg(PairDecision(PairDecisionCode.REJECTED), 4_000)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.REJECTED))
            pr(initiatorAwaiting(), msg(PairDecision(PairDecisionCode.REJECTED), 1_500)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.REJECTED))
        }

        test("正在显示一个请求时，其他设备的请求自动回复 BUSY（7.9）") {
            val other = peerInfo(3)
            val conn2 = ConnId(8)
            val showing = receiverShowing()
            pr(showing, PairingEvent.PairRequestReceived(conn2, other, pfR.publicKey, pfCommit, pfNR, 2_000)) shouldBe Step(
                showing,
                listOf(PairingEffect.Send(conn2, PairDecision(PairDecisionCode.BUSY)), PairingEffect.Close(conn2), PairingEffect.AutoRejected(other.deviceId, PairingResult.BUSY)),
            )
            pr(initiatorAwaiting(), msg(PairDecision(PairDecisionCode.BUSY), 1_500)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.BUSY))
        }

        test("60 s 超时：发起方发送 PAIR_CANCEL(TIMEOUT)，接收方关闭确认界面（7.7）") {
            val awaitingPeer = initiatorAwaiting()
            pr(awaitingPeer, PairingEvent.Tick(60_999)) shouldBe Step(awaitingPeer, emptyList())
            val timeout = listOf(PairingEffect.Send(pfConn, PairCancel(PairCancelReason.TIMEOUT)), PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.TIMEOUT))
            pr(awaitingPeer, PairingEvent.Tick(61_000)).effects shouldBe timeout
            pr(initiatorSas(), PairingEvent.Tick(61_000)).effects shouldBe timeout
            pr(initiatorSas(), msg(PairDecision(PairDecisionCode.TIMEOUT), 61_000)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.TIMEOUT))

            val revealed = receiverRevealed()
            pr(revealed, PairingEvent.Tick(60_999)) shouldBe Step(revealed, emptyList())
            pr(revealed, PairingEvent.Tick(61_000)).effects shouldBe listOf(
                PairingEffect.Send(pfConn, PairDecision(PairDecisionCode.TIMEOUT)),
                PairingEffect.DismissRequest,
                PairingEffect.Close(pfConn),
                fin(pfI, PairingRole.RECEIVER, PairingResult.TIMEOUT),
            )
            pr(revealed, msg(PairCancel(PairCancelReason.TIMEOUT), 61_000)).effects shouldBe
                listOf(PairingEffect.DismissRequest, PairingEffect.Close(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.TIMEOUT))
        }

        test("发起方取消 → CANCELLED，接收方关闭确认界面（7.10）") {
            val cancel = listOf(PairingEffect.Send(pfConn, PairCancel(PairCancelReason.USER)), PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.CANCELLED))
            pr(initiatorAwaiting(), PairingEvent.UserCancel(1_500)).effects shouldBe cancel
            pr(initiatorSas(), PairingEvent.UserCancel(2_500)).effects shouldBe cancel
            val connecting = pr(PairingState(), PairingEvent.StartPair(pfR.deviceId, 0)).state
            pr(connecting, PairingEvent.UserCancel(10)).effects shouldBe listOf(PairingEffect.CancelConnect, fin(pfR, PairingRole.INITIATOR, PairingResult.CANCELLED))
            pr(receiverShowing(), msg(PairCancel(PairCancelReason.USER), 2_000)).effects shouldBe
                listOf(PairingEffect.DismissRequest, PairingEffect.Close(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.CANCELLED))
            pr(receiverCompleting(), msg(PairCancel(PairCancelReason.USER), 6_000)).effects shouldBe
                listOf(PairingEffect.DiscardStagedTrust(pfI.deviceId), PairingEffect.Close(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.CANCELLED))
        }

        test("揭示的随机数与承诺不符 → CODE_MISMATCH（C1.10）") {
            pr(receiverShowing(), msg(PairReveal(bytes32(41)), 2_000)).effects shouldBe
                listOf(PairingEffect.DismissRequest, PairingEffect.Close(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.CODE_MISMATCH))
        }

        test("连接中断 → DISCONNECTED，不保留信任（7.12）") {
            pr(receiverShowing(), PairingEvent.Disconnected(pfConn, 2_000)).effects shouldBe
                listOf(PairingEffect.DismissRequest, fin(pfI, PairingRole.RECEIVER, PairingResult.DISCONNECTED))
            pr(receiverCompleting(), PairingEvent.Disconnected(pfConn, 6_000)).effects shouldBe
                listOf(PairingEffect.DiscardStagedTrust(pfI.deviceId), fin(pfI, PairingRole.RECEIVER, PairingResult.DISCONNECTED))
            pr(receiverCompleting(), PairingEvent.Tick(20_000)).effects shouldBe
                listOf(PairingEffect.DiscardStagedTrust(pfI.deviceId), PairingEffect.Close(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.DISCONNECTED))
            pr(initiatorAwaiting(), PairingEvent.Disconnected(pfConn, 1_500)).effects shouldBe listOf(fin(pfR, PairingRole.INITIATOR, PairingResult.DISCONNECTED))
            pr(initiatorSaving(), PairingEvent.Disconnected(pfConn, 3_500)).effects shouldBe
                listOf(PairingEffect.DeleteTrust(pfR.deviceId), fin(pfR, PairingRole.INITIATOR, PairingResult.DISCONNECTED))
            // 接收方已收到 PAIR_COMPLETE：结局由提交结果决定
            val saving = receiverSaving()
            pr(saving, PairingEvent.Disconnected(pfConn, 7_000)) shouldBe Step(saving, emptyList())
        }

        test("10 s 内未建立连接 → CONNECT_FAILED（7.11）") {
            val connecting = pr(PairingState(), PairingEvent.StartPair(pfR.deviceId, 0)).state
            pr(connecting, PairingEvent.ConnectFailed(500)).effects shouldBe listOf(fin(pfR, PairingRole.INITIATOR, PairingResult.CONNECT_FAILED))
            pr(connecting, PairingEvent.Tick(9_999)) shouldBe Step(connecting, emptyList())
            pr(connecting, PairingEvent.Tick(10_000)).effects shouldBe listOf(PairingEffect.CancelConnect, fin(pfR, PairingRole.INITIATOR, PairingResult.CONNECT_FAILED))
        }

        test("保存或提交信任失败 → STORE_FAILED（设计 E 表）") {
            pr(initiatorSaving(), PairingEvent.TrustStored(pfR.deviceId, false)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.STORE_FAILED))
            pr(receiverSaving(), PairingEvent.TrustStored(pfI.deviceId, false)).effects shouldBe
                listOf(PairingEffect.DiscardStagedTrust(pfI.deviceId), PairingEffect.Close(pfConn), fin(pfI, PairingRole.RECEIVER, PairingResult.STORE_FAILED))
        }

        test("迟到或无关的消息被忽略；同一时刻只有 1 个配对（7.9、设计 E 表）") {
            val awaitingPeer = initiatorAwaiting()
            pr(awaitingPeer, PairingEvent.MessageReceived(ConnId(99), PairNonce(pfNR), 1_500)) shouldBe Step(awaitingPeer, emptyList())
            pr(PairingState(), msg(PairDecision(PairDecisionCode.ACCEPTED), 0)) shouldBe Step(PairingState(), emptyList())
            pr(awaitingPeer, PairingEvent.StartPair(peerInfo(3).deviceId, 1_500)) shouldBe Step(awaitingPeer, emptyList())
            pr(PairingState(), PairingEvent.Connected(pfConn, pfR, pfI.publicKey, pfNI, 0)).effects shouldBe listOf(PairingEffect.Close(pfConn))
            // 未揭示随机数前收到 ACCEPTED：按协议错误结束
            pr(awaitingPeer, msg(PairDecision(PairDecisionCode.ACCEPTED), 1_500)).effects shouldBe
                listOf(PairingEffect.Close(pfConn), fin(pfR, PairingRole.INITIATOR, PairingResult.DISCONNECTED))
        }
    }
})
