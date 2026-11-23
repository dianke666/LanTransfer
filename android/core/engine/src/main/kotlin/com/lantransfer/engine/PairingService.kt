package com.lantransfer.engine

import com.lantransfer.domain.AddressRules
import com.lantransfer.domain.ConnId
import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.PairingDone
import com.lantransfer.domain.PairingEffect
import com.lantransfer.domain.PairingEvent
import com.lantransfer.domain.PairingReducer
import com.lantransfer.domain.PairingResult
import com.lantransfer.domain.PairingRole
import com.lantransfer.domain.PairingSession
import com.lantransfer.domain.PairingState
import com.lantransfer.domain.PairingTokenRegistry
import com.lantransfer.domain.PeerInfo
import com.lantransfer.domain.Rng
import com.lantransfer.domain.TokenCheck
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.NetworkMonitor
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.Fingerprint
import com.lantransfer.protocol.Intent
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.QrCodec
import com.lantransfer.protocol.QrDecodeResult
import com.lantransfer.protocol.QrError
import com.lantransfer.protocol.QrPairRequest
import com.lantransfer.protocol.QrPayload
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.SessionCloseReason
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch

// 配对服务（设计 C4.3，需求 7、8、9.3、9.9、10.3、10.7、10.8）：执行 PairingReducer 的 effects。
// 全部状态只在一个 actor 协程里修改；连接的读取、计时、连接尝试都把结果作为命令送回 actor。

/** 配对界面状态（由 PairingReducer 的会话推导）。 */
sealed interface PairingUiState {
    data object Idle : PairingUiState

    /** 发起方 / 扫描方：连接与握手中。 */
    data class Connecting(val role: PairingRole) : PairingUiState

    /** 发起方：已发出请求，等待对方确认（7.1）。 */
    data class WaitingForPeer(val peer: PeerInfo) : PairingUiState

    /** 发起方：显示验证码，等待对方决定（7.2）。 */
    data class ShowingCode(val peer: PeerInfo, val sas: String) : PairingUiState

    /** 接收方确认界面（7.3）；[sas] 非空后才能接受。 */
    data class Request(val id: RequestId, val peer: PeerInfo, val sas: String?) : PairingUiState

    /** 已接受 / 令牌已通过，等待完成或正在保存信任。 */
    data class Completing(val peer: PeerInfo, val role: PairingRole) : PairingUiState
}

/** 接收方确认界面的标识；过期的界面上点击“接受”“拒绝”不起作用。 */
@JvmInline
value class RequestId(val value: Long)

/** 配对的界面提示事件。 */
sealed interface PairingNotice {
    /** 本次配对结束；CONNECT_FAILED 时 [failure] 为连接失败原因（E1）。 */
    data class Finished(val done: PairingDone, val failure: OpenFailure?) : PairingNotice

    /** 接收方自动拒绝的请求（冷却期内或忙），不弹出界面（7.8、7.9）。 */
    data class AutoRejected(val peer: DeviceId, val result: PairingResult) : PairingNotice
}

sealed interface PairingOutcome {
    /** 配对结束；CONNECT_FAILED 时 [failure] 为连接失败原因。 */
    data class Done(val result: PairingResult, val failure: OpenFailure? = null) : PairingOutcome

    /** 手动连接的目标已是可信设备：直接得到 Session，不进入配对（6.6）。 */
    data class AlreadyTrusted(val session: Session) : PairingOutcome

    /** 本机已有进行中的配对（7.9）。 */
    data object Busy : PairingOutcome

    /** 二维码文本无法解析（23.6）。 */
    data class InvalidQr(val error: QrError) : PairingOutcome
}

/** 二维码界面（显示方）。 */
sealed interface QrScreenState {
    data class Showing(val text: String, val payload: QrPayload) : QrScreenState

    /** 没有活动接口（8.13）。 */
    data object NoNetwork : QrScreenState
}

