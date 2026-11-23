package com.lantransfer.engine

import com.lantransfer.domain.AddressRules
import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.Route
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.NetworkMonitor
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.engine.ports.TcpListener
import com.lantransfer.engine.ports.Transport
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.EphemeralKeySource
import com.lantransfer.protocol.Fingerprint
import com.lantransfer.protocol.Heartbeat
import com.lantransfer.protocol.Intent
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.QrPayload
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.SessionClose
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.SessionResult
import com.lantransfer.protocol.SessionResultCode
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch

/** 已注册的 Session：与一个对端设备之间唯一的已认证连接（C1.11）。 */
class Session internal constructor(
    val peer: PeerIdentity,
    /** 发起方设备 ID（同时互连去重用）。 */
    val initiator: DeviceId,
    internal val connection: Connection,
    /** 连接所在的本地接口；找不到时为 null。 */
    val via: LocalInterface?,
    val initiatedLocally: Boolean,
) {
    val info: SessionInfo get() = SessionInfo(peer, connection.remote, initiatedLocally, connection.version)

    /** 应用消息（单一消费者，通常是 EngineHost 的分发器）；Session 关闭后通道关闭。 */
    val incoming: ReceiveChannel<ProtocolMessage> get() = connection.incoming

    val closed: Deferred<CloseReason> get() = connection.closed

    val isOpen: Boolean get() = connection.state.value !is ConnState.Closed

    val version: ProtocolVersion get() = connection.version

    fun send(msg: ProtocolMessage, priority: Priority = Priority.CONTROL): Boolean = connection.enqueue(msg, priority)

    fun attachChunkSource(src: ChunkSource): ChunkSourceHandle = connection.attachChunkSource(src)

    override fun toString(): String = "Session(${peer.deviceId}, initiator=$initiator)"
}

/** 握手完成、intent 为 PAIR / QR_PAIR 的连接（Pending），由 PairingService 驱动。 */
class PendingConnection internal constructor(
    val peer: PeerIdentity,
    val intent: Intent,
    val role: Role,
    internal val connection: Connection,
    val via: LocalInterface?,
    private val manager: SessionManager,
) {
    /** 收到的配对消息（只有一个消费者）。 */
    val incoming: ReceiveChannel<ProtocolMessage> get() = connection.incoming

    val closed: Deferred<CloseReason> get() = connection.closed

    val remote: SocketAddr get() = connection.remote

    fun send(msg: ProtocolMessage): Boolean = connection.enqueue(msg, Priority.CONTROL)

    /** 写出 [last]（可为 null）后关闭。 */
    fun close(last: ProtocolMessage? = null) = connection.sendAndClose(last, CloseReason.Local(SessionCloseReason.NORMAL))

    /** 配对完成（对方此时已是 Trusted_Device）：转为 Session 并注册；连接已关闭时返回 null。 */
    fun promote(): Session? = manager.promote(this)
}

/**
 * 会话管理（设计 C4.1、C1.11）：监听与入站握手、出站 open（Device / Address / Qr 目标、10 s 共用预算、扫码每地址 5 s）、
 * 同一对端的 open 合并、同时互连去重、SESSION_RESULT 处理，以及接口消失检测。
 *
 * 同时互连的判定（C1.11 的具体化，两端一致）：
 * - 对端 P 发来 intent = SESSION 的入站连接 N，本机已注册与 P 的 Session S：S 由 P 发起，或 P 的设备 ID 小于本机 → P 已丢失 S，
 *   关闭 S（Superseded）并接受 N；否则回复 DUPLICATE（并在 S 上发 1 次 HEARTBEAT 探测）。
 * - 未注册 S，但本机有到 P 的出站尝试 O 且 O 已读完 msg2：P 的设备 ID 小于本机 → 接受 N 并放弃 O（O 的调用方得到 N 的 Session）；
 *   否则回复 DUPLICATE。O 尚未读完 msg2 时直接接受 N 并放弃 O。
 * - 发起方收到 DUPLICATE 时不算失败：在剩余预算内等待保留下来的那条连接注册完成。
 */
