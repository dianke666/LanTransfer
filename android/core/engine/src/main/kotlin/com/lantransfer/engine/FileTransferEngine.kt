package com.lantransfer.engine

import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileNameSanitizer
import com.lantransfer.domain.FileState
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.InterruptReason
import com.lantransfer.domain.OfferPrecheck
import com.lantransfer.domain.PrecheckDecision
import com.lantransfer.domain.Progress
import com.lantransfer.domain.ProgressCalculator
import com.lantransfer.domain.ResumePlanEntry
import com.lantransfer.domain.ResumePlanner
import com.lantransfer.domain.Rng
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TempFileSelector
import com.lantransfer.domain.TempRef
import com.lantransfer.domain.TransferEffect
import com.lantransfer.domain.TransferEvent
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TransferReducer
import com.lantransfer.domain.TransferTask
import com.lantransfer.domain.VerifyOutcome
import com.lantransfer.engine.ports.AppLifecycle
import com.lantransfer.engine.ports.ChunkReader
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.CommitResult
import com.lantransfer.engine.ports.IoResult
import com.lantransfer.engine.ports.Notifier
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.ports.OutgoingTextCache
import com.lantransfer.engine.ports.ReadResult
import com.lantransfer.engine.ports.ReceiveStorage
import com.lantransfer.engine.ports.RequestKind
import com.lantransfer.engine.ports.SettingsStore
import com.lantransfer.engine.ports.SourceFiles
import com.lantransfer.engine.ports.SourceMeta
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.TaskStore
import com.lantransfer.engine.ports.TempWriter
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.FileAck
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.FileEnd
import com.lantransfer.protocol.FileEntry
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.FileReadFailed
import com.lantransfer.protocol.FileResult
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumeFileState
import com.lantransfer.protocol.ResumeFileStatus
import com.lantransfer.protocol.ResumePlan
import com.lantransfer.protocol.ResumeRequest
import com.lantransfer.protocol.ResumeState
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

// 文件传输引擎（设计 C4.5、C1.13，需求 14–18、25）：执行 TransferReducer 的 effects。
//
// - 一个 actor 协程持有全部任务（TransferTask + 运行期状态）；收到的消息由分发器经 [FileTransferEngine.onMessage] 交进来。
// - FileSender：每个发送任务一个读盘协程，把 FILE_CHUNK / FILE_END 放进容量为 2 的 ChunkSource（P2），保证未确认数据 ≤ 4 MiB。
//   FILE_END 与数据块走同一个 ChunkSource，保证它排在该文件的全部数据块之后。
// - FileReceiver：每个接收任务一个写盘协程，按到达顺序处理该任务的 FILE_CHUNK / FILE_END / FILE_READ_FAILED 与校验、提交、删除。
//   actor 在入队前检查 offset 连续性与 4 MiB 窗口（违反时按 PROTOCOL_ERROR 关闭连接，E3）；写盘结果按顺序回到 actor，
//   再交给 TransferReducer，因此 ChunkWritten 总是先于同一文件的 EndReceived。
// - 读写盘协程运行在 [io] 上（生产环境传 Dispatchers.IO，测试用调用方的调度器）。

