package com.lantransfer.app.platform

import android.content.Context
import android.net.wifi.WifiManager
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.LocalInterface
import com.lantransfer.engine.ports.ConnectResult
import com.lantransfer.engine.ports.Datagram
import com.lantransfer.engine.ports.SocketAddr
import com.lantransfer.engine.ports.TcpConn
import com.lantransfer.engine.ports.TcpListener
import com.lantransfer.engine.ports.Transport
import com.lantransfer.engine.ports.UdpEndpoint
import com.lantransfer.engine.ports.UdpOpenResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.ProtocolConstants
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.ConnectException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.MulticastSocket
import java.net.NetworkInterface
import java.net.NoRouteToHostException
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Android 局域网传输层（设计 C6.1、需求 2.6–2.10）：
 * - TCP 出站强制通过 Wi-Fi Network.bindSocket 或本地接口 IP 绑定，绝不走蜂窝网络或 DNS；
 * - 统一 512 KiB 发送缓冲与 TCP_NODELAY；
 * - UDP 发现使用 MulticastLock + MulticastSocket(SO_REUSEADDR) 并加入 239.255.42.58。
 */
class AndroidTransport(
    private val context: Context,
    private val networkMonitor: AndroidNetworkMonitor,
) : Transport {
    override suspend fun connect(via: LocalInterface, ip: Ipv4, port: Int, timeoutMs: Long): ConnectResult =
        withContext(Dispatchers.IO) {
            val socket = Socket()
            try {
                socket.tcpNoDelay = true
                socket.sendBufferSize = SEND_BUFFER_BYTES
                socket.receiveBufferSize = SEND_BUFFER_BYTES
                val wifiNet = networkMonitor.wifiNetworkFor(via.name)
                try {
                    if (wifiNet != null) {
                        wifiNet.bindSocket(socket)
                    } else {
                        socket.bind(InetSocketAddress(via.address.toInetAddress(), 0))
                    }
                } catch (_: IOException) {
                    runCatching { socket.close() }
                    return@withContext ConnectResult.BindFailed(networkMonitor.hasActiveVpn())
                }

                socket.connect(InetSocketAddress(ip.toInetAddress(), port), timeoutMs.toInt().coerceAtLeast(100))
                ConnectResult.Ok(SocketTcpConn(socket))
            } catch (_: SocketTimeoutException) {
                runCatching { socket.close() }
                ConnectResult.Timeout
            } catch (_: ConnectException) {
                runCatching { socket.close() }
                ConnectResult.Refused
            } catch (_: NoRouteToHostException) {
                runCatching { socket.close() }
                ConnectResult.Unreachable
            } catch (_: IOException) {
                runCatching { socket.close() }
                ConnectResult.Unreachable
            }
        }

    override fun listen(candidatePorts: List<Int>): TcpListener? {
        for (p in candidatePorts) {
            try {
                val ss = ServerSocket()
                ss.reuseAddress = true
                ss.bind(InetSocketAddress(p))
                return ServerSocketTcpListener(ss)
            } catch (_: IOException) {
                // 尝试下一个候选端口
            }
        }
        return null
    }

    override fun openUdp(port: Int): UdpOpenResult = try {
        val wifiMgr = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val lock = wifiMgr?.createMulticastLock("LanTransferDiscovery")?.apply {
            setReferenceCounted(false)
            runCatching { acquire() }
        }
        val socket = MulticastSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(InetSocketAddress(port))
        }
        val group = InetSocketAddress(ProtocolConstants.MULTICAST_GROUP.toIpv4().toInetAddress(), port)
        for (iface in networkMonitor.interfaces.value) {
            runCatching {
                val nif = NetworkInterface.getByName(iface.name) ?: return@runCatching
                socket.joinGroup(group, nif)
            }
        }
        UdpOpenResult.Ok(AndroidUdpEndpoint(socket, lock, networkMonitor))
    } catch (e: IOException) {
        UdpOpenResult.Failed(e.message ?: "无法绑定发现端口")
    }

    private companion object {
        const val SEND_BUFFER_BYTES = 512 * 1024

        fun String.toIpv4(): Ipv4 {
            val parts = split('.').map { it.toInt() }
            return Ipv4.of(parts[0], parts[1], parts[2], parts[3])
        }
    }
}

