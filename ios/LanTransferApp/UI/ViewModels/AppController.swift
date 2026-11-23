import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import Observation
import SwiftUI
import UIKit

public enum AppTab: Hashable, Sendable {
    case devices
    case history
    case settings
}

public struct PendingLocalFile: Identifiable, Hashable, Sendable {
    public let id = UUID()
    public let ref: SourceRef
    public let name: String
    public let size: Int64

    public init(ref: SourceRef, name: String, size: Int64) {
        self.ref = ref
        self.name = name
        self.size = size
    }
}

public struct AlertPayload: Identifiable, Sendable {
    public let id = UUID()
    public let title: String
    public let message: String
}

public struct DeviceListItem: Identifiable, Hashable, Sendable {
    public var id: DeviceId { deviceId }
    public let deviceId: DeviceId
    public let displayName: String
    public let deviceType: DeviceType
    public let ipText: String?
    public let port: Int?
    public let isOnline: Bool
    public let isConnected: Bool
    public let isTrusted: Bool
}

/// 全局 SwiftUI 状态控制器（iOS 17 `@Observable` + `@MainActor`），桥接后台 `EngineHost` 与 9 大界面。
@Observable
@MainActor
public final class AppController {
    public var snapshot: EngineSnapshot
    public var selectedTab: AppTab = .devices
    public var navigationPath: [DeviceId] = []
    public var showQrSheet: Bool = false
    public var showQrScanner: Bool = false
    public var showManualConnect: Bool = false
    public var quickLookURL: URL?
    public var toastMessage: String?
    public var alertPayload: AlertPayload?
    public var pendingFilesByPeer: [DeviceId: [PendingLocalFile]] = [:]
    public var draftTextByPeer: [DeviceId: String] = [:]
    public var hapticTrigger: Int = 0
    public var lastHapticKind: HapticKind = .light

    public enum HapticKind: Sendable {
        case light, success, warning, error
    }

    @ObservationIgnored public let engine: EngineHost
    @ObservationIgnored private let networkMonitor: IosNetworkMonitor
    @ObservationIgnored private let database: SqliteDatabase
    @ObservationIgnored private let sourceFiles: IosSourceFiles
    @ObservationIgnored private let clipboard: IosClipboard
    @ObservationIgnored private let notifier: IosNotifier
    @ObservationIgnored private let lifecycle: IosAppLifecycle
    @ObservationIgnored private var fileLocator: IosFileLocator?

    @ObservationIgnored private var started = false
    @ObservationIgnored private var toastTask: Task<Void, Never>?

    public init() {
        let netMon = IosNetworkMonitor()
        let transport = IosTransport()
        let db = SqliteDatabase()
        let recvStorage = IosReceiveStorage()
        let srcFiles = IosSourceFiles()
        let idStore = KeychainIdentityStore()
        let settingsStore = UserDefaultsSettingsStore()
        let clip = IosClipboard()
        let notif = IosNotifier()
        let life = IosAppLifecycle()

        self.networkMonitor = netMon
        self.database = db
        self.sourceFiles = srcFiles
        self.clipboard = clip
        self.notifier = notif
        self.lifecycle = life
        self.engine = EngineHost(
            networkMonitor: netMon,
            transport: transport,
            receiveStorage: recvStorage,
            sourceFiles: srcFiles,
            identityStore: idStore,
            trustStore: db,
            historyStore: db,
            taskStore: db,
            settingsStore: settingsStore,
            clipboard: clip,
            notifier: notif,
            lifecycle: life,
            deviceType: .ios
        )
        self.snapshot = EngineSnapshot(
            identityLoad: nil,
            localInterfaces: [],
            listenPort: nil,
            listenFailed: false,
            discoveryStatus: .stopped,
            discoveredDevices: [],
            trustedDevices: [],
            connectedPeers: [],
            pairingState: .idle,
            pendingOffers: [],
            tasks: [],
            progress: [:],
            history: [],
            settings: Settings(),
            notificationsEnabled: true
        )
        self.fileLocator = IosFileLocator { [weak self] url in
            self?.quickLookURL = url
        }
    }

