package com.lantransfer.engine

import com.lantransfer.domain.AddressRules
import com.lantransfer.domain.DeviceEntry
import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.DeviceListEvent
import com.lantransfer.domain.DeviceListReducer
import com.lantransfer.domain.DeviceListState
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.Rng
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.NetworkMonitor
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.Transport
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.engine.ports.UdpEndpoint
import com.lantransfer.engine.ports.UdpOpenResult
import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.AnnouncementCodec
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ProtocolVersion
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

// 设备发现（设计 C4.6，需求 3.2、3.6、5.1–5.5、5.10）：
// - 发送：每个活动接口每 2 s（±0.2 s 抖动）向定向广播地址和组播组各发 1 份公告；新接口出现时立即在该接口上发送。
// - 接收：数据报 → acceptsInbound → AnnouncementCodec.decode → DeviceListReducer；1 s 的 Tick 驱动过期移除（10 s）。
// - UDP 端口无法打开，或某一轮在全部接口上都发送失败时为 Unavailable；之后某一轮发送成功时回到 Running。

/** 发现服务的状态（主界面据此显示“自动发现不可用”，5.10）。 */
sealed interface DiscoveryStatus {
    data object Stopped : DiscoveryStatus

    data object Running : DiscoveryStatus

    /** UDP 端口无法打开或发送全部失败；手动连接与扫码配对不受影响。 */
    data class Unavailable(val reason: String) : DiscoveryStatus
}

/** 发现相关的时限（需求与设计规定的数值；只为测试注入而做成参数）。 */
data class DiscoveryTimings(
    /** 公告周期（5.1）。 */
    val intervalMs: Long = 2_000,
    /** 每轮在周期上加的随机抖动的上限（±，设计 C4.6）。 */
    val jitterMs: Long = 200,
    /** 过期检查的 Tick 周期（5.4）。 */
    val tickMs: Long = 1_000,
)

