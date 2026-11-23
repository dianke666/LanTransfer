import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import UIKit
import UserNotifications

/// iOS 系统剪贴板端口：写入后执行逐码点读回校验（需求 13.3）。
public final class IosClipboard: ClipboardPort, @unchecked Sendable {
    public init() {}

    public func readText() async -> ClipRead {
        await MainActor.run {
            if let text = UIPasteboard.general.string {
                return .text(text)
            }
            return .noText
        }
    }

    public func writeText(_ text: String) async -> ClipWrite {
        await MainActor.run {
            UIPasteboard.general.string = text
            let readBack = UIPasteboard.general.string
            return (readBack == text) ? .ok : .failed
        }
    }
}

/// iOS 文件预览与定位端口：预览回调交由 SwiftUI 弹出 QuickLook，目录定位使用 `shareddocuments://` 打开系统“文件”App。
public final class IosFileLocator: FileLocator, @unchecked Sendable {
    private let onQuickLook: @MainActor @Sendable (URL) -> Void

    public init(onQuickLook: @escaping @MainActor @Sendable (URL) -> Void) {
        self.onQuickLook = onQuickLook
    }

    public func openFile(_ ref: StoredFileRef, mime: String) async -> LocateResult {
        let url = URL(fileURLWithPath: ref.value)
        guard FileManager.default.fileExists(atPath: url.path) else {
            return .notFound
        }
        await MainActor.run {
            onQuickLook(url)
        }
        return .ok
    }

    public func revealInFolder(_ ref: StoredFileRef) async -> LocateResult {
        let url = URL(fileURLWithPath: ref.value)
        guard FileManager.default.fileExists(atPath: url.path) else {
            return .notFound
        }
        return await openFilesApp(directoryURL: url.deletingLastPathComponent())
    }

    public func openDirectory(_ dir: DirRef) async -> LocateResult {
        let dirURL: URL
        switch dir {
        case .default:
            dirURL = IosReceiveStorage.defaultReceiveDirectoryURL()
        case .path(let path):
            dirURL = URL(fileURLWithPath: path, isDirectory: true)
        case .tree(let uri):
            dirURL = URL(string: uri) ?? IosReceiveStorage.defaultReceiveDirectoryURL()
        }
        try? FileManager.default.createDirectory(at: dirURL, withIntermediateDirectories: true)
        return await openFilesApp(directoryURL: dirURL)
    }

    private func openFilesApp(directoryURL: URL) async -> LocateResult {
        let rawPath = directoryURL.absoluteString
        let filesScheme = rawPath.replacingOccurrences(of: "file://", with: "shareddocuments://")
        guard let targetURL = URL(string: filesScheme) else { return .noFileManager }
        let opened = await MainActor.run { () async -> Bool in
            await UIApplication.shared.open(targetURL)
        }
        return opened ? .ok : .noFileManager
    }
}

/// iOS 本地通知端口（`UNUserNotificationCenter`）。
public final class IosNotifier: Notifier, @unchecked Sendable {
    public init() {}

    public func requestAuthorizationIfNeeded() async {
        _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])
    }

    public func isEnabled() async -> Bool {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        return settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional
    }

    public func notifyText(key: NotifyKey, peerName: String, preview: String) async {
        await post(id: identifier(for: key), title: UiText.notifyTextTitle(peer: peerName), body: preview)
    }

    public func notifyFileCompleted(key: NotifyKey, peerName: String, fileName: String, file: StoredFileRef) async {
        await post(id: identifier(for: key), title: UiText.notifyFileDoneTitle(peer: peerName), body: fileName)
    }

    public func notifyRequest(key: NotifyKey, peerName: String, kind: RequestKind, fileCount: Int?) async {
        switch kind {
        case .pairing:
            await post(id: identifier(for: key), title: UiText.notifyPairReqTitle, body: UiText.notifyPairReqBody(peer: peerName))
        case .fileOffer:
            await post(id: identifier(for: key), title: UiText.notifyFileReqTitle, body: UiText.notifyFileReqBody(peer: peerName, count: fileCount ?? 1))
        }
    }

    public func notifyProblem(key: NotifyKey, peerName: String, fileName: String?, status: String) async {
        let body = fileName.map { "\($0)：\(status)" } ?? status
        await post(id: identifier(for: key), title: UiText.notifyProblemTitle(peer: peerName), body: body)
    }

    public func notifyBackgroundPaused() async {
        await post(id: "bg-paused", title: UiText.appName, body: UiText.iosNotifyBgPaused)
    }

    public func cancel(key: NotifyKey) async {
        UNUserNotificationCenter.current().removeDeliveredNotifications(withIdentifiers: [identifier(for: key)])
    }

    private func post(id: String, title: String, body: String) async {
        guard await isEnabled() else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        let req = UNNotificationRequest(identifier: id, content: content, trigger: nil)
        try? await UNUserNotificationCenter.current().add(req)
    }

    private func identifier(for key: NotifyKey) -> String {
        switch key {
        case .text(let p, let m): return "text-\(p.hexString)-\(m.hexString)"
        case .pairingRequest(let p): return "pair-\(p.hexString)"
        case .transfer(let t): return "transfer-\(t.hexString)"
        case .file(let t, let idx): return "file-\(t.hexString)-\(idx)"
        }
    }
}

/// iOS 前后台与防熄屏协调器：传输期间保持亮屏；切后台申请 `beginBackgroundTask` 宽限期并在到期前优雅暂停传输。
public actor IosAppLifecycle: AppLifecycle {
    private var background: Bool = false
    private var transferActive: Bool = false
    private var bgTaskId: UIBackgroundTaskIdentifier = .invalid
    private var onBackgroundExpiring: (@Sendable () async -> Void)?

    public init() {}

    public func setExpirationHandler(_ handler: @escaping @Sendable () async -> Void) {
        onBackgroundExpiring = handler
    }

    public func isInBackground() -> Bool {
        background
    }

    public func setTransferActive(_ active: Bool) async {
        transferActive = active
        await MainActor.run {
            UIApplication.shared.isIdleTimerDisabled = active
        }
        if !active {
            await endBackgroundTaskIfNeeded()
        }
    }

    public func didEnterBackground() async {
        background = true
        if transferActive {
            await beginBackgroundGracePeriod()
        }
    }

    public func willEnterForeground() async {
        background = false
        await endBackgroundTaskIfNeeded()
    }

    private func beginBackgroundGracePeriod() async {
        await endBackgroundTaskIfNeeded()
        let taskId = await MainActor.run { () -> UIBackgroundTaskIdentifier in
            UIApplication.shared.beginBackgroundTask(withName: "LanTransferActive") { [weak self] in
                Task { await self?.handleBackgroundExpired() }
            }
        }
        bgTaskId = taskId
    }

    private func handleBackgroundExpired() async {
        await onBackgroundExpiring?()
        await endBackgroundTaskIfNeeded()
    }

    private func endBackgroundTaskIfNeeded() async {
        let id = bgTaskId
        bgTaskId = .invalid
        if id != .invalid {
            await MainActor.run {
                UIApplication.shared.endBackgroundTask(id)
            }
        }
    }
}
