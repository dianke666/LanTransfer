package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.PairCancel
import com.lantransfer.protocol.PairCancelReason
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairDecision
import com.lantransfer.protocol.PairDecisionCode
import com.lantransfer.protocol.PairNonce
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.PairReveal
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.QrPairRequest
import com.lantransfer.protocol.QrPairResult
import com.lantransfer.protocol.QrPairResultCode
import com.lantransfer.protocol.VerificationCode

/** 纯归约器的一步：新状态与交给引擎层执行的 effects（设计 C3.5）。 */
data class Step<S, F>(val state: S, val effects: List<F>)

/** 引擎层分配的连接编号；归约器只用它区分“当前配对连接”和其他连接。 */
@JvmInline
value class ConnId(val value: Long)

/** 握手后得到的对方信息；保存信任时由引擎层补上配对时刻，组成 TrustedDevice。 */
data class PeerInfo(
    val deviceId: DeviceId,
    val displayName: String,
    val deviceType: DeviceType,
    val publicKey: Bytes,
    val ip: Ipv4? = null,
    val port: Int? = null,
)

enum class PairingRole { INITIATOR, RECEIVER, QR_SCANNER, QR_DISPLAY }

/** 进行中的配对（M3.4）。[deadline] 为本阶段的截止时刻（单调时钟毫秒），到期由 Tick 处理；null 表示本阶段不计时。 */
sealed interface PairingSession {
    val peer: DeviceId
    val role: PairingRole
    val deadline: Long?

    /** 发起方或扫描方：连接与握手中（点选 10 s，扫码 4 个候选地址 × 5 s）。[qrToken] 非空表示扫码。 */
    data class Connecting(override val peer: DeviceId, val qrToken: Bytes16?, override val deadline: Long) : PairingSession {
        override val role: PairingRole get() = if (qrToken == null) PairingRole.INITIATOR else PairingRole.QR_SCANNER
    }

    /** 发起方：已发出 PAIR_REQUEST，显示“等待对方确认”。 */
    data class AwaitingPeer(
        override val peer: DeviceId,
        val conn: ConnId,
        val info: PeerInfo,
        val localPublicKey: Bytes,
        val nI: Bytes,
        override val deadline: Long,
    ) : PairingSession {
        override val role: PairingRole get() = PairingRole.INITIATOR
    }

    /** 发起方：收到 PAIR_NONCE、已发出 PAIR_REVEAL，显示 SAS，继续等待对方决定。 */
    data class ShowingSas(
        override val peer: DeviceId,
        val conn: ConnId,
        val info: PeerInfo,
        val sas: String,
        override val deadline: Long,
    ) : PairingSession {
        override val role: PairingRole get() = PairingRole.INITIATOR
    }

    /** 接收方：已发出 PAIR_NONCE，显示请求；收到 PAIR_REVEAL 并校验承诺后 [sas] 非空，才允许接受。 */
    data class AwaitingUser(
        override val peer: DeviceId,
        val conn: ConnId,
        val info: PeerInfo,
        val localPublicKey: Bytes,
        val commitment: Bytes,
        val nR: Bytes,
        val sas: String?,
        override val deadline: Long,
    ) : PairingSession {
        override val role: PairingRole get() = PairingRole.RECEIVER
    }

    /** 扫描方：已发出 QR_PAIR_REQUEST，等待 QR_PAIR_RESULT。 */
    data class AwaitingQrResult(override val peer: DeviceId, val conn: ConnId, val info: PeerInfo, override val deadline: Long) : PairingSession {
        override val role: PairingRole get() = PairingRole.QR_SCANNER
    }

    /** 接收方或显示方：已暂存信任，等待 PAIR_COMPLETE。显示方持有令牌租约 [lease]。 */
    data class AwaitingComplete(
        override val peer: DeviceId,
        val conn: ConnId,
        val info: PeerInfo,
        override val role: PairingRole,
        val lease: TokenLease?,
        override val deadline: Long,
    ) : PairingSession

    /** 正在保存（发起方、扫描方）或提交（接收方、显示方）信任，等待 TrustStored。 */
    data class Saving(
        override val peer: DeviceId,
        val conn: ConnId,
        val info: PeerInfo,
        override val role: PairingRole,
        val lease: TokenLease?,
    ) : PairingSession {
        override val deadline: Long? get() = null
    }
}

data class PairingDone(val peer: DeviceId, val role: PairingRole, val result: PairingResult)

