import Foundation
import LanTransferProtocol

/// 接收端文件系统的命名约束（设计 C3.1）：Android / iOS 为 255 UTF-8 字节，Windows 为 255 UTF-16 码元。
public enum FsProfile: String, CaseIterable, Hashable, Sendable {
    case android
    case windows
    case ios
}

/// 界面外观主题模式。
public enum ThemeMode: String, CaseIterable, Hashable, Sendable {
    case system
    case light
    case dark
}

/// 不含时区歧义的本地日期时间（用于回退文件名 `file_YYYYMMDD_HHMMSS` 与数据库 `started_at`）。
public struct LocalDateTime: Hashable, Sendable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int
    public let hour: Int
    public let minute: Int
    public let second: Int

    public init(year: Int, month: Int, day: Int, hour: Int, minute: Int, second: Int) {
        self.year = year
        self.month = month
        self.day = day
        self.hour = hour
        self.minute = minute
        self.second = second
    }

    public static func from(date: Date, timeZone: TimeZone = .current) -> LocalDateTime {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = timeZone
        let c = cal.dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        return LocalDateTime(
            year: c.year ?? 1970,
            month: c.month ?? 1,
            day: c.day ?? 1,
            hour: c.hour ?? 0,
            minute: c.minute ?? 0,
            second: c.second ?? 0
        )
    }

    public static func parseIso(_ s: String) -> LocalDateTime? {
        // YYYY-MM-DDTHH:MM:SS
        let parts = s.split(separator: "T")
        guard parts.count == 2 else { return nil }
        let d = parts[0].split(separator: "-")
        let t = parts[1].split(separator: ":")
        guard d.count == 3, t.count == 3,
              let y = Int(d[0]), let m = Int(d[1]), let day = Int(d[2]),
              let h = Int(t[0]), let min = Int(t[1]), let sec = Int(t[2]) else {
            return nil
        }
        return LocalDateTime(year: y, month: m, day: day, hour: h, minute: min, second: sec)
    }

    public var isoString: String {
        String(format: "%04d-%02d-%02dT%02d:%02d:%02d", year, month, day, hour, minute, second)
    }

    public var compactTimestamp: String {
        String(format: "%04d%02d%02d_%02d%02d%02d", year, month, day, hour, minute, second)
    }

    public var description: String { isoString }
}

/// 纯归约器单步结果：新状态 + 交由引擎执行的副作用列表。
public struct Step<State: Sendable, Effect: Sendable>: Sendable {
    public let state: State
    public let effects: [Effect]

    public init(state: State, effects: [Effect] = []) {
        self.state = state
        self.effects = effects
    }
}

/// 随机数端口（领域与引擎共用；测试可注入确定性序列）。
public protocol RngPort: Sendable {
    func nextBytes(count: Int) -> Data
}

/// 传输方向（M1，写入数据库，不可重编号）。
public enum Direction: Int, Hashable, Sendable {
    case send = 0
    case receive = 1
}

/// 文字消息状态（M3.1，写入数据库，不可重编号）。
public enum TextStatus: Int, Hashable, Sendable {
    case sending = 0
    case delivered = 1
    case failed = 2
    case received = 3
}

/// 传输任务状态（M3.2，写入数据库，终态码值均 >= 10）。
public enum TaskState: Int, Hashable, Sendable {
    case offering = 0
    case awaitingUser = 1
    case running = 2
    case interrupted = 3
    case resuming = 4
    case completed = 10
    case rejected = 11
    case noResponse = 12
    case noSpace = 13
    case cannotSave = 14
    case connectionLost = 15
    case cancelled = 16
    case peerCancelled = 17
    case offerInvalid = 18

    public var isTerminal: Bool { rawValue >= 10 }
    public var isActive: Bool { self == .running || self == .resuming }
}

/// 任务中断原因（M3.2，写入数据库）。
public enum InterruptReason: Int, Hashable, Sendable {
    case peerUnresponsive = 1
    case peerClosed = 2
    case localNetworkLost = 3
    case integrityFailed = 4
    case protocolError = 5
    case writeFailed = 6
    case appExit = 7
    case appKilled = 8
}

/// 单个文件状态（M3.3，写入数据库，终态码值均 >= 10）。
public enum FileState: Int, Hashable, Sendable {
    case waiting = 0
    case transferring = 1
    case verifying = 2
    case writeFailed = 3
    case completed = 10
    case readFailed = 11
    case checksumFailed = 12

    public var isTerminal: Bool { rawValue >= 10 }
}

