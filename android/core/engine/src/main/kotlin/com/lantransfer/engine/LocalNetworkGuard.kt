package com.lantransfer.engine

import com.lantransfer.domain.AddressRules
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.Route
import com.lantransfer.engine.ports.ConnectResult
import com.lantransfer.engine.ports.NetworkMonitor
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.engine.ports.Transport

/**
 * 出站连接的统一出口（设计“网络接口识别与零流量策略”、需求 2.3、2.5、2.9、2.10）：只有 [AddressRules.classify] 返回 Local
 * 才调用 [Transport.connect]，并绑定到该接口；NotLocal / Self 立即失败，不发出任何数据包。
 */
class LocalNetworkGuard(private val transport: Transport, private val network: NetworkMonitor) {
    sealed interface Outcome {
        data class Connected(val conn: TcpConn, val via: LocalInterface) : Outcome

        data class Failed(val failure: OpenFailure) : Outcome
    }

    fun route(ip: Ipv4): Route = AddressRules.classify(ip, network.interfaces.value)

    suspend fun connect(ip: Ipv4, port: Int, timeoutMs: Long): Outcome {
        val via = when (val r = route(ip)) {
            Route.Self -> return Outcome.Failed(OpenFailure.SelfConnection)
            Route.NotLocal -> return Outcome.Failed(OpenFailure.NotLocal)
            is Route.Local -> r.iface
        }
        if (timeoutMs <= 0) return Outcome.Failed(OpenFailure.Timeout)
        return when (val c = transport.connect(via, ip, port, timeoutMs)) {
            is ConnectResult.Ok -> Outcome.Connected(c.conn, via)
            is ConnectResult.BindFailed -> Outcome.Failed(OpenFailure.BindFailed(c.vpnSuspected))
            ConnectResult.Refused -> Outcome.Failed(OpenFailure.Refused)
            ConnectResult.Unreachable -> Outcome.Failed(OpenFailure.Unreachable)
            ConnectResult.Timeout -> Outcome.Failed(OpenFailure.Timeout)
        }
    }

    /** 入站连接的本地接口（按本地地址查找）；来源不是该接口子网内的私有地址时返回 null（2.9），调用方立即关闭。 */
    fun acceptInbound(conn: TcpConn): InboundRoute? {
        val ifaces = network.interfaces.value
        val via = ifaces.firstOrNull { it.address == conn.local.ip }
        return if (AddressRules.acceptsInbound(conn.remote.ip, via, ifaces)) InboundRoute(via) else null
    }

    /** 入站连接所在的本地接口；找不到时为 null（此时不做接口消失检测以外的限制）。 */
    data class InboundRoute(val via: LocalInterface?)
}
