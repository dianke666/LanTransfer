package com.lantransfer.engine

import com.lantransfer.domain.DisplayNameRules
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.EphemeralKeySource
import com.lantransfer.protocol.HandshakeAuthError
import com.lantransfer.protocol.HandshakeIdentity
import com.lantransfer.protocol.HandshakeIdentityCodec
import com.lantransfer.protocol.Intent
import com.lantransfer.protocol.Negotiation
import com.lantransfer.protocol.NoiseXX
import com.lantransfer.protocol.PreambleCodec
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.RecordCipher
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.X25519KeyPair
import kotlinx.coroutines.withTimeoutOrNull

// 连接前导与 Noise_XX 握手（设计 C1.5、C1.6，规范第 5、6 节）。握手消息在 TCP 上封装为 len:u16 ‖ noise_message。

/** 带缓冲的读取器：握手阶段多读到的字节（对方紧接着发来的 record）留给连接的读循环。 */
internal class ConnReader(private val conn: TcpConn) {
    private val buf = ByteArray(BUF_SIZE)
    private var pos = 0
    private var lim = 0

    /** 读满 [n] 字节；对方关闭时返回 null。 */
    suspend fun readExactly(n: Int): ByteArray? {
        val out = ByteArray(n)
        var got = 0
        while (got < n) {
            if (pos == lim) {
                val k = conn.read(buf, 0, buf.size)
                if (k < 0) return null
                pos = 0
                lim = k
            }
            val take = minOf(n - got, lim - pos)
            System.arraycopy(buf, pos, out, got, take)
            pos += take
            got += take
        }
        return out
    }

    /** 先交出缓冲中剩余的字节，再从连接读取；EOF 返回 -1。 */
    suspend fun readSome(out: ByteArray): Int {
        if (pos < lim) {
            val take = minOf(out.size, lim - pos)
            System.arraycopy(buf, pos, out, 0, take)
            pos += take
            return take
        }
        return conn.read(out, 0, out.size)
    }

    private companion object {
        const val BUF_SIZE = 64 * 1024
    }
}

/** 截止时刻之前完成的结果。 */
internal sealed interface Timed<out T> {
    data class Value<T>(val value: T) : Timed<T>

    data object Expired : Timed<Nothing>
}

/** 在单调时钟的 [deadlineMs] 之前执行 [block]；已过期或超时返回 [Timed.Expired]。 */
internal suspend fun <T> withDeadline(clock: Clock, deadlineMs: Long, block: suspend () -> T): Timed<T> {
    val remaining = deadlineMs - clock.monotonicMs()
    if (remaining <= 0) return Timed.Expired
    return withTimeoutOrNull(remaining) { Timed.Value(block()) } ?: Timed.Expired
}

/** 本机在握手中出示的身份。 */
internal class LocalIdentity(val deviceId: DeviceId, val keyPair: X25519KeyPair, val displayName: String, val deviceType: DeviceType)

/** 发起方读完 msg2 后的决定。 */
internal sealed interface Msg2Decision {
    data class Proceed(val intent: Intent) : Msg2Decision

    /** 不发送 msg3，直接关闭；[failure] 为 null 表示已并入同一对端的其他连接（不算失败）。 */
    data class Stop(val failure: OpenFailure?) : Msg2Decision
}

internal sealed interface HandshakeOutcome {
    class Done(
        val peer: PeerIdentity,
        val intent: Intent,
        val version: ProtocolVersion,
        val send: RecordCipher,
        val receive: RecordCipher,
        val reader: ConnReader,
    ) : HandshakeOutcome

    data class Failed(val failure: OpenFailure) : HandshakeOutcome

    /** 发起方在 msg2 之后按 [Msg2Decision.Stop]（failure 为 null）停止。 */
    data class Merged(val peer: PeerIdentity) : HandshakeOutcome
}