    public func startIfNeeded() {
        guard !started else { return }
        started = true
        Task {
            await notifier.requestAuthorizationIfNeeded()
            let engine = self.engine
            let notifier = self.notifier
            await lifecycle.setExpirationHandler {
                await engine.stopNetworking(reason: .background)
                await notifier.notifyBackgroundPaused()
            }
            await networkMonitor.start()
            await engine.start()

            Task { [weak self] in
                guard let self else { return }
                for await snap in await self.engine.observeSnapshots() {
                    self.snapshot = snap
                }
            }
            Task { [weak self] in
                guard let self else { return }
                for await notice in await self.engine.observeNotices() {
                    self.handleNotice(notice)
                }
            }
        }
    }

    public func handleScenePhase(_ phase: ScenePhase) {
        switch phase {
        case .active:
            Task {
                await lifecycle.willEnterForeground()
                await engine.startNetworking()
            }
        case .background:
            Task {
                await lifecycle.didEnterBackground()
            }
        default:
            break
        }
    }

    /// 处理 Share Extension 唤起：`lantransfer://share?batch=<uuid>&peer=<hex>`
    public func handleIncomingURL(_ url: URL) {
        guard url.scheme == AppGroupConfig.urlScheme,
              url.host == "share",
              let comps = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let batchId = comps.queryItems?.first(where: { $0.name == "batch" })?.value else {
            return
        }
        let batchDir = AppGroupConfig.shareInboxDirectoryURL().appendingPathComponent(batchId, isDirectory: true)
        let manifestURL = batchDir.appendingPathComponent("manifest.json")
        guard let data = try? Data(contentsOf: manifestURL),
              let manifest = try? JSONDecoder().decode(ShareHandoverManifest.self, from: data),
              let peerBytes = decodeHex(manifest.peerHex),
              let peerId = DeviceId(bytes: peerBytes) else {
            return
        }

        selectedTab = .devices
        navigationPath = [peerId]
        if let text = manifest.text, !text.isEmpty {
            draftTextByPeer[peerId] = text
        }
        if !manifest.filePaths.isEmpty {
            let urls = manifest.filePaths.map { URL(fileURLWithPath: $0) }
            addPickedFiles(peer: peerId, urls: urls)
        }
    }

    // MARK: - Derived Properties

    public var localDisplayName: String {
        snapshot.identityLoad?.readyIdentity?.displayName ?? DisplayNameRules.defaultIosName
    }

    public var localDeviceIdSummary: String {
        guard let hex = snapshot.identityLoad?.readyIdentity?.deviceId.hexString.uppercased(), hex.count >= 12 else {
            return "—"
        }
        let a = hex.prefix(4)
        let b = hex.dropFirst(4).prefix(4)
        let c = hex.dropFirst(8).prefix(4)
        return "\(a)-\(b)-\(c)"
    }

    public var unfinishedTaskCount: Int {
        snapshot.tasks.filter { !$0.state.isTerminal }.count
    }

    public var mergedDevices: [DeviceListItem] {
        var result: [DeviceListItem] = []
        var seen = Set<DeviceId>()

        for entry in snapshot.discoveredDevices {
            seen.insert(entry.deviceId)
            result.append(DeviceListItem(
                deviceId: entry.deviceId,
                displayName: entry.announcement.displayName,
                deviceType: entry.announcement.deviceType,
                ipText: entry.ip.description,
                port: Int(entry.announcement.port),
                isOnline: true,
                isConnected: snapshot.connectedPeers.contains(entry.deviceId),
                isTrusted: entry.trusted
            ))
        }

        for trusted in snapshot.trustedDevices where !seen.contains(trusted.deviceId) {
            result.append(DeviceListItem(
                deviceId: trusted.deviceId,
                displayName: trusted.displayName,
                deviceType: trusted.deviceType,
                ipText: trusted.lastAddress?.description,
                port: trusted.lastPort,
                isOnline: snapshot.connectedPeers.contains(trusted.deviceId),
                isConnected: snapshot.connectedPeers.contains(trusted.deviceId),
                isTrusted: true
            ))
        }
        return result
    }

