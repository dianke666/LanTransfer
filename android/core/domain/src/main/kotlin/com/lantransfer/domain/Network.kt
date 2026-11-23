package com.lantransfer.domain

import com.lantransfer.protocol.ProtocolConstants

/** IPv4 地址（大端，按 Int 存放）。 */
@JvmInline
value class Ipv4(val bits: Int) {
    override fun toString(): String = "${(bits ushr 24) and 0xFF}.${(bits ushr 16) and 0xFF}.${(bits ushr 8) and 0xFF}.${bits and 0xFF}"

    companion object {
        fun of(a: Int, b: Int, c: Int, d: Int): Ipv4 {
            require(listOf(a, b, c, d).all { it in 0..255 }) { "octet out of range" }
            return Ipv4((a shl 24) or (b shl 16) or (c shl 8) or d)
        }
    }
}

/** 本机接口类型；前缀长度相同时按 WIFI、ETHERNET、HOTSPOT、OTHER 的顺序优先（C3.2）。 */
enum class IfaceKind { WIFI, HOTSPOT, ETHERNET, OTHER }

/** 子网掩码（前缀长度 0–32）。 */
fun subnetMask(prefixLength: Int): Int {
    require(prefixLength in 0..32) { "prefix length out of range" }
    return if (prefixLength == 0) 0 else (-1 shl (32 - prefixLength))
}

/** 一个活动的本地网络接口。平台绑定句柄由平台适配层按 [name] 另行维护，不放进领域模型。 */
data class LocalInterface(val name: String, val kind: IfaceKind, val address: Ipv4, val prefixLength: Int) {
    init {
        require(prefixLength in 0..32) { "prefix length out of range" }
    }

    /** 定向广播地址。 */
    val broadcast: Ipv4 get() = Ipv4(address.bits or subnetMask(prefixLength).inv())

    fun contains(ip: Ipv4): Boolean {
        val m = subnetMask(prefixLength)
        return (ip.bits and m) == (address.bits and m)
    }
}

/** [AddressRules.classify] 的结果。 */
sealed interface Route {
    data object Self : Route

    data class Local(val iface: LocalInterface) : Route

    data object NotLocal : Route
}

/** [AddressRules.parsePort] 的结果。 */
sealed interface PortParse {
    data class Default(val port: Int) : PortParse

    data class Ok(val port: Int) : PortParse

    data object Invalid : PortParse
}

/** 地址规则（需求 2、6、8，设计 C3.2）。不使用平台地址解析 API，从不触发 DNS（2.7）。 */
object AddressRules {
    private val kindOrder = listOf(IfaceKind.WIFI, IfaceKind.ETHERNET, IfaceKind.HOTSPOT, IfaceKind.OTHER)

    /** 去除首尾 White_Space 后要求恰好 4 段 ASCII 十进制数，每段 0–255，除单个 "0" 外不以 0 开头。 */
    fun parseIpv4(input: String): Ipv4? {
        val s = DisplayNameRules.trimWhiteSpace(input)
        val parts = s.split('.')
        if (parts.size != 4) return null
        var bits = 0
        for (p in parts) {
            if (p.isEmpty() || p.length > 3 || p.any { it !in '0'..'9' }) return null
            if (p.length > 1 && p[0] == '0') return null
            val v = p.toInt()
            if (v > 255) return null
            bits = (bits shl 8) or v
        }
        return Ipv4(bits)
    }

    /** 去除首尾 White_Space 后：为空 → Default(42580)；只含 ASCII 数字且数值 1–65535 → Ok；其余 → Invalid。 */
    fun parsePort(input: String): PortParse {
        val s = DisplayNameRules.trimWhiteSpace(input)
        if (s.isEmpty()) return PortParse.Default(ProtocolConstants.DEFAULT_PORT)
        if (s.any { it !in '0'..'9' }) return PortParse.Invalid
        val digits = s.trimStart('0')
        if (digits.length > 5) return PortParse.Invalid
        val v = if (digits.isEmpty()) 0 else digits.toInt()
        return if (v in 1..65535) PortParse.Ok(v) else PortParse.Invalid
    }

    /** 10/8、172.16/12、192.168/16、169.254/16。 */
    fun isPrivate(ip: Ipv4): Boolean {
        val b = ip.bits
        return (b ushr 24) == 10 || (b ushr 20) == 0xAC1 || (b ushr 16) == 0xC0A8 || (b ushr 16) == 0xA9FE
    }

    /** 目标等于本机任一接口地址 → Self；是私有地址且落在接口子网内 → Local（最长前缀，同前缀按类型优先）；否则 NotLocal。 */
    fun classify(target: Ipv4, ifaces: Collection<LocalInterface>): Route {
        if (ifaces.any { it.address == target }) return Route.Self
        if (!isPrivate(target)) return Route.NotLocal
        val best = ifaces.filter { it.contains(target) }
            .sortedWith(compareByDescending<LocalInterface> { it.prefixLength }.thenBy { kindOrder.indexOf(it.kind) }.thenBy { it.name })
            .firstOrNull()
        return if (best == null) Route.NotLocal else Route.Local(best)
    }

    /** 来源是私有地址且落在接收接口 [via] 的子网内；[via] 为 null（Android）时任一接口子网包含来源即可。 */
    fun acceptsInbound(src: Ipv4, via: LocalInterface?, ifaces: Collection<LocalInterface>): Boolean =
        isPrivate(src) && if (via != null) via.contains(src) else ifaces.any { it.contains(src) }

    /** 保持 QR 中的顺序，只保留 classify 为 Local 的地址（8.5）。 */
    fun qrCandidates(ips: List<Ipv4>, ifaces: Collection<LocalInterface>): List<Pair<Ipv4, LocalInterface>> =
        ips.mapNotNull { ip -> (classify(ip, ifaces) as? Route.Local)?.let { ip to it.iface } }

    /** 排序后的接口地址去重，最多 4 个（23.8）。 */
    fun qrAddressList(sortedIfaces: List<LocalInterface>): List<Ipv4> =
        sortedIfaces.map { it.address }.distinct().take(ProtocolConstants.MAX_QR_IPS)

    /**
     * 按 QR 中的顺序逐个尝试候选地址，在第一个成功处停止（供 SessionManager 的 Target.Qr 使用）。
     * 只对 classify 为 Local 的地址调用 [attempt]；全部失败或全部被跳过时返回 null。
     */
    suspend fun <R : Any> connectInQrOrder(
        ips: List<Ipv4>,
        ifaces: Collection<LocalInterface>,
        attempt: suspend (Ipv4, LocalInterface) -> R?,
    ): R? {
        for ((ip, iface) in qrCandidates(ips, ifaces)) {
            attempt(ip, iface)?.let { return it }
        }
        return null
    }
}