class PairingService(
    private val sessions: SessionManager,
    private val trust: TrustStore,
    private val tokens: PairingTokenRegistry,
    private val clock: Clock,
    private val rng: Rng,
    private val scope: CoroutineScope,
    private val identity: DeviceIdentity,
    private val network: NetworkMonitor,
    private val displayName: () -> String = { identity.displayName },
    /** 实际监听端口（写入二维码）；尚未监听时为 null。 */
    private val listenPort: () -> Int? = { null },
    /** 传输引擎的“按对端取消”入口：解除配对时对进行中的任务按本机取消处理并发送 TRANSFER_CANCEL(UNPAIRED)（Q7）。 */
    private val cancelTransfers: suspend (DeviceId) -> Unit = {},
) {
    private val inbox = Channel<Cmd>(Channel.UNLIMITED)
    private val uiFlow = MutableStateFlow<PairingUiState>(PairingUiState.Idle)
    private val noticeFlow = MutableSharedFlow<PairingNotice>(extraBufferCapacity = 64)

    // ---- 只在 actor 中访问 ----
    private var pairing = PairingState()
    private val conns = HashMap<ConnId, Entry>()
    private val staged = HashMap<DeviceId, PeerInfo>()
    private var nextConn = 1L
    private var outcome: CompletableDeferred<PairingOutcome>? = null
    private var lastFailure: OpenFailure? = null
    private var alreadyTrusted: Session? = null

    private class Entry(val pending: PendingConnection) {
        var reader: Job? = null
        var requested = false
    }

    private sealed interface Cmd {
        data class Event(val e: PairingEvent) : Cmd

        class StartPair(val target: Target, val forcePair: Boolean, val result: CompletableDeferred<PairingOutcome>) : Cmd

        class StartQr(val payload: QrPayload, val result: CompletableDeferred<PairingOutcome>) : Cmd

        class OpenDone(val r: OpenResult) : Cmd

        class Inbound(val p: PendingConnection) : Cmd

        class Message(val conn: ConnId, val msg: ProtocolMessage) : Cmd

        class Closed(val conn: ConnId) : Cmd

        class IdleCheck(val conn: ConnId) : Cmd

        class Respond(val id: RequestId, val accept: Boolean) : Cmd
    }

    init {
        scope.launch { for (cmd in inbox) handle(cmd) }
        scope.launch { sessions.pendingInbound.collect { inbox.send(Cmd.Inbound(it)) } }
    }

    val state: StateFlow<PairingUiState> get() = uiFlow

    val notices: SharedFlow<PairingNotice> get() = noticeFlow

    /**
     * 点选配对与手动连接共用（6.6、7.1）：对方是可信设备且公钥一致时直接得到 Session（[PairingOutcome.AlreadyTrusted]）；
     * [forcePair] 为 true 时总是配对（对方已解除配对后的“重新配对”，9.5）。
     */
    suspend fun pair(target: Target, forcePair: Boolean = false): PairingOutcome {
        val d = CompletableDeferred<PairingOutcome>()
        inbox.send(Cmd.StartPair(target, forcePair, d))
        return d.await()
    }

    /** 发起方 / 扫描方取消（7.10）。 */
    fun cancel() {
        inbox.trySend(Cmd.Event(PairingEvent.UserCancel(clock.monotonicMs())))
    }

    /** 接收方点击“接受”或“拒绝”（7.4、7.6）。 */
    fun respond(request: RequestId, accept: Boolean) {
        inbox.trySend(Cmd.Respond(request, accept))
    }

    /** 扫描方（8.4–8.12）：解析二维码，连接时在发送 msg3 之前比对公钥指纹（SessionManager 内完成，10.7）。 */
    suspend fun pairByQr(qrText: String): PairingOutcome {
        val payload = when (val r = QrCodec.decode(qrText)) {
            is QrDecodeResult.Error -> return PairingOutcome.InvalidQr(r.kind)
            is QrDecodeResult.Ok -> r.payload
        }
        val d = CompletableDeferred<PairingOutcome>()
        inbox.send(Cmd.StartQr(payload, d))
        return d.await()
    }

    /**
     * 显示方（8.1、8.7、8.13）：每秒检查令牌是否需要刷新以及本机地址集合是否变化，任一变化都重新生成令牌和二维码；
     * 没有活动接口（或尚未监听）时输出 NoNetwork。
     */
    fun qrScreen(): Flow<QrScreenState> = flow {
        var shownIps: List<Ipv4>? = null
        var shownPort: Int? = null
        var shownName: String? = null
        var shown: QrScreenState? = null
        while (true) {
            val ips = AddressRules.qrAddressList(network.interfaces.value.toList())
            val port = listenPort()
            val name = displayName()
            val next: QrScreenState? = if (ips.isEmpty() || port == null) {
                QrScreenState.NoNetwork.takeIf { shown != it }
            } else {
                val now = clock.monotonicMs()
                val refresh = shown !is QrScreenState.Showing || ips != shownIps || port != shownPort || name != shownName ||
                    synchronized(tokens) { tokens.needsRefresh(now) }
                if (refresh) {
                    val token = synchronized(tokens) { tokens.issue(now) }
                    val fp = Bytes.of(Fingerprint.of(identity.keyPair.publicKey.toByteArray()))
                    val payload = QrPayload(identity.deviceId, name, ips.map { it.bits }, port, fp, token.value)
                    QrScreenState.Showing(QrCodec.encode(payload), payload)
                } else {
                    null
                }
            }
            if (next != null) {
                shown = next
                shownIps = ips
                shownPort = port
                shownName = name
                emit(next)
            }
            delay(QR_CHECK_MS)
        }
    }

    /**
     * 解除配对（9.3、10.8）：删除信任（含“自动接收文件”设置），对进行中的任务按本机取消处理，再发送 SESSION_CLOSE(UNPAIRED)。
     * Session 关闭最多等待 closeGraceMs，整个过程在 2 s 内完成。
     */
    suspend fun unpair(peer: DeviceId) {
        trust.delete(peer)
        cancelTransfers(peer)
        sessions.close(peer, SessionCloseReason.UNPAIRED)
    }

    // ---- actor ----

    private fun handle(cmd: Cmd) {
        val now = clock.monotonicMs()
        when (cmd) {
            is Cmd.Event -> step(cmd.e)
            is Cmd.StartPair -> {
                if (pairing.session != null) {
                    cmd.result.complete(PairingOutcome.Busy)
                    return
                }
                begin(cmd.result)
                step(PairingEvent.StartPair((cmd.target as? Target.Device)?.id ?: UNKNOWN_PEER, now))
                val intent = if (cmd.forcePair) Intent.PAIR else null
                scope.launch { inbox.send(Cmd.OpenDone(sessions.open(cmd.target, intent))) }
            }
            is Cmd.StartQr -> {
                if (pairing.session != null) {
                    cmd.result.complete(PairingOutcome.Busy)
                    return
                }
                begin(cmd.result)
                step(PairingEvent.StartQrPair(cmd.payload.deviceId, cmd.payload.token, now))
                scope.launch { inbox.send(Cmd.OpenDone(sessions.open(Target.Qr(cmd.payload)))) }
            }
            is Cmd.OpenDone -> onOpenDone(cmd.r, now)
            is Cmd.Inbound -> {
                val id = register(cmd.p, requested = false)
                scope.launch {
                    delay(REQUEST_WAIT_MS)
                    inbox.send(Cmd.IdleCheck(id))
                }
            }
            is Cmd.Message -> onMessage(cmd.conn, cmd.msg, now)
            is Cmd.Closed -> if (conns.containsKey(cmd.conn)) {
                step(PairingEvent.Disconnected(cmd.conn, now))
                conns.remove(cmd.conn)?.reader?.cancel()
            }
            is Cmd.IdleCheck -> conns[cmd.conn]?.takeIf { !it.requested }?.pending?.close() // 入站连接迟迟不发请求：关闭
            is Cmd.Respond -> {
                val s = pairing.session
                if (s is PairingSession.AwaitingUser && s.conn.value == cmd.id.value) step(PairingEvent.UserDecision(cmd.accept, now))
            }
        }
    }

    private fun begin(result: CompletableDeferred<PairingOutcome>) {
        outcome = result
        lastFailure = null
        alreadyTrusted = null
    }

    private fun onOpenDone(r: OpenResult, now: Long) {
        if (pairing.session !is PairingSession.Connecting) {
            // 已取消或本机计时已到期：丢弃迟到的连接
            if (r is OpenResult.Pairing) r.connection.close()
            return
        }
        when (r) {
            is OpenResult.Pairing -> {
                val id = register(r.connection, requested = true)
                step(PairingEvent.Connected(id, infoOf(r.connection), identity.keyPair.publicKey, Bytes.of(rng.nextBytes(NONCE_SIZE)), now))
            }
            is OpenResult.Ok -> {
                alreadyTrusted = r.session
                step(PairingEvent.ConnectFailed(now))
            }
            is OpenResult.Failed -> {
                lastFailure = r.failure
                step(PairingEvent.ConnectFailed(now))
            }
        }
    }

    /** 登记一条配对连接：读取消息、监视关闭。接收方读到 PAIR_COMPLETE 后停止读取，之后的消息属于 Session。 */
    private fun register(p: PendingConnection, requested: Boolean): ConnId {
        val id = ConnId(nextConn++)
        val e = Entry(p).also { it.requested = requested }
        conns[id] = e
        val reader = scope.launch {
            for (m in p.incoming) {
                inbox.send(Cmd.Message(id, m))
                if (m === PairComplete && p.role == Role.RESPONDER) break
            }
        }
        e.reader = reader
        scope.launch {
            // 等读取结束后再报告断开：保证对方关闭前发来的最后一条消息（PAIR_CANCEL、PAIR_DECISION 等）先被处理
            p.closed.await()
            reader.join()
            inbox.send(Cmd.Closed(id))
        }
        return id
    }

    /** 对方信息；只有本机发起时记录地址与端口（入站连接的对端端口不是监听端口）。 */
    private fun infoOf(p: PendingConnection): PeerInfo = PeerInfo(
        p.peer.deviceId,
        p.peer.displayName,
        p.peer.deviceType,
        p.peer.publicKey,
        ip = p.remote.ip,
        port = if (p.role == Role.INITIATOR) p.remote.port else null,
    )

    private fun onMessage(conn: ConnId, msg: ProtocolMessage, now: Long) {
        val e = conns[conn] ?: return
        if (!e.requested) {
            // 接收方 / 显示方：第一条消息必须是对应的请求；自动拒绝在发出 PAIR_NONCE 之前由归约器判定（7.8、7.9）
            when {
                msg is PairRequest && e.pending.intent == Intent.PAIR -> {
                    e.requested = true
                    step(PairingEvent.PairRequestReceived(conn, infoOf(e.pending), identity.keyPair.publicKey, msg.commitment, Bytes.of(rng.nextBytes(NONCE_SIZE)), now))
                }
                msg is QrPairRequest && e.pending.intent == Intent.QR_PAIR -> {
                    e.requested = true
                    val lease = (synchronized(tokens) { tokens.check(msg.token.toByteArray(), now) } as? TokenCheck.Ok)?.lease
                    step(PairingEvent.QrRequestChecked(conn, infoOf(e.pending), lease, now))
                }
                else -> e.pending.close()
            }
            return
        }
        step(PairingEvent.MessageReceived(conn, msg, now))
    }

    private fun step(e: PairingEvent) {
        val s = PairingReducer.reduce(pairing, e)
        pairing = s.state
        // 先停止发起方/扫描方读取并更新 UI 状态，再执行含 Finished 的 effects，避免观察者 / await 唤醒时看到旧状态
        val now = pairing.session
        if (now is PairingSession.Saving && (now.role == PairingRole.INITIATOR || now.role == PairingRole.QR_SCANNER)) {
            conns[now.conn]?.reader?.cancel()
        }
        uiFlow.value = uiOf(pairing.session)
        for (f in s.effects) execute(f)
    }

    private fun execute(f: PairingEffect) {
        when (f) {
            is PairingEffect.Send -> conns[f.conn]?.pending?.send(f.msg)
            is PairingEffect.Close -> conns[f.conn]?.pending?.close()
            PairingEffect.CancelConnect -> Unit // 连接尝试自行结束；结果到达时若已不在 Connecting 就关闭（onOpenDone）
            is PairingEffect.ArmTimer -> scope.launch {
                delay(maxOf(0L, f.atMono - clock.monotonicMs()))
                inbox.send(Cmd.Event(PairingEvent.Tick(clock.monotonicMs())))
            }
            is PairingEffect.ShowRequest, is PairingEffect.ShowSas, PairingEffect.DismissRequest -> Unit // 界面状态由会话推导
            is PairingEffect.SaveTrust -> {
                val ok = trust.upsert(trusted(f.info)) == StoreResult.Ok
                inbox.trySend(Cmd.Event(PairingEvent.TrustStored(f.info.deviceId, ok)))
            }
            is PairingEffect.StageTrust -> staged[f.info.deviceId] = f.info
            is PairingEffect.CommitTrust -> {
                val info = staged.remove(f.peer)
                val ok = info != null && trust.upsert(trusted(info)) == StoreResult.Ok
                inbox.trySend(Cmd.Event(PairingEvent.TrustStored(f.peer, ok)))
            }
            is PairingEffect.DiscardStagedTrust -> staged.remove(f.peer)
            is PairingEffect.DeleteTrust -> trust.delete(f.peer)
            is PairingEffect.MarkTokenUsed -> synchronized(tokens) { tokens.markUsed(f.lease) }
            is PairingEffect.ReleaseToken -> synchronized(tokens) { tokens.release(f.lease) }
            is PairingEffect.PromoteSession -> conns.remove(f.conn)?.let {
                it.reader?.cancel()
                it.pending.promote()
            }
            is PairingEffect.AutoRejected -> noticeFlow.tryEmit(PairingNotice.AutoRejected(f.peer, f.result))
            is PairingEffect.Finished -> onFinished(f.done)
        }
    }

    /** 整条替换同一设备 ID 的旧条目（9.9），“自动接收文件”回到默认的关闭。 */
    private fun trusted(info: PeerInfo) = TrustedDevice(
        info.deviceId,
        info.displayName,
        info.deviceType,
        info.publicKey,
        autoAccept = false,
        pairedAtUtcMs = clock.wallMs(),
        lastAddress = info.port?.let { info.ip },
        lastPort = info.port,
    )

    private fun onFinished(done: PairingDone) {
        val mine = done.role == PairingRole.INITIATOR || done.role == PairingRole.QR_SCANNER
        val session = alreadyTrusted
        if (mine && session != null) {
            outcome?.complete(PairingOutcome.AlreadyTrusted(session))
            outcome = null
            alreadyTrusted = null
            return
        }
        val failure = if (done.result == PairingResult.CONNECT_FAILED) lastFailure ?: OpenFailure.Timeout else null
        noticeFlow.tryEmit(PairingNotice.Finished(done, failure))
        if (mine) {
            outcome?.complete(PairingOutcome.Done(done.result, failure))
            outcome = null
            lastFailure = null
        }
    }

    private fun uiOf(s: PairingSession?): PairingUiState = when (s) {
        null -> PairingUiState.Idle
        is PairingSession.Connecting -> PairingUiState.Connecting(s.role)
        is PairingSession.AwaitingPeer -> PairingUiState.WaitingForPeer(s.info)
        is PairingSession.ShowingSas -> PairingUiState.ShowingCode(s.info, s.sas)
        is PairingSession.AwaitingUser -> PairingUiState.Request(RequestId(s.conn.value), s.info, s.sas)
        is PairingSession.AwaitingQrResult -> PairingUiState.Completing(s.info, PairingRole.QR_SCANNER)
        is PairingSession.AwaitingComplete -> PairingUiState.Completing(s.info, s.role)
        is PairingSession.Saving -> PairingUiState.Completing(s.info, s.role)
    }

    companion object {
        /** 二维码界面的检查周期（8.7 要求 2 s 内刷新）。 */
        const val QR_CHECK_MS = 1_000L

        /** 入站配对连接在握手后这么久仍未发来请求时关闭。 */
        const val REQUEST_WAIT_MS = 10_000L

        private const val NONCE_SIZE = 32

        /** Address 目标在握手前不知道对方设备 ID，连接阶段用全零占位。 */
        private val UNKNOWN_PEER = DeviceId(Bytes16.of(ByteArray(16)))
    }
}