internal object Handshake {
    /**
     * 发起方：前导 → msg1 → msg2（[decide] 检查对方身份并选择 intent）→ msg3。失败时不关闭 [conn]，由调用方关闭。
     * 前导在 [deadlineMs] 前未完成报 NoVersionResponse，之后的步骤超时报 Timeout。
     */
    suspend fun initiate(
        conn: TcpConn,
        local: LocalIdentity,
        keys: EphemeralKeySource,
        clock: Clock,
        deadlineMs: Long,
        version: ProtocolVersion,
        decide: (PeerIdentity) -> Msg2Decision,
    ): HandshakeOutcome {
        val reader = ConnReader(conn)
        val mine = PreambleCodec.encode(version)
        if (!conn.write(mine)) return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        val theirs = when (val r = withDeadline(clock, deadlineMs) { reader.readExactly(PreambleCodec.SIZE) }) {
            Timed.Expired -> return HandshakeOutcome.Failed(OpenFailure.NoVersionResponse)
            is Timed.Value -> r.value ?: return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        }
        val negotiated = when (val n = negotiate(version, theirs)) {
            is NegotiationResult.Fail -> return HandshakeOutcome.Failed(n.failure)
            is NegotiationResult.Ok -> n.version
        }
        val noise = NoiseXX(Role.INITIATOR, local.keyPair, NoiseXX.prologue(mine, theirs), keys)
        if (!writeHandshake(conn, noise.writeMessage(ByteArray(0)))) return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        val msg2 = when (val r = readHandshake(reader, clock, deadlineMs)) {
            is HsRead.Fail -> return HandshakeOutcome.Failed(r.failure)
            is HsRead.Ok -> r.bytes
        }
        val payload = try {
            noise.readMessage(msg2)
        } catch (_: HandshakeAuthError) {
            return HandshakeOutcome.Failed(OpenFailure.HandshakeAuthFailed)
        }
        val id = HandshakeIdentityCodec.decode(payload, expectIntent = false, version = negotiated)
            ?.takeIf { DisplayNameRules.isAcceptableRemote(it.displayName) }
            ?: return HandshakeOutcome.Failed(OpenFailure.HandshakeAuthFailed)
        val peer = PeerIdentity(id.deviceId, id.displayName, id.deviceType, Bytes.of(noise.remoteStatic!!))
        val intent = when (val d = decide(peer)) {
            is Msg2Decision.Stop -> return d.failure?.let { HandshakeOutcome.Failed(it) } ?: HandshakeOutcome.Merged(peer)
            is Msg2Decision.Proceed -> d.intent
        }
        val msg3 = noise.writeMessage(HandshakeIdentityCodec.encode(HandshakeIdentity(local.deviceId, local.displayName, local.deviceType, intent)))
        if (!writeHandshake(conn, msg3)) return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        val (send, receive) = noise.split()
        return HandshakeOutcome.Done(peer, intent, negotiated, send, receive, reader)
    }

