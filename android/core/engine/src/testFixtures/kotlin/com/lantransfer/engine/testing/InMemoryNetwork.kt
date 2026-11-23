package com.lantransfer.engine.testing

import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.engine.ports.ConnectResult
import com.lantransfer.engine.ports.Datagram
import com.lantransfer.engine.ports.NetworkMonitor
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.engine.ports.TcpListener
import com.lantransfer.engine.ports.Transport
import com.lantransfer.engine.ports.UdpEndpoint
import com.lantransfer.engine.ports.UdpOpenResult
import com.lantransfer.protocol.Bytes
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.io.ByteArrayOutputStream

// 内存网络（设计 C5、任务 14.1）：多台“主机”各有一个 InMemoryTransport 和一组接口；TCP 连接是成对的内存管道，
// 线上字节全部被捕获；UDP 按接口子网投递单播、定向广播与组播（含回环）。可注入连接失败、端口占用、静默断网和 RST。

/** NetworkMonitor 替身：[set] 改变活动接口集合；[fastPollingHistory] 记录 setFastPolling 的调用。 */
class FakeNetworkMonitor(initial: Set<LocalInterface> = emptySet()) : NetworkMonitor {
    private val flow = MutableStateFlow(initial)
    override val interfaces: StateFlow<Set<LocalInterface>> get() = flow
    var fastPolling = false
        private set
    val fastPollingHistory = mutableListOf<Boolean>()

    fun set(ifaces: Set<LocalInterface>) {
        flow.value = ifaces
    }

    override fun setFastPolling(enabled: Boolean) {
        fastPolling = enabled
        fastPollingHistory += enabled
    }
}

/** 注入的连接结果（优先于正常的路由与监听查找）。 */
sealed interface ConnectBehavior {
    data object Refuse : ConnectBehavior

    data object Unreachable : ConnectBehavior

    /** 对方不应答：等满 timeoutMs 后返回 Timeout（在虚拟时间下）。 */
    data object Hang : ConnectBehavior

    data class BindFail(val vpnSuspected: Boolean) : ConnectBehavior
}

data class ConnectAttempt(val via: LocalInterface, val target: SocketAddr)

data class SentDatagram(val from: String, val via: LocalInterface, val dst: Ipv4, val port: Int, val data: Bytes)

class InMemoryNetwork {
    private val hosts = mutableListOf<InMemoryTransport>()
    private val allConnections = mutableListOf<InMemoryConnection>()
    private val allDatagrams = mutableListOf<SentDatagram>()
    private var nextPort = 50_000

    val connections: List<InMemoryConnection> get() = synchronized(this) { allConnections.toList() }
    val datagrams: List<SentDatagram> get() = synchronized(this) { allDatagrams.toList() }

    /** 新建一台主机。[reportUdpVia] 为 false 时模拟 Android（收到的数据报不带接收接口）。 */
    fun host(name: String, interfaces: Set<LocalInterface>, reportUdpVia: Boolean = true): InMemoryTransport = synchronized(this) {
        InMemoryTransport(this, name, FakeNetworkMonitor(interfaces), reportUdpVia).also { hosts += it }
    }

    internal fun allocatePort(): Int = synchronized(this) { nextPort++ }

    internal fun hostOwning(ip: Ipv4): InMemoryTransport? =
        synchronized(this) { hosts.firstOrNull { h -> h.monitor.interfaces.value.any { it.address == ip } } }

    internal fun link(client: InMemoryTransport, clientAddr: SocketAddr, server: InMemoryTransport, serverAddr: SocketAddr): InMemoryConnection =
        InMemoryConnection(client.name, server.name, clientAddr, serverAddr).also { synchronized(this) { allConnections += it } }

    internal fun deliverUdp(sender: InMemoryTransport, srcPort: Int, via: LocalInterface, dst: Ipv4, dstPort: Int, bytes: ByteArray): Boolean {
        if (sender.udpSendFails || via !in sender.monitor.interfaces.value) return false
        val data = Bytes.of(bytes)
        val targets = synchronized(this) {
            allDatagrams += SentDatagram(sender.name, via, dst, dstPort, data)
            hosts.toList()
        }
        val broadcastLike = dst == via.broadcast || dst == MULTICAST_GROUP || dst == LIMITED_BROADCAST
        for (h in targets) {
            val ep = h.udpEndpoint(dstPort) ?: continue
            // 每台主机最多从 1 个接口收到同一份数据报；只有与发送接口处于同一链路（子网）的接口能收到
            val r = h.monitor.interfaces.value.firstOrNull { r -> r.contains(via.address) && (broadcastLike || dst == r.address) } ?: continue
            ep.deliver(Datagram(data, via.address, srcPort, if (h.reportUdpVia) r else null))
        }
        return true
    }