    public func deviceInfo(for peerId: DeviceId) -> DeviceListItem {
        if let found = mergedDevices.first(where: { $0.deviceId == peerId }) {
            return found
        }
        return DeviceListItem(
            deviceId: peerId,
            displayName: peerId.hexString.prefix(8).uppercased(),
            deviceType: .windows,
            ipText: nil,
            port: nil,
            isOnline: snapshot.connectedPeers.contains(peerId),
            isConnected: snapshot.connectedPeers.contains(peerId),
            isTrusted: snapshot.trustedDevices.contains(where: { $0.deviceId == peerId })
        )
    }

    // MARK: - Actions

    public func tapDevice(_ item: DeviceListItem) {
        if item.isTrusted || item.isConnected {
            navigationPath = [item.deviceId]
            Task {
                _ = await engine.sessionManager.open(target: .device(item.deviceId), forcePair: false)
            }
        } else {
            Task {
                if let session = await engine.pairingService.pair(target: .device(item.deviceId), forcePair: false) {
                    navigationPath = [session.peer.deviceId]
                }
            }
        }
    }

    public func connectManually(ip: Ipv4, port: Int) async -> Bool {
        if let session = await engine.pairingService.pair(target: .address(ip: ip, port: port), forcePair: false) {
            showManualConnect = false
            navigationPath = [session.peer.deviceId]
            return true
        }
        return false
    }

    public func handleScannedQr(_ text: String) async -> Bool {
        if let err = await engine.pairingService.pairByQr(text: text) {
            _ = err
            return false
        }
        showQrScanner = false
        triggerHaptic(.success)
        return true
    }

    public func respondPairing(accept: Bool) {
        Task { await engine.pairingService.respondToRequest(accept: accept) }
    }

    public func cancelPairing() {
        Task { await engine.pairingService.cancel() }
    }

    public func respondOffer(transferId: TransferId, accept: Bool) {
        Task { await engine.fileTransferEngine.respondToOffer(transferId: transferId, accept: accept) }
    }

    public func sendText(peer: DeviceId, text: String) async -> TextSendOutcome {
        let outcome = await engine.textService.sendText(peer: peer, text: text)
        if case .sent = outcome {
            draftTextByPeer[peer] = ""
            triggerHaptic(.light)
        }
        return outcome
    }

    public func sendTextAsTxtFile(peer: DeviceId, text: String) {
        let fileName = "text_\(LocalDateTime.from(date: Date()).compactTimestamp).txt"
        let tempURL = FileManager.default.temporaryDirectory.appendingPathComponent(fileName)
        do {
            try StrictUtf8.encode(text).write(to: tempURL, options: .atomic)
            Task {
                let res = await engine.fileTransferEngine.sendFiles(peer: peer, sources: [SourceRef(tempURL.path)])
                handleSendFilesResult(res)
                draftTextByPeer[peer] = ""
            }
        } catch {
            showToast(error.localizedDescription)
        }
    }

    public func retryText(peer: DeviceId, messageId: MessageId) {
        Task { await engine.textService.retry(peer: peer, messageId: messageId) }
    }

    public func pasteToDraft(peer: DeviceId) {
        Task {
            switch await clipboard.readText() {
            case .text(let s):
                draftTextByPeer[peer] = (draftTextByPeer[peer] ?? "") + s
            case .noText:
                showToast(UiText.toastClipboardEmpty)
            case .failed:
                showToast(UiText.toastClipboardFailed)
            }
        }
    }

    public func sendClipboardDirectly(peer: DeviceId) {
        Task {
            switch await clipboard.readText() {
            case .text(let s):
                let res = await sendText(peer: peer, text: s)
                if case .tooLong = res {
                    showToast(UiText.dialogTextTooLong)
                }
            case .noText:
                showToast(UiText.toastClipboardEmpty)
            case .failed:
                showToast(UiText.toastClipboardFailed)
            }
        }
    }

    public func copyTextLossless(_ text: String) {
        Task {
            switch await clipboard.writeText(text) {
            case .ok:
                triggerHaptic(.light)
                showToast(TextRules.containsNul(text) ? UiText.toastCopiedWithNul : UiText.toastCopied)
            case .failed:
                showToast(UiText.toastClipboardFailed)
            }
        }
    }

    public func copyAddress(_ text: String) {
        Task {
            if case .ok = await clipboard.writeText(text) {
                triggerHaptic(.light)
                showToast("\(UiText.toastCopied) \(text)")
            }
        }
    }