/**
 * 配对服务的内存状态：至多 1 个进行中的配对（7.9）、按设备 ID 记录的用户拒绝时刻（60 s 冷却，7.8）、最近一次结局。
 * 不持久化，进程结束后未提交的暂存信任随之丢弃（7.12）。
 */
data class PairingState(
    val session: PairingSession? = null,
    val rejectedAt: Map<DeviceId, Long> = emptyMap(),
    val last: PairingDone? = null,
)

sealed interface PairingEvent {
    /** 发起方：用户点选设备或手动连接得到非信任设备（6.6、7.1）。引擎随即开始连接。 */
    data class StartPair(val peer: DeviceId, val now: Long) : PairingEvent

    /** 扫描方：扫到有效的 QR_Payload（8.4）。 */
    data class StartQrPair(val peer: DeviceId, val token: Bytes16, val now: Long) : PairingEvent

    /** 发起方 / 扫描方：连接与握手完成。[nI] 为本次配对的 32 字节随机数（扫码时不使用）。 */
    data class Connected(val conn: ConnId, val info: PeerInfo, val localPublicKey: Bytes, val nI: Bytes, val now: Long) : PairingEvent

    data class ConnectFailed(val now: Long) : PairingEvent

    /** 接收方：收到 PAIR_REQUEST。[nR] 为引擎生成的 32 字节随机数，只在接受该请求时使用。 */
    data class PairRequestReceived(
        val conn: ConnId,
        val info: PeerInfo,
        val localPublicKey: Bytes,
        val commitment: Bytes,
        val nR: Bytes,
        val now: Long,
    ) : PairingEvent

    /** 显示方：收到 QR_PAIR_REQUEST，引擎已用 PairingTokenRegistry 校验；[lease] 为 null 表示令牌无效。 */
    data class QrRequestChecked(val conn: ConnId, val info: PeerInfo, val lease: TokenLease?, val now: Long) : PairingEvent

    /** PAIR_NONCE、PAIR_REVEAL、PAIR_DECISION、PAIR_CANCEL、PAIR_COMPLETE、QR_PAIR_RESULT。 */
    data class MessageReceived(val conn: ConnId, val msg: ProtocolMessage, val now: Long) : PairingEvent

    /** 接收方用户点击“接受”或“拒绝”。 */
    data class UserDecision(val accept: Boolean, val now: Long) : PairingEvent

    /** 发起方 / 扫描方用户点击“取消”（7.10）。 */
    data class UserCancel(val now: Long) : PairingEvent

    data class TrustStored(val peer: DeviceId, val ok: Boolean) : PairingEvent

    data class Disconnected(val conn: ConnId, val now: Long) : PairingEvent

    data class Tick(val now: Long) : PairingEvent
}

sealed interface PairingEffect {
    data class Send(val conn: ConnId, val msg: ProtocolMessage) : PairingEffect

    data class Close(val conn: ConnId) : PairingEffect

    /** 放弃尚未完成的连接尝试。 */
    data object CancelConnect : PairingEffect

    /** 请在 [atMono] 或之后送来 Tick。 */
    data class ArmTimer(val atMono: Long) : PairingEffect

    /** 接收方确认界面：对方名称、类型、IP 与“接受”“拒绝”（7.3）。 */
    data class ShowRequest(val info: PeerInfo) : PairingEffect

    data class ShowSas(val sas: String) : PairingEffect

    /** 关闭接收方确认界面（7.7、7.10、7.12）。 */
    data object DismissRequest : PairingEffect

    /** 发起方 / 扫描方：保存信任（整条替换同一设备 ID，9.9）。 */
    data class SaveTrust(val info: PeerInfo) : PairingEffect

    /** 接收方 / 显示方：暂存信任，收到 PAIR_COMPLETE 后再提交。 */
    data class StageTrust(val info: PeerInfo) : PairingEffect

    data class CommitTrust(val peer: DeviceId) : PairingEffect

    data class DiscardStagedTrust(val peer: DeviceId) : PairingEffect

    /** 删除可能已保存的信任（7.12）。 */
    data class DeleteTrust(val peer: DeviceId) : PairingEffect

    data class MarkTokenUsed(val lease: TokenLease) : PairingEffect

    data class ReleaseToken(val lease: TokenLease) : PairingEffect

    /** 当前连接直接转为 Session（7.4、8.4）。 */
    data class PromoteSession(val conn: ConnId) : PairingEffect

