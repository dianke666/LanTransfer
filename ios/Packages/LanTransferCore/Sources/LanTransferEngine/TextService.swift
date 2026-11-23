import Foundation
import LanTransferDomain
import LanTransferProtocol

public enum TextSendOutcome: Hashable, Sendable {
    case sent(MessageId)
    case empty
    case tooLong(utf8Bytes: Int)
}

/// 无损文字收发服务（actor）：驱动 `TextReducer`，管理去重落库、自动复制、后台通知与 10 s 失败重试。
public actor TextService {
    private let sessionManager: SessionManager
    private let historyStore: any HistoryStore
    private let trustStore: any TrustStore
    private let settingsStore: any SettingsStore
    private let clipboard: any ClipboardPort
    private let notifier: any Notifier
    private let lifecycle: any AppLifecycle
    private let clock: any ClockPort
    private let rng: any RngPort

    private var state = TextState()
    private var timerTask: Task<Void, Never>?
    private var onHistoryChanged: (@Sendable () async -> Void)?
    private var onNotice: (@Sendable (EngineNotice) async -> Void)?

    public init(
        sessionManager: SessionManager,
        historyStore: any HistoryStore,
        trustStore: any TrustStore,
        settingsStore: any SettingsStore,
        clipboard: any ClipboardPort,
        notifier: any Notifier,
        lifecycle: any AppLifecycle,
        clock: any ClockPort,
        rng: any RngPort
    ) {
        self.sessionManager = sessionManager
        self.historyStore = historyStore
        self.trustStore = trustStore
        self.settingsStore = settingsStore
        self.clipboard = clipboard
        self.notifier = notifier
        self.lifecycle = lifecycle
        self.clock = clock
        self.rng = rng
    }

    public func configure(
        onHistoryChanged: @escaping @Sendable () async -> Void,
        onNotice: @escaping @Sendable (EngineNotice) async -> Void
    ) {
        self.onHistoryChanged = onHistoryChanged
        self.onNotice = onNotice
    }

    public func restoreFromHistory() async {
        guard case .success(let records) = await historyStore.list(limit: 10_000) else { return }
        let texts = records.compactMap { rec -> TextRecord? in
            if case .text(let t) = rec { return t }
            return nil
        }
        let step = TextReducer.reduce(state, .restored(records: texts))
        state = step.state
        await execute(step.effects)
    }

    public func sendText(peer: DeviceId, text: String) async -> TextSendOutcome {
        switch TextRules.checkOutgoing(text) {
        case .empty:
            return .empty
        case .tooLong(let bytes):
            return .tooLong(utf8Bytes: bytes)
        case .ok(let utf8):
            let msgId = MessageId(bytes: rng.nextBytes(count: 16))!
            let nowWall = clock.wallMs()
            let nowMono = clock.monotonicMs()
            let peerName = await resolvePeerName(peer)
            let record = TextRecord(
                seq: 0,
                peer: peer,
                peerName: peerName,
                messageId: msgId,
                direction: .send,
                textUtf8: utf8,
                sentAtUtcMs: nowWall,
                recordedAt: RecordTime(utcMs: nowWall, offsetMin: clock.tzOffsetMinutes()),
                status: .sending
            )
            if case .failed(let detail) = await historyStore.appendText(record) {
                await onNotice?(.storeFailed(detail: detail))
            }
            let key = TextKey(peer: peer, messageId: msgId)
            let step = TextReducer.reduce(state, .send(key: key, body: utf8, sentAtUtcMs: nowWall, now: nowMono))
            state = step.state
            await onHistoryChanged?()
            await execute(step.effects)
            return .sent(msgId)
        }
    }

    public func retry(peer: DeviceId, messageId: MessageId) async {
        let key = TextKey(peer: peer, messageId: messageId)
        let step = TextReducer.reduce(state, .retry(key: key, now: clock.monotonicMs()))
        state = step.state
        await execute(step.effects)
    }

    public func forget(keys: Set<TextKey>) {
        let step = TextReducer.reduce(state, .forget(keys: keys))
        state = step.state
    }

    public func handleInbound(peer: DeviceId, message: ProtocolMessage) async {
        switch message {
        case .text(let messageId, let sentAt, let body):
            await handleIncomingText(peer: peer, messageId: messageId, sentAt: sentAt, body: body)
        case .textAck(let messageId):
            let step = TextReducer.reduce(state, .ackReceived(key: TextKey(peer: peer, messageId: messageId)))
            state = step.state
            await execute(step.effects)
        case .textCorrupt(let messageId):
            let step = TextReducer.reduce(state, .corruptReceived(key: TextKey(peer: peer, messageId: messageId)))
            state = step.state
            await execute(step.effects)
        default:
            break
        }
    }

    // MARK: - Private

    private func handleIncomingText(peer: DeviceId, messageId: MessageId, sentAt: Int64, body: Data) async {
        guard let decoded = StrictUtf8.decode(body), body.count <= ProtocolConstants.maxTextBytes else {
            if let session = await sessionManager.session(for: peer) {
                await session.connection.send(.textCorrupt(messageId: messageId), priority: .control)
            }
            return
        }

        let peerName = await resolvePeerName(peer)
        let nowWall = clock.wallMs()
        let record = TextRecord(
            seq: 0,
            peer: peer,
            peerName: peerName,
            messageId: messageId,
            direction: .receive,
            textUtf8: body,
            sentAtUtcMs: sentAt,
            recordedAt: RecordTime(utcMs: nowWall, offsetMin: clock.tzOffsetMinutes()),
            status: .received
        )

        let storeRes = await historyStore.appendText(record)
        if let session = await sessionManager.session(for: peer) {
            await session.connection.send(.textAck(messageId: messageId), priority: .control)
        }
        switch storeRes {
        case .duplicate:
            return
        case .failed(let detail):
            await onNotice?(.storeFailed(detail: detail))
        case .ok:
            break
        }

        await onHistoryChanged?()

        let inBackground = await lifecycle.isInBackground()
        if !inBackground {
            let settings = await settingsStore.current()
            if settings.autoCopyText {
                _ = await clipboard.writeText(decoded)
            }
        } else {
            let preview = TextRules.notificationPreview(decoded)
            await notifier.notifyText(key: .text(peer: peer, messageId: messageId), peerName: peerName, preview: preview)
        }
    }

    private func execute(_ effects: [TextEffect]) async {
        for fx in effects {
            switch fx {
            case .transmit(let peer, let msg):
                Task { [weak self] in
                    guard let self else { return }
                    let openRes = await self.sessionManager.open(target: .device(peer), forcePair: false)
                    switch openRes {
                    case .ok(let session):
                        await session.connection.send(msg, priority: .text)
                    case .failed(.notTrusted(let p)):
                        await self.onNotice?(.peerUnpaired(p))
                    default:
                        break
                    }
                }
            case .saveStatus(let key, let status):
                if case .failed(let detail) = await historyStore.updateTextStatus(id: key.messageId, peer: key.peer, status: status) {
                    await onNotice?(.storeFailed(detail: detail))
                }
                await onHistoryChanged?()
            case .armTimer(let atMono):
                scheduleNextTimer(targetMono: atMono)
            }
        }
    }

    private func scheduleNextTimer(targetMono: Int64) {
        timerTask?.cancel()
        let nextDeadline = state.outgoing.values.compactMap(\.deadline).min() ?? targetMono
        let delay = max(nextDeadline - clock.monotonicMs(), 0)
        timerTask = Task { [weak self] in
            do {
                try await self?.clock.sleep(milliseconds: delay)
                await self?.handleTick()
            } catch {}
        }
    }

    private func handleTick() async {
        let step = TextReducer.reduce(state, .tick(now: clock.monotonicMs()))
        state = step.state
        await execute(step.effects)
        if let nextDeadline = state.outgoing.values.compactMap(\.deadline).min() {
            scheduleNextTimer(targetMono: nextDeadline)
        }
    }

    private func resolvePeerName(_ peer: DeviceId) async -> String {
        if let session = await sessionManager.session(for: peer) {
            return session.peer.displayName
        }
        if let trusted = await trustStore.get(peer) {
            return trusted.displayName
        }
        return peer.hexString.prefix(8).uppercased()
    }
}
