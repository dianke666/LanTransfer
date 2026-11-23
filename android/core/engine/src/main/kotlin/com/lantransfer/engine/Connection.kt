package com.lantransfer.engine

import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.protocol.DecodeBatch
import com.lantransfer.protocol.EncodeResult
import com.lantransfer.protocol.FrameDecoder
import com.lantransfer.protocol.FrameEncoder
import com.lantransfer.protocol.Heartbeat
import com.lantransfer.protocol.MessageType
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.RecordCipher
import com.lantransfer.protocol.RecordFeedResult
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.SessionClose
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.SessionResult
import com.lantransfer.protocol.SessionResultCode
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.atomic.AtomicBoolean

// 连接驱动（设计 C4.2、并发模型）：握手完成后的读循环、单写循环（P0 → P1 → P2）、心跳与无响应判定。

/** 出站优先级：P0 控制（心跳、ACK、配对、取消、结果），P1 文字。文件块走 [ChunkSource]（P2）。 */
enum class Priority { CONTROL, TEXT }

sealed interface ConnState {
    /** 握手已完成，尚未成为 Session：只接受 C1.11 白名单中的消息。 */
    data object Pending : ConnState

    data object Active : ConnState

    data class Closed(val reason: CloseReason) : ConnState
}

/** 文件块来源（P2）。写循环每次最多从一个源拉取 1 条消息；没有可写的数据时返回 null。[poll] 不得挂起或阻塞。 */
fun interface ChunkSource {
    fun poll(): ProtocolMessage?
}

/** [Connection.attachChunkSource] 的句柄。 */
class ChunkSourceHandle internal constructor(private val owner: Connection, internal val source: ChunkSource) {
    /** 源有新数据可写时调用，唤醒写循环。 */
    fun signal() = owner.wake()

    fun detach() = owner.detach(this)
}