    /** 自动拒绝的请求（不显示界面）：[result] 为 AUTO_REJECTED（冷却期内）或 BUSY。 */
    data class AutoRejected(val peer: DeviceId, val result: PairingResult) : PairingEffect

    /** 本次配对结束，界面显示对应提示。 */
    data class Finished(val done: PairingDone) : PairingEffect
}

/** 配对状态机（M3.4，需求 7、8）：(state, event) → (state′, effects)。不认识或迟到的消息一律忽略（设计 E 表）。 */
object PairingReducer {
    const val CONNECT_TIMEOUT_MS = 10_000L
    const val QR_CONNECT_TIMEOUT_MS = 4 * 5_000L
    const val REQUEST_TIMEOUT_MS = 60_000L
    const val COOLDOWN_MS = 60_000L

    /** 等待 PAIR_COMPLETE / QR_PAIR_RESULT 的上限；与心跳的 15 s 断线判定一致（7.12）。 */
    const val COMPLETE_TIMEOUT_MS = 15_000L

    fun reduce(s: PairingState, e: PairingEvent): Step<PairingState, PairingEffect> = when (e) {
        is PairingEvent.StartPair -> start(s, PairingSession.Connecting(e.peer, null, e.now + CONNECT_TIMEOUT_MS))
        is PairingEvent.StartQrPair -> start(s, PairingSession.Connecting(e.peer, e.token, e.now + QR_CONNECT_TIMEOUT_MS))
        is PairingEvent.Connected -> onConnected(s, e)
        is PairingEvent.ConnectFailed -> {
            val c = s.session as? PairingSession.Connecting
            if (c == null) unchanged(s) else done(s, c, PairingResult.CONNECT_FAILED)
        }
        is PairingEvent.PairRequestReceived -> onPairRequest(s, e)
        is PairingEvent.QrRequestChecked -> onQrRequest(s, e)
        is PairingEvent.MessageReceived -> onMessage(s, e)
        is PairingEvent.UserDecision -> onDecision(s, e)
        is PairingEvent.UserCancel -> onCancel(s)
        is PairingEvent.TrustStored -> onTrustStored(s, e)
        is PairingEvent.Disconnected -> onDisconnected(s, e)
        is PairingEvent.Tick -> onTick(s, e.now)
    }

    private fun unchanged(s: PairingState) = Step<PairingState, PairingEffect>(s, emptyList())

    private fun start(s: PairingState, c: PairingSession.Connecting): Step<PairingState, PairingEffect> =
        if (s.session != null) unchanged(s) else Step(s.copy(session = c), listOf(PairingEffect.ArmTimer(c.deadline)))

    private fun done(s: PairingState, session: PairingSession, result: PairingResult, vararg effects: PairingEffect): Step<PairingState, PairingEffect> {
        val d = PairingDone(session.peer, session.role, result)
        return Step(s.copy(session = null, last = d), effects.toList() + PairingEffect.Finished(d))
    }

    private fun onConnected(s: PairingState, e: PairingEvent.Connected): Step<PairingState, PairingEffect> {
        val c = s.session as? PairingSession.Connecting ?: return Step(s, listOf(PairingEffect.Close(e.conn)))
        val peer = e.info.deviceId
        if (c.qrToken != null) {
            val next = PairingSession.AwaitingQrResult(peer, e.conn, e.info, e.now + COMPLETE_TIMEOUT_MS)
            return Step(s.copy(session = next), listOf(PairingEffect.Send(e.conn, QrPairRequest(c.qrToken)), PairingEffect.ArmTimer(next.deadline)))
        }
        val commitment = VerificationCode.commitment(e.localPublicKey.toByteArray(), e.info.publicKey.toByteArray(), e.nI.toByteArray())
        val next = PairingSession.AwaitingPeer(peer, e.conn, e.info, e.localPublicKey, e.nI, e.now + REQUEST_TIMEOUT_MS)
        return Step(s.copy(session = next), listOf(PairingEffect.Send(e.conn, PairRequest(Bytes.of(commitment))), PairingEffect.ArmTimer(next.deadline)))
    }