    public func addPickedFiles(peer: DeviceId, urls: [URL]) {
        Task {
            var current = pendingFilesByPeer[peer] ?? []
            var pickedItems: [PickedItem] = []
            var metas: [SourceRef: SourceMeta] = [:]

            for url in urls {
                let ref = SourceRef(url.path)
                switch await sourceFiles.stat(ref) {
                case .success(let meta):
                    pickedItems.append(PickedItem(ref: ref, isDirectory: meta.isDirectory))
                    metas[ref] = meta
                case .failure:
                    showToast(UiText.errorFileSizeUnknown)
                }
            }

            let ruleRes = PendingListRules.add(current: current.map(\.ref), incoming: pickedItems)
            if ruleRes.foldersSkipped {
                showToast(UiText.errorFoldersNotSupported)
            }
            if ruleRes.tooMany {
                showToast(UiText.errorTooManyFiles)
                return
            }
            let existingRefs = Set(current.map(\.ref))
            for ref in ruleRes.list where !existingRefs.contains(ref) {
                if let meta = metas[ref] {
                    current.append(PendingLocalFile(ref: ref, name: meta.name, size: meta.size))
                }
            }
            pendingFilesByPeer[peer] = current
        }
    }

    public func removePendingFile(peer: DeviceId, id: UUID) {
        pendingFilesByPeer[peer]?.removeAll { $0.id == id }
    }

    public func clearPendingFiles(peer: DeviceId) {
        pendingFilesByPeer[peer] = []
    }

    public func sendPendingFiles(peer: DeviceId) {
        let items = pendingFilesByPeer[peer] ?? []
        guard !items.isEmpty else { return }
        let sources = items.map(\.ref)
        pendingFilesByPeer[peer] = []
        Task {
            let res = await engine.fileTransferEngine.sendFiles(peer: peer, sources: sources)
            handleSendFilesResult(res)
        }
    }

    public func resumeTask(_ transferId: TransferId) {
        Task { await engine.fileTransferEngine.resumeTask(transferId: transferId) }
    }

    public func cancelTask(_ transferId: TransferId) {
        Task { await engine.fileTransferEngine.cancelTask(transferId: transferId) }
    }

    public func openStoredFile(_ ref: StoredFileRef, mime: String) {
        Task {
            guard let locator = fileLocator else { return }
            let res = await locator.openFile(ref, mime: mime)
            handleLocateResult(res)
        }
    }

    public func revealStoredFile(_ ref: StoredFileRef) {
        Task {
            guard let locator = fileLocator else { return }
            let res = await locator.revealInFolder(ref)
            handleLocateResult(res)
        }
    }

    public func openReceiveDirectory() {
        Task {
            guard let locator = fileLocator else { return }
            let res = await locator.openDirectory(snapshot.settings.receiveDir)
            if res != .ok {
                showToast(UiText.errorReceiveDirInaccessible)
            }
        }
    }

    public func openSystemAppSettings() {
        if let url = URL(string: UIApplication.openSettingsURLString) {
            UIApplication.shared.open(url)
        }
    }

    public func saveDisplayName(_ input: String) async -> NameCheck {
        let check = await engine.updateDisplayName(input)
        if case .ok = check {
            triggerHaptic(.success)
            showToast(UiText.toastNameSaved)
        }
        return check
    }

    public func setThemeMode(_ mode: ThemeMode) {
        Task {
            await engine.updateSettings { cur in
                var next = cur
                next.themeMode = mode
                return next
            }
        }
    }

    public func toggleQuickTheme(currentScheme: ColorScheme) {
        setThemeMode(currentScheme == .dark ? .light : .dark)
    }

    public func setAutoCopy(_ enabled: Bool) {
        Task {
            await engine.updateSettings { cur in
                var next = cur
                next.autoCopyText = enabled
                return next
            }
        }
    }

    public func setAutoAccept(peer: DeviceId, enabled: Bool) {
        Task { await engine.setAutoAccept(peer: peer, enabled: enabled) }
    }

    public func unpair(_ peer: DeviceId) {
        Task {
            _ = await engine.pairingService.unpair(peer: peer)
            navigationPath.removeAll { $0 == peer }
            triggerHaptic(.warning)
        }
    }

