package com.lantransfer.app.platform

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import com.lantransfer.domain.AddressRules
import com.lantransfer.domain.IfaceKind
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.engine.ports.NetworkMonitor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Android 局域网接口监视器（设计 C6.1、需求 2.1–2.4、18.8）：
 * 1. 通过 ConnectivityManager 监听 Wi-Fi 网络（显式移除 NET_CAPABILITY_INTERNET，支持无外网路由器）；
 * 2. 周期枚举 NetworkInterface（默认 2 s，有会话时 1 s）以同时覆盖本机热点（ap/swlan/wlan）与以太网；
 * 3. 过滤蜂窝（rmnet/ccmni/pdp）与 VPN（tun/ppp/wg）接口，仅输出带私有 IPv4 的接口。
 */
class AndroidNetworkMonitor(
    context: Context,
    private val scope: CoroutineScope,
) : NetworkMonitor {
    private val cm = context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val wifiNetworks = ConcurrentHashMap<String, Network>()
    private val wifiNetworkIds = ConcurrentHashMap<Network, String>()
    private val fastPolling = AtomicBoolean(false)
    private val interfacesFlow = MutableStateFlow<Set<LocalInterface>>(emptySet())
    private var pollJob: Job? = null

    private val wifiCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
            val name = linkProperties.interfaceName ?: return
            wifiNetworks[name] = network
            wifiNetworkIds[network] = name
            refreshNow()
        }

        override fun onAvailable(network: Network) {
            cm.getLinkProperties(network)?.interfaceName?.let { name ->
                wifiNetworks[name] = network
                wifiNetworkIds[network] = name
            }
            refreshNow()
        }

        override fun onLost(network: Network) {
            wifiNetworkIds.remove(network)?.let { wifiNetworks.remove(it) }
            refreshNow()
        }
    }

    override val interfaces: StateFlow<Set<LocalInterface>> = interfacesFlow.asStateFlow()

    init {
        registerWifiCallback()
        refreshNow()
        pollJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                delay(if (fastPolling.get()) 1_000L else 2_000L)
                refreshNow()
            }
        }
    }

    override fun setFastPolling(enabled: Boolean) {
        fastPolling.set(enabled)
    }

    fun wifiNetworkFor(ifaceName: String): Network? = wifiNetworks[ifaceName]

    fun hasActiveVpn(): Boolean = runCatching {
        val active = cm.activeNetwork ?: return@runCatching false
        val caps = cm.getNetworkCapabilities(active) ?: return@runCatching false
        caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
    }.getOrDefault(false)

    fun refreshNow() {
        val collected = LinkedHashMap<String, LocalInterface>()
        val order = listOf(IfaceKind.WIFI, IfaceKind.ETHERNET, IfaceKind.HOTSPOT, IfaceKind.OTHER)
        val raw = mutableListOf<LocalInterface>()

        runCatching {
            val en = NetworkInterface.getNetworkInterfaces() ?: return@runCatching
            while (en.hasMoreElements()) {
                val nif = en.nextElement()
                if (!nif.isUp || nif.isLoopback || nif.isPointToPoint || nif.isVirtual) continue
                val name = nif.name ?: continue
                if (isExcludedInterface(name)) continue
                val kind = classifyInterface(name)
                for (ia in nif.interfaceAddresses) {
                    val addr = ia.address as? Inet4Address ?: continue
                    val prefix = ia.networkPrefixLength.toInt().coerceIn(0, 32)
                    val ip = addr.toIpv4()
                    if (!AddressRules.isPrivate(ip)) continue
                    raw += LocalInterface(name = name, kind = kind, address = ip, prefixLength = prefix)
                }
            }
        }

        raw.sortedWith(
            compareBy<LocalInterface> { order.indexOf(it.kind) }
                .thenByDescending { it.prefixLength }
                .thenBy { it.name },
        ).forEach { iface ->
            collected.putIfAbsent("${iface.name}:${iface.address.bits}", iface)
        }

        interfacesFlow.value = collected.values.toCollection(LinkedHashSet())
    }

    fun close() {
        pollJob?.cancel()
        runCatching { cm.unregisterNetworkCallback(wifiCallback) }
    }

    private fun registerWifiCallback() {
        runCatching {
            val req = NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .build()
            cm.registerNetworkCallback(req, wifiCallback)
        }
    }

    private fun classifyInterface(name: String): IfaceKind {
        val lower = name.lowercase()
        if (wifiNetworks.containsKey(name)) return IfaceKind.WIFI
        return when {
            lower.startsWith("ap") || lower.startsWith("swlan") || lower.startsWith("softap") || lower.contains("tether") -> IfaceKind.HOTSPOT
            lower.startsWith("wlan") || lower.startsWith("wifi") -> IfaceKind.WIFI
            lower.startsWith("eth") || lower.startsWith("usb") -> IfaceKind.ETHERNET
            else -> IfaceKind.OTHER
        }
    }

    private fun isExcludedInterface(name: String): Boolean {
        val lower = name.lowercase()
        return lower.startsWith("rmnet") ||
            lower.startsWith("ccmni") ||
            lower.startsWith("pdp") ||
            lower.startsWith("wwan") ||
            lower.startsWith("tun") ||
            lower.startsWith("tap") ||
            lower.startsWith("ppp") ||
            lower.startsWith("wg") ||
            lower.startsWith("dummy") ||
            lower.startsWith("lo") ||
            lower.startsWith("p2p")
    }
}

fun Inet4Address.toIpv4(): Ipv4 {
    val b = address
    return Ipv4(
        ((b[0].toInt() and 0xFF) shl 24) or
            ((b[1].toInt() and 0xFF) shl 16) or
            ((b[2].toInt() and 0xFF) shl 8) or
            (b[3].toInt() and 0xFF),
    )
}

fun Ipv4.toInetAddress(): Inet4Address {
    val bytes = byteArrayOf(
        ((bits ushr 24) and 0xFF).toByte(),
        ((bits ushr 16) and 0xFF).toByte(),
        ((bits ushr 8) and 0xFF).toByte(),
        (bits and 0xFF).toByte(),
    )
    return Inet4Address.getByAddress(bytes) as Inet4Address
}