    companion object {
        val MULTICAST_GROUP: Ipv4 = Ipv4.of(239, 255, 42, 58)
        val LIMITED_BROADCAST: Ipv4 = Ipv4.of(255, 255, 255, 255)
    }
}

/** 一台主机的 Transport。 */
class InMemoryTransport internal constructor(
    internal val network: InMemoryNetwork,
    val name: String,
    val monitor: FakeNetworkMonitor,
    internal val reportUdpVia: Boolean,
) : Transport {
    private val listeners = HashMap<Int, InMemoryListener>()
    private val udp = HashMap<Int, InMemoryUdpEndpoint>()

    /** 被其他程序占用的端口（5.7、5.9、5.10）。 */
    val blockedTcpPorts = mutableSetOf<Int>()
    val blockedUdpPorts = mutableSetOf<Int>()

    /** 所有 UDP 发送都失败（5.10）。 */
    var udpSendFails = false

    /** 所有 TCP 监听都失败，包括由系统分配的端口（5.9）。 */
    var listenFails = false

    /** 按目标注入连接结果；返回 null 时按正常路由处理。 */
    var connectBehavior: (SocketAddr) -> ConnectBehavior? = { null }

    /** 每次 connect 调用（用于检查“不向非本地地址发包”，2.5）。 */
    val connectAttempts = mutableListOf<ConnectAttempt>()

    val listeningPorts: Set<Int> get() = synchronized(this) { listeners.keys.toSet() }

    internal fun udpEndpoint(port: Int): InMemoryUdpEndpoint? = synchronized(this) { udp[port] }

    internal fun removeListener(port: Int) = synchronized(this) { listeners.remove(port) }

    internal fun removeUdp(port: Int) = synchronized(this) { udp.remove(port) }

    private fun listenerAt(port: Int): InMemoryListener? = synchronized(this) { listeners[port] }

    override suspend fun connect(via: LocalInterface, ip: Ipv4, port: Int, timeoutMs: Long): ConnectResult {
        val target = SocketAddr(ip, port)
        synchronized(this) { connectAttempts += ConnectAttempt(via, target) }
        if (via !in monitor.interfaces.value) return ConnectResult.BindFailed(vpnSuspected = false)
        when (val b = connectBehavior(target)) {
            ConnectBehavior.Refuse -> return ConnectResult.Refused
            ConnectBehavior.Unreachable -> return ConnectResult.Unreachable
            ConnectBehavior.Hang -> {
                delay(timeoutMs)
                return ConnectResult.Timeout
            }
            is ConnectBehavior.BindFail -> return ConnectResult.BindFailed(b.vpnSuspected)
            null -> Unit
        }
        val server = network.hostOwning(ip)
        if (server == null || !via.contains(ip)) return ConnectResult.Unreachable
        val listener = server.listenerAt(port) ?: return ConnectResult.Refused
        val conn = network.link(this, SocketAddr(via.address, network.allocatePort()), server, target)
        if (!listener.offer(conn.serverEnd)) return ConnectResult.Refused
        return ConnectResult.Ok(conn.clientEnd)
    }

    override fun listen(candidatePorts: List<Int>): TcpListener? = synchronized(this) {
        if (listenFails) return null
        for (p in candidatePorts) {
            val actual = if (p == 0) network.allocatePort() else p
            if (actual in blockedTcpPorts || actual in listeners) continue
            return InMemoryListener(this, actual).also { listeners[actual] = it }
        }
        null
    }

    override fun openUdp(port: Int): UdpOpenResult = synchronized(this) {
        if (port in blockedUdpPorts || port in udp) return UdpOpenResult.Failed("端口 $port 已被占用")
        UdpOpenResult.Ok(InMemoryUdpEndpoint(this, port).also { udp[port] = it })
    }
}

internal class InMemoryListener(private val owner: InMemoryTransport, override val port: Int) : TcpListener {
    private val pending = Channel<TcpConn>(Channel.UNLIMITED)

