import Darwin
import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import Network

/// iOS 零外网原生传输层：TCP 基于 `Network.framework`（显式禁用蜂窝 + 绑定本机私有接口 IP）；UDP 发现基于 POSIX BSD Socket（无组播权限时优雅降级）。
public final class IosTransport: Transport, @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.lantransfer.transport", qos: .userInitiated, attributes: .concurrent)

    public init() {}

    public func connect(via: LocalInterface, ip: Ipv4, port: Int, timeoutSeconds: TimeInterval) async -> ConnectResult {
        guard let nwPort = NWEndpoint.Port(rawValue: UInt16(port)),
              let remoteIp = IPv4Address(ip.description),
              let localIp = IPv4Address(via.address.description) else {
            return .unreachable
        }

        let tcpOptions = NWProtocolTCP.Options()
        tcpOptions.noDelay = true
        tcpOptions.connectionTimeout = max(1, Int(ceil(timeoutSeconds)))

        let params = NWParameters(tls: nil, tcp: tcpOptions)
        params.prohibitedInterfaceTypes = [.cellular]
        params.requiredLocalEndpoint = .hostPort(host: .ipv4(localIp), port: .any)
        params.allowLocalEndpointReuse = true

        let connection = NWConnection(host: .ipv4(remoteIp), port: nwPort, using: params)
        let stateBox = ConnectStateBox()

        return await withCheckedContinuation { continuation in
            let timeoutItem = DispatchWorkItem {
                if stateBox.finish() {
                    connection.cancel()
                    continuation.resume(returning: .timeout)
                }
            }
            self.queue.asyncAfter(deadline: .now() + timeoutSeconds, execute: timeoutItem)

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if stateBox.finish() {
                        timeoutItem.cancel()
                        let localAddr = Self.extractLocalAddr(connection, fallback: via.address)
                        let remoteAddr = SocketAddr(ip: ip, port: port)
                        continuation.resume(returning: .ok(NwTcpConn(connection: connection, local: localAddr, remote: remoteAddr)))
                    }
                case .failed(let error):
                    if stateBox.finish() {
                        timeoutItem.cancel()
                        connection.cancel()
                        continuation.resume(returning: Self.mapConnectError(error))
                    }
                case .waiting(let error):
                    if case .posix(let code) = error, code == .ECONNREFUSED {
                        if stateBox.finish() {
                            timeoutItem.cancel()
                            connection.cancel()
                            continuation.resume(returning: .refused)
                        }
                    }
                case .cancelled:
                    if stateBox.finish() {
                        timeoutItem.cancel()
                        continuation.resume(returning: .unreachable)
                    }
                default:
                    break
                }
            }
            connection.start(queue: self.queue)
        }
    }

    public func listen(candidatePorts: [Int]) async -> (any TcpListener)? {
        for candidate in candidatePorts {
            let tcpOptions = NWProtocolTCP.Options()
            tcpOptions.noDelay = true
            let params = NWParameters(tls: nil, tcp: tcpOptions)
            params.prohibitedInterfaceTypes = [.cellular]
            params.allowLocalEndpointReuse = true

            do {
                let listener: NWListener
                if candidate == 0 {
                    listener = try NWListener(using: params)
                } else if let nwPort = NWEndpoint.Port(rawValue: UInt16(candidate)) {
                    listener = try NWListener(using: params, on: nwPort)
                } else {
                    continue
                }
                if let started = await NwTcpListener.start(listener: listener, queue: queue) {
                    return started
                }
            } catch {
                continue
            }
        }
        return nil
    }

    public func openUdp(port: Int, multicastGroup: Ipv4, interfaces: [LocalInterface]) async -> UdpOpenResult {
        BsdUdpEndpoint.open(port: port, multicastGroup: multicastGroup, interfaces: interfaces)
    }

    private static func extractLocalAddr(_ conn: NWConnection, fallback: Ipv4) -> SocketAddr {
        if let local = conn.currentPath?.localEndpoint,
           case .hostPort(let host, let port) = local,
           case .ipv4(let v4) = host {
            let bytes = [UInt8](v4.rawValue)
            if bytes.count == 4 {
                return SocketAddr(ip: Ipv4(bytes[0], bytes[1], bytes[2], bytes[3]), port: Int(port.rawValue))
            }
        }
        return SocketAddr(ip: fallback, port: 0)
    }

    private static func mapConnectError(_ error: NWError) -> ConnectResult {
        if case .posix(let code) = error {
            switch code {
            case .ECONNREFUSED: return .refused
            case .EADDRNOTAVAIL, .EPERM, .EACCES: return .bindFailed(vpnSuspected: true)
            case .ETIMEDOUT: return .timeout
            default: return .unreachable
            }
        }
        return .unreachable
    }
}

