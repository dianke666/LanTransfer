import Foundation
import LanTransferProtocol

public struct TextKey: Hashable, Sendable {
    public let peer: DeviceId
    public let messageId: MessageId

    public init(peer: DeviceId, messageId: MessageId) {
        self.peer = peer
        self.messageId = messageId
    }
}

public struct OutgoingText: Hashable, Sendable {
    public let key: TextKey
    public let body: Data
    public let sentAtUtcMs: Int64
    public var status: TextStatus
    public var deadline: Int64?

    public var showRetry: Bool { status == .failed }

    public init(key: TextKey, body: Data, sentAtUtcMs: Int64, status: TextStatus, deadline: Int64?) {
        self.key = key
        self.body = body
        self.sentAtUtcMs = sentAtUtcMs
        self.status = status
        self.deadline = deadline
    }
}

public struct TextState: Hashable, Sendable {
    public var outgoing: [TextKey: OutgoingText]

    public init(outgoing: [TextKey: OutgoingText] = [:]) {
        self.outgoing = outgoing
    }
}

public enum TextEvent: Hashable, Sendable {
    case send(key: TextKey, body: Data, sentAtUtcMs: Int64, now: Int64)
    case retry(key: TextKey, now: Int64)
    case ackReceived(key: TextKey)
    case corruptReceived(key: TextKey)
    case tick(now: Int64)
    case restored(records: [TextRecord])
    case forget(keys: Set<TextKey>)
}

public enum TextEffect: Hashable, Sendable {
    case transmit(peer: DeviceId, msg: ProtocolMessage)
    case saveStatus(key: TextKey, status: TextStatus)
    case armTimer(atMono: Int64)
}

/// 文字发送状态机（M3.1，需求 11.5–11.7、12.12）。
public enum TextReducer: Sendable {
    public static let ackTimeoutMs: Int64 = 10_000

    public static func reduce(_ state: TextState, _ event: TextEvent) -> Step<TextState, TextEffect> {
        var s = state
        switch event {
        case .send(let key, let body, let sentAt, let now):
            if s.outgoing[key] != nil {
                return Step(state: s)
            }
            let deadline = now + ackTimeoutMs
            let item = OutgoingText(key: key, body: body, sentAtUtcMs: sentAt, status: .sending, deadline: deadline)
            s.outgoing[key] = item
            return Step(state: s, effects: [
                .transmit(peer: key.peer, msg: .text(messageId: key.messageId, sentAt: sentAt, body: body)),
                .armTimer(atMono: deadline),
            ])

        case .retry(let key, let now):
            guard var item = s.outgoing[key], item.status == .failed else {
                return Step(state: s)
            }
            let deadline = now + ackTimeoutMs
            item.status = .sending
            item.deadline = deadline
            s.outgoing[key] = item
            return Step(state: s, effects: [
                .saveStatus(key: key, status: .sending),
                .transmit(peer: key.peer, msg: .text(messageId: key.messageId, sentAt: item.sentAtUtcMs, body: item.body)),
                .armTimer(atMono: deadline),
            ])

        case .ackReceived(let key):
            return transition(s, key: key, to: .delivered) { $0 == .sending || $0 == .failed }

        case .corruptReceived(let key):
            return transition(s, key: key, to: .failed) { $0 == .sending }

        case .tick(let now):
            var effects: [TextEffect] = []
            for (key, var item) in s.outgoing where item.status == .sending {
                if let deadline = item.deadline, now >= deadline {
                    item.status = .failed
                    item.deadline = nil
                    s.outgoing[key] = item
                    effects.append(.saveStatus(key: key, status: .failed))
                }
            }
            return Step(state: s, effects: effects)

        case .restored(let records):
            var effects: [TextEffect] = []
            for r in records where r.direction == .send {
                let key = TextKey(peer: r.peer, messageId: r.messageId)
                let nextStatus: TextStatus = (r.status == .sending) ? .failed : r.status
                if nextStatus != r.status {
                    effects.append(.saveStatus(key: key, status: nextStatus))
                }
                s.outgoing[key] = OutgoingText(
                    key: key,
                    body: r.textUtf8,
                    sentAtUtcMs: r.sentAtUtcMs,
                    status: nextStatus,
                    deadline: nil
                )
            }
            return Step(state: s, effects: effects)

        case .forget(let keys):
            for key in keys {
                s.outgoing.removeValue(forKey: key)
            }
            return Step(state: s)
        }
    }

    private static func transition(
        _ state: TextState,
        key: TextKey,
        to target: TextStatus,
        from predicate: (TextStatus) -> Bool
    ) -> Step<TextState, TextEffect> {
        var s = state
        guard var item = s.outgoing[key], predicate(item.status) else {
            return Step(state: s)
        }
        item.status = target
        item.deadline = nil
        s.outgoing[key] = item
        return Step(state: s, effects: [.saveStatus(key: key, status: target)])
    }
}