class FileTransferEngine(
    private val sessions: SessionManager,
    private val storage: ReceiveStorage,
    private val sources: SourceFiles,
    private val store: TaskStore,
    private val trust: TrustStore,
    private val notifier: Notifier,
    private val settings: SettingsStore,
    private val lifecycle: AppLifecycle,
    private val textCache: OutgoingTextCache,
    private val clock: Clock,
    private val rng: Rng,
    private val scope: CoroutineScope,
    private val profile: FsProfile,
    private val io: CoroutineContext = EmptyCoroutineContext,
    private val timings: TransferTimings = TransferTimings(),
) {
    private val inbox = Channel<Cmd>(Channel.UNLIMITED)
    private val tasksFlow = MutableStateFlow<List<TransferTaskView>>(emptyList())
    private val unfinishedFlow = MutableStateFlow(0)
    private val noticeFlow = MutableSharedFlow<TransferNotice>(extraBufferCapacity = 64)

    // ---- 只在 actor 中访问 ----

    /** 按创建顺序。 */
    private val entries = LinkedHashMap<TransferId, Entry>()
    private var publishScheduled = false
    private var janitorStarted = false

    init {
        scope.launch { for (cmd in inbox) handle(cmd) }
        // 构造时就订阅（UNDISPATCHED）：EngineHost 随后开始监听，不能漏掉第一个 Session 的 Opened
        scope.launch(start = CoroutineStart.UNDISPATCHED) {
            sessions.events.collect { e ->
                when (e) {
                    is SessionEvent.Opened -> inbox.send(Cmd.SessionOpened(e.session))
                    is SessionEvent.Closed -> inbox.send(Cmd.SessionClosed(e.session, e.reason))
                }
            }
        }
    }

    val tasks: StateFlow<List<TransferTaskView>> get() = tasksFlow

    val notices: SharedFlow<TransferNotice> get() = noticeFlow

    /** OFFERING、AWAITING_USER、RUNNING、RESUMING、INTERRUPTED 的任务数（20.11）。 */
    fun unfinishedCount(): StateFlow<Int> = unfinishedFlow

    /**
     * 点击“发送”（14.4、14.10）：读取元数据，10 s 内建立 Session，然后创建任务并发出 FILE_OFFER。
     * 无法建立 Session 时不发送 Offer（调用方保留待发送列表）。
     */
    suspend fun send(peer: DeviceId, files: List<SourceRef>): SendOfferResult {
        if (files.isEmpty()) return SendOfferResult.Empty
        if (files.size > ProtocolConstants.MAX_FILES_PER_OFFER) return SendOfferResult.TooMany
        val metas = files.map { it to sources.describe(it) }
        val bad = metas.filter { (_, m) -> m == null || m.isDirectory || m.size > ProtocolConstants.MAX_FILE_SIZE }.map { it.first }
        if (bad.isNotEmpty()) return SendOfferResult.Unreadable(bad)
        val session = when (val r = sessions.open(Target.Device(peer))) {
            is OpenResult.Ok -> r.session
            is OpenResult.Failed -> return SendOfferResult.ConnectFailed(r.failure)
            is OpenResult.Pairing -> {
                r.connection.close()
                return SendOfferResult.ConnectFailed(OpenFailure.NotTrusted)
            }
        }
        val done = CompletableDeferred<TransferId>()
        inbox.send(Cmd.StartSend(session, metas.map { (ref, m) -> ref to m!! }, done))
        return SendOfferResult.Sent(done.await())
    }

    /** 接收方点击“接收”（15.6）：固定当前接收目录（16.3），写第一个字节之前创建目录（16.14）。 */
    fun accept(id: TransferId) {
        inbox.trySend(Cmd.Accept(id))
    }

    /** 接收方点击“拒绝”（15.3）。 */
    fun reject(id: TransferId) {
        inbox.trySend(Cmd.Reject(id))
    }

    /** 取消（18.2、18.3、18.9）。 */
    fun cancel(id: TransferId) {
        inbox.trySend(Cmd.Cancel(id))
    }

    /** “继续”（17.4、17.10）：10 s 内重新建立 Session 并交换 RESUME_STATE / RESUME_PLAN。 */
    suspend fun resume(id: TransferId): ResumeResult {
        val d = CompletableDeferred<ResumeResult>()
        inbox.send(Cmd.Resume(id, d))
        return d.await()
    }

    /** 启动恢复（17.9、M3.2）：未结束的任务 → “已中断” / “连接已断开”，随后运行一次 TempFileJanitor，之后每小时一次（17.7）。 */
    suspend fun restoreOnStartup() = request { Cmd.Restore(it) }

    /** 清空记录前放弃全部未结束任务（20.5）；返回后这些任务不再出现在 [tasks] 中。 */
    suspend fun abandonAll() = request { Cmd.AbandonAll(it) }

    /** 删除“已中断”任务的文件记录前取消整个任务（20.10）。 */
    suspend fun abandon(id: TransferId) = request { Cmd.Abandon(id, it) }

    /** 解除配对（Q7）：对该对端全部未结束的任务按本机取消处理，并发送 TRANSFER_CANCEL(UNPAIRED)。供 PairingService.unpair 使用。 */
    suspend fun cancelPeer(peer: DeviceId) = request { Cmd.CancelPeer(peer, it) }

    /** 分发器交来的应用消息；文件传输相关的消息返回 true。 */
    fun onMessage(session: Session, msg: ProtocolMessage): Boolean {
        when (msg) {
            is FileOffer, is FileOfferReply, is FileChunk, is FileEnd, is FileAck, is FileResult, is FileReadFailed,
            is TransferCancel, is ResumeRequest, is ResumeState, is ResumePlan,
            -> inbox.trySend(Cmd.Inbound(session, msg))
            else -> return false
        }
        return true
    }

    /** 立即运行一次 TempFileJanitor（测试与宿主使用）。 */
    suspend fun runJanitor() = request { Cmd.Janitor(it) }

    private suspend fun request(make: (CompletableDeferred<Unit>) -> Cmd) {
        val d = CompletableDeferred<Unit>()
        inbox.send(make(d))
        d.await()
    }

    // ---- 命令 ----

    private sealed interface Cmd {
        class StartSend(val session: Session, val files: List<Pair<SourceRef, SourceMeta>>, val done: CompletableDeferred<TransferId>) : Cmd

        class Accept(val id: TransferId) : Cmd

        class Reject(val id: TransferId) : Cmd

        class Cancel(val id: TransferId) : Cmd

        class Resume(val id: TransferId, val done: CompletableDeferred<ResumeResult>) : Cmd

        class ResumeOpened(val id: TransferId, val attempt: Any, val result: OpenResult, val done: CompletableDeferred<ResumeResult>?) : Cmd

        class ResumeStateReady(val id: TransferId, val attempt: Any, val session: Session, val states: List<ResumeFileState>, val digests: Map<Int, Pair<Long, MessageDigest>>) : Cmd

        class ResumeTimeout(val id: TransferId, val attempt: Any) : Cmd

        class Restore(val done: CompletableDeferred<Unit>) : Cmd

        class AbandonAll(val done: CompletableDeferred<Unit>) : Cmd

        class Abandon(val id: TransferId, val done: CompletableDeferred<Unit>) : Cmd

        class CancelPeer(val peer: DeviceId, val done: CompletableDeferred<Unit>) : Cmd

        class Janitor(val done: CompletableDeferred<Unit>?) : Cmd

        class Inbound(val session: Session, val msg: ProtocolMessage) : Cmd

        class SessionOpened(val session: Session) : Cmd

        class SessionClosed(val session: Session, val reason: CloseReason) : Cmd

        class OfferTimeout(val id: TransferId) : Cmd

        /** 发送方读盘协程的结果。 */
        class SenderEvent(val sender: Sender, val event: TransferEvent) : Cmd

        class SenderChecking(val sender: Sender, val on: Boolean) : Cmd

        /** 接收方写盘协程的结果。 */
        class ReceiverEvent(val receiver: Receiver, val event: TransferEvent) : Cmd

        class TempReady(val receiver: Receiver, val index: Int, val ref: TempRef) : Cmd

        data object Publish : Cmd
    }

    /** 一个任务的运行期状态。 */
    private inner class Entry(var task: TransferTask) {
        var session: Session? = null
        var offerTimer: Job? = null

        /** 已显示文件接收请求通知（21.8）。 */
        var requestShown = false

        /** RESUMING → RUNNING 时要先删除的临时文件（RESTART），交给新的写盘协程按顺序处理。 */
        val pendingDeletes = ArrayList<Pair<Int, TempRef>>()
        var sender: Sender? = null
        var receiver: Receiver? = null
        val progress = ProgressCalculator()
        var checkingPrefix = false
        var lastSaved: TransferTask? = null
        var lastSavedAt = Long.MIN_VALUE

        /** 当前这次续传尝试的标识（过期的结果按它丢弃）与续传前的中断原因（10 s 内未完成时恢复，17.10）。 */
        var resumeAttempt: Any? = null
        var resumeJob: Job? = null
        var resumeTimer: Job? = null
        var resumeReason: InterruptReason? = null

        /** 接收方续传前预先计算的临时文件哈希：index → (长度, 哈希状态)。 */
        var prefixDigests: Map<Int, Pair<Long, MessageDigest>> = emptyMap()

        /** 后台进度通知（21.1）。 */
        var progressNotifiedAt = Long.MIN_VALUE
        var progressShown = false

        val id: TransferId get() = task.transferId
        val peer: DeviceId get() = task.peer
    }

    // ---- actor ----

    private fun handle(cmd: Cmd) {
        try {
            dispatch(cmd)
        } catch (e: RuntimeException) {
            // 兜底（设计“处理原则”）：未预期的异常不让 actor 退出
            noticeFlow.tryEmit(TransferNotice.StoreFailed("internal error: ${e.javaClass.simpleName}"))
        }
    }

    private fun dispatch(cmd: Cmd) {
        when (cmd) {
            is Cmd.StartSend -> startSend(cmd)
            is Cmd.Accept -> entries[cmd.id]?.let { e ->
                if (e.task.direction == Direction.RECEIVE && e.task.state == TaskState.AWAITING_USER) acceptNow(e)
            }
            is Cmd.Reject -> entries[cmd.id]?.let { step(it, TransferEvent.UserRejected) }
            is Cmd.Cancel -> entries[cmd.id]?.let { localCancel(it, TransferCancelReason.USER) }
            is Cmd.Resume -> startResumeByUser(cmd)
            is Cmd.ResumeOpened -> onResumeOpened(cmd)
            is Cmd.ResumeStateReady -> onResumeStateReady(cmd)
            is Cmd.ResumeTimeout -> entries[cmd.id]?.let { e ->
                if (e.resumeAttempt === cmd.attempt && e.task.state == TaskState.RESUMING) {
                    step(e, TransferEvent.ResumeFailed(e.resumeReason ?: InterruptReason.PEER_UNRESPONSIVE))
                }
            }
            is Cmd.Restore -> {
                restore()
                cmd.done.complete(Unit)
            }
            is Cmd.AbandonAll -> {
                entries.values.toList().forEach { if (!it.task.state.isTerminal) localCancel(it, TransferCancelReason.USER) }
                entries.values.toList().forEach { dispose(it) }
                entries.clear()
                publish()
                cmd.done.complete(Unit)
            }
            is Cmd.Abandon -> {
                entries[cmd.id]?.let { localCancel(it, TransferCancelReason.USER) }
                cmd.done.complete(Unit)
            }
            is Cmd.CancelPeer -> {
                entries.values.filter { it.peer == cmd.peer && !it.task.state.isTerminal }.forEach { localCancel(it, TransferCancelReason.UNPAIRED) }
                cmd.done.complete(Unit)
            }
            is Cmd.Janitor -> {
                janitor()
                cmd.done?.complete(Unit)
            }
            is Cmd.Inbound -> inbound(cmd.session, cmd.msg)
            is Cmd.SessionOpened -> deliverPendingCancels(cmd.session)
            is Cmd.SessionClosed -> {
                val reason = cmd.reason.interruptReason ?: return
                for (e in entries.values.toList()) {
                    if (e.session === cmd.session && !e.task.state.isTerminal) step(e, TransferEvent.Disconnected(reason))
                }
            }
            is Cmd.OfferTimeout -> entries[cmd.id]?.let { step(it, TransferEvent.OfferTimedOut) }
            is Cmd.SenderEvent -> entryOf(cmd.sender)?.let { e -> step(e, cmd.event) }
            is Cmd.SenderChecking -> entryOf(cmd.sender)?.let { e ->
                e.checkingPrefix = cmd.on
                publish()
            }
            is Cmd.ReceiverEvent -> onReceiverEvent(cmd)
            is Cmd.TempReady -> entryOf(cmd.receiver)?.let { e ->
                val pos = e.task.files.indexOfFirst { it.index == cmd.index }
                if (pos >= 0 && e.task.files[pos].tempRef != cmd.ref) {
                    e.task = e.task.copy(files = e.task.files.toMutableList().also { it[pos] = it[pos].copy(tempRef = cmd.ref) })
                    save(e, force = true)
                }
            }
            Cmd.Publish -> {
                publishScheduled = false
                publish()
            }
        }
    }

    private fun entryOf(s: Sender): Entry? = entries[s.id]?.takeIf { it.sender === s }

    private fun entryOf(r: Receiver): Entry? = entries[r.id]?.takeIf { it.receiver === r }

    // ---- 发送方：创建任务与 Offer ----

    private fun startSend(cmd: Cmd.StartSend) {
        var id: TransferId
        do {
            id = TransferId(Bytes16.of(rng.nextBytes(16)))
        } while (id in entries)
        val now = clock.wallMs()
        val files = cmd.files.mapIndexed { i, (ref, m) ->
            TransferFile(id, i, 0, m.name, m.size, m.mime, m.mtime, FileState.WAITING, sourceRef = ref)
        }
        val peer = cmd.session.peer.deviceId
        val task = TransferTask(id, peer, peerName(peer, cmd.session), Direction.SEND, TaskState.OFFERING, null, null, clock.recordTime(), now, files)
        val e = Entry(task)
        e.session = cmd.session
        entries[id] = e
        save(e, force = true)
        val offer = FileOffer(id, files.map { FileEntry(it.originalName, it.size.toULong(), it.mime, it.mtime) })
        e.offerTimer = timer(timings.senderOfferMs) { Cmd.OfferTimeout(id) }
        publish()
        if (!cmd.session.send(offer)) step(e, TransferEvent.Disconnected(InterruptReason.PEER_CLOSED))
        cmd.done.complete(id)
    }

    // ---- 收到的消息 ----

    private fun inbound(session: Session, msg: ProtocolMessage) {
        if (msg is FileOffer) {
            onOffer(session, msg)
            return
        }
        val id = when (msg) {
            is FileOfferReply -> msg.transferId
            is FileChunk -> msg.transferId
            is FileEnd -> msg.transferId
            is FileAck -> msg.transferId
            is FileResult -> msg.transferId
            is FileReadFailed -> msg.transferId
            is TransferCancel -> msg.transferId
            is ResumeRequest -> msg.transferId
            is ResumeState -> msg.transferId
            is ResumePlan -> msg.transferId
            else -> return
        }
        // 只有该任务的对端才能操作它
        val e = entries[id]?.takeIf { it.peer == session.peer.deviceId }
        if (e == null) {
            // E3：未知任务的 FILE_*、RESUME_* 回复 UNKNOWN_TRANSFER；TRANSFER_CANCEL、FILE_ACK、FILE_RESULT、FILE_OFFER_REPLY 忽略
            when (msg) {
                is FileChunk, is FileEnd, is FileReadFailed, is ResumeRequest, is ResumeState, is ResumePlan ->
                    session.send(TransferCancel(id, TransferCancelReason.UNKNOWN_TRANSFER))
                else -> Unit
            }
            return
        }
        if (e.task.state.isTerminal) {
            // 迟到的消息忽略。已结束的任务被请求续传时：本机作为接收方已完成（对方没收到最后的 FILE_RESULT）→ 回复全部已结束的
            // RESUME_STATE，对方据此全部 SKIP 并完成；其他情况告知对方已取消。
            if (msg is ResumeRequest && e.task.direction == Direction.RECEIVE && e.task.state == TaskState.COMPLETED) {
                session.send(ResumeState(id, e.task.files.map { f ->
                    ResumeFileState(f.index.toUInt(), if (f.state == FileState.COMPLETED) ResumeFileStatus.COMPLETED else ResumeFileStatus.TERMINAL, 0u)
                }))
            } else if (msg is ResumeRequest || msg is ResumeState) {
                session.send(TransferCancel(id, TransferCancelReason.USER))
            }
            return
        }
        when (msg) {
            is FileOfferReply -> if (e.session === session) step(e, TransferEvent.OfferReplied(msg.decision))
            is FileAck -> if (e.session === session) {
                val index = msg.fileIndex.toInt()
                e.sender?.onAck(index, msg.persisted.toLong())
                step(e, TransferEvent.AckReceived(index, msg.persisted.toLong()))
            }
            is FileResult -> if (e.session === session) step(e, TransferEvent.ResultReceived(msg.fileIndex.toInt(), msg.status, msg.detail))
            is TransferCancel -> step(e, TransferEvent.PeerCancelled(msg.reason))
            is FileChunk, is FileEnd, is FileReadFailed -> if (e.session === session) onWireFileMessage(e, session, msg)
            is ResumeRequest -> onResumeRequest(e, session)
            is ResumeState -> onResumeState(e, session, msg)
            is ResumePlan -> onResumePlan(e, session, msg)
            else -> Unit
        }
    }

    private fun onOffer(session: Session, offer: FileOffer) {
        val peer = session.peer.deviceId
        if (offer.transferId in entries) return // 重复的 Offer
        val dir = settings.current().receiveDirectory
        val autoAccept = trust.get(peer)?.autoAccept == true
        val decision = OfferPrecheck.decide(offer, storage.probe(dir), storage.freeSpace(dir), autoAccept)
        if (decision is PrecheckDecision.Reject && decision.reply == OfferDecision.INVALID_OFFER) {
            session.send(FileOfferReply(offer.transferId, OfferDecision.INVALID_OFFER)) // 不创建任务（E3）
            return
        }
        val files = offer.files.mapIndexed { i, f -> TransferFile(offer.transferId, i, 0, f.name, f.size.toLong(), f.mime, f.mtime, FileState.WAITING) }
        val state = when (decision) {
            is PrecheckDecision.Reject -> if (decision.reply == OfferDecision.NO_SPACE) TaskState.NO_SPACE else TaskState.CANNOT_SAVE
            else -> TaskState.AWAITING_USER
        }
        val task = TransferTask(offer.transferId, peer, peerName(peer, session), Direction.RECEIVE, state, null, null, clock.recordTime(), clock.wallMs(), files)
        val e = Entry(task)
        e.session = session
        entries[offer.transferId] = e
        save(e, force = true)
        when (decision) {
            is PrecheckDecision.Reject -> session.send(FileOfferReply(offer.transferId, decision.reply))
            PrecheckDecision.AutoAccept -> {
                acceptNow(e)
                return
            }
            PrecheckDecision.AskUser -> {
                e.offerTimer = timer(timings.receiverOfferMs) { Cmd.OfferTimeout(offer.transferId) }
                if (lifecycle.background.value) {
                    notifier.request(NotifyKey.Transfer(offer.transferId), task.peerName, RequestKind.FILE_OFFER, files.size)
                    e.requestShown = true
                }
            }
        }
        publish()
    }

    private fun acceptNow(e: Entry) {
        val dir = settings.current().receiveDirectory
        step(e, TransferEvent.UserAccepted(dir, storage.ensureDirectory(dir)))
    }

    // ---- 接收方：数据 ----

    private fun onWireFileMessage(e: Entry, session: Session, msg: ProtocolMessage) {
        val rx = e.receiver
        if (e.task.direction != Direction.RECEIVE || e.task.state != TaskState.RUNNING || rx == null) return
        val index = when (msg) {
            is FileChunk -> msg.fileIndex.toInt()
            is FileEnd -> msg.fileIndex.toInt()
            is FileReadFailed -> msg.fileIndex.toInt()
            else -> return
        }
        val pos = e.task.files.indexOfFirst { it.index == index }
        if (pos < 0 || pos > rx.wirePos) {
            violation(session, "file index $index out of order")
            return
        }
        if (pos < rx.wirePos) return // 已结束的文件仍在途的数据（如判定校验失败之后）
        when (msg) {
            is FileChunk -> {
                val expected = rx.expected
                val n = msg.data.size.toLong()
                if (msg.offset.toLong() != expected) {
                    violation(session, "offset ${msg.offset} != $expected")
                    return
                }
                if (rx.queued.get() + n > timings.windowBytes) {
                    violation(session, "window exceeded")
                    return
                }
                rx.expected = expected + n
                rx.queued.addAndGet(n)
                rx.submit(WriteCmd.Chunk(index, msg.offset.toLong(), msg.data))
            }
            is FileEnd -> {
                rx.submit(WriteCmd.End(index, msg.sha256, rx.startOf[index] ?: 0L))
                advanceWire(e, rx, pos + 1)
            }
            is FileReadFailed -> {
                rx.dropped += index
                rx.submit(WriteCmd.PeerReadFailed(index))
                advanceWire(e, rx, pos + 1)
            }
            else -> Unit
        }
    }

    /** 把接收方的“线上当前文件”移到 [from] 起第一个未结束的文件。 */
    private fun advanceWire(e: Entry, rx: Receiver, from: Int) {
        var p = from
        while (p < e.task.files.size && e.task.files[p].state.isTerminal) p++
        rx.wirePos = p
        rx.expected = if (p < e.task.files.size) rx.startOf[e.task.files[p].index] ?: 0L else 0L
    }

    private fun violation(session: Session, detail: String) {
        session.connection.close(CloseReason.ProtocolError(detail))
    }

    private fun onReceiverEvent(cmd: Cmd.ReceiverEvent) {
        val e = entries[cmd.receiver.id] ?: return
        val ev = cmd.event
        // 断开后写盘协程仍可能完成提交：文件已经以最终名称保存，按已完成记录（否则续传会重复接收）
        val committed = (ev as? TransferEvent.VerifyDone)?.outcome as? VerifyOutcome.Committed
        if (ev is TransferEvent.VerifyDone && committed != null && e.task.state == TaskState.INTERRUPTED) {
            val pos = e.task.files.indexOfFirst { it.index == ev.index && it.state == FileState.VERIFYING }
            if (pos >= 0) {
                val o = committed
                val f = e.task.files[pos]
                e.task = e.task.copy(files = e.task.files.toMutableList().also { it[pos] = f.copy(state = FileState.COMPLETED, persisted = f.size, finalName = o.finalName, fullPath = o.fullPath, storedRef = o.storedRef) })
                save(e, force = true)
                publish()
            }
            return
        }
        if (e.receiver !== cmd.receiver) return
        step(e, ev)
    }

    // ---- 续传 ----

    private fun startResumeByUser(cmd: Cmd.Resume) {
        val e = entries[cmd.id]
        if (e == null || e.task.state != TaskState.INTERRUPTED) {
            cmd.done.complete(ResumeResult.NotResumable)
            return
        }
        e.resumeReason = e.task.interrupt?.reason
        stepWith(e, TransferEvent.ResumeRequested) { f -> if (f == TransferEffect.StartResume) openForResume(e, cmd.done) else execute(e, f) }
        if (!cmd.done.isCompleted && e.task.state != TaskState.RESUMING) cmd.done.complete(ResumeResult.NotResumable)
    }

    /** 用户点击“继续”：在 10 s 预算内重新建立 Session。 */
    private fun openForResume(e: Entry, done: CompletableDeferred<ResumeResult>?) {
        val attempt = Any()
        e.resumeAttempt = attempt
        e.resumeTimer?.cancel()
        e.resumeTimer = timer(timings.resumeMs) { Cmd.ResumeTimeout(e.id, attempt) }
        e.resumeJob = scope.launch { inbox.send(Cmd.ResumeOpened(e.id, attempt, sessions.open(Target.Device(e.peer)), done)) }
    }

    private fun onResumeOpened(cmd: Cmd.ResumeOpened) {
        val e = entries[cmd.id]
        if (e == null || e.resumeAttempt !== cmd.attempt || e.task.state != TaskState.RESUMING) {
            (cmd.result as? OpenResult.Pairing)?.connection?.close()
            cmd.done?.complete(ResumeResult.NotResumable)
            return
        }
        when (val r = cmd.result) {
            is OpenResult.Ok -> {
                e.session = r.session
                if (e.task.direction == Direction.SEND) {
                    if (!r.session.send(ResumeRequest(e.id))) step(e, TransferEvent.Disconnected(InterruptReason.PEER_CLOSED))
                } else {
                    prepareResumeState(e, r.session, cmd.attempt)
                }
                cmd.done?.complete(ResumeResult.Started)
            }
            is OpenResult.Failed -> {
                step(e, TransferEvent.ResumeFailed(e.resumeReason ?: InterruptReason.PEER_UNRESPONSIVE))
                cmd.done?.complete(ResumeResult.ConnectFailed(r.failure))
            }
            is OpenResult.Pairing -> {
                r.connection.close()
                step(e, TransferEvent.ResumeFailed(e.resumeReason ?: InterruptReason.PEER_UNRESPONSIVE))
                cmd.done?.complete(ResumeResult.ConnectFailed(OpenFailure.NotTrusted))
            }
        }
    }

    /** 对方发来 RESUME_REQUEST：本机是接收方，回复 RESUME_STATE（17.4，不需要再次确认）。 */
    private fun onResumeRequest(e: Entry, session: Session) {
        if (e.task.direction != Direction.RECEIVE) return
        when (e.task.state) {
            TaskState.INTERRUPTED -> {
                e.resumeReason = e.task.interrupt?.reason
                val attempt = Any()
                e.resumeAttempt = attempt
                e.session = session
                stepWith(e, TransferEvent.ResumeRequested) { f -> if (f != TransferEffect.StartResume) execute(e, f) }
                e.resumeTimer = timer(timings.resumeMs) { Cmd.ResumeTimeout(e.id, attempt) }
                prepareResumeState(e, session, attempt)
            }
            TaskState.RESUMING -> {
                // 双方同时点击“继续”：用对方的这条 Session 回复
                val attempt = Any()
                e.resumeAttempt = attempt
                e.session = session
                prepareResumeState(e, session, attempt)
            }
            else -> Unit
        }
    }

    /** 接收方：在 io 上读取临时文件长度并预先计算其哈希（之后续传从这里接着计算），再回到 actor 发送 RESUME_STATE。 */
    private fun prepareResumeState(e: Entry, session: Session, attempt: Any) {
        e.checkingPrefix = true
        publish()
        val files = e.task.files
        val previous = e.receiver
        e.resumeJob = scope.launch(io) {
            previous?.job?.join() // 先等上一次的写盘协程写完已收到的数据
            val states = ArrayList<ResumeFileState>(files.size)
            val digests = HashMap<Int, Pair<Long, MessageDigest>>()
            for (f in files) {
                val u = f.index.toUInt()
                if (f.state.isTerminal) {
                    states += ResumeFileState(u, if (f.state == FileState.COMPLETED) ResumeFileStatus.COMPLETED else ResumeFileStatus.TERMINAL, 0u)
                    continue
                }
                val temp = f.tempRef
                val hashed = temp?.let { hashTemp(it, null) }
                if (temp == null || hashed == null) {
                    states += ResumeFileState(u, ResumeFileStatus.NONE, 0u)
                } else {
                    digests[f.index] = hashed
                    states += ResumeFileState(u, ResumeFileStatus.PARTIAL, hashed.first.toULong())
                }
            }
            inbox.send(Cmd.ResumeStateReady(e.id, attempt, session, states, digests))
        }
    }

    private fun onResumeStateReady(cmd: Cmd.ResumeStateReady) {
        val e = entries[cmd.id] ?: return
        if (e.resumeAttempt !== cmd.attempt || e.task.state != TaskState.RESUMING) return
        e.checkingPrefix = false
        e.prefixDigests = cmd.digests
        publish()
        if (!cmd.session.send(ResumeState(e.id, cmd.states))) step(e, TransferEvent.Disconnected(InterruptReason.PEER_CLOSED))
    }

    /** 本机是发送方：收到 RESUME_STATE，由 ResumePlanner 生成计划（17.4、17.5）。 */
    private fun onResumeState(e: Entry, session: Session, msg: ResumeState) {
        if (e.task.direction != Direction.SEND) return
        if (e.task.state == TaskState.INTERRUPTED) {
            // 接收方点击了“继续”
            e.resumeReason = e.task.interrupt?.reason
            stepWith(e, TransferEvent.ResumeRequested) { f -> if (f != TransferEffect.StartResume) execute(e, f) }
        }
        if (e.task.state != TaskState.RESUMING) return
        e.resumeTimer?.cancel()
        e.resumeAttempt = null
        e.session = session
        val stats = e.task.files.map { f -> f.sourceRef?.let { sources.stat(it) } }
        step(e, TransferEvent.ResumePlanned(ResumePlanner.plan(e.task, msg.files, stats)))
    }

    /** 本机是接收方：收到 RESUME_PLAN。 */
    private fun onResumePlan(e: Entry, session: Session, msg: ResumePlan) {
        if (e.task.direction != Direction.RECEIVE || e.task.state != TaskState.RESUMING || e.session !== session) return
        e.resumeTimer?.cancel()
        e.resumeAttempt = null
        e.checkingPrefix = false
        val plan = msg.files.map { ResumePlanEntry(it.fileIndex.toInt(), it.action, it.start.toLong(), it.size?.toLong(), it.mtime) }
        step(e, TransferEvent.ResumePlanned(plan))
        val rx = e.receiver ?: return
        for (p in plan) if (p.action == ResumeAction.CONTINUE) rx.startOf[p.index] = p.start
        advanceWire(e, rx, 0)
    }

    // ---- 取消与放弃 ----

    private fun localCancel(e: Entry, reason: TransferCancelReason) {
        val active = e.session?.isOpen == true && e.task.state != TaskState.INTERRUPTED
        step(e, TransferEvent.LocalCancel(active, reason))
    }

    /** 与该对端的 Session 建立后（以及写入 pending_cancel 时恰好有 Session），发出待送达的取消通知（18.9）。 */
    private fun deliverPendingCancels(session: Session) {
        if (!session.isOpen) return
        val peer = session.peer.deviceId
        for (id in store.takePendingCancels(peer)) {
            // 发不出去（连接正在关闭）时放回，下次 Session 建立后再发
            if (!session.send(TransferCancel(id, TransferCancelReason.USER))) store.addPendingCancel(peer, id)
        }
    }

    // ---- 启动恢复与清理 ----

    private fun restore() {
        for (t in store.unfinished()) {
            if (t.transferId in entries) continue
            val e = Entry(t)
            entries[t.transferId] = e
            stepWith(e, TransferEvent.Restored) { f -> execute(e, f) }
            save(e, force = true)
        }
        // 启动时删除没有被任何未结束发送任务引用的 .txt 缓存（M4）
        val referenced = entries.values.filter { it.task.direction == Direction.SEND && !it.task.state.isTerminal }
            .flatMap { it.task.files.mapNotNull { f -> f.sourceRef } }.toSet()
        for (ref in textCache.list()) if (ref !in referenced) textCache.delete(ref)
        janitor()
        if (!janitorStarted) {
            janitorStarted = true
            scope.launch {
                while (true) {
                    delay(timings.janitorIntervalMs)
                    inbox.send(Cmd.Janitor(null))
                }
            }
        }
        publish()
    }

    /** TempFileJanitor（17.7）：删除不属于进行中任务、且超过 24 h 未写入的临时文件。 */
    private fun janitor() {
        val active = entries.values.filter { it.task.state.isActive }.mapTo(HashSet()) { it.id }
        val lastWrite = HashMap<TempRef, Long>()
        for (e in entries.values) for (f in e.task.files) {
            val ref = f.tempRef ?: continue
            f.lastWriteAtUtcMs?.let { lastWrite[ref] = it }
        }
        val temps = storage.listTemps().map { t -> lastWrite[t.ref]?.let { if (it > t.lastWriteUtcMs) t.copy(lastWriteUtcMs = it) else t } ?: t }
        for (t in TempFileSelector.select(temps, active, clock.wallMs())) storage.delete(t.ref)
    }

    // ---- 归约与 effects ----

    private fun step(e: Entry, ev: TransferEvent) = stepWith(e, ev) { f -> execute(e, f) }

    private inline fun stepWith(e: Entry, ev: TransferEvent, run: (TransferEffect) -> Unit) {
        val before = e.task
        val s = TransferReducer.reduce(before, ev)
        e.task = s.state
        s.effects.forEach(run)
        afterStep(e, before, ev)
    }

    private fun execute(e: Entry, f: TransferEffect) {
        when (f) {
            is TransferEffect.Send -> {
                val s = e.session?.takeIf { it.isOpen } ?: sessions.session(e.peer)
                val ok = s?.send(f.msg) == true
                // 取消通知发不出去时改为待送达（18.9）
                if (!ok && f.msg is TransferCancel) addPendingCancel(e)
            }
            is TransferEffect.StartFile -> startFile(e, f.index, f.offset)
            TransferEffect.StopSending -> e.sender?.stop()
            is TransferEffect.Verify -> {
                val file = e.task.files.firstOrNull { it.index == f.index } ?: return
                val name = FileNameSanitizer.sanitize(file.originalName, profile, file.startedAt ?: clock.localNow())
                e.receiver?.let { it.submit(WriteCmd.Verify(f.index, f.sha256, name, file.size, it.startOf[f.index] ?: 0L)) }
            }
            is TransferEffect.DeleteTemp -> {
                val rx = e.receiver
                val ref = e.task.files.firstOrNull { it.index == f.index }?.tempRef
                if (rx != null && rx.isOpen) {
                    rx.dropped.add(f.index)
                    rx.submit(WriteCmd.Delete(f.index, ref))
                } else if (e.task.state == TaskState.RUNNING) {
                    // RESUMING → RUNNING（RESTART）：由即将创建的写盘协程在写入之前删除
                    if (ref != null) e.pendingDeletes += f.index to ref
                } else if (ref != null) {
                    val previous = rx?.job
                    scope.launch(io) {
                        previous?.join()
                        storage.delete(ref)
                    }
                }
            }
            is TransferEffect.AddPendingCancel -> addPendingCancel(e)
            is TransferEffect.StartOfferTimer -> e.offerTimer = timer(f.ms) { Cmd.OfferTimeout(e.id) }
            TransferEffect.CancelOfferTimer -> {
                e.offerTimer?.cancel()
                e.offerTimer = null
            }
            TransferEffect.StartResume -> openForResume(e, null)
            TransferEffect.AbortResume -> {
                e.resumeAttempt = null
                e.resumeJob?.cancel()
                e.resumeTimer?.cancel()
                e.checkingPrefix = false
            }
            TransferEffect.ProtocolViolation -> e.session?.connection?.close(CloseReason.ProtocolError("transfer ${e.id}"))
        }
    }

    private fun addPendingCancel(e: Entry) {
        if (store.addPendingCancel(e.peer, e.id) is StoreResult.Failed) noticeFlow.tryEmit(TransferNotice.PendingCancelNotSaved(e.id))
        sessions.session(e.peer)?.let { deliverPendingCancels(it) }
    }

    private fun startFile(e: Entry, index: Int, offset: Long) {
        val session = e.session ?: return
        val sender = e.sender?.takeIf { it.session === session } ?: run {
            e.sender?.close()
            Sender(e.id, session).also { e.sender = it }
        }
        val f = e.task.files.firstOrNull { it.index == index } ?: return
        val ref = f.sourceRef
        if (ref == null) {
            inbox.trySend(Cmd.SenderEvent(sender, TransferEvent.SourceReadFailed(index)))
            return
        }
        sender.start(index, ref, offset)
    }

    /** 每次归约之后：维护运行期状态、通知、写库与发布。 */
    private fun afterStep(e: Entry, before: TransferTask, ev: TransferEvent) {
        val t = e.task
        val now = clock.monotonicMs()
        if (before.state != t.state) {
            if (t.state == TaskState.RUNNING) {
                e.progress.reset(now, transferred(t))
                val session = e.session
                val dir = t.receiveDir
                if (t.direction == Direction.RECEIVE && session != null && dir != null) {
                    val previous = e.receiver
                    previous?.close()
                    val rx = Receiver(e.id, session, dir, e.prefixDigests, previous?.job)
                    e.receiver = rx
                    for ((index, ref) in e.pendingDeletes) rx.submit(WriteCmd.Delete(index, ref))
                    e.pendingDeletes.clear()
                    e.prefixDigests = emptyMap()
                    advanceWire(e, rx, 0)
                }
            }
            if (before.state == TaskState.AWAITING_USER && e.requestShown) {
                notifier.cancel(NotifyKey.Transfer(t.transferId))
                e.requestShown = false
            }
            if (t.state.isTerminal || t.state == TaskState.INTERRUPTED) stopRuntime(e)
            if (t.state.isTerminal && t.direction == Direction.SEND) {
                // 发送任务结束后删除“以 .txt 文件发送”的缓存（M4）
                for (f in t.files) f.sourceRef?.let { if (textCache.owns(it)) textCache.delete(it) }
            }
        } else if (t !== before) {
            e.progress.sample(now, transferred(t))
        }
        notifyChanges(e, before)
        if (t != before) save(e, force = structural(before, t))
        if (before.state != t.state || structural(before, t)) publish() else if (t !== before) schedulePublish()
        // 进入 RUNNING 后若 ChunkWritten 让当前文件进入终态（如超过 size），线上当前文件随之前移
        val rx = e.receiver
        if (rx != null && t.state == TaskState.RUNNING && rx.wirePos < t.files.size && t.files[rx.wirePos].state.isTerminal) {
            rx.dropped += t.files[rx.wirePos].index
            advanceWire(e, rx, rx.wirePos + 1)
        }
        if (ev is TransferEvent.Disconnected) e.checkingPrefix = false
    }

    /** 状态或任何文件的状态、临时文件引用发生变化时立即写库；只有进度变化时最多每 1 s 写一次（M2）。 */
    private fun structural(a: TransferTask, b: TransferTask): Boolean =
        a.state != b.state || a.interrupt != b.interrupt || a.files.size != b.files.size ||
            a.files.indices.any { a.files[it].state != b.files[it].state || a.files[it].tempRef != b.files[it].tempRef }

    private fun stopRuntime(e: Entry) {
        e.offerTimer?.cancel()
        e.offerTimer = null
        e.resumeTimer?.cancel()
        e.resumeJob?.takeIf { e.task.state.isTerminal }?.cancel()
        e.sender?.close()
        e.sender = null
        e.receiver?.close()
        e.checkingPrefix = false
        if (e.progressShown) {
            notifier.cancel(NotifyKey.Transfer(e.id))
            e.progressShown = false
        }
    }

    /** 释放一个将被移出内存的任务。 */
    private fun dispose(e: Entry) {
        stopRuntime(e)
        e.resumeJob?.cancel()
    }

    private fun transferred(t: TransferTask): Long = t.files.sumOf { if (it.state.isTerminal) it.size else it.persisted }

    private fun save(e: Entry, force: Boolean) {
        val now = clock.monotonicMs()
        if (!force && now - e.lastSavedAt < timings.saveIntervalMs) return
        if (e.lastSaved == e.task) return
        val t = e.task.copy(updatedAtUtcMs = clock.wallMs())
        e.task = t
        e.lastSaved = t
        e.lastSavedAt = now
        val r = store.save(t)
        if (r is StoreResult.Failed) noticeFlow.tryEmit(TransferNotice.StoreFailed(r.detail))
    }

    // ---- 通知（21.1、21.7、21.10） ----

    private fun notifyChanges(e: Entry, before: TransferTask) {
        val t = e.task
        if (!lifecycle.background.value) return
        val name = t.peerName
        for (i in t.files.indices) {
            val f = t.files[i]
            val old = before.files.getOrNull(i)?.state
            if (old == f.state) continue
            when (f.state) {
                FileState.COMPLETED -> if (t.direction == Direction.RECEIVE) {
                    f.storedRef?.let { notifier.fileCompleted(NotifyKey.File(t.transferId, f.index), name, f.finalName ?: f.originalName, it) }
                }
                FileState.READ_FAILED, FileState.CHECKSUM_FAILED, FileState.WRITE_FAILED ->
                    notifier.problem(NotifyKey.File(t.transferId, f.index), name, f.originalName, f.state.name)
                else -> Unit
            }
        }
        if (before.state != t.state && t.state == TaskState.INTERRUPTED) {
            val file = t.files.firstOrNull { !it.state.isTerminal }?.originalName
            notifier.problem(NotifyKey.Transfer(t.transferId), name, file, "INTERRUPTED:${t.interrupt?.reason?.name}")
        }
        if (t.state == TaskState.RUNNING) {
            val now = clock.monotonicMs()
            if (now - e.progressNotifiedAt >= timings.progressNotifyMs) {
                e.progressNotifiedAt = now
                e.progressShown = true
                val cur = t.files.firstOrNull { !it.state.isTerminal }
                val p = e.progress.snapshot(now, t.files.sumOf { it.size }, false)
                notifier.progress(t.transferId, name, cur?.originalName ?: "", p.percent)
            }
        }
    }

    // ---- 发布 ----

    private fun schedulePublish() {
        if (publishScheduled) return
        publishScheduled = true
        timer(timings.publishIntervalMs) { Cmd.Publish }
    }

    private fun publish() {
        val now = clock.monotonicMs()
        tasksFlow.value = entries.values.map { view(it, now) }
        unfinishedFlow.value = entries.values.count { !it.task.state.isTerminal }
    }

    private fun view(e: Entry, now: Long): TransferTaskView {
        val t = e.task
        val total = t.files.sumOf { it.size }
        val done = transferred(t)
        val running = t.state == TaskState.RUNNING
        val snap = e.progress.snapshot(now, total, t.state == TaskState.COMPLETED)
        val percent = if (total == 0L) (if (t.state == TaskState.COMPLETED) 100 else 0) else ((done.coerceAtMost(total) * 100) / total).toInt()
        val p = Progress(percent, done, total, if (running) snap.bytesPerSec else 0, if (running) snap.etaSec else null)
        val current = if (t.state == TaskState.RUNNING) t.files.firstOrNull { !it.state.isTerminal }?.index else null
        val files = t.files.map { f ->
            val fileDone = if (f.state == FileState.COMPLETED) f.size else f.persisted
            val filePercent = when {
                f.size == 0L -> if (f.state == FileState.COMPLETED) 100 else 0
                else -> ((fileDone.coerceAtMost(f.size) * 100) / f.size).toInt()
            }
            if (f.index == current && p.bytesPerSec > 0) {
                Progress(filePercent, fileDone, f.size, p.bytesPerSec, (f.size - fileDone).coerceAtLeast(0) / p.bytesPerSec)
            } else {
                Progress(filePercent, fileDone, f.size, 0, null)
            }
        }
        return TransferTaskView(t, p, files, e.checkingPrefix)
    }

    private fun timer(ms: Long, make: () -> Cmd): Job = scope.launch {
        delay(ms)
        inbox.send(make())
    }

    private fun peerName(peer: DeviceId, session: Session?): String =
        trust.get(peer)?.displayName ?: session?.peer?.displayName ?: ""

    // ---- 发送方读盘协程 ----

    /** 一个发送任务在一个 Session 上的数据源：容量为 2 的缓冲 + 读盘协程（C4.5 FileSender）。 */
    private inner class Sender(val id: TransferId, val session: Session) : ChunkSource {
        private val buffer = ArrayDeque<ProtocolMessage>()
        private val wake = Channel<Unit>(Channel.CONFLATED)
        private val handle = session.attachChunkSource(this)
        private var job: Job? = null

        /** 每次 start / stop 加 1；被取消的读盘协程不能再往缓冲里放数据（在 buffer 锁内检查）。 */
        private var gen = 0

        @Volatile
        private var index = -1

        @Volatile
        private var acked = 0L

        override fun poll(): ProtocolMessage? {
            val m = synchronized(buffer) { buffer.removeFirstOrNull() }
            if (m != null) wake.trySend(Unit)
            return m
        }

        fun onAck(index: Int, persisted: Long) {
            if (index == this.index && persisted > acked) {
                acked = persisted
                wake.trySend(Unit)
            }
        }

        fun start(index: Int, ref: SourceRef, offset: Long) {
            stop()
            this.index = index
            acked = offset
            val g = synchronized(buffer) { gen }
            job = scope.launch(io) { run(g, index, ref, offset) }
        }

        fun stop() {
            job?.cancel()
            job = null
            synchronized(buffer) {
                gen++
                buffer.clear()
            }
        }

        fun close() {
            stop()
            handle.detach()
        }

        /** 放入缓冲；本协程已被 stop 取代时返回 false。 */
        private suspend fun push(g: Int, m: ProtocolMessage): Boolean {
            while (true) {
                val r = synchronized(buffer) {
                    when {
                        gen != g -> 0
                        buffer.size < BUFFER_CAPACITY -> {
                            buffer.addLast(m)
                            1
                        }
                        else -> 2
                    }
                }
                when (r) {
                    0 -> return false
                    1 -> {
                        handle.signal()
                        return true
                    }
                }
                wake.receive()
            }
        }

        private suspend fun post(ev: TransferEvent) = inbox.send(Cmd.SenderEvent(this, ev))

        private suspend fun run(g: Int, index: Int, ref: SourceRef, offset: Long) {
            val reader = when (val r = sources.open(ref, 0)) {
                is ReadResult.Ok -> r.value
                is ReadResult.Failed -> {
                    post(TransferEvent.SourceReadFailed(index))
                    return
                }
            }
            try {
                val md = MessageDigest.getInstance("SHA-256")
                val buf = ByteArray(timings.chunkBytes)
                var pos = 0L
                if (offset > 0) {
                    // 续传：先从磁盘重新计算 [0, offset) 的哈希
                    inbox.send(Cmd.SenderChecking(this, true))
                    while (pos < offset) {
                        val want = minOf(buf.size.toLong(), offset - pos).toInt()
                        val n = readSome(reader, buf, want) ?: return post(TransferEvent.SourceReadFailed(index))
                        if (n < 0) return post(TransferEvent.SourceReadFailed(index))
                        md.update(buf, 0, n)
                        pos += n
                    }
                    inbox.send(Cmd.SenderChecking(this, false))
                }
                while (true) {
                    // 未确认数据 ≤ 4 MiB（C1.13）
                    while (pos + buf.size - acked > timings.windowBytes) wake.receive()
                    val n = readSome(reader, buf, buf.size) ?: return post(TransferEvent.SourceReadFailed(index))
                    if (n < 0) break
                    md.update(buf, 0, n)
                    if (!push(g, FileChunk(id, index.toUInt(), pos.toULong(), Bytes.of(buf, 0, n)))) return
                    pos += n
                }
                val sha = Bytes.of(md.digest())
                if (!push(g, FileEnd(id, index.toUInt(), sha))) return
                post(TransferEvent.FileEndSent(index, sha))
            } finally {
                reader.close()
            }
        }

        /** 读取最多 [len] 字节；失败返回 null，EOF 返回 -1。 */
        private fun readSome(reader: ChunkReader, buf: ByteArray, len: Int): Int? = when (val r = reader.read(buf, 0, len)) {
            is ReadResult.Ok -> r.value
            is ReadResult.Failed -> null
        }
    }

    // ---- 接收方写盘协程 ----

    private sealed interface WriteCmd {
        class Chunk(val index: Int, val offset: Long, val data: Bytes) : WriteCmd

        /** [start] 为该文件本次的起点（没有收到数据块时，从这里打开临时文件）。 */
        class End(val index: Int, val sha256: Bytes, val start: Long) : WriteCmd

        class PeerReadFailed(val index: Int) : WriteCmd

        class Verify(val index: Int, val sha256: Bytes, val name: String, val size: Long, val start: Long) : WriteCmd

        class Delete(val index: Int, val ref: TempRef?) : WriteCmd
    }

    /** 一个接收任务在一个 Session 上的写盘协程（C4.5 FileReceiver）。 */
    private inner class Receiver(
        val id: TransferId,
        val session: Session,
        private val dir: com.lantransfer.domain.DirRef,
        private val prefix: Map<Int, Pair<Long, MessageDigest>>,
        /** 上一次的写盘协程（续传时先等它写完）。 */
        private val previous: Job?,
    ) {
        private val queue = Channel<WriteCmd>(Channel.UNLIMITED)

        /** 已入队、尚未写盘的数据字节数（窗口检查用）。 */
        val queued = AtomicLong(0)

        /** 已判定结束的文件：队列中它的数据直接丢弃。 */
        val dropped: MutableSet<Int> = ConcurrentHashMap.newKeySet()

        // ---- 以下三项只在 actor 中访问 ----

        /** 线上当前文件在 files 中的位置；之前的文件不再接受数据。 */
        var wirePos = 0

        /** 当前文件期望的下一个 offset。 */
        var expected = 0L

        /** 续传计划中 CONTINUE 的起点。 */
        val startOf = HashMap<Int, Long>()

        @Volatile
        var isOpen = true
            private set

        fun submit(c: WriteCmd) {
            if (!queue.trySend(c).isSuccess && c is WriteCmd.Chunk) queued.addAndGet(-c.data.size.toLong())
        }

        /** 不再接收新数据；已入队的命令处理完后结束。 */
        fun close() {
            isOpen = false
            queue.close()
        }

        // ---- 写盘协程内部状态 ----
        private var cur = -1
        private var writer: TempWriter? = null
        private var temp: TempRef? = null
        private var md: MessageDigest? = null
        private var persisted = 0L
        private var ackedBytes = 0L
        private var ackedAt = 0L
        private var failed = false

        /** 放在全部字段之后启动：协程可能立即在另一个线程上运行。 */
        val job: Job = scope.launch(io) { loop() }

        private suspend fun post(ev: TransferEvent) = inbox.send(Cmd.ReceiverEvent(this, ev))

        private suspend fun loop() {
            previous?.join()
            try {
                while (true) {
                    val c = if (writer != null && persisted > ackedBytes) {
                        val wait = (ackedAt + timings.ackIntervalMs - clock.monotonicMs()).coerceAtLeast(1)
                        withTimeoutOrNull(wait) { queue.receiveCatching() }
                    } else {
                        queue.receiveCatching()
                    }
                    if (c == null) {
                        sendAck() // 200 ms 内没有新数据：确认已写入的部分
                        continue
                    }
                    val cmd = c.getOrNull() ?: break
                    process(cmd)
                }
            } finally {
                closeWriter()
            }
        }

        private suspend fun process(c: WriteCmd) {
            when (c) {
                is WriteCmd.Chunk -> {
                    queued.addAndGet(-c.data.size.toLong())
                    if (failed || c.index in dropped) return
                    if (!ensureOpen(c.index, c.offset)) return
                    val w = writer ?: return
                    val bytes = c.data.toByteArray()
                    when (val r = w.write(bytes)) {
                        IoResult.Ok -> Unit
                        is IoResult.Failed -> {
                            failed = true
                            post(TransferEvent.WriteFailed(c.index, r.detail))
                            return
                        }
                    }
                    md?.update(bytes)
                    persisted = c.offset + c.data.size
                    post(TransferEvent.ChunkWritten(c.index, persisted, clock.localNow(), clock.wallMs()))
                    val now = clock.monotonicMs()
                    if (persisted - ackedBytes >= timings.ackBytes || now - ackedAt >= timings.ackIntervalMs) sendAck()
                }
                is WriteCmd.End -> {
                    if (failed || c.index in dropped) return
                    if (!ensureOpen(c.index, c.start)) return
                    sendAck(force = true) // 文件结束时也确认一次
                    post(TransferEvent.EndReceived(c.index, c.sha256, clock.localNow()))
                }
                is WriteCmd.PeerReadFailed -> {
                    if (cur == c.index) closeWriter()
                    post(TransferEvent.PeerReadFailed(c.index))
                }
                is WriteCmd.Verify -> verify(c)
                is WriteCmd.Delete -> {
                    if (cur == c.index) closeWriter()
                    (c.ref ?: temp.takeIf { cur == c.index })?.let { storage.delete(it) }
                }
            }
        }

        /** 打开第 [index] 个文件的临时文件，从 [offset] 处追加；续传时接着之前的哈希状态。 */
        private suspend fun ensureOpen(index: Int, offset: Long): Boolean {
            if (cur == index && writer != null) return true
            closeWriter()
            val ref = storage.createTemp(dir, id, index)
            inbox.send(Cmd.TempReady(this, index, ref))
            val digest = when {
                offset == 0L -> MessageDigest.getInstance("SHA-256")
                prefix[index]?.first == offset -> prefix[index]!!.second.clone() as MessageDigest
                else -> hashTemp(ref, offset)?.second
            }
            if (digest == null) {
                failed = true
                post(TransferEvent.WriteFailed(index, "无法读取临时文件"))
                return false
            }
            val w = when (val r = storage.openAppend(ref, offset)) {
                is WriteResult.Ok -> r.value
                is WriteResult.Failed -> {
                    failed = true
                    post(TransferEvent.WriteFailed(index, r.detail))
                    return false
                }
            }
            cur = index
            temp = ref
            writer = w
            md = digest
            persisted = offset
            ackedBytes = offset
            ackedAt = clock.monotonicMs()
            return true
        }

        private fun sendAck(force: Boolean = false) {
            if (cur < 0 || (!force && persisted <= ackedBytes)) return
            session.send(FileAck(id, cur.toUInt(), persisted.toULong()))
            ackedBytes = persisted
            ackedAt = clock.monotonicMs()
        }

        private suspend fun verify(c: WriteCmd.Verify) {
            if (failed || c.index in dropped) return
            if (cur != c.index || writer == null) {
                if (!ensureOpen(c.index, c.start)) return
            }
            val w = writer!!
            when (val r = w.flushSync()) {
                IoResult.Ok -> Unit
                is IoResult.Failed -> {
                    failed = true
                    post(TransferEvent.WriteFailed(c.index, r.detail))
                    return
                }
            }
            val ok = persisted == c.size && Bytes.of(md!!.digest()) == c.sha256
            val ref = temp!!
            closeWriter()
            val outcome = if (!ok) {
                VerifyOutcome.ChecksumMismatch
            } else {
                when (val r = storage.commit(ref, c.name)) {
                    is CommitResult.Ok -> VerifyOutcome.Committed(r.finalName, r.fullPath, r.storedRef)
                    is CommitResult.Failed -> VerifyOutcome.CommitFailed(r.detail)
                }
            }
            post(TransferEvent.VerifyDone(c.index, outcome))
        }

        private fun closeWriter() {
            writer?.close()
            writer = null
            cur = -1
            md = null
        }
    }

    /** 读出临时文件 [0, upTo)（null 表示全部）的哈希；读不出或长度不足时返回 null（E4：按 NONE 处理）。 */
    private fun hashTemp(ref: TempRef, upTo: Long?): Pair<Long, MessageDigest>? {
        val reader = when (val r = storage.openRead(ref)) {
            is ReadResult.Ok -> r.value
            is ReadResult.Failed -> return null
        }
        try {
            val md = MessageDigest.getInstance("SHA-256")
            val buf = ByteArray(timings.chunkBytes)
            var total = 0L
            while (upTo == null || total < upTo) {
                val want = if (upTo == null) buf.size else minOf(buf.size.toLong(), upTo - total).toInt()
                val n = when (val r = reader.read(buf, 0, want)) {
                    is ReadResult.Ok -> r.value
                    is ReadResult.Failed -> return null
                }
                if (n < 0) break
                md.update(buf, 0, n)
                total += n
            }
            if (upTo != null && total < upTo) return null
            return total to md
        } finally {
            reader.close()
        }
    }

    private companion object {
        /** FileSender 的缓冲容量（C4.5）。 */
        const val BUFFER_CAPACITY = 2
    }
}