    private fun onPairRequest(s: PairingState, e: PairingEvent.PairRequestReceived): Step<PairingState, PairingEffect> {
        val peer = e.info.deviceId
        val rejected = s.rejectedAt[peer]
        if (rejected != null && e.now - rejected < COOLDOWN_MS) {
            return Step(s, autoReject(e.conn, peer, PairDecisionCode.REJECTED, PairingResult.AUTO_REJECTED))
        }
        if (s.session != null) return Step(s, autoReject(e.conn, peer, PairDecisionCode.BUSY, PairingResult.BUSY))
        val next = PairingSession.AwaitingUser(peer, e.conn, e.info, e.localPublicKey, e.commitment, e.nR, null, e.now + REQUEST_TIMEOUT_MS)
        return Step(
            s.copy(session = next, rejectedAt = s.rejectedAt - peer),
            listOf(PairingEffect.Send(e.conn, PairNonce(e.nR)), PairingEffect.ShowRequest(e.info), PairingEffect.ArmTimer(next.deadline)),
        )
    }

    private fun autoReject(conn: ConnId, peer: DeviceId, code: PairDecisionCode, result: PairingResult): List<PairingEffect> =
        listOf(PairingEffect.Send(conn, PairDecision(code)), PairingEffect.Close(conn), PairingEffect.AutoRejected(peer, result))

    private fun onQrRequest(s: PairingState, e: PairingEvent.QrRequestChecked): Step<PairingState, PairingEffect> {
        val lease = e.lease
        if (lease == null || s.session != null) {
            val release = if (lease != null) listOf(PairingEffect.ReleaseToken(lease)) else emptyList()
            return Step(s, release + PairingEffect.Send(e.conn, QrPairResult(QrPairResultCode.TOKEN_INVALID)) + PairingEffect.Close(e.conn))
        }
        val next = PairingSession.AwaitingComplete(e.info.deviceId, e.conn, e.info, PairingRole.QR_DISPLAY, lease, e.now + COMPLETE_TIMEOUT_MS)
        return Step(
            s.copy(session = next),
            listOf(PairingEffect.Send(e.conn, QrPairResult(QrPairResultCode.OK)), PairingEffect.StageTrust(e.info), PairingEffect.ArmTimer(next.deadline)),
        )
    }

    private fun connOf(session: PairingSession?): ConnId? = when (session) {
        is PairingSession.AwaitingPeer -> session.conn
        is PairingSession.ShowingSas -> session.conn
        is PairingSession.AwaitingUser -> session.conn
        is PairingSession.AwaitingQrResult -> session.conn
        is PairingSession.AwaitingComplete -> session.conn
        is PairingSession.Saving -> session.conn
        is PairingSession.Connecting, null -> null
    }

    private fun onMessage(s: PairingState, e: PairingEvent.MessageReceived): Step<PairingState, PairingEffect> {
        val session = s.session
        if (session == null || connOf(session) != e.conn) return unchanged(s)
        val msg = e.msg
        return when (session) {
            is PairingSession.AwaitingPeer -> when (msg) {
                is PairNonce -> {
                    val sas = VerificationCode.sas(session.localPublicKey.toByteArray(), session.info.publicKey.toByteArray(), session.nI.toByteArray(), msg.nonce.toByteArray())
                    val next = PairingSession.ShowingSas(session.peer, session.conn, session.info, sas, session.deadline)
                    Step(s.copy(session = next), listOf(PairingEffect.Send(session.conn, PairReveal(session.nI)), PairingEffect.ShowSas(sas)))
                }
                // 揭示 nI 之前不可能被接受：按协议错误处理
                is PairDecision -> done(s, session, decisionResult(msg.decision) ?: PairingResult.DISCONNECTED, PairingEffect.Close(session.conn))
                else -> unchanged(s)
            }
            is PairingSession.ShowingSas -> when (msg) {
                is PairDecision -> {
                    val r = decisionResult(msg.decision)
                    if (r == null) {
                        Step(s.copy(session = PairingSession.Saving(session.peer, session.conn, session.info, PairingRole.INITIATOR, null)), listOf(PairingEffect.SaveTrust(session.info)))
                    } else {
                        done(s, session, r, PairingEffect.Close(session.conn))
                    }
                }
                else -> unchanged(s)
            }
            is PairingSession.AwaitingUser -> when (msg) {
                is PairReveal -> {
                    if (session.sas != null) return unchanged(s)
                    val pkI = session.info.publicKey.toByteArray()
                    val pkR = session.localPublicKey.toByteArray()
                    val nI = msg.nonce.toByteArray()
                    if (!VerificationCode.verifyCommitment(session.commitment.toByteArray(), pkI, pkR, nI)) {
                        done(s, session, PairingResult.CODE_MISMATCH, PairingEffect.DismissRequest, PairingEffect.Close(session.conn))
                    } else {
                        val sas = VerificationCode.sas(pkI, pkR, nI, session.nR.toByteArray())
                        Step(s.copy(session = session.copy(sas = sas)), listOf(PairingEffect.ShowSas(sas)))
                    }
                }
                is PairCancel -> done(s, session, cancelResult(msg.reason), PairingEffect.DismissRequest, PairingEffect.Close(session.conn))
                else -> unchanged(s)
            }
            is PairingSession.AwaitingQrResult -> when (msg) {
                is QrPairResult -> if (msg.result == QrPairResultCode.OK) {
                    Step(s.copy(session = PairingSession.Saving(session.peer, session.conn, session.info, PairingRole.QR_SCANNER, null)), listOf(PairingEffect.SaveTrust(session.info)))
                } else {
                    done(s, session, PairingResult.TOKEN_INVALID, PairingEffect.Close(session.conn))
                }
                else -> unchanged(s)
            }
            is PairingSession.AwaitingComplete -> when (msg) {
                is PairComplete -> Step(
                    s.copy(session = PairingSession.Saving(session.peer, session.conn, session.info, session.role, session.lease)),
                    listOf(PairingEffect.CommitTrust(session.peer)),
                )
                is PairCancel -> done(s, session, cancelResult(msg.reason), *abortStaged(session), PairingEffect.Close(session.conn))
                else -> unchanged(s)
            }
            is PairingSession.Connecting, is PairingSession.Saving -> unchanged(s)
        }
    }