private class SocketTcpConn(private val socket: Socket) : TcpConn {
    private val input = socket.getInputStream()
    private val output = socket.getOutputStream()
    private val closed = AtomicBoolean(false)

    override val local: SocketAddr = SocketAddr(
        (socket.localAddress as? Inet4Address)?.toIpv4() ?: Ipv4(0),
        socket.localPort,
    )

    override val remote: SocketAddr = SocketAddr(
        (socket.inetAddress as? Inet4Address)?.toIpv4() ?: Ipv4(0),
        socket.port,
    )

    override suspend fun read(buf: ByteArray, off: Int, len: Int): Int = withContext(Dispatchers.IO) {
        if (closed.get()) return@withContext -1
        try {
            input.read(buf, off, len)
        } catch (_: IOException) {
            -1
        }
    }

    override suspend fun write(b: ByteArray, off: Int, len: Int): Boolean = withContext(Dispatchers.IO) {
        if (closed.get()) return@withContext false
        try {
            output.write(b, off, len)
            output.flush()
            true
        } catch (_: IOException) {
            false
        }
    }

    override fun close() {
        if (closed.compareAndSet(false, true)) {
            runCatching { socket.close() }
        }
    }
}

private class ServerSocketTcpListener(private val server: ServerSocket) : TcpListener {
    override val port: Int get() = server.localPort

    override suspend fun accept(): TcpConn? = withContext(Dispatchers.IO) {
        try {
            val s = server.accept()
            s.tcpNoDelay = true
            s.sendBufferSize = 512 * 1024
            s.receiveBufferSize = 512 * 1024
            SocketTcpConn(s)
        } catch (_: IOException) {
            null
        }
    }

    override fun close() {
        runCatching { server.close() }
    }
}

private class AndroidUdpEndpoint(
    private val socket: MulticastSocket,
    private val multicastLock: WifiManager.MulticastLock?,
    private val networkMonitor: AndroidNetworkMonitor,
) : UdpEndpoint {
    private val closed = AtomicBoolean(false)
    private val joined = HashSet<String>()

    override suspend fun receive(): Datagram? = withContext(Dispatchers.IO) {
        val buf = ByteArray(4096)
        val packet = DatagramPacket(buf, buf.size)
        while (!closed.get()) {
            try {
                socket.receive(packet)
                val addr = packet.address as? Inet4Address ?: continue
                val payload = buf.copyOf(packet.length)
                return@withContext Datagram(
                    data = Bytes.of(payload),
                    src = addr.toIpv4(),
                    srcPort = packet.port,
                    via = null,
                )
            } catch (_: SocketException) {
                if (closed.get()) return@withContext null
            } catch (_: IOException) {
                if (closed.get()) return@withContext null
            }
        }
        null
    }

    override suspend fun send(via: LocalInterface, dst: Ipv4, port: Int, bytes: ByteArray): Boolean =
        withContext(Dispatchers.IO) {
            if (closed.get()) return@withContext false
            ensureJoined(via.name)
            try {
                DatagramSocket(null).use { tx ->
                    tx.reuseAddress = true
                    tx.broadcast = true
                    val wifiNet = networkMonitor.wifiNetworkFor(via.name)
                    if (wifiNet != null) {
                        wifiNet.bindSocket(tx)
                    } else {
                        tx.bind(InetSocketAddress(via.address.toInetAddress(), 0))
                    }
                    val packet = DatagramPacket(bytes, bytes.size, dst.toInetAddress(), port)
                    tx.send(packet)
                }
                true
            } catch (_: IOException) {
                false
            }
        }

    override fun close() {
        if (closed.compareAndSet(false, true)) {
            runCatching { socket.close() }
            runCatching {
                if (multicastLock?.isHeld == true) {
                    multicastLock.release()
                }
            }
        }
    }

    private fun ensureJoined(ifaceName: String) {
        synchronized(joined) {
            if (joined.add(ifaceName)) {
                runCatching {
                    val nif = NetworkInterface.getByName(ifaceName) ?: return@runCatching
                    val parts = ProtocolConstants.MULTICAST_GROUP.split('.').map { it.toInt() }
                    val groupIp = Ipv4.of(parts[0], parts[1], parts[2], parts[3])
                    socket.joinGroup(InetSocketAddress(groupIp.toInetAddress(), ProtocolConstants.DISCOVERY_PORT), nif)
                }
            }
        }
    }
}
