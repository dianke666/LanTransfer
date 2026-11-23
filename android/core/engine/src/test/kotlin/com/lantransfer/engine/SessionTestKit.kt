package com.lantransfer.engine

import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.IfaceKind
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.ConnectResult
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.engine.testing.InMemoryTrustStore
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.EphemeralKeySource
import com.lantransfer.protocol.FrameDecoder
import com.lantransfer.protocol.FrameEncoder
import com.lantransfer.protocol.NoiseXX
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.RecordCipher
import com.lantransfer.protocol.RecordFeedResult
import com.lantransfer.protocol.Role
import com.lantransfer.protocol.X25519KeyPair
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import java.util.ArrayDeque

// 会话层测试的公共装置：内存网络上的测试设备、直接构造的 Connection 与只持有密钥的“原始对端”。

fun iface(name: String, a: Int, b: Int, c: Int, d: Int, prefix: Int = 24) = LocalInterface(name, IfaceKind.WIFI, Ipv4.of(a, b, c, d), prefix)

fun deviceIdOf(b: Int): DeviceId = DeviceId(Bytes16.of(ByteArray(16) { b.toByte() }))

fun keyPairOf(seed: Int): X25519KeyPair = X25519KeyPair.fromPrivateKey(ByteArray(32) { (seed * 31 + it + 1).toByte() })

/** 内存网络上的一台设备：SessionManager + 内存信任存储 + 可写的地址簿。设备 ID 的 16 个字节都是 [idByte]，便于控制大小关系。 */
class TestDevice(
    val name: String,
    idByte: Int,
    val iface: LocalInterface,
    net: InMemoryNetwork,
    clock: Clock,
    scope: CoroutineScope,
    val type: DeviceType = DeviceType.ANDROID,
    version: ProtocolVersion = ProtocolVersion.CURRENT,
    timings: SessionTimings = SessionTimings(),
    identityOverride: DeviceIdentity? = null,
) {
    val transport = net.host(name, setOf(iface))
    val identity = identityOverride ?: DeviceIdentity(deviceIdOf(idByte), keyPairOf(idByte), name)
    val id: DeviceId get() = identity.deviceId
    val publicKey: Bytes get() = identity.keyPair.publicKey
    val trust = InMemoryTrustStore()
    val addressBook = HashMap<DeviceId, SocketAddr>()
    val manager = SessionManager(
        transport, transport.monitor, identity, type, trust, clock, scope,
        keys = EphemeralKeySource.secure(),
        addressBook = { addressBook[it] },
        version = version,
        timings = timings,
    )
    var port = 0
        private set

    fun listen(): Int {
        port = (manager.startListening() as ListenResult.Ok).port
        return port
    }

    val address: SocketAddr get() = SocketAddr(iface.address, port)

    fun record(publicKey: Bytes = this.publicKey, displayName: String = name) = TrustedDevice(id, displayName, type, publicKey, pairedAtUtcMs = 1)

    /** 把 [other] 记为可信设备（[key] 默认是它的真实公钥），并把它的地址写进地址簿。 */
    fun trusts(other: TestDevice, key: Bytes = other.publicKey, displayName: String = other.name) {
        trust.upsert(other.record(key, displayName))
        knows(other)
    }

    fun knows(other: TestDevice) {
        addressBook[other.id] = other.address
    }
}

/** 在内存中完成一次 NoiseXX，返回（发起方发送, 发起方接收, 接收方发送, 接收方接收）。 */
fun cipherQuad(): List<RecordCipher> {
    val prologue = NoiseXX.prologue(ByteArray(6), ByteArray(6))
    val i = NoiseXX(Role.INITIATOR, keyPairOf(101), prologue, EphemeralKeySource.secure())
    val r = NoiseXX(Role.RESPONDER, keyPairOf(102), prologue, EphemeralKeySource.secure())
    r.readMessage(i.writeMessage(ByteArray(0)))
    i.readMessage(r.writeMessage(ByteArray(0)))
    r.readMessage(i.writeMessage(ByteArray(0)))
    val (iSend, iRecv) = i.split()
    val (rSend, rRecv) = r.split()
    return listOf(iSend, iRecv, rSend, rRecv)
}