private final class ConnectStateBox: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    func finish() -> Bool {
        lock.withLock {
            if done { return false }
            done = true
            return true
        }
    }
}

/// NWConnection 封装为引擎 `TcpConn` 端口。
private final class NwTcpConn: TcpConn, @unchecked Sendable {
    let local: SocketAddr
    let remote: SocketAddr
    private let connection: NWConnection

    init(connection: NWConnection, local: SocketAddr, remote: SocketAddr) {
        self.connection = connection
        self.local = local
        self.remote = remote
    }

    func read(maxBytes: Int) async -> Data? {
        await withCheckedContinuation { continuation in
            connection.receive(minimumIncompleteLength: 1, maximumLength: maxBytes) { content, _, isComplete, error in
                if let data = content, !data.isEmpty {
                    continuation.resume(returning: data)
                } else if isComplete || error != nil {
                    continuation.resume(returning: nil)
                } else {
                    continuation.resume(returning: nil)
                }
            }
        }
    }

    func write(_ data: Data) async -> Bool {
        if data.isEmpty { return true }
        return await withCheckedContinuation { continuation in
            connection.send(content: data, completion: .contentProcessed { error in
                continuation.resume(returning: error == nil)
            })
        }
    }

    func close() async {
        connection.cancel()
    }
}

/// NWListener 封装为引擎 `TcpListener` 端口。
private actor NwTcpListener: TcpListener {
    nonisolated let port: Int
    private let listener: NWListener
    private let queue: DispatchQueue
    private var incoming: [any TcpConn] = []
    private var waiter: CheckedContinuation<(any TcpConn)?, Never>?
    private var closed = false

    private init(listener: NWListener, port: Int, queue: DispatchQueue) {
        self.listener = listener
        self.port = port
        self.queue = queue
    }

    static func start(listener: NWListener, queue: DispatchQueue) async -> NwTcpListener? {
        let stateBox = ConnectStateBox()
        let boundPort: Int? = await withCheckedContinuation { continuation in
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if stateBox.finish() {
                        continuation.resume(returning: listener.port.map { Int($0.rawValue) })
                    }
                case .failed, .cancelled:
                    if stateBox.finish() {
                        listener.cancel()
                        continuation.resume(returning: nil)
                    }
                default:
                    break
                }
            }
            listener.start(queue: queue)
        }
        guard let port = boundPort, port > 0 else { return nil }
        let wrapper = NwTcpListener(listener: listener, port: port, queue: queue)
        await wrapper.attachAcceptHandler()
        return wrapper
    }

    private func attachAcceptHandler() {
        let q = self.queue
        let listenPort = self.port
        listener.newConnectionHandler = { [weak self] nwConn in
            nwConn.start(queue: q)
            let remoteAddr = Self.extractRemote(nwConn)
            let localAddr = SocketAddr(ip: Ipv4(0, 0, 0, 0), port: listenPort)
            let tcp = NwTcpConn(connection: nwConn, local: localAddr, remote: remoteAddr)
            Task { await self?.enqueue(tcp) }
        }
    }

    private func enqueue(_ conn: any TcpConn) {
        guard !closed else {
            Task { await conn.close() }
            return
        }
        if let w = waiter {
            waiter = nil
            w.resume(returning: conn)
        } else {
            incoming.append(conn)
        }
    }

    func accept() async -> (any TcpConn)? {
        if !incoming.isEmpty {
            return incoming.removeFirst()
        }
        if closed { return nil }
        return await withCheckedContinuation { cont in
            self.waiter = cont
        }
    }

    func close() {
        closed = true
        listener.cancel()
        if let w = waiter {
            waiter = nil
            w.resume(returning: nil)
        }
    }

    private static func extractRemote(_ conn: NWConnection) -> SocketAddr {
        if case .hostPort(let host, let port) = conn.endpoint,
           case .ipv4(let v4) = host {
            let bytes = [UInt8](v4.rawValue)
            if bytes.count == 4 {
                return SocketAddr(ip: Ipv4(bytes[0], bytes[1], bytes[2], bytes[3]), port: Int(port.rawValue))
            }
        }
        return SocketAddr(ip: Ipv4(0, 0, 0, 0), port: 0)
    }
}

