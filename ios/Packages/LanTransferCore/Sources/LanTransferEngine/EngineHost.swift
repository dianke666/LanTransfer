import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 引擎对外聚合快照，驱动 SwiftUI 主界面状态更新。
public struct EngineSnapshot: Sendable {
    public let identityLoad: IdentityLoad?
    public let localInterfaces: [LocalInterface]
    public let listenPort: Int?
    public let listenFailed: Bool
    public let discoveryStatus: DiscoveryStatus
    public let discoveredDevices: [DeviceEntry]
    public let trustedDevices: [TrustedDevice]
    public let connectedPeers: Set<DeviceId>
    public let pairingState: PairingUiState
    public let pendingOffers: [PendingIncomingOffer]
    public let tasks: [TransferTask]
    public let progress: [TransferId: TransferProgress]
    public let history: [HistoryRecord]
    public let settings: Settings
    public let notificationsEnabled: Bool

    public init(
        identityLoad: IdentityLoad?,
        localInterfaces: [LocalInterface],
        listenPort: Int?,
        listenFailed: Bool,
        discoveryStatus: DiscoveryStatus,
        discoveredDevices: [DeviceEntry],
        trustedDevices: [TrustedDevice],
        connectedPeers: Set<DeviceId>,
        pairingState: PairingUiState,
        pendingOffers: [PendingIncomingOffer],
        tasks: [TransferTask],
        progress: [TransferId: TransferProgress],
        history: [HistoryRecord],
        settings: Settings,
        notificationsEnabled: Bool
    ) {
        self.identityLoad = identityLoad
        self.localInterfaces = localInterfaces
        self.listenPort = listenPort
        self.listenFailed = listenFailed
        self.discoveryStatus = discoveryStatus
        self.discoveredDevices = discoveredDevices
        self.trustedDevices = trustedDevices
        self.connectedPeers = connectedPeers
        self.pairingState = pairingState
        self.pendingOffers = pendingOffers
        self.tasks = tasks
        self.progress = progress
        self.history = history
        self.settings = settings
        self.notificationsEnabled = notificationsEnabled
    }
}