class SessionManager(
    private val transport: Transport,
    private val network: NetworkMonitor,
    private val identity: DeviceIdentity,
    private val deviceType: DeviceType,
    private val trust: TrustStore,
    private val clock: Clock,
    private val scope: CoroutineScope,
    private val keys: EphemeralKeySource = EphemeralKeySource.secure(),
    private val displayName: () -> String = { identity.displayName },
    /** 设备列表中的地址（DiscoveryService 提供）；不在列表中时取 TrustStore 记录的最近成功地址。 */
    private val addressBook: (DeviceId) -> SocketAddr? = { null },
    private val version: ProtocolVersion = ProtocolVersion.CURRENT,
    private val timings: SessionTimings = SessionTimings(),
) {
    private val guard = LocalNetworkGuard(transport, network)
    private val lock = Any()
    private val registered = HashMap<DeviceId, Session>()
    private val outgoing = HashMap<DeviceId, Attempt>()
    private val live = LinkedHashSet<Live>()
    private val sessionsFlow = MutableStateFlow<Map<DeviceId, SessionInfo>>(emptyMap())
    private val eventsFlow = MutableSharedFlow<SessionEvent>(extraBufferCapacity = 256)
    private val failuresFlow = MutableSharedFlow<InboundFailure>(extraBufferCapacity = 64)
    private val pending = Channel<PendingConnection>(Channel.UNLIMITED)
    private var listener: TcpListener? = null

    /** 一次出站尝试（同一对端的并发 open 共用）。 */
    private class Attempt {
        val result = CompletableDeferred<OpenResult>()

        @Volatile
        var authenticated = false

        @Volatile
        var aborted = false

        @Volatile
        var sock: TcpConn? = null

        @Volatile
        var connection: Connection? = null
    }

    /** 一条存活的连接及其本地地址（接口消失检测用）。 */
    private class Live(val connection: Connection, val localIp: Ipv4)

    init {
        scope.launch {
            network.interfaces.collect { ifaces ->
                val addresses = ifaces.mapTo(HashSet()) { it.address }
                val lost = synchronized(lock) { live.filter { it.localIp !in addresses } }
                lost.forEach { it.connection.close(CloseReason.LocalNetworkLost) }
            }
        }
    }

    val sessions: StateFlow<Map<DeviceId, SessionInfo>> get() = sessionsFlow

    val events: SharedFlow<SessionEvent> get() = eventsFlow

    val inboundFailures: SharedFlow<InboundFailure> get() = failuresFlow

    /** 入站的配对连接（intent = PAIR / QR_PAIR），交给 PairingService。 */
    val pendingInbound: Flow<PendingConnection> get() = pending.receiveAsFlow()

    fun session(peer: DeviceId): Session? = synchronized(lock) { registered[peer] }

    private val localIdentity: LocalIdentity get() = LocalIdentity(identity.deviceId, identity.keyPair, displayName(), deviceType)

    private val selfId: DeviceId get() = identity.deviceId

    // ---- 监听 ----

    /** 依次尝试 42580、42590–42599，最后由系统分配（5.7、5.9）。 */
    fun startListening(): ListenResult {
        val l = transport.listen(LISTEN_PORTS) ?: return ListenResult.Failed
        listener = l
        scope.launch {
            while (true) {
                val c = l.accept() ?: break
                scope.launch { handleInbound(c) }
            }
        }
        return ListenResult.Ok(l.port)
    }

    /** 停止监听并主动关闭全部 Session 与 Pending 连接（21.4、21.9）。 */
    suspend fun shutdown(reason: SessionCloseReason) {
        listener?.close()
        listener = null
        val all = synchronized(lock) { live.map { it.connection } }
        all.forEach { it.sendAndClose(SessionClose(reason), CloseReason.Local(reason)) }
        all.forEach { it.closed.await() }
    }

    /** 主动关闭与 [peer] 的 Session（先发 SESSION_CLOSE）。 */
    suspend fun close(peer: DeviceId, reason: SessionCloseReason) {
        session(peer)?.connection?.closeGracefully(reason)
    }

    private suspend fun handleInbound(conn: TcpConn) {
        val route = guard.acceptInbound(conn)
        if (route == null) {
            conn.close() // 来源不在本地子网内：静默关闭（2.9）
            return
        }
        val deadline = clock.monotonicMs() + timings.inboundHandshakeMs
        val done = when (val h = Handshake.respond(conn, localIdentity, keys, clock, deadline, version)) {
            is HandshakeOutcome.Done -> h
            is HandshakeOutcome.Failed -> {
                conn.close()
                if (h.failure is OpenFailure.VersionIncompatible || h.failure == OpenFailure.HandshakeAuthFailed) {
                    failuresFlow.tryEmit(InboundFailure(conn.remote, null, h.failure))
                }
                return
            }
            is HandshakeOutcome.Merged -> {
                conn.close()
                return
            }
        }
        val peer = done.peer
        if (peer.deviceId == selfId) {
            conn.close()
            return
        }
        val c = newConnection(conn, done, Role.RESPONDER)
        when (done.intent) {
            Intent.PAIR, Intent.QR_PAIR -> pending.trySend(PendingConnection(peer, done.intent, Role.RESPONDER, c, route.via, this))
            Intent.SESSION -> acceptSession(c, peer, route.via)
        }
    }

    /** 接收方处理 intent = SESSION（C1.11）。 */
    private fun acceptSession(c: Connection, peer: PeerIdentity, via: LocalInterface?) {
        val trusted = trust.get(peer.deviceId)
        if (trusted == null) {
            c.sendAndClose(SessionResult(SessionResultCode.NOT_TRUSTED), CloseReason.Local(SessionCloseReason.NORMAL))
            return
        }
        if (trusted.publicKey != peer.publicKey) {
            c.sendAndClose(SessionResult(SessionResultCode.KEY_MISMATCH), CloseReason.Local(SessionCloseReason.NORMAL))
            failuresFlow.tryEmit(InboundFailure(c.remote, peer.deviceId, OpenFailure.KeyMismatch(trusted.displayName)))
            return
        }
        var superseded: Session? = null
        var abandoned: Attempt? = null
        var duplicateOf: Session? = null
        var accepted: Session? = null
        synchronized(lock) {
            val p = peer.deviceId
            val peerSmaller = p.value.compareUnsigned(selfId.value) < 0
            val s = registered[p]
            val o = outgoing[p]
            val accept = when {
                s != null -> s.initiator == p || peerSmaller
                o != null && o.authenticated -> peerSmaller
                else -> true
            }
            if (!accept) {
                duplicateOf = s
            } else {
                if (s != null) {
                    superseded = s
                    registered.remove(p)
                }
                if (o != null) {
                    o.aborted = true
                    outgoing.remove(p)
                    abandoned = o
                }
                c.enqueue(SessionResult(SessionResultCode.ACCEPTED), Priority.CONTROL)
                c.activate()
                accepted = register(c, peer, initiator = p, via = via)
            }
        }
        val session = accepted
        if (session == null) {
            c.sendAndClose(SessionResult(SessionResultCode.DUPLICATE), CloseReason.Local(SessionCloseReason.DUPLICATE))
            duplicateOf?.send(Heartbeat)
            return
        }
        superseded?.connection?.close(CloseReason.Superseded)
        abandoned?.let { a ->
            a.result.complete(OpenResult.Ok(session))
            abandon(a)
        }
        afterRegister(session)
    }

    /** 放弃一次出站尝试：已完成握手的发 SESSION_CLOSE(DUPLICATE)，否则直接关闭套接字。 */
    private fun abandon(a: Attempt) {
        val c = a.connection
        if (c != null) {
            c.sendAndClose(SessionClose(SessionCloseReason.DUPLICATE), CloseReason.Local(SessionCloseReason.DUPLICATE))
        } else {
            a.sock?.close()
        }
    }

    /** 在 [lock] 内调用。 */
    private fun register(c: Connection, peer: PeerIdentity, initiator: DeviceId, via: LocalInterface?): Session {
        val s = Session(peer, initiator, c, via, initiatedLocally = initiator == selfId)
        registered[peer.deviceId] = s
        sessionsFlow.value = registered.mapValues { it.value.info }
        return s
    }

    /** 注册之后：发出事件、更新可信设备名称（9.6）与最近成功地址（C4.1）、存在 Session 时加快接口轮询（18.8）、关闭时注销。 */
    private fun afterRegister(s: Session) {
        eventsFlow.tryEmit(SessionEvent.Opened(s))
        trust.get(s.peer.deviceId)?.let { t ->
            if (t.displayName != s.peer.displayName) trust.updateName(s.peer.deviceId, s.peer.displayName)
        }
        if (s.initiatedLocally) trust.recordAddress(s.peer.deviceId, s.connection.remote.ip, s.connection.remote.port)
        network.setFastPolling(true)
        scope.launch {
            val reason = s.closed.await()
            val anyLeft = synchronized(lock) {
                if (registered[s.peer.deviceId] === s) {
                    registered.remove(s.peer.deviceId)
                    sessionsFlow.value = registered.mapValues { it.value.info }
                }
                registered.isNotEmpty()
            }
            if (!anyLeft) network.setFastPolling(false)
            eventsFlow.tryEmit(SessionEvent.Closed(s, reason))
        }
    }

    internal fun promote(p: PendingConnection): Session? {
        var old: Session? = null
        val s = synchronized(lock) {
            if (p.connection.state.value is ConnState.Closed) return null
            old = registered.remove(p.peer.deviceId)
            p.connection.activate()
            register(p.connection, p.peer, initiator = if (p.role == Role.INITIATOR) selfId else p.peer.deviceId, via = p.via)
        }
        old?.connection?.close(CloseReason.Superseded)
        afterRegister(s)
        return s
    }

    private fun newConnection(conn: TcpConn, done: HandshakeOutcome.Done, role: Role): Connection {
        val c = Connection(conn, done.reader, role, done.version, done.send, done.receive, clock, scope, timings)
        val entry = Live(c, conn.local.ip)
        synchronized(lock) { live += entry }
        scope.launch {
            c.closed.await()
            synchronized(lock) { live -= entry }
        }
        c.start()
        return c
    }

    // ---- 出站 ----

    /**
     * 建立连接。[intent] 为 null 时收到 msg2 后按信任关系选择 SESSION 或 PAIR；PAIR / QR_PAIR 由 PairingService 显式传入
     * （传入 SESSION 等同于 null）。Qr 目标总是 QR_PAIR。
     */
    suspend fun open(target: Target, intent: Intent? = null): OpenResult {
        val pairIntent = intent?.takeIf { it != Intent.SESSION }
        return when (target) {
            is Target.Device -> openDevice(target.id, pairIntent)
            is Target.Address -> connectTo(target.ip, target.port, expected = null, pairIntent, qr = null, deadline = clock.monotonicMs() + timings.openMs, preset = null)
            is Target.Qr -> openQr(target.payload)
        }
    }

    private suspend fun openDevice(id: DeviceId, pairIntent: Intent?): OpenResult {
        val deadline = clock.monotonicMs() + timings.openMs
        val address = addressBook(id) ?: trust.get(id)?.let { t -> t.lastAddress?.let { ip -> t.lastPort?.let { SocketAddr(ip, it) } } }
        if (pairIntent != null) {
            address ?: return OpenResult.Failed(OpenFailure.Unreachable)
            return connectTo(address.ip, address.port, id, pairIntent, qr = null, deadline = deadline, preset = null)
        }
        var fresh = false
        val attempt = synchronized(lock) {
            registered[id]?.let { return OpenResult.Ok(it) }
            outgoing[id] ?: Attempt().also {
                outgoing[id] = it
                fresh = true
            }
        }
        if (fresh) {
            scope.launch {
                val r = if (address == null) {
                    OpenResult.Failed(OpenFailure.Unreachable)
                } else {
                    connectTo(address.ip, address.port, id, null, qr = null, deadline = deadline, preset = attempt)
                }
                attempt.result.complete(r)
                synchronized(lock) { if (outgoing[id] === attempt) outgoing.remove(id) }
            }
        }
        return attempt.result.await()
    }

    private suspend fun openQr(p: QrPayload): OpenResult {
        val ifaces = network.interfaces.value
        val ips = p.ipv4List.map { Ipv4(it) }
        val candidates = AddressRules.qrCandidates(ips, ifaces)
        if (candidates.isEmpty()) {
            val self = ips.any { AddressRules.classify(it, ifaces) == Route.Self }
            return OpenResult.Failed(if (self) OpenFailure.SelfConnection else OpenFailure.NotLocal)
        }
        var last: OpenFailure = OpenFailure.Unreachable
        for ((ip, _) in candidates) {
            val r = connectTo(ip, p.port, expected = null, Intent.QR_PAIR, qr = p, deadline = clock.monotonicMs() + timings.qrPerAddressMs, preset = null)
            if (r !is OpenResult.Failed) return r
            last = r.failure
            if (stopsQr(r.failure)) break
        }
        return OpenResult.Failed(last)
    }

    /**
     * 一次连接尝试：LocalNetworkGuard → 前导 → NoiseXX（msg2 后做身份检查）→ SESSION_RESULT。
     * [preset] 是 Device 目标预先登记的尝试；Address 目标在 msg2 之后（知道对端设备 ID 时）才登记。
     */
    private suspend fun connectTo(
        ip: Ipv4,
        port: Int,
        expected: DeviceId?,
        pairIntent: Intent?,
        qr: QrPayload?,
        deadline: Long,
        preset: Attempt?,
    ): OpenResult {
        var attempt = preset
        val sock = when (val g = guard.connect(ip, port, deadline - clock.monotonicMs())) {
            is LocalNetworkGuard.Outcome.Failed -> return OpenResult.Failed(g.failure)
            is LocalNetworkGuard.Outcome.Connected -> g
        }
        val conn = sock.conn
        attempt?.sock = conn
        if (attempt?.aborted == true) {
            conn.close()
            return attempt.result.await()
        }
        val outcome = Handshake.initiate(conn, localIdentity, keys, clock, deadline, version) { peer ->
            val d = decide(peer, expected, pairIntent, qr, attempt)
            attempt = d.attempt
            d.decision
        }
        val done = when (outcome) {
            is HandshakeOutcome.Failed -> {
                conn.close()
                return attempt?.takeIf { it.aborted }?.result?.await() ?: OpenResult.Failed(outcome.failure)
            }
            is HandshakeOutcome.Merged -> {
                conn.close()
                return awaitRetained(outcome.peer.deviceId, attempt, deadline)
            }
            is HandshakeOutcome.Done -> outcome
        }
        val c = newConnection(conn, done, Role.INITIATOR)
        if (done.intent != Intent.SESSION) return OpenResult.Pairing(PendingConnection(done.peer, done.intent, Role.INITIATOR, c, sock.via, this))
        val a = attempt!!
        a.connection = c
        if (a.aborted) {
            abandon(a)
            return a.result.await()
        }
        val first = withDeadline(clock, deadline) { c.incoming.receiveCatching().getOrNull() }
        val result = when {
            first == Timed.Expired -> {
                c.close(CloseReason.Local(SessionCloseReason.NORMAL))
                OpenResult.Failed(OpenFailure.Timeout)
            }
            a.aborted -> {
                abandon(a)
                a.result.await()
            }
            else -> onSessionResult(c, done.peer, (first as Timed.Value).value, a, sock.via, deadline)
        }
        synchronized(lock) { if (outgoing[done.peer.deviceId] === a) outgoing.remove(done.peer.deviceId) }
        return result
    }

    private suspend fun onSessionResult(c: Connection, peer: PeerIdentity, first: ProtocolMessage?, a: Attempt, via: LocalInterface, deadline: Long): OpenResult {
        if (first !is SessionResult) {
            c.close(CloseReason.ProtocolError("expected SESSION_RESULT"))
            return OpenResult.Failed(OpenFailure.ProtocolError)
        }
        return when (first.result) {
            SessionResultCode.ACCEPTED -> {
                var old: Session? = null
                val s = synchronized(lock) {
                    if (a.aborted) {
                        null
                    } else {
                        if (outgoing[peer.deviceId] === a) outgoing.remove(peer.deviceId)
                        old = registered.remove(peer.deviceId)
                        register(c, peer, initiator = selfId, via = via)
                    }
                }
                old?.connection?.close(CloseReason.Superseded)
                if (s == null) {
                    abandon(a)
                    a.result.await()
                } else {
                    afterRegister(s)
                    OpenResult.Ok(s)
                }
            }
            SessionResultCode.NOT_TRUSTED -> {
                c.close(CloseReason.Local(SessionCloseReason.NORMAL))
                OpenResult.Failed(OpenFailure.NotTrusted)
            }
            SessionResultCode.KEY_MISMATCH -> {
                c.close(CloseReason.Local(SessionCloseReason.NORMAL))
                OpenResult.Failed(OpenFailure.PeerKeyMismatch(trust.get(peer.deviceId)?.displayName ?: peer.displayName))
            }
            SessionResultCode.DUPLICATE -> {
                c.close(CloseReason.Local(SessionCloseReason.DUPLICATE))
                synchronized(lock) { if (outgoing[peer.deviceId] === a) outgoing.remove(peer.deviceId) }
                awaitRetained(peer.deviceId, a, deadline)
            }
        }
    }

    /** 等待按 C1.11 保留下来的那条连接注册完成（在剩余预算内）。 */
    private suspend fun awaitRetained(peer: DeviceId, mine: Attempt?, deadline: Long): OpenResult {
        if (mine?.aborted == true) return mine.result.await()
        val other = synchronized(lock) {
            registered[peer]?.let { return OpenResult.Ok(it) }
            outgoing[peer]?.takeIf { it !== mine }
        }
        if (other != null) {
            return when (val r = withDeadline(clock, deadline) { other.result.await() }) {
                Timed.Expired -> OpenResult.Failed(OpenFailure.Timeout)
                is Timed.Value -> r.value
            }
        }
        return when (withDeadline(clock, deadline) { sessionsFlow.first { peer in it } }) {
            Timed.Expired -> OpenResult.Failed(OpenFailure.Timeout)
            is Timed.Value -> session(peer)?.let { OpenResult.Ok(it) } ?: OpenResult.Failed(OpenFailure.Timeout)
        }
    }

    private class Decided(val decision: Msg2Decision, val attempt: Attempt?)

    /** 发起方读完 msg2 后的身份检查（规范第 6 节）与 intent 选择、同一对端合并。 */
    private fun decide(peer: PeerIdentity, expected: DeviceId?, pairIntent: Intent?, qr: QrPayload?, attempt: Attempt?): Decided {
        fun stop(f: OpenFailure?) = Decided(Msg2Decision.Stop(f), attempt)
        if (peer.deviceId == selfId) return stop(OpenFailure.SelfConnection)
        if (qr != null && Bytes.of(Fingerprint.of(peer.publicKey.toByteArray())) != qr.fingerprint) return stop(OpenFailure.FingerprintMismatch)
        if (expected != null && peer.deviceId != expected) return stop(OpenFailure.Unreachable)
        val trusted = trust.get(peer.deviceId)
        if (trusted != null && trusted.publicKey != peer.publicKey) return stop(OpenFailure.KeyMismatch(trusted.displayName))
        val intent = pairIntent ?: if (trusted != null) Intent.SESSION else Intent.PAIR
        if (intent != Intent.SESSION) return Decided(Msg2Decision.Proceed(intent), attempt)
        synchronized(lock) {
            if (attempt?.aborted == true) return stop(null)
            if (registered.containsKey(peer.deviceId)) return stop(null)
            val other = outgoing[peer.deviceId]
            if (other != null && other !== attempt) return stop(null)
            val a = attempt ?: Attempt()
            outgoing[peer.deviceId] = a
            a.authenticated = true
            return Decided(Msg2Decision.Proceed(Intent.SESSION), a)
        }
    }

    companion object {
        /** 监听端口：42580 → 42590–42599 → 系统分配（5.7、5.9）。 */
        val LISTEN_PORTS: List<Int> = listOf(ProtocolConstants.DEFAULT_PORT) + (42590..42599) + 0

        /** 扫码时遇到这些失败不再尝试后面的候选地址：安全类失败、版本不兼容与自连接；网络类失败继续尝试下一个。 */
        private fun stopsQr(f: OpenFailure): Boolean = f is OpenFailure.FingerprintMismatch || f is OpenFailure.HandshakeAuthFailed ||
            f is OpenFailure.KeyMismatch || f is OpenFailure.VersionIncompatible || f is OpenFailure.SelfConnection
    }
}