/// POSIX BSD UDP 组播 + 定向广播端点（无组播权限或本地网络被拒时返回 `.failed` 以触发优雅降级）。
private final class BsdUdpEndpoint: UdpEndpoint, @unchecked Sendable {
    private let lock = NSLock()
    private var fd: Int32
    private var closed = false
    private let recvQueue = DispatchQueue(label: "com.lantransfer.udp.rx", qos: .utility)

    private init(fd: Int32) {
        self.fd = fd
    }

    static func open(port: Int, multicastGroup: Ipv4, interfaces: [LocalInterface]) -> UdpOpenResult {
        let sock = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard sock >= 0 else {
            return .failed(reason: "UDP socket failed: errno=\(errno)")
        }

        var yes: Int32 = 1
        _ = setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        _ = setsockopt(sock, SOL_SOCKET, SO_REUSEPORT, &yes, socklen_t(MemoryLayout<Int32>.size))
        _ = setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &yes, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = UInt16(port).bigEndian
        addr.sin_addr = in_addr(s_addr: INADDR_ANY)

        let bindRes = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(sock, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindRes == 0 else {
            let err = errno
            Darwin.close(sock)
            return .failed(reason: "UDP bind failed: errno=\(err)")
        }

        // 尝试在各私有接口加入 239.255.42.58 组播组
        var joinedAny = false
        for iface in interfaces {
            var mreq = ip_mreq(
                imr_multiaddr: in_addr(s_addr: multicastGroup.rawValue.bigEndian),
                imr_interface: in_addr(s_addr: iface.address.rawValue.bigEndian)
            )
            if setsockopt(sock, IPPROTO_IP, IP_ADD_MEMBERSHIP, &mreq, socklen_t(MemoryLayout<ip_mreq>.size)) == 0 {
                joinedAny = true
            }
        }
        if !joinedAny && !interfaces.isEmpty {
            // 未获批组播权限或本地网络拒绝，安全降级（仍支持扫码与手动连接）
            Darwin.close(sock)
            return .failed(reason: "Multicast membership unavailable")
        }

        // 设置接收超时 1s 以便 close 时迅速退出
        var tv = timeval(tv_sec: 1, tv_usec: 0)
        _ = setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        return .ok(BsdUdpEndpoint(fd: sock))
    }

    func receive() async -> Datagram? {
        await withCheckedContinuation { continuation in
            recvQueue.async {
                var buf = [UInt8](repeating: 0, count: 2048)
                while true {
                    let currentFd = self.lock.withLock { self.closed ? Int32(-1) : self.fd }
                    if currentFd < 0 {
                        continuation.resume(returning: nil)
                        return
                    }
                    var srcAddr = sockaddr_in()
                    var srcLen = socklen_t(MemoryLayout<sockaddr_in>.size)
                    let n = withUnsafeMutablePointer(to: &srcAddr) {
                        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                            Darwin.recvfrom(currentFd, &buf, buf.count, 0, $0, &srcLen)
                        }
                    }
                    if n > 0 {
                        let ip = Ipv4(rawValue: UInt32(bigEndian: srcAddr.sin_addr.s_addr))
                        let port = Int(UInt16(bigEndian: srcAddr.sin_port))
                        continuation.resume(returning: Datagram(data: Data(buf[0..<n]), src: ip, srcPort: port, via: nil))
                        return
                    }
                    let err = errno
                    if err == EAGAIN || err == EWOULDBLOCK || err == EINTR {
                        continue
                    }
                    continuation.resume(returning: nil)
                    return
                }
            }
        }
    }

    func send(via: LocalInterface, dst: Ipv4, port: Int, data: Data) async -> Bool {
        lock.withLock {
            guard !closed, fd >= 0 else { return false }
            var ifAddr = in_addr(s_addr: via.address.rawValue.bigEndian)
            _ = setsockopt(fd, IPPROTO_IP, IP_MULTICAST_IF, &ifAddr, socklen_t(MemoryLayout<in_addr>.size))

            var dstAddr = sockaddr_in()
            dstAddr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            dstAddr.sin_family = sa_family_t(AF_INET)
            dstAddr.sin_port = UInt16(port).bigEndian
            dstAddr.sin_addr = in_addr(s_addr: dst.rawValue.bigEndian)

            let sent = data.withUnsafeBytes { rawBuf -> Int in
                guard let base = rawBuf.baseAddress else { return -1 }
                return withUnsafePointer(to: &dstAddr) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        Darwin.sendto(fd, base, data.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
            }
            return sent == data.count
        }
    }

    func close() async {
        lock.withLock {
            if !closed && fd >= 0 {
                closed = true
                Darwin.shutdown(fd, SHUT_RDWR)
                Darwin.close(fd)
                fd = -1
            }
        }
    }
}
