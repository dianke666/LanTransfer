package com.lantransfer.engine.ports

import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.protocol.Bytes
import kotlinx.coroutines.flow.StateFlow

// C5.1 网络端口。Transport 是唯一能创建套接字的地方，只由 LocalNetworkGuard（AddressRules.classify 返回 Local 之后）调用。
// 不提供任何按主机名连接的接口（2.7）。可能失败的方法都返回结果类型，不向引擎层抛出平台异常。

/** 套接字地址。 */
data class SocketAddr(val ip: Ipv4, val port: Int) {
    override fun toString(): String = "$ip:$port"
}

interface NetworkMonitor {
    /** 只包含带 Private_IPv4_Address 的活动接口（2.6、3.1），按 C3.2 的接口顺序排列（集合保持插入顺序）。 */
    val interfaces: StateFlow<Set<LocalInterface>>

    /** 存在 Session 时每 1 s 轮询，否则每 2 s（18.8）。 */
    fun setFastPolling(enabled: Boolean)
}

sealed interface ConnectResult {
    data class Ok(val conn: TcpConn) : ConnectResult

    /** 无法把套接字绑定到接口（Android 上通常是 VPN 禁止绕过，2.10）；此时没有发出任何数据包。 */
    data class BindFailed(val vpnSuspected: Boolean) : ConnectResult

    data object Refused : ConnectResult

    data object Unreachable : ConnectResult

    data object Timeout : ConnectResult
}

interface TcpConn {
    val local: SocketAddr
    val remote: SocketAddr

    /** 读入 buf[off, off + len)，返回读到的字节数（≥ 1）；-1 表示 EOF 或连接被重置（C1.11 都判定为“连接被对方关闭”）。 */
    suspend fun read(buf: ByteArray, off: Int = 0, len: Int = buf.size - off): Int

    /** 全部写出时返回 true；连接已关闭或已断开时返回 false。 */
    suspend fun write(b: ByteArray, off: Int = 0, len: Int = b.size - off): Boolean

    fun close()
}

interface TcpListener {
    val port: Int

    /** 下一个入站连接；监听已关闭时返回 null。 */
    suspend fun accept(): TcpConn?

    fun close()
}

/** 收到的 UDP 数据报。[via] 为实际接收接口（Windows 由 IPPacketInformation 给出），Android 上为 null。 */
data class Datagram(val data: Bytes, val src: Ipv4, val srcPort: Int, val via: LocalInterface?)

interface UdpEndpoint {
    /** 下一个数据报；端点已关闭时返回 null。 */
    suspend fun receive(): Datagram?

    /** 经接口 [via] 发往 dst:port（单播、定向广播或组播）；发送失败时返回 false。 */
    suspend fun send(via: LocalInterface, dst: Ipv4, port: Int, bytes: ByteArray): Boolean

    fun close()
}

sealed interface UdpOpenResult {
    data class Ok(val endpoint: UdpEndpoint) : UdpOpenResult

    /** 端口被占用、系统拒绝访问等（5.10）。 */
    data class Failed(val reason: String) : UdpOpenResult
}

interface Transport {
    /** 从接口 [via] 连接 ip:port，[timeoutMs] 内未建立返回 Timeout。 */
    suspend fun connect(via: LocalInterface, ip: Ipv4, port: Int, timeoutMs: Long): ConnectResult

    /** 按顺序尝试 [candidatePorts]（0 表示由系统分配），返回第一个成功的监听；全部失败时返回 null（5.7、5.9）。 */
    fun listen(candidatePorts: List<Int>): TcpListener?

    fun openUdp(port: Int): UdpOpenResult
}