    fun offer(c: TcpConn): Boolean = pending.trySend(c).isSuccess

    override suspend fun accept(): TcpConn? = pending.receiveCatching().getOrNull()

    override fun close() {
        pending.close()
        owner.removeListener(port)
    }
}

internal class InMemoryUdpEndpoint(private val owner: InMemoryTransport, private val port: Int) : UdpEndpoint {
    private val inbox = Channel<Datagram>(Channel.UNLIMITED)

    fun deliver(d: Datagram) {
        inbox.trySend(d)
    }

    override suspend fun receive(): Datagram? = inbox.receiveCatching().getOrNull()

    override suspend fun send(via: LocalInterface, dst: Ipv4, port: Int, bytes: ByteArray): Boolean =
        owner.network.deliverUdp(owner, this.port, via, dst, port, bytes)

    override fun close() {
        inbox.close()
        owner.removeUdp(port)
    }
}

/** 一条内存 TCP 连接：两端各一个 [TcpConn]，并捕获两个方向的全部线上字节。 */
class InMemoryConnection internal constructor(
    val clientHost: String,
    val serverHost: String,
    val clientAddr: SocketAddr,
    val serverAddr: SocketAddr,
) {
    private val toServer = Channel<ByteArray>(Channel.UNLIMITED)
    private val toClient = Channel<ByteArray>(Channel.UNLIMITED)
    private val fromClient = ByteArrayOutputStream()
    private val fromServer = ByteArrayOutputStream()

    @Volatile
    internal var blackholed = false
        private set

    val clientEnd: InMemoryTcpConn = InMemoryTcpConn(this, incoming = toClient, outgoing = toServer, capture = fromClient, local = clientAddr, remote = serverAddr)
    val serverEnd: InMemoryTcpConn = InMemoryTcpConn(this, incoming = toServer, outgoing = toClient, capture = fromServer, local = serverAddr, remote = clientAddr)

    /** 客户端写出的全部字节（含静默断网后丢失的部分）。 */
    fun bytesFromClient(): ByteArray = synchronized(fromClient) { fromClient.toByteArray() }

    fun bytesFromServer(): ByteArray = synchronized(fromServer) { fromServer.toByteArray() }

    /** 静默断网（网线拔出、接口消失）：之后写出的字节全部丢失，双方都读不到 EOF，只能靠心跳超时发现（18.6）。 */
    fun blackhole() {
        blackholed = true
    }

    /** RST：双方立即读到 -1，在途数据丢失，之后的写入失败（18.8）。 */
    fun reset() {
        toServer.cancel()
        toClient.cancel()
    }

    val isOpen: Boolean get() = !clientEnd.isClosed && !serverEnd.isClosed
}

class InMemoryTcpConn internal constructor(
    private val connection: InMemoryConnection,
    private val incoming: Channel<ByteArray>,
    private val outgoing: Channel<ByteArray>,
    private val capture: ByteArrayOutputStream,
    override val local: SocketAddr,
    override val remote: SocketAddr,
) : TcpConn {
    private var pending: ByteArray? = null
    private var pendingOff = 0

    @Volatile
    var isClosed = false
        private set

    override suspend fun read(buf: ByteArray, off: Int, len: Int): Int {
        require(len > 0 && off >= 0 && off + len <= buf.size) { "bad range" }
        if (isClosed) return -1
        var chunk = pending
        if (chunk == null) {
            chunk = incoming.receiveCatching().getOrNull() ?: return -1
            pendingOff = 0
        }
        val n = minOf(len, chunk.size - pendingOff)
        System.arraycopy(chunk, pendingOff, buf, off, n)
        pendingOff += n
        pending = if (pendingOff == chunk.size) null else chunk
        return n
    }

    override suspend fun write(b: ByteArray, off: Int, len: Int): Boolean {
        require(len >= 0 && off >= 0 && off + len <= b.size) { "bad range" }
        if (isClosed) return false
        if (len == 0) return true
        val copy = b.copyOfRange(off, off + len)
        synchronized(capture) { capture.write(copy) }
        if (connection.blackholed) return true
        return outgoing.trySend(copy).isSuccess
    }

    /** 正常关闭：对方读完已发出的数据后读到 EOF；对方之后的写入失败。 */
    override fun close() {
        isClosed = true
        outgoing.close()
        incoming.cancel()
    }
}