/// 配对结局（M3.4，仅内存使用）。
public enum PairingResult: Hashable, Sendable {
    case success
    case rejected
    case autoRejected
    case busy
    case timeout
    case cancelled
    case codeMismatch
    case tokenInvalid
    case disconnected
    case connectFailed
    case storeFailed
}

/// 接收目录状态（15.7）。
public enum DirStatus: Hashable, Sendable {
    case usable
    case cannotCreate
    case notWritable
}

/// 记录时刻及当时本机 UTC 偏移分钟数（20.1）。
public struct RecordTime: Hashable, Sendable {
    public let utcMs: Int64
    public let offsetMin: Int

    public init(utcMs: Int64, offsetMin: Int) {
        self.utcMs = utcMs
        self.offsetMin = offsetMin
    }
}

public struct SourceRef: Hashable, Sendable {
    public let value: String
    public init(_ value: String) { self.value = value }
}

public struct TempRef: Hashable, Sendable {
    public let value: String
    public init(_ value: String) { self.value = value }
}

public struct StoredFileRef: Hashable, Sendable {
    public let value: String
    public init(_ value: String) { self.value = value }
}

/// 接收目录引用：序列化为 `default`、`tree:<uri>` 或 `path:<绝对路径>`。
public enum DirRef: Hashable, Sendable {
    case `default`
    case tree(String)
    case path(String)

    public var serialized: String {
        switch self {
        case .default:
            return "default"
        case .tree(let uri):
            return "tree:\(uri)"
        case .path(let value):
            return "path:\(value)"
        }
    }

    public static func parse(_ raw: String) -> DirRef? {
        if raw == "default" {
            return .default
        }
        if raw.hasPrefix("tree:") {
            let value = String(raw.dropFirst(5))
            return value.isEmpty ? nil : .tree(value)
        }
        if raw.hasPrefix("path:") {
            let value = String(raw.dropFirst(5))
            return value.isEmpty ? nil : .path(value)
        }
        return nil
    }
}

/// 本机接口类型；同前缀长度下优先级为 wifi < ethernet < hotspot < other。
public enum IfaceKind: Int, CaseIterable, Hashable, Sendable {
    case wifi = 0
    case ethernet = 1
    case hotspot = 2
    case other = 3
}

/// 本机活动私有 IPv4 网络接口（C3.2）。
public struct LocalInterface: Hashable, Sendable {
    public let name: String
    public let kind: IfaceKind
    public let address: Ipv4
    public let prefixLength: Int

    public init(name: String, kind: IfaceKind, address: Ipv4, prefixLength: Int) {
        precondition(prefixLength >= 0 && prefixLength <= 32, "prefixLength must be 0..32")
        self.name = name
        self.kind = kind
        self.address = address
        self.prefixLength = prefixLength
    }

    public var broadcast: Ipv4 {
        Ipv4(rawValue: address.rawValue | ~AddressRules.subnetMask(prefixLength))
    }

    public func contains(_ ip: Ipv4) -> Bool {
        let mask = AddressRules.subnetMask(prefixLength)
        return (ip.rawValue & mask) == (address.rawValue & mask)
    }
}

/// 本机完整身份（私钥仅在 IdentityStore 与 NoiseXX 内部使用）。
public struct DeviceIdentity: Hashable, Sendable {
    public let deviceId: DeviceId
    public let keyPair: X25519KeyPair
    public var displayName: String

    public init(deviceId: DeviceId, keyPair: X25519KeyPair, displayName: String) {
        self.deviceId = deviceId
        self.keyPair = keyPair
        self.displayName = displayName
    }
}

/// 任务中断信息。
public struct InterruptInfo: Hashable, Sendable {
    public let reason: InterruptReason
    public let detail: String?

    public init(reason: InterruptReason, detail: String? = nil) {
        self.reason = reason
        self.detail = detail
    }
}

/// 本机已信任设备记录（M1）。
public struct TrustedDevice: Hashable, Sendable {
    public let deviceId: DeviceId
    public var displayName: String
    public let deviceType: DeviceType
    public let publicKey: Data
    public var autoAccept: Bool
    public let pairedAtUtcMs: Int64
    public var lastAddress: Ipv4?
    public var lastPort: Int?

    public init(
        deviceId: DeviceId,
        displayName: String,
        deviceType: DeviceType,
        publicKey: Data,
        autoAccept: Bool,
        pairedAtUtcMs: Int64,
        lastAddress: Ipv4? = nil,
        lastPort: Int? = nil
    ) {
        self.deviceId = deviceId
        self.displayName = displayName
        self.deviceType = deviceType
        self.publicKey = publicKey
        self.autoAccept = autoAccept
        self.pairedAtUtcMs = pairedAtUtcMs
        self.lastAddress = lastAddress
        self.lastPort = lastPort
    }
}