class Connection internal constructor(
    private val sock: TcpConn,
    private val reader: ConnReader,
    val role: Role,
    val version: ProtocolVersion,
    private val sendCipher: RecordCipher,
    receiveCipher: RecordCipher,
    private val clock: Clock,
    parentScope: CoroutineScope,
    private val timings: SessionTimings = SessionTimings(),
) {
    private val job = SupervisorJob(parentScope.coroutineContext[Job])
    private val scope = CoroutineScope(parentScope.coroutineContext + job)
    private val p0 = Channel<Outgoing>(Channel.UNLIMITED)
    private val p1 = Channel<Outgoing>(Channel.UNLIMITED)
    private val sources = ArrayList<ChunkSourceHandle>()
    private var nextSource = 0
    private val wakeup = Channel<Unit>(Channel.CONFLATED)
    private val timerKick = Channel<Unit>(Channel.CONFLATED)
    private val inbox = Channel<ProtocolMessage>(Channel.UNLIMITED)
    private val stateFlow = MutableStateFlow<ConnState>(ConnState.Pending)
    private val closedReason = CompletableDeferred<CloseReason>()
    private val receiveStream = receiveCipher.openStream()
    private val closing = AtomicBoolean(false)

    @Volatile
    private var lastSentMs = clock.monotonicMs()

    @Volatile
    private var lastReceivedMs = lastSentMs

    @Volatile
    private var heartbeatQueued = false

    private class Outgoing(val message: ProtocolMessage?, val closeAfter: CloseReason?)

    val state: StateFlow<ConnState> get() = stateFlow

    /** 收到的消息（HEARTBEAT 与 SESSION_CLOSE 由连接自己处理，不出现在这里）；连接关闭后通道关闭。只能有一个消费者。 */
    val incoming: ReceiveChannel<ProtocolMessage> get() = inbox

    val closed: Deferred<CloseReason> get() = closedReason

    val local: SocketAddr get() = sock.local

    val remote: SocketAddr get() = sock.remote

    internal fun start() {
        scope.launch { readLoop() }
        scope.launch { writeLoop() }
        scope.launch { timerLoop() }
    }

    /** Pending → Active（SESSION_RESULT(ACCEPTED) 或配对完成）。 */
    fun activate(): Boolean = stateFlow.compareAndSet(ConnState.Pending, ConnState.Active)

    /** 入队一条消息；连接正在关闭或已关闭时返回 false。 */
    fun enqueue(msg: ProtocolMessage, priority: Priority): Boolean {
        if (closing.get()) return false
        val ok = (if (priority == Priority.CONTROL) p0 else p1).trySend(Outgoing(msg, null)).isSuccess
        wake()
        return ok
    }

    fun attachChunkSource(src: ChunkSource): ChunkSourceHandle {
        val h = ChunkSourceHandle(this, src)
        synchronized(sources) { sources += h }
        wake()
        return h
    }

    internal fun wake() {
        wakeup.trySend(Unit)
    }

    internal fun detach(h: ChunkSourceHandle) {
        synchronized(sources) {
            val i = sources.indexOf(h)
            if (i >= 0) {
                sources.removeAt(i)
                if (i < nextSource) nextSource--
                if (nextSource >= sources.size) nextSource = 0
            }
        }
    }

    /** 写出已入队的 P0 消息和 [last] 之后以 [reason] 关闭；此后不再接受新的出站消息。超过 closeGraceMs 仍未写出时强制关闭。 */
    internal fun sendAndClose(last: ProtocolMessage?, reason: CloseReason) {
        if (!closing.compareAndSet(false, true)) return
        p0.trySend(Outgoing(last, reason))
        wake()
        scope.launch {
            delay(timings.closeGraceMs)
            terminate(reason)
        }
    }

    /** 主动关闭：先发送 SESSION_CLOSE([reason])，再关闭 TCP 连接（C1.11）。 */
    suspend fun closeGracefully(reason: SessionCloseReason): CloseReason {
        sendAndClose(SessionClose(reason), CloseReason.Local(reason))
        return closedReason.await()
    }

    /** 立即关闭，不发送任何消息。 */
    fun close(reason: CloseReason) = terminate(reason)

    private fun terminate(reason: CloseReason) {
        synchronized(this) {
            if (stateFlow.value is ConnState.Closed) return
            stateFlow.value = ConnState.Closed(reason)
        }
        closing.set(true)
        sock.close()
        inbox.close()
        closedReason.complete(reason)
        job.cancel()
    }

    // ---- 读循环：socket → RecordReader → FrameDecoder → 分发 ----

    private suspend fun readLoop() {
        val decoder = FrameDecoder(version)
        val buf = ByteArray(READ_BUFFER)
        while (true) {
            val n = reader.readSome(buf)
            if (n < 0) {
                terminate(CloseReason.PeerClosed(null))
                return
            }
            when (val r = receiveStream.feed(buf, 0, n)) {
                is RecordFeedResult.Plain -> for (c in r.chunks) if (!handle(decoder.feed(c.toByteArray()))) return
                is RecordFeedResult.IntegrityError -> {
                    for (c in r.chunksBefore) if (!handle(decoder.feed(c.toByteArray()))) return
                    terminate(CloseReason.IntegrityFailed)
                    return
                }
            }
        }
    }

    /** 处理一批解码结果；返回 false 表示连接已关闭或正在关闭，读循环应停止（之后不再解析任何字节）。 */
    private fun handle(batch: DecodeBatch): Boolean {
        for (m in batch.messages) {
            lastReceivedMs = clock.monotonicMs()
            if (!dispatch(m)) return false
        }
        val e = batch.error ?: return true
        terminate(CloseReason.ProtocolError(e.toString()))
        return false
    }

    private fun dispatch(m: ProtocolMessage): Boolean {
        if (closing.get()) return false
        when (m) {
            Heartbeat -> return true
            is SessionClose -> {
                terminate(CloseReason.PeerClosed(m.reason))
                return false
            }
            else -> Unit
        }
        if (stateFlow.value == ConnState.Pending) {
            if (m.type !in PENDING_ALLOWED) {
                sendAndClose(SessionResult(SessionResultCode.NOT_TRUSTED), CloseReason.PendingViolation)
                return false
            }
            // 由读循环原子地切换为 Active，使对方紧随其后的应用消息不会被当成 Pending 违规：
            // 发起方收到 ACCEPTED；接收方收到 PAIR_COMPLETE（之后的信任提交失败时由 PairingService 关闭连接）
            val activates = (role == Role.INITIATOR && m is SessionResult && m.result == SessionResultCode.ACCEPTED) ||
                (role == Role.RESPONDER && m === PairComplete)
            if (activates) activate()
        }
        inbox.trySend(m)
        return true
    }

    // ---- 写循环：P0 → P1 → P2（各源轮流各取 1 块） ----

    private suspend fun writeLoop() {
        while (true) {
            val item = nextOutgoing()
            if (item == null) {
                wakeup.receive()
                continue
            }
            val msg = item.message
            if (msg != null && !(msg === Heartbeat && skipHeartbeat())) {
                val frame = when (val e = FrameEncoder.encode(msg, version)) {
                    is EncodeResult.Ok -> e.bytes
                    is EncodeResult.PayloadTooLarge -> null // 调用方保证不超限；万一超限只丢弃这一条
                }
                if (frame != null) {
                    val record = try {
                        sendCipher.seal(frame)
                    } catch (_: IllegalStateException) {
                        terminate(CloseReason.ProtocolError("record counter exhausted"))
                        return
                    }
                    if (!sock.write(record)) {
                        terminate(CloseReason.PeerClosed(null))
                        return
                    }
                    lastSentMs = clock.monotonicMs()
                    timerKick.trySend(Unit)
                }
            }
            if (msg === Heartbeat) {
                // 写出或跳过都要让计时循环重新计算：排队期间它只等无响应时刻
                heartbeatQueued = false
                timerKick.trySend(Unit)
            }
            item.closeAfter?.let {
                terminate(it)
                return
            }
        }
    }

    /** 心跳出队时如果最近 heartbeatMs 内已经发送过其他帧，就不再发送（只在空闲满 5 s 时发送 HEARTBEAT）。 */
    private fun skipHeartbeat(): Boolean = clock.monotonicMs() - lastSentMs < timings.heartbeatMs

    private fun nextOutgoing(): Outgoing? {
        p0.tryReceive().getOrNull()?.let { return it }
        p1.tryReceive().getOrNull()?.let { return it }
        return pollSources()?.let { Outgoing(it, null) }
    }

    private fun pollSources(): ProtocolMessage? = synchronized(sources) {
        val n = sources.size
        for (k in 0 until n) {
            val i = (nextSource + k) % n
            val m = sources[i].source.poll()
            if (m != null) {
                nextSource = (i + 1) % n
                return m
            }
        }
        null
    }

    // ---- 计时：空闲 5 s 发送 HEARTBEAT；15 s 未收到任何帧以“对方无响应”关闭 ----

    private suspend fun timerLoop() {
        while (true) {
            val now = clock.monotonicMs()
            val deadAt = lastReceivedMs + timings.unresponsiveMs
            if (now >= deadAt) {
                terminate(CloseReason.PeerUnresponsive)
                return
            }
            val beatAt = lastSentMs + timings.heartbeatMs
            if (now >= beatAt && !heartbeatQueued && !closing.get()) {
                heartbeatQueued = true
                p0.trySend(Outgoing(Heartbeat, null))
                wake()
            }
            val next = if (heartbeatQueued) deadAt else minOf(beatAt, deadAt)
            withTimeoutOrNull(maxOf(1L, next - now)) { timerKick.receive() }
        }
    }

    internal companion object {
        const val READ_BUFFER = 64 * 1024

        /** Pending 阶段允许的消息（C1.11 白名单，另加 HEARTBEAT 与 SESSION_CLOSE）。 */
        val PENDING_ALLOWED: Set<MessageType> = setOf(
            MessageType.SESSION_RESULT,
            MessageType.SESSION_CLOSE,
            MessageType.HEARTBEAT,
            MessageType.PAIR_REQUEST,
            MessageType.PAIR_NONCE,
            MessageType.PAIR_REVEAL,
            MessageType.PAIR_DECISION,
            MessageType.PAIR_CANCEL,
            MessageType.PAIR_COMPLETE,
            MessageType.QR_PAIR_REQUEST,
            MessageType.QR_PAIR_RESULT,
        )
    }
}
