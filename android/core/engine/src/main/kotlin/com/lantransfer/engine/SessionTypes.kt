package com.lantransfer.engine

import com.lantransfer.domain.InterruptReason
import com.lantransfer.domain.Ipv4
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.QrPayload
import com.lantransfer.protocol.SessionCloseReason

// SessionManager 的对外类型（设计 C4.1、C4.2、E1、E2）。

/** 连接目标。 */
sealed interface Target {
    /** 地址取设备列表；不在列表中时取 TrustStore 记录的最近成功地址。 */
    data class Device(val id: DeviceId) : Target

    /** 手动连接。 */
    data class Address(val ip: Ipv4, val port: Int) : Target

    /** 候选地址按二维码中的顺序逐个尝试，每个 5 s（8.5）。 */
    data class Qr(val payload: QrPayload) : Target
}

/** 连接建立失败（E1）。提示文案由 UI 层按类型查表。 */
sealed interface OpenFailure {
    data object NotLocal : OpenFailure

    data object SelfConnection : OpenFailure

    data class BindFailed(val vpnSuspected: Boolean) : OpenFailure

    data object Unreachable : OpenFailure

    data object Refused : OpenFailure

    data object Timeout : OpenFailure

    data object NoVersionResponse : OpenFailure

    data object ProtocolError : OpenFailure

    data class VersionIncompatible(val local: ProtocolVersion, val remote: ProtocolVersion) : OpenFailure

    data object HandshakeAuthFailed : OpenFailure

    /** 对方是 Trusted_Device，但出示的公钥与保存的不一致（9.4）；[name] 为保存的设备名称。 */
    data class KeyMismatch(val name: String) : OpenFailure

    data object FingerprintMismatch : OpenFailure

    /** 收到 SESSION_RESULT(NOT_TRUSTED)（9.5）。 */
    data object NotTrusted : OpenFailure

    /** 收到 SESSION_RESULT(KEY_MISMATCH)：对方保存的本机公钥与本机出示的不一致（设计补充，Q13）。 */
    data class PeerKeyMismatch(val name: String) : OpenFailure
}

/** 握手中认证过的对端身份；[publicKey] 是对方在 Noise 握手中证明持有私钥的静态公钥。 */
data class PeerIdentity(val deviceId: DeviceId, val displayName: String, val deviceType: DeviceType, val publicKey: Bytes)

sealed interface OpenResult {
    data class Ok(val session: Session) : OpenResult

    /** intent 为 PAIR / QR_PAIR：握手已完成，连接处于 Pending，交给 PairingService。 */
    data class Pairing(val connection: PendingConnection) : OpenResult

    data class Failed(val failure: OpenFailure) : OpenResult
}

sealed interface ListenResult {
    data class Ok(val port: Int) : ListenResult

    /** 42580、42590–42599 与系统分配端口全部失败（5.9）。 */
    data object Failed : ListenResult
}

/** 连接关闭的原因。 */
sealed interface CloseReason {
    /** 本机主动关闭，关闭前已尝试发送 SESSION_CLOSE([reason])。 */
    data class Local(val reason: SessionCloseReason) : CloseReason

    /** 对方发来 SESSION_CLOSE([reason])，或读到 EOF / RST（[reason] 为 null）。 */
    data class PeerClosed(val reason: SessionCloseReason?) : CloseReason

    /** 连续 15 s 没有收到任何帧（18.6）。 */
    data object PeerUnresponsive : CloseReason

    /** 连接所在的本地接口消失（18.8）。 */
    data object LocalNetworkLost : CloseReason

    /** 记录层完整性校验失败（10.5）。 */
    data object IntegrityFailed : CloseReason

    /** FrameDecoder 报错或对方违反协议（22.6）。 */
    data class ProtocolError(val detail: String) : CloseReason

    /** Pending 阶段收到白名单以外的消息，已回复 SESSION_RESULT(NOT_TRUSTED)（10.4）。 */
    data object PendingViolation : CloseReason

    /** 同一对端的新连接取代了这条连接（对方已丢失旧连接）。 */
    data object Superseded : CloseReason
}

/**
 * 关闭原因 → 进行中任务的中断原因（C4.2、E2、M3.2）；null 表示不影响任务（DUPLICATE：该连接上没有应用消息；
 * 本机解除配对：相关任务已由解除配对流程取消）。对方因退出发来的 SESSION_CLOSE 在本机记为 PEER_CLOSED。
 */
val CloseReason.interruptReason: InterruptReason?
    get() = when (this) {
        is CloseReason.Local -> when (reason) {
            SessionCloseReason.DUPLICATE, SessionCloseReason.UNPAIRED -> null
            SessionCloseReason.NORMAL, SessionCloseReason.APP_EXIT, SessionCloseReason.BACKGROUND -> InterruptReason.APP_EXIT
        }
        is CloseReason.PeerClosed -> if (reason == SessionCloseReason.DUPLICATE) null else InterruptReason.PEER_CLOSED
        CloseReason.PeerUnresponsive -> InterruptReason.PEER_UNRESPONSIVE
        CloseReason.LocalNetworkLost -> InterruptReason.LOCAL_NETWORK_LOST
        CloseReason.IntegrityFailed -> InterruptReason.INTEGRITY_FAILED
        is CloseReason.ProtocolError, CloseReason.PendingViolation -> InterruptReason.PROTOCOL_ERROR
        CloseReason.Superseded -> InterruptReason.PEER_CLOSED
    }

/** 已注册 Session 的快照（供界面显示）。 */
data class SessionInfo(val peer: PeerIdentity, val remote: SocketAddr, val initiatedLocally: Boolean, val version: ProtocolVersion)

/** Session 的注册与关闭事件（供文字、文件服务订阅）。 */
sealed interface SessionEvent {
    data class Opened(val session: Session) : SessionEvent

    data class Closed(val session: Session, val reason: CloseReason) : SessionEvent
}

/** 接收方一侧需要提示的握手失败（E1：VersionIncompatible、HandshakeAuthFailed、KeyMismatch），去抖由 UI 层负责（E7）。 */
data class InboundFailure(val remote: SocketAddr, val peer: DeviceId?, val failure: OpenFailure)

/** 时限（需求规定的数值；只为测试注入而做成参数）。 */
data class SessionTimings(
    /** Device / Address 目标从调用起共用的预算（6.5、7.11、9.8）。 */
    val openMs: Long = 10_000,
    /** 扫码时每个候选地址的预算（8.5）。 */
    val qrPerAddressMs: Long = 5_000,
    /** 入站连接完成前导与握手的时限。 */
    val inboundHandshakeMs: Long = 10_000,
    /** 连续这么久没有发送任何帧时发送 HEARTBEAT（18.5）。 */
    val heartbeatMs: Long = 5_000,
    /** 连续这么久没有收到任何帧时以“对方无响应”关闭（18.6）。 */
    val unresponsiveMs: Long = 15_000,
    /** 主动关闭时等待 SESSION_CLOSE 写出的最长时间。 */
    val closeGraceMs: Long = 1_000,
)