    public func deleteTextRecord(_ record: TextRecord) {
        Task { await engine.deleteTextRecord(record) }
    }

    public func deleteFileRecord(_ record: FileRecord, deleteLocal: Bool) {
        Task {
            let ok = await engine.deleteFileRecord(record, deleteLocalFile: deleteLocal)
            if !ok {
                showToast(UiText.toastDeleteLocalFailed)
            }
        }
    }

    public func clearAllHistory() {
        Task { await engine.clearAllHistory() }
    }

    public func showToast(_ message: String) {
        toastMessage = message
        toastTask?.cancel()
        toastTask = Task {
            try? await Task.sleep(nanoseconds: 2_300_000_000)
            if !Task.isCancelled {
                toastMessage = nil
            }
        }
    }

    public func triggerHaptic(_ kind: HapticKind) {
        lastHapticKind = kind
        hapticTrigger += 1
    }

    // MARK: - Notice & Result Handling

    private func handleNotice(_ notice: EngineNotice) {
        switch notice {
        case .pairingFinished(let done, let failure):
            if let f = failure, isSecurityFailure(f) {
                triggerHaptic(.error)
                alertPayload = AlertPayload(title: UiText.securityTitle, message: UiTextMapper.openFailure(f))
                return
            }
            let peerName = snapshot.trustedDevices.first(where: { $0.deviceId == done.peer })?.displayName
            showToast(UiTextMapper.pairingResult(done, peerName: peerName, failure: failure))
            if done.result == .success {
                triggerHaptic(.success)
                showQrSheet = false
                showManualConnect = false
                navigationPath = [done.peer]
            } else {
                triggerHaptic(.warning)
            }

        case .pairingAutoRejected:
            break

        case .inboundFailure(let failure):
            triggerHaptic(.error)
            switch failure {
            case .versionIncompatible(let loc, let rem):
                alertPayload = AlertPayload(
                    title: UiText.errorVersionIncompatible,
                    message: UiText.labelVersionDetail(local: loc.description, remote: rem.description)
                )
            case .handshakeAuthFailed:
                alertPayload = AlertPayload(title: UiText.securityTitle, message: UiText.securityAuthFailed)
            case .keyMismatch(let peerName):
                alertPayload = AlertPayload(title: UiText.securityTitle, message: UiText.securityKeyMismatch(peer: peerName))
            }

        case .storeFailed:
            showToast(UiText.bannerHistoryStoreFailed)

        case .peerUnpaired:
            showToast(UiText.errorPeerUnpaired)
        }
    }

    private func isSecurityFailure(_ failure: OpenFailure) -> Bool {
        switch failure {
        case .keyMismatch, .handshakeAuthFailed, .fingerprintMismatch, .versionIncompatible:
            return true
        default:
            return false
        }
    }

    private func handleSendFilesResult(_ res: SendFilesResult) {
        switch res {
        case .started:
            triggerHaptic(.light)
        case .empty:
            break
        case .tooManyFiles:
            showToast(UiText.errorTooManyFiles)
        case .foldersNotSupported:
            showToast(UiText.errorFoldersNotSupported)
        case .unreadable(let detail):
            showToast(detail)
        case .connectFailed(let f):
            showToast(UiTextMapper.openFailure(f))
        }
    }

    private func handleLocateResult(_ res: LocateResult) {
        switch res {
        case .ok: break
        case .notFound: showToast(UiText.errorFileNotFound)
        case .noHandler: showToast(UiText.errorNoFileHandler)
        case .noFileManager, .noAccess: showToast(UiText.errorReceiveDirInaccessible)
        }
    }

    private func decodeHex(_ s: String) -> Data? {
        let chars = Array(s.utf8)
        guard chars.count % 2 == 0 else { return nil }
        func nib(_ c: UInt8) -> UInt8? {
            switch c {
            case 48...57: return c - 48
            case 97...102: return c - 97 + 10
            case 65...70: return c - 65 + 10
            default: return nil
            }
        }
        var out: [UInt8] = []
        var i = 0
        while i < chars.count {
            guard let hi = nib(chars[i]), let lo = nib(chars[i + 1]) else { return nil }
            out.append((hi << 4) | lo)
            i += 2
        }
        return Data(out)
    }
}
