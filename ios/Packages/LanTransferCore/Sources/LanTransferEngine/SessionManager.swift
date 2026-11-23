import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 握手完成、等待配对升级的挂起连接。
public struct PendingConnection: Sendable {
    public let connId: ConnId
    public let connection: Connection
    public let peer: PeerIdentity
    public let intent: Intent
    public let role: HandshakeRole
}

/// 已建立并认证的加密会话句柄。
public struct ActiveSession: Sendable {
    public let peer: PeerIdentity
    public let connection: Connection
    public let role: HandshakeRole
}

public enum OpenResult: Sendable {
    case ok(ActiveSession)
    case pairing(PendingConnection)
    case failed(OpenFailure)
}

/// 会话与监听管理器（actor）：管理 TCP 监听（42580 -> 42590..42599 -> 0）、双向并发建连消重与信任校验。
public actor SessionManager {
    public static let candidatePorts: [Int] = [ProtocolConstants.defaultPort] + Array(ProtocolConstants.fallbackPortRange) + [0]

    private let transport: any Transport
    private let networkGuard: LocalNetworkGuard
    private let networkMonitor: any NetworkMonitor
    private let trustStore: any TrustStore
    private let clock: any ClockPort
    private let ephemeral: any EphemeralKeySource
    private let timings: SessionTimings
    private let deviceType: DeviceType

    private var localIdentity: DeviceIdentity?
    private var listener: (any TcpListener)?
    private var acceptTask: Task<Void, Never>?
    private var sessions: [DeviceId: ActiveSession] = [:]
    private var nextConnId: Int64 = 1
    private var addressLookup: (@Sendable (DeviceId) async -> (Ipv4, Int)?)?

    private var onSessionOpened: (@Sendable (ActiveSession) async -> Void)?
    private var onSessionClosed: (@Sendable (DeviceId, CloseReason) async -> Void)?
    private var onInboundMessage: (@Sendable (DeviceId, ProtocolMessage) async -> Void)?
    private var onInboundPairing: (@Sendable (PendingConnection) async -> Void)?
    private var onInboundFailure: (@Sendable (InboundFailure) async -> Void)?

    public init(
        transport: any Transport,
        networkGuard: LocalNetworkGuard,
        networkMonitor: any NetworkMonitor,
        trustStore: any TrustStore,
        clock: any ClockPort,
        ephemeral: any EphemeralKeySource = EphemeralKeySources.secure,
        timings: SessionTimings = .default,
        deviceType: DeviceType = .ios
    ) {
        self.transport = transport
        self.networkGuard = networkGuard
        self.networkMonitor = networkMonitor
        self.trustStore = trustStore
        self.clock = clock
        self.ephemeral = ephemeral
        self.timings = timings
        self.deviceType = deviceType
    }

    public func configure(
        identity: DeviceIdentity,
        addressLookup: @escaping @Sendable (DeviceId) async -> (Ipv4, Int)?,
        onSessionOpened: @escaping @Sendable (ActiveSession) async -> Void,
        onSessionClosed: @escaping @Sendable (DeviceId, CloseReason) async -> Void,
        onInboundMessage: @escaping @Sendable (DeviceId, ProtocolMessage) async -> Void,
        onInboundPairing: @escaping @Sendable (PendingConnection) async -> Void,
        onInboundFailure: @escaping @Sendable (InboundFailure) async -> Void
    ) {
        self.localIdentity = identity
        self.addressLookup = addressLookup
        self.onSessionOpened = onSessionOpened
        self.onSessionClosed = onSessionClosed
        self.onInboundMessage = onInboundMessage
        self.onInboundPairing = onInboundPairing
        self.onInboundFailure = onInboundFailure
    }

    public func updateLocalDisplayName(_ name: String) {
        localIdentity?.displayName = name
    }

    public var listeningPort: Int? {
        listener?.port
    }

    public func startListening() async -> Int? {
        if let existing = listener {
            return existing.port
        }
        guard let tcpListener = await transport.listen(candidatePorts: Self.candidatePorts) else {
            return nil
        }
        listener = tcpListener
        acceptTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let conn = await tcpListener.accept() else { break }
                guard let manager = self else { break }
                Task { await manager.handleInbound(conn) }
            }
        }
        return tcpListener.port
    }

    public func stopAll(reason: SessionCloseReason) async {
        acceptTask?.cancel()
        acceptTask = nil
        await listener?.close()
        listener = nil
        let active = Array(sessions.values)
        sessions.removeAll()
        for s in active {
            await s.connection.closeGracefully(reason: reason)
        }
        await networkMonitor.setFastPolling(false)
    }

    public func session(for peer: DeviceId) -> ActiveSession? {
        sessions[peer]
    }

    public func connectedPeerIds() -> Set<DeviceId> {
        Set(sessions.keys)
    }

    public func closeSession(peer: DeviceId, reason: SessionCloseReason) async {
        guard let s = sessions.removeValue(forKey: peer) else { return }
        await s.connection.closeGracefully(reason: reason)
        if sessions.isEmpty {
            await networkMonitor.setFastPolling(false)
        }
    }

    /// 供配对完成后将 PendingConnection 升级为正式 ActiveSession。
    public func promote(_ pending: PendingConnection) async -> ActiveSession {
        await pending.connection.promoteToActive()
        let session = ActiveSession(peer: pending.peer, connection: pending.connection, role: pending.role)
        await registerSession(session, recordAddress: pending.role == .initiator)
        return session
    }

    /// 打开到指定目标的会话或配对连接。
    public func open(target: ConnectTarget, forcePair: Bool = false) async -> OpenResult {
        guard let local = localIdentity else {
            return .failed(.protocolError)
        }

        if case .device(let peerId) = target, !forcePair, let existing = sessions[peerId] {
            return .ok(existing)
        }

        let sockResult: LocalNetworkGuard.GuardConnectResult
        let expectedPeerId: DeviceId?
        let expectedFingerprint: Data?
        let explicitIntent: Intent?

        switch target {
        case .device(let peerId):
            expectedPeerId = peerId
            expectedFingerprint = nil
            explicitIntent = forcePair ? .pair : nil
            if let discovered = await addressLookup?(peerId) {
                sockResult = await networkGuard.connect(ip: discovered.0, port: discovered.1, timeoutMs: timings.connectBudgetMs)
            } else if let trusted = await trustStore.get(peerId), let ip = trusted.lastAddress, let port = trusted.lastPort {
                sockResult = await networkGuard.connect(ip: ip, port: port, timeoutMs: timings.connectBudgetMs)
            } else {
                return .failed(.unreachable)
            }

        case .address(let ip, let port):
            expectedPeerId = nil
            expectedFingerprint = nil
            explicitIntent = forcePair ? .pair : nil
            sockResult = await networkGuard.connect(ip: ip, port: port, timeoutMs: timings.connectBudgetMs)

        case .qr(let payload):
            expectedPeerId = payload.deviceId
            expectedFingerprint = payload.fingerprint
            explicitIntent = .qrPair
            let ips = payload.ipv4List.map { Ipv4(rawValue: $0) }
            sockResult = await networkGuard.connectQr(ips: ips, port: Int(payload.port), perAddressTimeoutMs: timings.qrPerAddressMs)
        }

        let sock: any TcpConn
        switch sockResult {
        case .ok(let c):
            sock = c
        case .failed(let failure):
            return .failed(failure)
        }

        let localHandshake = LocalHandshakeIdentity(
            deviceId: local.deviceId,
            keyPair: local.keyPair,
            displayName: local.displayName,
            deviceType: deviceType
        )

        let outcome = await Handshake.initiate(
            conn: sock,
            local: localHandshake,
            ephemeral: ephemeral
        ) { [trustStore] peer in
            if peer.deviceId == local.deviceId {
                return .stop(.selfConnection)
            }
            if let expectedFp = expectedFingerprint, Fingerprint.of(peer.publicKey) != expectedFp {
                return .stop(.fingerprintMismatch)
            }
            if let expId = expectedPeerId, peer.deviceId != expId {
                return .stop(.unreachable)
            }
            if let forced = explicitIntent {
                return .proceed(forced)
            }
            if let trusted = await trustStore.get(peer.deviceId) {
                if trusted.publicKey != peer.publicKey {
                    return .stop(.keyMismatch(peerName: trusted.displayName))
                }
                return .proceed(.session)
            }
            return .proceed(.pair)
        }

        switch outcome {
        case .failed(let failure):
            await sock.close()
            return .failed(failure)
        case .merged(let peer):
            await sock.close()
            if let existing = sessions[peer.deviceId] {
                return .ok(existing)
            }
            return .failed(.timeout)
        case .done(let peer, let intent, let version, let sendCipher, let recvCipher, let reader):
            let conn = Connection(
                sock: sock,
                reader: reader,
                peer: peer,
                role: .initiator,
                version: version,
                sendCipher: sendCipher,
                receiveCipher: recvCipher,
                clock: clock,
                timings: timings
            )
            await conn.start()

            if intent == .pair || intent == .qrPair {
                let cid = allocateConnId()
                return .pairing(PendingConnection(connId: cid, connection: conn, peer: peer, intent: intent, role: .initiator))
            }

            // 等待对端 SESSION_RESULT
            guard let first = await conn.nextMessage() else {
                await conn.close(reason: .peerClosed(nil))
                return .failed(.unreachable)
            }
            guard case .sessionResult(let code) = first else {
                await conn.close(reason: .protocolError)
                return .failed(.protocolError)
            }
            switch code {
            case .accepted:
                await conn.promoteToActive()
                let session = ActiveSession(peer: peer, connection: conn, role: .initiator)
                await registerSession(session, recordAddress: true)
                return .ok(session)
            case .notTrusted:
                await conn.close(reason: .peerClosed(nil))
                return .failed(.notTrusted(peer: peer.deviceId))
            case .keyMismatch:
                await conn.close(reason: .peerClosed(nil))
                return .failed(.keyMismatch(peerName: peer.displayName))
            case .duplicate:
                await conn.close(reason: .peerClosed(.duplicate))
                // 稍等保留的入站连接注册完成
                for _ in 0..<20 {
                    if let active = sessions[peer.deviceId] {
                        return .ok(active)
                    }
                    try? await clock.sleep(milliseconds: 50)
                }
                return .failed(.timeout)
            }
        }
    }

    // MARK: - Inbound

    private func handleInbound(_ sock: any TcpConn) async {
        guard let local = localIdentity else {
            await sock.close()
            return
        }
        let interfaces = await networkMonitor.currentInterfaces()
        guard AddressRules.acceptsInbound(src: sock.remote.ip, via: nil, interfaces: interfaces) else {
            await sock.close()
            return
        }

        let localHandshake = LocalHandshakeIdentity(
            deviceId: local.deviceId,
            keyPair: local.keyPair,
            displayName: local.displayName,
            deviceType: deviceType
        )
        let outcome = await Handshake.respond(conn: sock, local: localHandshake, ephemeral: ephemeral)
        switch outcome {
        case .failed(let failure):
            await sock.close()
            switch failure {
            case .versionIncompatible(let loc, let rem):
                await onInboundFailure?(.versionIncompatible(local: loc, remote: rem))
            case .handshakeAuthFailed:
                await onInboundFailure?(.handshakeAuthFailed)
            default:
                break
            }
        case .merged:
            await sock.close()
        case .done(let peer, let intent, let version, let sendCipher, let recvCipher, let reader):
            if peer.deviceId == local.deviceId {
                await sock.close()
                return
            }
            let conn = Connection(
                sock: sock,
                reader: reader,
                peer: peer,
                role: .responder,
                version: version,
                sendCipher: sendCipher,
                receiveCipher: recvCipher,
                clock: clock,
                timings: timings
            )
            await conn.start()

            switch intent {
            case .pair, .qrPair:
                let cid = allocateConnId()
                await onInboundPairing?(PendingConnection(connId: cid, connection: conn, peer: peer, intent: intent, role: .responder))

            case .session:
                guard let trusted = await trustStore.get(peer.deviceId) else {
                    await conn.sendAndClose(.sessionResult(result: .notTrusted), reason: .local(.normal))
                    return
                }
                if trusted.publicKey != peer.publicKey {
                    await conn.sendAndClose(.sessionResult(result: .keyMismatch), reason: .local(.normal))
                    await onInboundFailure?(.keyMismatch(peerName: trusted.displayName))
                    return
                }
                if let existing = sessions[peer.deviceId] {
                    // 并发消重：若现有会话由对端发起或对端 DeviceId 更小，替换旧会话；否则回复 DUPLICATE
                    let peerWins = existing.role == .responder || peer.deviceId.bytes.lexicographicallyPrecedes(local.deviceId.bytes)
                    if peerWins {
                        sessions.removeValue(forKey: peer.deviceId)
                        await existing.connection.close(reason: .superseded)
                    } else {
                        await conn.sendAndClose(.sessionResult(result: .duplicate), reason: .local(.duplicate))
                        await existing.connection.send(.heartbeat, priority: .control)
                        return
                    }
                }
                await conn.send(.sessionResult(result: .accepted), priority: .control)
                await conn.promoteToActive()
                let session = ActiveSession(peer: peer, connection: conn, role: .responder)
                await registerSession(session, recordAddress: false)
            }
        }
    }

    private func registerSession(
        _ session: ActiveSession,
        recordAddress: Bool
    ) async {
        let peerId = session.peer.deviceId
        if let old = sessions.removeValue(forKey: peerId) {
            await old.connection.close(reason: .superseded)
        }
        sessions[peerId] = session
        _ = await trustStore.updateName(peerId, name: session.peer.displayName)
        if recordAddress {
            _ = await trustStore.recordAddress(peerId, ip: session.peer.remoteAddr.ip, port: session.peer.remoteAddr.port)
        }
        await networkMonitor.setFastPolling(true)

        await session.connection.onClose { [weak self] reason in
            guard let manager = self else { return }
            Task { await manager.handleSessionClosed(peerId: peerId, conn: session.connection, reason: reason) }
        }

        let handler = self.onInboundMessage
        Task {
            while let msg = await session.connection.nextMessage() {
                await handler?(peerId, msg)
            }
        }

        await onSessionOpened?(session)
    }

    private func handleSessionClosed(peerId: DeviceId, conn: Connection, reason: CloseReason) async {
        if let current = sessions[peerId], current.connection === conn {
            sessions.removeValue(forKey: peerId)
            if sessions.isEmpty {
                await networkMonitor.setFastPolling(false)
            }
            await onSessionClosed?(peerId, reason)
        }
    }

    private func allocateConnId() -> ConnId {
        let id = ConnId(nextConnId)
        nextConnId += 1
        return id
    }
}