/// 文字记录（M1）。
public struct TextRecord: Hashable, Sendable {
    public var seq: Int64
    public let peer: DeviceId
    public let peerName: String
    public let messageId: MessageId
    public let direction: Direction
    public let textUtf8: Data
    public let sentAtUtcMs: Int64
    public let recordedAt: RecordTime
    public var status: TextStatus

    public init(
        seq: Int64,
        peer: DeviceId,
        peerName: String,
        messageId: MessageId,
        direction: Direction,
        textUtf8: Data,
        sentAtUtcMs: Int64,
        recordedAt: RecordTime,
        status: TextStatus
    ) {
        self.seq = seq
        self.peer = peer
        self.peerName = peerName
        self.messageId = messageId
        self.direction = direction
        self.textUtf8 = textUtf8
        self.sentAtUtcMs = sentAtUtcMs
        self.recordedAt = recordedAt
        self.status = status
    }
}

/// 单个传输文件记录（M1）。
public struct TransferFile: Hashable, Sendable {
    public let transferId: TransferId
    public let index: Int
    public var seq: Int64
    public let originalName: String
    public var size: Int64
    public let mime: String
    public var mtime: Int64?
    public var state: FileState
    public var persisted: Int64
    public var sha256: Data?
    public var sourceRef: SourceRef?
    public var tempRef: TempRef?
    public var startedAt: LocalDateTime?
    public var lastWriteAtUtcMs: Int64?
    public var finalName: String?
    public var fullPath: String?
    public var storedRef: StoredFileRef?

    public init(
        transferId: TransferId,
        index: Int,
        seq: Int64,
        originalName: String,
        size: Int64,
        mime: String,
        mtime: Int64? = nil,
        state: FileState,
        persisted: Int64 = 0,
        sha256: Data? = nil,
        sourceRef: SourceRef? = nil,
        tempRef: TempRef? = nil,
        startedAt: LocalDateTime? = nil,
        lastWriteAtUtcMs: Int64? = nil,
        finalName: String? = nil,
        fullPath: String? = nil,
        storedRef: StoredFileRef? = nil
    ) {
        self.transferId = transferId
        self.index = index
        self.seq = seq
        self.originalName = originalName
        self.size = size
        self.mime = mime
        self.mtime = mtime
        self.state = state
        self.persisted = persisted
        self.sha256 = sha256
        self.sourceRef = sourceRef
        self.tempRef = tempRef
        self.startedAt = startedAt
        self.lastWriteAtUtcMs = lastWriteAtUtcMs
        self.finalName = finalName
        self.fullPath = fullPath
        self.storedRef = storedRef
    }
}

/// 传输任务（M1）。
public struct TransferTask: Hashable, Sendable {
    public let transferId: TransferId
    public let peer: DeviceId
    public let peerName: String
    public let direction: Direction
    public var state: TaskState
    public var interrupt: InterruptInfo?
    public var receiveDir: DirRef?
    public let recordedAt: RecordTime
    public var updatedAtUtcMs: Int64
    public var files: [TransferFile]

    public init(
        transferId: TransferId,
        peer: DeviceId,
        peerName: String,
        direction: Direction,
        state: TaskState,
        interrupt: InterruptInfo? = nil,
        receiveDir: DirRef? = nil,
        recordedAt: RecordTime,
        updatedAtUtcMs: Int64,
        files: [TransferFile]
    ) {
        self.transferId = transferId
        self.peer = peer
        self.peerName = peerName
        self.direction = direction
        self.state = state
        self.interrupt = interrupt
        self.receiveDir = receiveDir
        self.recordedAt = recordedAt
        self.updatedAtUtcMs = updatedAtUtcMs
        self.files = files
    }
}

/// 待送达取消通知（18.9）。
public struct PendingCancel: Hashable, Sendable {
    public let peer: DeviceId
    public let transferId: TransferId
    public let createdAtUtcMs: Int64

    public init(peer: DeviceId, transferId: TransferId, createdAtUtcMs: Int64) {
        self.peer = peer
        self.transferId = transferId
        self.createdAtUtcMs = createdAtUtcMs
    }
}

/// 应用设置（M4）。
public struct Settings: Hashable, Sendable {
    public var receiveDir: DirRef
    public var autoCopyText: Bool
    public var themeMode: ThemeMode

    public init(
        receiveDir: DirRef = .default,
        autoCopyText: Bool = false,
        themeMode: ThemeMode = .system
    ) {
        self.receiveDir = receiveDir
        self.autoCopyText = autoCopyText
        self.themeMode = themeMode
    }
}