    /** ACCEPTED → null（继续），其余 → 对应结局。 */
    private fun decisionResult(code: PairDecisionCode): PairingResult? = when (code) {
        PairDecisionCode.ACCEPTED -> null
        PairDecisionCode.REJECTED -> PairingResult.REJECTED
        PairDecisionCode.BUSY -> PairingResult.BUSY
        PairDecisionCode.TIMEOUT -> PairingResult.TIMEOUT
    }

    private fun cancelResult(reason: PairCancelReason): PairingResult =
        if (reason == PairCancelReason.TIMEOUT) PairingResult.TIMEOUT else PairingResult.CANCELLED

    /** 放弃已暂存的信任；显示方同时归还令牌租约。 */
    private fun abortStaged(session: PairingSession.AwaitingComplete): Array<PairingEffect> {
        val list = mutableListOf<PairingEffect>(PairingEffect.DiscardStagedTrust(session.peer))
        session.lease?.let { list += PairingEffect.ReleaseToken(it) }
        return list.toTypedArray()
    }

    private fun onDecision(s: PairingState, e: PairingEvent.UserDecision): Step<PairingState, PairingEffect> {
        val session = s.session as? PairingSession.AwaitingUser ?: return unchanged(s)
        if (session.sas == null) return unchanged(s) // 还没有显示验证码，不能决定
        return if (e.accept) {
            val next = PairingSession.AwaitingComplete(session.peer, session.conn, session.info, PairingRole.RECEIVER, null, e.now + COMPLETE_TIMEOUT_MS)
            Step(
                s.copy(session = next),
                listOf(PairingEffect.StageTrust(session.info), PairingEffect.Send(session.conn, PairDecision(PairDecisionCode.ACCEPTED)), PairingEffect.ArmTimer(next.deadline)),
            )
        } else {
            val step = done(s, session, PairingResult.REJECTED, PairingEffect.Send(session.conn, PairDecision(PairDecisionCode.REJECTED)), PairingEffect.Close(session.conn))
            step.copy(state = step.state.copy(rejectedAt = s.rejectedAt + (session.peer to e.now)))
        }
    }

    private fun onCancel(s: PairingState): Step<PairingState, PairingEffect> = when (val session = s.session) {
        is PairingSession.Connecting -> done(s, session, PairingResult.CANCELLED, PairingEffect.CancelConnect)
        is PairingSession.AwaitingPeer -> done(s, session, PairingResult.CANCELLED, PairingEffect.Send(session.conn, PairCancel(PairCancelReason.USER)), PairingEffect.Close(session.conn))
        is PairingSession.ShowingSas -> done(s, session, PairingResult.CANCELLED, PairingEffect.Send(session.conn, PairCancel(PairCancelReason.USER)), PairingEffect.Close(session.conn))
        is PairingSession.AwaitingQrResult -> done(s, session, PairingResult.CANCELLED, PairingEffect.Close(session.conn))
        else -> unchanged(s)
    }