/** 只持有密钥的对端：可以发送任意消息或任意字节，并解密读取被测连接写出的内容。 */
class RawPeer(val sock: TcpConn, private val send: RecordCipher, recv: RecordCipher) {
    private val records = recv.openStream()
    private val decoder = FrameDecoder()
    private val queue = ArrayDeque<ProtocolMessage>()

    suspend fun send(m: ProtocolMessage): Boolean = sock.write(send.seal(FrameEncoder.encodeOrThrow(m)))

    /** 把任意明文封装为合法 record 发出（用于构造解码错误）。 */
    suspend fun sendPlain(plain: ByteArray): Boolean = sock.write(send.seal(plain))

    suspend fun sendRaw(bytes: ByteArray): Boolean = sock.write(bytes)

    /** 下一条消息；对方关闭或记录层出错时返回 null。 */
    suspend fun receive(): ProtocolMessage? {
        val buf = ByteArray(64 * 1024)
        while (queue.isEmpty()) {
            val n = sock.read(buf)
            if (n < 0) return null
            when (val r = records.feed(buf, 0, n)) {
                is RecordFeedResult.Plain -> r.chunks.forEach { c -> queue.addAll(decoder.feed(c.toByteArray()).messages) }
                is RecordFeedResult.IntegrityError -> return null
            }
        }
        return queue.removeFirst()
    }
}

/** 记录每次写入时刻（单调时钟）与长度的 TcpConn 包装。写循环每次写出 1 个 Frame 封装成的全部 record。 */
class RecordingConn(private val inner: TcpConn, private val clock: Clock) : TcpConn by inner {
    val writeTimes = ArrayList<Long>()
    val writeLengths = ArrayList<Int>()

    override suspend fun write(b: ByteArray, off: Int, len: Int): Boolean {
        writeTimes += clock.monotonicMs()
        writeLengths += len
        return inner.write(b, off, len)
    }

    companion object {
        /** HEARTBEAT 帧（7 字节帧头、空载荷）封装成 1 条 record 后的长度：2 + 7 + 16。 */
        const val HEARTBEAT_WIRE_LENGTH = 25
    }
}

/** 每次写入都要等测试放行 1 次的 TcpConn 包装（检查写循环的选择顺序）。 */
class GatedConn(private val inner: TcpConn) : TcpConn by inner {
    private val permits = Channel<Unit>(Channel.UNLIMITED)

    @Volatile
    var waiting = 0
        private set

    fun release(n: Int = 1) = repeat(n) { permits.trySend(Unit) }

    override suspend fun write(b: ByteArray, off: Int, len: Int): Boolean {
        waiting++
        permits.receive()
        waiting--
        return inner.write(b, off, len)
    }
}

/** 被测 Connection（[role]）与对面的 [RawPeer]，经内存网络上的一条 TCP 连接相连。 */
class ConnUnderTest(val conn: Connection, val peer: RawPeer, val wire: TcpConn)

suspend fun connectionUnderTest(
    scope: CoroutineScope,
    clock: Clock,
    role: Role = Role.RESPONDER,
    timings: SessionTimings = SessionTimings(),
    wrap: (TcpConn) -> TcpConn = { it },
): ConnUnderTest {
    val net = InMemoryNetwork()
    val ia = iface("wlan0", 192, 168, 1, 2)
    val ib = iface("wlan0", 192, 168, 1, 3)
    val a = net.host("peer", setOf(ia))
    val b = net.host("local", setOf(ib))
    val listener = b.listen(listOf(42580))!!
    val peerSock = (a.connect(ia, ib.address, 42580, 1_000) as ConnectResult.Ok).conn
    val localSock = wrap(listener.accept()!!)
    val (iSend, iRecv, rSend, rRecv) = cipherQuad()
    val (mySend, myRecv, peerSend, peerRecv) = if (role == Role.RESPONDER) listOf(rSend, rRecv, iSend, iRecv) else listOf(iSend, iRecv, rSend, rRecv)
    val conn = Connection(localSock, ConnReader(localSock), role, ProtocolVersion.CURRENT, mySend, myRecv, clock, scope, timings)
    conn.start()
    return ConnUnderTest(conn, RawPeer(peerSock, peerSend, peerRecv), localSock)
}