    /**
     * 接收方：前导 → msg1（载荷必须为空）→ msg2 → msg3（必须含 intent）。失败时不关闭 [conn]。
     * 前导 magic 不符、对方提前关闭报 ProtocolError（静默关闭）；版本不兼容、握手认证失败需要在接收方提示（E1）。
     */
    suspend fun respond(
        conn: TcpConn,
        local: LocalIdentity,
        keys: EphemeralKeySource,
        clock: Clock,
        deadlineMs: Long,
        version: ProtocolVersion,
    ): HandshakeOutcome {
        val reader = ConnReader(conn)
        val mine = PreambleCodec.encode(version)
        if (!conn.write(mine)) return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        val theirs = when (val r = withDeadline(clock, deadlineMs) { reader.readExactly(PreambleCodec.SIZE) }) {
            Timed.Expired -> return HandshakeOutcome.Failed(OpenFailure.NoVersionResponse)
            is Timed.Value -> r.value ?: return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        }
        val negotiated = when (val n = negotiate(version, theirs)) {
            is NegotiationResult.Fail -> return HandshakeOutcome.Failed(n.failure)
            is NegotiationResult.Ok -> n.version
        }
        val noise = NoiseXX(Role.RESPONDER, local.keyPair, NoiseXX.prologue(theirs, mine), keys)
        val msg1 = when (val r = readHandshake(reader, clock, deadlineMs)) {
            is HsRead.Fail -> return HandshakeOutcome.Failed(r.failure)
            is HsRead.Ok -> r.bytes
        }
        try {
            if (noise.readMessage(msg1).isNotEmpty()) return HandshakeOutcome.Failed(OpenFailure.HandshakeAuthFailed)
        } catch (_: HandshakeAuthError) {
            return HandshakeOutcome.Failed(OpenFailure.HandshakeAuthFailed)
        }
        val msg2 = noise.writeMessage(HandshakeIdentityCodec.encode(HandshakeIdentity(local.deviceId, local.displayName, local.deviceType)))
        if (!writeHandshake(conn, msg2)) return HandshakeOutcome.Failed(OpenFailure.ProtocolError)
        val msg3 = when (val r = readHandshake(reader, clock, deadlineMs)) {
            is HsRead.Fail -> return HandshakeOutcome.Failed(r.failure)
            is HsRead.Ok -> r.bytes
        }
        val payload = try {
            noise.readMessage(msg3)
        } catch (_: HandshakeAuthError) {
            return HandshakeOutcome.Failed(OpenFailure.HandshakeAuthFailed)
        }
        val id = HandshakeIdentityCodec.decode(payload, expectIntent = true, version = negotiated)
            ?.takeIf { DisplayNameRules.isAcceptableRemote(it.displayName) }
            ?: return HandshakeOutcome.Failed(OpenFailure.HandshakeAuthFailed)
        val peer = PeerIdentity(id.deviceId, id.displayName, id.deviceType, Bytes.of(noise.remoteStatic!!))
        val (send, receive) = noise.split()
        return HandshakeOutcome.Done(peer, id.intent!!, negotiated, send, receive, reader)
    }

    private sealed interface NegotiationResult {
        data class Ok(val version: ProtocolVersion) : NegotiationResult

        data class Fail(val failure: OpenFailure) : NegotiationResult
    }

    private fun negotiate(local: ProtocolVersion, theirs: ByteArray): NegotiationResult {
        val remote = PreambleCodec.decode(theirs) ?: return NegotiationResult.Fail(OpenFailure.ProtocolError)
        return when (val n = PreambleCodec.negotiate(local, remote)) {
            is Negotiation.Compatible -> NegotiationResult.Ok(n.version)
            is Negotiation.Incompatible -> NegotiationResult.Fail(OpenFailure.VersionIncompatible(n.local, n.remote))
        }
    }

    private suspend fun writeHandshake(conn: TcpConn, msg: ByteArray): Boolean {
        val out = ByteArray(2 + msg.size)
        out[0] = (msg.size ushr 8).toByte()
        out[1] = msg.size.toByte()
        msg.copyInto(out, 2)
        return conn.write(out)
    }

    private sealed interface HsRead {
        class Ok(val bytes: ByteArray) : HsRead

        data class Fail(val failure: OpenFailure) : HsRead
    }

    /** 读一条握手消息：超时报 Timeout，对方关闭或长度为 0 报 ProtocolError。 */
    private suspend fun readHandshake(reader: ConnReader, clock: Clock, deadlineMs: Long): HsRead {
        val r = withDeadline(clock, deadlineMs) {
            val len = reader.readExactly(2) ?: return@withDeadline null
            val n = ((len[0].toInt() and 0xFF) shl 8) or (len[1].toInt() and 0xFF)
            if (n == 0) ByteArray(0) else reader.readExactly(n)
        }
        return when (r) {
            Timed.Expired -> HsRead.Fail(OpenFailure.Timeout)
            is Timed.Value -> {
                val bytes = r.value
                if (bytes == null || bytes.isEmpty()) HsRead.Fail(OpenFailure.ProtocolError) else HsRead.Ok(bytes)
            }
        }
    }
}