    private fun onTrustStored(s: PairingState, e: PairingEvent.TrustStored): Step<PairingState, PairingEffect> {
        val session = s.session as? PairingSession.Saving ?: return unchanged(s)
        if (session.peer != e.peer) return unchanged(s)
        val conn = session.conn
        if (!e.ok) {
            // 接收方 / 显示方提交失败：丢弃暂存；显示方归还令牌租约
            val cleanup = mutableListOf<PairingEffect>()
            if (session.role == PairingRole.RECEIVER || session.role == PairingRole.QR_DISPLAY) cleanup += PairingEffect.DiscardStagedTrust(session.peer)
            session.lease?.let { cleanup += PairingEffect.ReleaseToken(it) }
            return done(s, session, PairingResult.STORE_FAILED, *cleanup.toTypedArray(), PairingEffect.Close(conn))
        }
        return when (session.role) {
            PairingRole.INITIATOR, PairingRole.QR_SCANNER ->
                done(s, session, PairingResult.SUCCESS, PairingEffect.Send(conn, PairComplete), PairingEffect.PromoteSession(conn))
            PairingRole.RECEIVER -> done(s, session, PairingResult.SUCCESS, PairingEffect.PromoteSession(conn))
            PairingRole.QR_DISPLAY -> {
                val used = session.lease?.let { arrayOf<PairingEffect>(PairingEffect.MarkTokenUsed(it)) } ?: emptyArray()
                done(s, session, PairingResult.SUCCESS, *used, PairingEffect.PromoteSession(conn))
            }
        }
    }

    private fun onDisconnected(s: PairingState, e: PairingEvent.Disconnected): Step<PairingState, PairingEffect> {
        val session = s.session
        if (session == null || connOf(session) != e.conn) return unchanged(s)
        return when (session) {
            is PairingSession.AwaitingUser -> done(s, session, PairingResult.DISCONNECTED, PairingEffect.DismissRequest)
            is PairingSession.AwaitingComplete -> done(s, session, PairingResult.DISCONNECTED, *abortStaged(session))
            // 发起方 / 扫描方可能已经保存：删除（7.12）。接收方 / 显示方已收到 PAIR_COMPLETE，结局由 TrustStored 决定
            is PairingSession.Saving -> if (session.role == PairingRole.INITIATOR || session.role == PairingRole.QR_SCANNER) {
                done(s, session, PairingResult.DISCONNECTED, PairingEffect.DeleteTrust(session.peer))
            } else {
                unchanged(s)
            }
            else -> done(s, session, PairingResult.DISCONNECTED)
        }
    }

    private fun onTick(s: PairingState, now: Long): Step<PairingState, PairingEffect> {
        val pruned = s.rejectedAt.filterValues { now - it < COOLDOWN_MS }
        val base = if (pruned.size == s.rejectedAt.size) s else s.copy(rejectedAt = pruned)
        val session = base.session
        val deadline = session?.deadline
        if (session == null || deadline == null || now < deadline) return unchanged(base)
        return when (session) {
            is PairingSession.Connecting -> done(base, session, PairingResult.CONNECT_FAILED, PairingEffect.CancelConnect)
            is PairingSession.AwaitingPeer ->
                done(base, session, PairingResult.TIMEOUT, PairingEffect.Send(session.conn, PairCancel(PairCancelReason.TIMEOUT)), PairingEffect.Close(session.conn))
            is PairingSession.ShowingSas ->
                done(base, session, PairingResult.TIMEOUT, PairingEffect.Send(session.conn, PairCancel(PairCancelReason.TIMEOUT)), PairingEffect.Close(session.conn))
            is PairingSession.AwaitingUser -> done(
                base,
                session,
                PairingResult.TIMEOUT,
                PairingEffect.Send(session.conn, PairDecision(PairDecisionCode.TIMEOUT)),
                PairingEffect.DismissRequest,
                PairingEffect.Close(session.conn),
            )
            is PairingSession.AwaitingQrResult -> done(base, session, PairingResult.TIMEOUT, PairingEffect.Close(session.conn))
            is PairingSession.AwaitingComplete -> done(base, session, PairingResult.DISCONNECTED, *abortStaged(session), PairingEffect.Close(session.conn))
            is PairingSession.Saving -> unchanged(base)
        }
    }
}