/// 引擎统一门面（actor）：协调身份加载、网络监控、发现、配对、无损文字与文件传输五大服务。
public actor EngineHost {
    public let sessionManager: SessionManager
    public let discoveryService: DiscoveryService
    public let pairingService: PairingService
    public let textService: TextService
    public let fileTransferEngine: FileTransferEngine

    private let networkMonitor: any NetworkMonitor
    private let identityStore: any IdentityStore
    private let trustStore: any TrustStore
    private let historyStore: any HistoryStore
    private let settingsStore: any SettingsStore
    private let receiveStorage: any ReceiveStorage
    private let notifier: any Notifier

    private var identityLoad: IdentityLoad?
    private var listenFailed: Bool = false
    private var networkingRunning: Bool = false
    private var monitorTask: Task<Void, Never>?
    private var snapshotObservers: [UUID: AsyncStream<EngineSnapshot>.Continuation] = [:]
    private var noticeObservers: [UUID: AsyncStream<EngineNotice>.Continuation] = [:]

    public init(
        networkMonitor: any NetworkMonitor,
        transport: any Transport,
        receiveStorage: any ReceiveStorage,
        sourceFiles: any SourceFiles,
        identityStore: any IdentityStore,
        trustStore: any TrustStore,
        historyStore: any HistoryStore,
        taskStore: any TaskStore,
        settingsStore: any SettingsStore,
        clipboard: any ClipboardPort,
        notifier: any Notifier,
        lifecycle: any AppLifecycle,
        clock: any ClockPort = SystemClock(),
        rng: any RngPort = SystemRng(),
        ephemeral: any EphemeralKeySource = EphemeralKeySources.secure,
        deviceType: DeviceType = .ios
    ) {
        self.networkMonitor = networkMonitor
        self.identityStore = identityStore
        self.trustStore = trustStore
        self.historyStore = historyStore
        self.settingsStore = settingsStore
        self.receiveStorage = receiveStorage
        self.notifier = notifier

        let guardActor = LocalNetworkGuard(transport: transport, networkMonitor: networkMonitor)
        self.sessionManager = SessionManager(
            transport: transport,
            networkGuard: guardActor,
            networkMonitor: networkMonitor,
            trustStore: trustStore,
            clock: clock,
            ephemeral: ephemeral,
            deviceType: deviceType
        )
        self.discoveryService = DiscoveryService(
            transport: transport,
            networkMonitor: networkMonitor,
            clock: clock,
            deviceType: deviceType
        )
        self.pairingService = PairingService(
            sessionManager: sessionManager,
            trustStore: trustStore,
            notifier: notifier,
            lifecycle: lifecycle,
            clock: clock,
            rng: rng
        )
        self.textService = TextService(
            sessionManager: sessionManager,
            historyStore: historyStore,
            trustStore: trustStore,
            settingsStore: settingsStore,
            clipboard: clipboard,
            notifier: notifier,
            lifecycle: lifecycle,
            clock: clock,
            rng: rng
        )
        self.fileTransferEngine = FileTransferEngine(
            sessionManager: sessionManager,
            receiveStorage: receiveStorage,
            sourceFiles: sourceFiles,
            taskStore: taskStore,
            trustStore: trustStore,
            settingsStore: settingsStore,
            notifier: notifier,
            lifecycle: lifecycle,
            clock: clock,
            rng: rng
        )
    }

    public func observeSnapshots() -> AsyncStream<EngineSnapshot> {
        let id = UUID()
        let (stream, continuation) = AsyncStream<EngineSnapshot>.makeStream()
        snapshotObservers[id] = continuation
        continuation.onTermination = { [weak self] _ in
            guard let host = self else { return }
            Task { await host.removeSnapshotObserver(id) }
        }
        Task {
            let snap = await self.snapshot()
            continuation.yield(snap)
        }
        return stream
    }

    public func observeNotices() -> AsyncStream<EngineNotice> {
        let id = UUID()
        let (stream, continuation) = AsyncStream<EngineNotice>.makeStream()
        noticeObservers[id] = continuation
        continuation.onTermination = { [weak self] _ in
            guard let host = self else { return }
            Task { await host.removeNoticeObserver(id) }
        }
        return stream
    }

    /// 启动引擎：加载本机身份、恢复历史与未完成任务、并启动局域网监听与自动发现。
    public func start() async {
        let loaded = await identityStore.loadOrCreate()
        self.identityLoad = loaded
        guard let identity = loaded.readyIdentity else {
            await publishSnapshot()
            return
        }

        let trusted = await trustStore.all()
        await discoveryService.configure(
            selfId: identity.deviceId,
            displayName: identity.displayName,
            trustedIds: Set(trusted.map(\.deviceId))
        ) { [weak self] _, _ in
            await self?.publishSnapshot()
        }

        await sessionManager.configure(
            identity: identity,
            addressLookup: { [discoveryService] peerId in
                await discoveryService.addressOf(peerId)
            },
            onSessionOpened: { [weak self] session in
                await self?.fileTransferEngine.handleSessionOpened(session)
                await self?.publishSnapshot()
            },
            onSessionClosed: { [weak self] peerId, reason in
                await self?.fileTransferEngine.handleSessionClosed(peer: peerId, reason: reason)
                await self?.publishSnapshot()
            },
            onInboundMessage: { [weak self] peerId, msg in
                await self?.routeInbound(peer: peerId, message: msg)
            },
            onInboundPairing: { [weak self] pending in
                await self?.pairingService.handleInboundPairing(pending)
            },
            onInboundFailure: { [weak self] failure in
                await self?.publishNotice(.inboundFailure(failure))
            }
        )

        await pairingService.configure(
            identity: identity,
            onStateChange: { [weak self] _ in
                await self?.publishSnapshot()
            },
            onNotice: { [weak self] notice in
                await self?.publishNotice(notice)
            },
            onTrustChanged: { [weak self] in
                await self?.syncTrustedToDiscovery()
                await self?.publishSnapshot()
            },
            onUnpairedPeer: { [weak self] peer in
                await self?.fileTransferEngine.cancelAllForPeer(peer, reason: .unpaired)
            }
        )

        await textService.configure(
            onHistoryChanged: { [weak self] in
                await self?.publishSnapshot()
            },
            onNotice: { [weak self] notice in
                await self?.publishNotice(notice)
            }
        )

        await fileTransferEngine.configure(
            onTasksChanged: { [weak self] in
                await self?.publishSnapshot()
            },
            onNotice: { [weak self] notice in
                await self?.publishNotice(notice)
            }
        )

        await textService.restoreFromHistory()
        await fileTransferEngine.restoreAndCleanup()
        await startNetworking()

        monitorTask?.cancel()
        monitorTask = Task { [weak self] in
            guard let self else { return }
            for await interfaces in self.networkMonitor.interfacesStream {
                await self.handleInterfacesChanged(interfaces)
            }
        }
    }

    public func startNetworking() async {
        guard identityLoad?.readyIdentity != nil else { return }
        if networkingRunning {
            await publishSnapshot()
            return
        }
        if let port = await sessionManager.startListening() {
            listenFailed = false
            networkingRunning = true
            await discoveryService.start(listenPort: port)
        } else {
            listenFailed = true
            networkingRunning = false
        }
        await publishSnapshot()
    }

    public func stopNetworking(reason: SessionCloseReason) async {
        networkingRunning = false
        await discoveryService.stop()
        await sessionManager.stopAll(reason: reason)
        await publishSnapshot()
    }

    public func updateDisplayName(_ input: String) async -> NameCheck {
        let check = DisplayNameRules.validateUserInput(input)
        guard case .ok(let cleaned, _) = check else {
            return check
        }
        let res = await identityStore.setDisplayName(cleaned)
        if case .failed(let detail) = res {
            await publishNotice(.storeFailed(detail: detail))
            return check
        }
        await sessionManager.updateLocalDisplayName(cleaned)
        await discoveryService.updateDisplayName(cleaned)
        await pairingService.updateLocalDisplayName(cleaned)
        if let cur = identityLoad?.readyIdentity {
            var updated = cur
            updated.displayName = cleaned
            identityLoad = .loaded(updated)
        }
        await publishSnapshot()
        return check
    }

    public func setAutoAccept(peer: DeviceId, enabled: Bool) async {
        if case .failed(let detail) = await trustStore.setAutoAccept(peer, enabled: enabled) {
            await publishNotice(.storeFailed(detail: detail))
        }
        await publishSnapshot()
    }

    public func updateSettings(_ transform: @escaping @Sendable (Settings) -> Settings) async {
        if case .failed(let detail) = await settingsStore.update(transform) {
            await publishNotice(.storeFailed(detail: detail))
        }
        await publishSnapshot()
    }

    public func deleteTextRecord(_ record: TextRecord) async {
        await textService.forget(keys: [TextKey(peer: record.peer, messageId: record.messageId)])
        if case .failed(let detail) = await historyStore.delete(RecordId(record.seq)) {
            await publishNotice(.storeFailed(detail: detail))
        }
        await publishSnapshot()
    }

    public func deleteFileRecord(_ record: FileRecord, deleteLocalFile: Bool) async -> Bool {
        if deleteLocalFile, let storedRef = record.file.storedRef {
            let ok = await receiveStorage.deleteStoredFile(storedRef)
            if !ok {
                return false
            }
        }
        if !record.taskState.isTerminal {
            await fileTransferEngine.cancelTask(transferId: record.file.transferId)
        }
        if case .failed(let detail) = await historyStore.deleteFileRecord(transferId: record.file.transferId, index: record.file.index) {
            await publishNotice(.storeFailed(detail: detail))
        }
        await publishSnapshot()
        return true
    }

    public func clearAllHistory() async {
        let unfinished = await fileTransferEngine.allTasks().filter { !$0.state.isTerminal }
        for t in unfinished {
            await fileTransferEngine.cancelTask(transferId: t.transferId)
        }
        if case .failed(let detail) = await historyStore.clearAll() {
            await publishNotice(.storeFailed(detail: detail))
        }
        await publishSnapshot()
    }

    public func currentQrText(forceRefresh: Bool = false) async -> String? {
        let interfaces = await networkMonitor.currentInterfaces()
        let port = await sessionManager.listeningPort
        return await pairingService.currentQrPayloadText(interfaces: interfaces, listenPort: port, forceRefresh: forceRefresh)
    }

    public func snapshot() async -> EngineSnapshot {
        let interfaces = await networkMonitor.currentInterfaces()
        let listenPort = await sessionManager.listeningPort
        let discoveryStatus = await discoveryService.currentStatus()
        let discovered = await discoveryService.currentDevices()
        let trusted = await trustStore.all()
        let connected = await sessionManager.connectedPeerIds()
        let pairingState = await pairingService.uiState()
        let offers = await fileTransferEngine.currentIncomingOffers()
        let tasks = await fileTransferEngine.allTasks()
        let progress = await fileTransferEngine.currentProgress()
        let history: [HistoryRecord]
        switch await historyStore.list(limit: 10_000) {
        case .success(let items): history = items
        case .failure: history = []
        }
        let settings = await settingsStore.current()
        let notifEnabled = await notifier.isEnabled()

        return EngineSnapshot(
            identityLoad: identityLoad,
            localInterfaces: interfaces,
            listenPort: listenPort,
            listenFailed: listenFailed,
            discoveryStatus: discoveryStatus,
            discoveredDevices: discovered,
            trustedDevices: trusted,
            connectedPeers: connected,
            pairingState: pairingState,
            pendingOffers: offers,
            tasks: tasks,
            progress: progress,
            history: history,
            settings: settings,
            notificationsEnabled: notifEnabled
        )
    }

    // MARK: - Internal Routing

    private func routeInbound(peer: DeviceId, message: ProtocolMessage) async {
        switch message.type {
        case .text, .textAck, .textCorrupt:
            await textService.handleInbound(peer: peer, message: message)
        case .fileOffer, .fileOfferReply, .fileChunk, .fileEnd, .fileAck, .fileResult, .fileReadFailed,
             .transferCancel, .resumeRequest, .resumeState, .resumePlan:
            await fileTransferEngine.handleInbound(peer: peer, message: message)
        default:
            break
        }
    }

    private func handleInterfacesChanged(_ interfaces: [LocalInterface]) async {
        await discoveryService.updateInterfaces(interfaces)
        if networkingRunning, let port = await sessionManager.listeningPort {
            if !interfaces.isEmpty && (await discoveryService.currentStatus()) == .stopped {
                await discoveryService.start(listenPort: port)
            } else if interfaces.isEmpty {
                await discoveryService.stop()
            }
        }
        await publishSnapshot()
    }

    private func syncTrustedToDiscovery() async {
        let trusted = await trustStore.all()
        await discoveryService.updateTrusted(Set(trusted.map(\.deviceId)))
    }

    private func publishSnapshot() async {
        let snap = await snapshot()
        for cont in snapshotObservers.values {
            cont.yield(snap)
        }
    }

    private func publishNotice(_ notice: EngineNotice) async {
        for cont in noticeObservers.values {
            cont.yield(notice)
        }
    }

    private func removeSnapshotObserver(_ id: UUID) {
        snapshotObservers.removeValue(forKey: id)
    }

    private func removeNoticeObserver(_ id: UUID) {
        noticeObservers.removeValue(forKey: id)
    }
}