class DiscoveryService(
    private val transport: Transport,
    private val network: NetworkMonitor,
    private val identity: DeviceIdentity,
    private val deviceType: DeviceType,
    private val trust: TrustStore,
    private val clock: Clock,
    private val rng: Rng,
    private val scope: CoroutineScope,
    private val displayName: () -> String = { identity.displayName },
    private val version: ProtocolVersion = ProtocolVersion.CURRENT,
    private val timings: DiscoveryTimings = DiscoveryTimings(),
) {
    private val lock = Any()
    private var state = DeviceListState(identity.deviceId, emptySet())
    private val devicesFlow = MutableStateFlow<List<DeviceEntry>>(emptyList())
    private val statusFlow = MutableStateFlow<DiscoveryStatus>(DiscoveryStatus.Stopped)
    private var job: Job? = null
    private var endpoint: UdpEndpoint? = null

    /** 设备列表（按首次出现的顺序，不含本机）。 */
    val devices: StateFlow<List<DeviceEntry>> get() = devicesFlow

    val status: StateFlow<DiscoveryStatus> get() = statusFlow

    /** 设备列表中的地址：最近一次有效公告的来源 IP 与公告中的监听端口（供 SessionManager 的 addressBook）。 */
    fun addressOf(id: DeviceId): SocketAddr? =
        devicesFlow.value.firstOrNull { it.deviceId == id }?.let { SocketAddr(it.ip, it.announcement.port) }

    /** 打开 UDP 端口并开始收发；[listenPort] 是写进公告的 TCP 监听端口。已在运行时先停止再重新开始。 */
    fun start(listenPort: Int) {
        stop()
        val ep = when (val r = transport.openUdp(ProtocolConstants.DISCOVERY_PORT)) {
            is UdpOpenResult.Failed -> {
                statusFlow.value = DiscoveryStatus.Unavailable(r.reason)
                return
            }
            is UdpOpenResult.Ok -> r.endpoint
        }
        val initial = network.interfaces.value
        val trusted = trustedIds()
        update { it.copy(interfaces = initial, trusted = trusted) }
        statusFlow.value = DiscoveryStatus.Running
        val j = Job(scope.coroutineContext[Job])
        synchronized(lock) {
            endpoint = ep
            job = j
        }
        val s = CoroutineScope(scope.coroutineContext + j)
        // 接收
        s.launch {
            while (true) {
                val d = ep.receive() ?: break
                val bytes = d.data.toByteArray()
                update { DeviceListReducer.onDatagram(it, bytes, d.src, d.via, clock.monotonicMs()) }
            }
        }
        // 过期
        s.launch {
            while (true) {
                delay(timings.tickMs)
                update { DeviceListReducer.reduce(it, DeviceListEvent.Tick(clock.monotonicMs())) }
            }
        }
        // 信任变化：更新列表中的信任标识（先于第一次变化订阅，避免漏掉）
        s.launch(start = CoroutineStart.UNDISPATCHED) {
            trust.changes.collect {
                val ids = trustedIds()
                update { s -> DeviceListReducer.reduce(s, DeviceListEvent.TrustChanged(ids)) }
            }
        }
        // 接口变化：更新子网校验用的接口集合，新出现的接口立即发送一次（3.6、5.1）
        s.launch(start = CoroutineStart.UNDISPATCHED) {
            var known = initial
            network.interfaces.collect { now ->
                val added = now.filter { it !in known }
                known = now
                update { it.copy(interfaces = now) }
                if (added.isNotEmpty()) sendRound(ep, added, listenPort)
            }
        }
        // 周期发送：启动时立即发送第一轮
        s.launch {
            while (true) {
                sendRound(ep, network.interfaces.value.toList(), listenPort)
                delay(timings.intervalMs + jitter())
            }
        }
    }

    /** 停止收发并关闭 UDP 端口；清空设备列表（之后收不到公告，列表内容已不可信）。 */
    fun stop() {
        val (j, ep) = synchronized(lock) {
            val pair = job to endpoint
            job = null
            endpoint = null
            pair
        }
        j?.cancel()
        ep?.close()
        update { it.copy(entries = emptyList()) }
        statusFlow.value = DiscoveryStatus.Stopped
    }

    /** 在 [ifaces] 上各发一份到定向广播与组播组；接口非空而全部发送失败时为 Unavailable，否则为 Running。 */
    private suspend fun sendRound(ep: UdpEndpoint, ifaces: List<LocalInterface>, listenPort: Int) {
        if (ifaces.isEmpty()) return
        val bytes = AnnouncementCodec.encode(Announcement(identity.deviceId, displayName(), deviceType, version, listenPort))
        var ok = 0
        for (i in ifaces) {
            if (ep.send(i, i.broadcast, ProtocolConstants.DISCOVERY_PORT, bytes)) ok++
            if (ep.send(i, MULTICAST, ProtocolConstants.DISCOVERY_PORT, bytes)) ok++
        }
        synchronized(lock) {
            if (endpoint !== ep) return // 已停止
            statusFlow.value = if (ok > 0) DiscoveryStatus.Running else DiscoveryStatus.Unavailable(SEND_FAILED)
        }
    }

    /** [-jitterMs, jitterMs] 内的均匀抖动。 */
    private fun jitter(): Long {
        val span = 2 * timings.jitterMs + 1
        if (span <= 1) return 0
        val b = rng.nextBytes(4)
        val v = ((b[0].toLong() and 0xFF) shl 24) or ((b[1].toLong() and 0xFF) shl 16) or ((b[2].toLong() and 0xFF) shl 8) or (b[3].toLong() and 0xFF)
        return v % span - timings.jitterMs
    }

    private fun trustedIds(): Set<DeviceId> = trust.all().mapTo(LinkedHashSet()) { it.deviceId }

    private inline fun update(f: (DeviceListState) -> DeviceListState) {
        synchronized(lock) {
            val next = f(state)
            if (next != state) {
                state = next
                devicesFlow.value = next.entries
            }
        }
    }

    companion object {
        /** 组播组 239.255.42.58（C1 发现）。 */
        val MULTICAST: Ipv4 = requireNotNull(AddressRules.parseIpv4(ProtocolConstants.MULTICAST_GROUP))

        const val SEND_FAILED = "无法在任何接口上发送公告"
    }
}
