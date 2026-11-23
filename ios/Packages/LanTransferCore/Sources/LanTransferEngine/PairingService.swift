import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 配对协调服务（actor）：驱动 `PairingReducer` 与 `PairingTokenRegistry`，处理点选 SAS 配对、二维码配对与解除信任。
public actor PairingService {
    private let sessionManager: SessionManager
    private let trustStore: any TrustStore
    private let notifier: any Notifier
    private let lifecycle: any AppLifecycle
    private let clock: any ClockPort
    private let rng: any RngPort
    private let tokenRegistry: PairingTokenRegistry

    private var localIdentity: DeviceIdentity?
    private var state = PairingState()
    private var connections: [ConnId: PendingConnection] = [:]
    private var readerTasks: [ConnId: Task<Void, Never>] = [:]
    private var stagedTrust: [DeviceId: PeerInfo] = [:]
    private var connectCancel: (@Sendable () -> Void)?
    private var timerTask: Task<Void, Never>?
    private var lastOpenFailure: OpenFailure?
    private var currentQrToken: PairingToken?

    private var onStateChange: (@Sendable (PairingUiState) async -> Void)?
    private var onNotice: (@Sendable (EngineNotice) async -> Void)?
    private var onTrustChanged: (@Sendable () async -> Void)?
    private var onUnpairedPeer: (@Sendable (DeviceId) async -> Void)?

    public init(
        sessionManager: SessionManager,
        trustStore: any TrustStore,
        notifier: any Notifier,
        lifecycle: any AppLifecycle,
        clock: any ClockPort,
        rng: any RngPort
    ) {
        self.sessionManager = sessionManager
        self.trustStore = trustStore
        self.notifier = notifier
        self.lifecycle = lifecycle
        self.clock = clock
        self.rng = rng
        self.tokenRegistry = PairingTokenRegistry(rng: rng)
    }

    public func configure(
        identity: DeviceIdentity,
        onStateChange: @escaping @Sendable (PairingUiState) async -> Void,
        onNotice: @escaping @Sendable (EngineNotice) async -> Void,
        onTrustChanged: @escaping @Sendable () async -> Void,
        onUnpairedPeer: @escaping @Sendable (DeviceId) async -> Void
    ) {
        self.localIdentity = identity
        self.onStateChange = onStateChange
        self.onNotice = onNotice
        self.onTrustChanged = onTrustChanged
        self.onUnpairedPeer = onUnpairedPeer
    }

    public func updateLocalDisplayName(_ name: String) {
        localIdentity?.displayName = name
    }

    public func uiState() -> PairingUiState {
        deriveUiState()
    }

    /// 签发或复用当前有效二维码载荷文本（`LT1:...`）；无有效局域网接口或未监听时返回 nil。
    public func currentQrPayloadText(interfaces: [LocalInterface], listenPort: Int?, forceRefresh: Bool = false) -> String? {
        guard let local = localIdentity, let port = listenPort, let u16Port = UInt16(exactly: port), port != 0 else {
            return nil
        }
        let ips = AddressRules.qrAddressList(sortedInterfaces: interfaces)
        guard !ips.isEmpty else { return nil }
        let now = clock.monotonicMs()
        if forceRefresh || currentQrToken == nil || tokenRegistry.needsRefresh(nowMono: now) {
            currentQrToken = tokenRegistry.issue(nowMono: now)
        }
        guard let token = currentQrToken else { return nil }
        let payload = QrPayload(
            deviceId: local.deviceId,
            displayName: local.displayName,
            ipv4List: ips.map(\.rawValue),
            port: u16Port,
            fingerprint: Fingerprint.of(local.keyPair.publicKey),
            token: token.value
        )
        return QrCodec.encode(payload)
    }

    /// 发起点选 / 手动 IP 连接或配对。若对方已信任且未强制重新配对，直接打开加密会话。
    public func pair(target: ConnectTarget, forcePair: Bool = false) async -> ActiveSession? {
        guard state.session == nil else { return nil }
        let placeholderPeer: DeviceId
        switch target {
        case .device(let id): placeholderPeer = id
        case .qr(let p): placeholderPeer = p.deviceId
        case .address: placeholderPeer = DeviceId(bytes: Data(repeating: 0, count: 16))!
        }

        lastOpenFailure = nil
        await apply(.startPair(peer: placeholderPeer, now: clock.monotonicMs()))

        let openTask = Task { await self.sessionManager.open(target: target, forcePair: forcePair) }
        connectCancel = { openTask.cancel() }
        let result = await openTask.value
        connectCancel = nil

        switch result {
        case .ok(let session):
            // 对方已信任，取消配对态直接返回会话
            state.session = nil
            timerTask?.cancel()
            await onStateChange?(.idle)
            return session
        case .failed(let failure):
            lastOpenFailure = failure
            await apply(.connectFailed(now: clock.monotonicMs()))
            return nil
        case .pairing(let pending):
            await attachOutbound(pending)
            return nil
        }
    }

    /// 扫描二维码文本发起免核对快速配对。
    public func pairByQr(text: String) async -> QrError? {
        switch QrCodec.decode(text) {
        case .fail(let err):
            return err
        case .ok(let payload):
            guard state.session == nil else { return nil }
            lastOpenFailure = nil
            await apply(.startQrPair(peer: payload.deviceId, token: payload.token, now: clock.monotonicMs()))

            let openTask = Task { await self.sessionManager.open(target: .qr(payload), forcePair: true) }
            connectCancel = { openTask.cancel() }
            let result = await openTask.value
            connectCancel = nil

            switch result {
            case .ok:
                state.session = nil
                timerTask?.cancel()
                await onStateChange?(.idle)
            case .failed(let failure):
                lastOpenFailure = failure
                await apply(.connectFailed(now: clock.monotonicMs()))
            case .pairing(let pending):
                await attachOutbound(pending)
            }
            return nil
        }
    }

    public func respondToRequest(accept: Bool) async {
        await apply(.userDecision(accept: accept, now: clock.monotonicMs()))
    }

    public func cancel() async {
        await apply(.userCancel(now: clock.monotonicMs()))
    }

    public func unpair(peer: DeviceId) async -> StoreResult {
        let res = await trustStore.delete(peer)
        await sessionManager.closeSession(peer: peer, reason: .unpaired)
        await onUnpairedPeer?(peer)
        await onTrustChanged?()
        return res
    }

    /// 由 SessionManager 转交的入站配对连接。
    public func handleInboundPairing(_ pending: PendingConnection) async {
        connections[pending.connId] = pending
        let reader = Task { [weak self] in
            guard let first = await pending.connection.nextMessage() else {
                await self?.handleDisconnected(pending.connId)
                return
            }
            await self?.handleFirstInboundMessage(pending: pending, message: first)
            while !Task.isCancelled, let msg = await pending.connection.nextMessage() {
                if case .pairComplete = msg {
                    await self?.handleMessage(connId: pending.connId, msg: msg)
                    // PAIR_COMPLETE 后停止由 PairingService 读取，后续消息归 SessionManager
                    return
                }
                await self?.handleMessage(connId: pending.connId, msg: msg)
            }
            if !Task.isCancelled {
                await self?.handleDisconnected(pending.connId)
            }
        }
        readerTasks[pending.connId] = reader
    }

    // MARK: - Internal Reducer Execution

    private func attachOutbound(_ pending: PendingConnection) async {
        guard let local = localIdentity else {
            await pending.connection.close(reason: .local(.normal))
            return
        }
        connections[pending.connId] = pending
        let nI = rng.nextBytes(count: 32)
        await apply(.connected(
            conn: pending.connId,
            info: pending.peer.toPeerInfo(),
            localPublicKey: local.keyPair.publicKey,
            nI: nI,
            now: clock.monotonicMs()
        ))

        let reader = Task { [weak self] in
            while !Task.isCancelled, let msg = await pending.connection.nextMessage() {
                await self?.handleMessage(connId: pending.connId, msg: msg)
                if let isSaving = await self?.isSaving(connId: pending.connId), isSaving {
                    return
                }
            }
            if !Task.isCancelled {
                await self?.handleDisconnected(pending.connId)
            }
        }
        readerTasks[pending.connId] = reader
    }

    private func isSaving(connId: ConnId) -> Bool {
        if case .saving(_, let c, _, _, _) = state.session, c == connId {
            return true
        }
        return false
    }

    private func handleFirstInboundMessage(pending: PendingConnection, message: ProtocolMessage) async {
        guard let local = localIdentity else {
            await pending.connection.close(reason: .local(.normal))
            return
        }
        let now = clock.monotonicMs()
        switch (pending.intent, message) {
        case (.pair, .pairRequest(let commitment)):
            let nR = rng.nextBytes(count: 32)
            await apply(.pairRequestReceived(
                conn: pending.connId,
                info: pending.peer.toPeerInfo(),
                localPublicKey: local.keyPair.publicKey,
                commitment: commitment,
                nR: nR,
                now: now
            ))
        case (.qrPair, .qrPairRequest(let token)):
            let lease: TokenLease?
            switch tokenRegistry.check(token: token, nowMono: now) {
            case .ok(let l): lease = l
            case .invalid: lease = nil
            }
            await apply(.qrRequestChecked(conn: pending.connId, info: pending.peer.toPeerInfo(), lease: lease, now: now))
        default:
            await pending.connection.close(reason: .protocolError)
            connections.removeValue(forKey: pending.connId)
        }
    }

    private func handleMessage(connId: ConnId, msg: ProtocolMessage) async {
        await apply(.messageReceived(conn: connId, msg: msg, now: clock.monotonicMs()))
    }

    private func handleDisconnected(_ connId: ConnId) async {
        readerTasks.removeValue(forKey: connId)
        connections.removeValue(forKey: connId)
        await apply(.disconnected(conn: connId, now: clock.monotonicMs()))
    }

    private func apply(_ event: PairingEvent) async {
        let step = PairingReducer.reduce(state, event)
        state = step.state
        await onStateChange?(deriveUiState())
        for fx in step.effects {
            await execute(fx)
        }
    }

    private func execute(_ effect: PairingEffect) async {
        switch effect {
        case .send(let connId, let msg):
            if let pending = connections[connId] {
                await pending.connection.send(msg, priority: .control)
            }
        case .close(let connId):
            readerTasks.removeValue(forKey: connId)?.cancel()
            if let pending = connections.removeValue(forKey: connId) {
                await pending.connection.close(reason: .local(.normal))
            }
        case .cancelConnect:
            connectCancel?()
            connectCancel = nil
        case .armTimer(let atMono):
            timerTask?.cancel()
            let delay = max(atMono - clock.monotonicMs(), 0)
            timerTask = Task { [weak self] in
                do {
                    try await self?.clock.sleep(milliseconds: delay)
                    await self?.apply(.tick(now: self?.clock.monotonicMs() ?? atMono))
                } catch {}
            }
        case .showRequest(let info):
            if await lifecycle.isInBackground() {
                await notifier.notifyRequest(key: .pairingRequest(peer: info.deviceId), peerName: info.displayName, kind: .pairing, fileCount: nil)
            }
        case .showSas:
            break
        case .dismissRequest:
            if let peer = state.last?.peer {
                await notifier.cancel(key: .pairingRequest(peer: peer))
            }
        case .saveTrust(let info):
            let ok = await persistTrust(info)
            await apply(.trustStored(peer: info.deviceId, ok: ok))
        case .stageTrust(let info):
            stagedTrust[info.deviceId] = info
        case .commitTrust(let peer):
            if let info = stagedTrust.removeValue(forKey: peer) {
                let ok = await persistTrust(info)
                await apply(.trustStored(peer: peer, ok: ok))
            } else {
                await apply(.trustStored(peer: peer, ok: false))
            }
        case .discardStagedTrust(let peer):
            stagedTrust.removeValue(forKey: peer)
        case .deleteTrust(let peer):
            _ = await trustStore.delete(peer)
            await onTrustChanged?()
        case .markTokenUsed(let lease):
            tokenRegistry.markUsed(lease)
            currentQrToken = nil
        case .releaseToken(let lease):
            tokenRegistry.release(lease)
        case .promoteSession(let connId):
            readerTasks.removeValue(forKey: connId)?.cancel()
            if let pending = connections.removeValue(forKey: connId) {
                _ = await sessionManager.promote(pending)
            }
        case .autoRejected(let peer, let result):
            await onNotice?(.pairingAutoRejected(peer, result))
        case .finished(let done):
            timerTask?.cancel()
            let failure = (done.result == .connectFailed) ? lastOpenFailure : nil
            lastOpenFailure = nil
            await notifier.cancel(key: .pairingRequest(peer: done.peer))
            await onNotice?(.pairingFinished(done, failure))
        }
    }

    private func persistTrust(_ info: PeerInfo) async -> Bool {
        let existingAutoAccept = await trustStore.get(info.deviceId)?.autoAccept ?? false
        let device = TrustedDevice(
            deviceId: info.deviceId,
            displayName: info.displayName,
            deviceType: info.deviceType,
            publicKey: info.publicKey,
            autoAccept: existingAutoAccept,
            pairedAtUtcMs: clock.wallMs(),
            lastAddress: info.ip,
            lastPort: info.port
        )
        if case .ok = await trustStore.upsert(device) {
            await onTrustChanged?()
            return true
        }
        return false
    }

    private func deriveUiState() -> PairingUiState {
        guard let session = state.session else { return .idle }
        switch session {
        case .connecting(let peer, _, _):
            return .connecting(peer: peer)
        case .awaitingPeer(_, _, let info, _, _, _), .awaitingQrResult(_, _, let info, _):
            return .waitingForPeer(peer: info)
        case .showingSas(_, _, let info, let sas, _):
            return .showingCode(peer: info, sas: sas)
        case .awaitingUser(_, _, let info, _, _, _, let sas, _):
            return .incomingRequest(peer: info, sas: sas)
        case .awaitingComplete(_, _, let info, _, _, _), .saving(_, _, let info, _, _):
            return .completing(peer: info)
        }
    }
}
