import Foundation
import LanTransferDomain
import LanTransferProtocol
import Security

// C5 平台适配层端口契约（Swift 6 并发安全镜像，对应 Android/Windows C5.1–C5.4）。

/// 套接字地址。
public struct SocketAddr: Hashable, Sendable, CustomStringConvertible {
    public let ip: Ipv4
    public let port: Int

    public init(ip: Ipv4, port: Int) {
        self.ip = ip
        self.port = port
    }

    public var description: String { "\(ip):\(port)" }
}

// MARK: - C5.1 网络端口

public protocol NetworkMonitor: Sendable {
    var interfacesStream: AsyncStream<[LocalInterface]> { get }
    func currentInterfaces() async -> [LocalInterface]
    func setFastPolling(_ enabled: Bool) async
}

public protocol TcpConn: Sendable {
    var local: SocketAddr { get }
    var remote: SocketAddr { get }
    /// 读取最多 maxBytes 字节；返回 nil 表示 EOF 或连接被重置。
    func read(maxBytes: Int) async -> Data?
    /// 全部写出返回 true；连接已关闭或断开返回 false。
    func write(_ data: Data) async -> Bool
    func close() async
}

public protocol TcpListener: Sendable {
    var port: Int { get }
    func accept() async -> (any TcpConn)?
    func close() async
}

public enum ConnectResult: Sendable {
    case ok(any TcpConn)
    case bindFailed(vpnSuspected: Bool)
    case refused
    case unreachable
    case timeout
}

public struct Datagram: Hashable, Sendable {
    public let data: Data
    public let src: Ipv4
    public let srcPort: Int
    public let via: LocalInterface?

    public init(data: Data, src: Ipv4, srcPort: Int, via: LocalInterface?) {
        self.data = data
        self.src = src
        self.srcPort = srcPort
        self.via = via
    }
}

public protocol UdpEndpoint: Sendable {
    func receive() async -> Datagram?
    func send(via: LocalInterface, dst: Ipv4, port: Int, data: Data) async -> Bool
    func close() async
}

public enum UdpOpenResult: Sendable {
    case ok(any UdpEndpoint)
    case failed(reason: String)
}

public protocol Transport: Sendable {
    func connect(via: LocalInterface, ip: Ipv4, port: Int, timeoutSeconds: TimeInterval) async -> ConnectResult
    func listen(candidatePorts: [Int]) async -> (any TcpListener)?
    func openUdp(port: Int, multicastGroup: Ipv4, interfaces: [LocalInterface]) async -> UdpOpenResult
}

// MARK: - C5.2 文件端口

public enum IoResult: Hashable, Sendable {
    case ok
    case failed(detail: String)
}

public struct IoError: Error, Hashable, Sendable {
    public let detail: String
    public init(_ detail: String) { self.detail = detail }
}

public protocol TempWriter: Sendable {
    func write(_ data: Data) async -> IoResult
    func flushSync() async -> IoResult
    func close() async
}

public protocol ChunkReader: Sendable {
    /// 读取最多 maxBytes 字节；Ok(nil) 表示 EOF。
    func read(maxBytes: Int) async -> Result<Data?, IoError>
    func close() async
}

public enum CommitResult: Hashable, Sendable {
    case ok(finalName: String, fullPath: String, storedRef: StoredFileRef)
    case failed(detail: String)
}

public struct SourceMeta: Hashable, Sendable {
    public static let octetStream = "application/octet-stream"

    public let name: String
    public let size: Int64
    public let mtime: Int64?
    public let mime: String
    public let isDirectory: Bool

    public init(name: String, size: Int64, mtime: Int64?, mime: String, isDirectory: Bool) {
        self.name = name
        self.size = size
        self.mtime = mtime
        self.mime = mime
        self.isDirectory = isDirectory
    }
}

public protocol ReceiveStorage: Sendable {
    var fsProfile: FsProfile { get }
    func probe(_ dir: DirRef) async -> DirStatus
    func ensureDirectory(_ dir: DirRef) async -> Bool
    func isWritable(_ dir: DirRef) async -> Bool
    func freeSpace(_ dir: DirRef) async -> Int64?
    func exists(_ dir: DirRef, name: String) async -> Bool
    func createTemp(dir: DirRef, task: TransferId, index: Int) async -> TempRef
    func openTempForWrite(_ ref: TempRef, offset: Int64) async -> Result<any TempWriter, IoError>
    func openTempForRead(_ ref: TempRef) async -> Result<any ChunkReader, IoError>
    func tempSize(_ ref: TempRef) async -> Int64?
    func deleteTemp(_ ref: TempRef) async
    func listTemps() async -> [TempInfo]
    func commitTemp(_ ref: TempRef, dir: DirRef, finalName: String, mtime: Int64?) async -> CommitResult
    func deleteStoredFile(_ ref: StoredFileRef) async -> Bool
}

public protocol SourceFiles: Sendable {
    func stat(_ ref: SourceRef) async -> Result<SourceMeta, IoError>
    func openForRead(_ ref: SourceRef, offset: Int64) async -> Result<any ChunkReader, IoError>
}

public protocol OutgoingTextCache: Sendable {
    func put(peer: DeviceId, messageId: MessageId, utf8: Data) async
    func get(peer: DeviceId, messageId: MessageId) async -> Data?
    func remove(peer: DeviceId, messageId: MessageId) async
    func clear() async
}

// MARK: - C5.3 持久化端口

public enum StoreResult: Hashable, Sendable {
    case ok
    case duplicate
    case failed(detail: String)
}

public enum IdentityLoad: Hashable, Sendable {
    case loaded(DeviceIdentity)
    case created(DeviceIdentity)
    case resetCorrupted(DeviceIdentity)
    case saveFailed(detail: String, corrupted: Bool)

    public var readyIdentity: DeviceIdentity? {
        switch self {
        case .loaded(let id), .created(let id), .resetCorrupted(let id):
            return id
        case .saveFailed:
            return nil
        }
    }
}

public struct RecordId: Hashable, Sendable {
    public let seq: Int64
    public init(_ seq: Int64) { self.seq = seq }
}

public struct FileRecord: Hashable, Sendable {
    public let file: TransferFile
    public let peer: DeviceId
    public let peerName: String
    public let direction: Direction
    public let recordedAt: RecordTime
    public let taskState: TaskState
    public let interrupt: InterruptInfo?

    public init(
        file: TransferFile,
        peer: DeviceId,
        peerName: String,
        direction: Direction,
        recordedAt: RecordTime,
        taskState: TaskState,
        interrupt: InterruptInfo? = nil
    ) {
        self.file = file
        self.peer = peer
        self.peerName = peerName
        self.direction = direction
        self.recordedAt = recordedAt
        self.taskState = taskState
        self.interrupt = interrupt
    }
}

public enum HistoryRecord: Hashable, Sendable {
    case text(TextRecord)
    case file(FileRecord)

    public var recordedAt: RecordTime {
        switch self {
        case .text(let record): return record.recordedAt
        case .file(let record): return record.recordedAt
        }
    }

    public var seq: Int64 {
        switch self {
        case .text(let record): return record.seq
        case .file(let record): return record.file.seq
        }
    }
}

public protocol IdentityStore: Sendable {
    func loadOrCreate() async -> IdentityLoad
    func displayName() async -> String
    func setDisplayName(_ name: String) async -> StoreResult
}

public protocol TrustStore: Sendable {
    func all() async -> [TrustedDevice]
    func get(_ id: DeviceId) async -> TrustedDevice?
    func upsert(_ device: TrustedDevice) async -> StoreResult
    func delete(_ id: DeviceId) async -> StoreResult
    func setAutoAccept(_ id: DeviceId, enabled: Bool) async -> StoreResult
    func updateName(_ id: DeviceId, name: String) async -> StoreResult
    func recordAddress(_ id: DeviceId, ip: Ipv4, port: Int) async -> StoreResult
}

public protocol HistoryStore: Sendable {
    func findText(peer: DeviceId, id: MessageId) async -> TextRecord?
    func appendText(_ record: TextRecord) async -> StoreResult
    func updateTextStatus(id: MessageId, peer: DeviceId, status: TextStatus) async -> StoreResult
    func upsertFile(_ record: FileRecord) async -> StoreResult
    func list(limit: Int) async -> Result<[HistoryRecord], IoError>
    func delete(_ id: RecordId) async -> StoreResult
    func deleteFileRecord(transferId: TransferId, index: Int) async -> StoreResult
    func clearAll() async -> StoreResult
}

public protocol TaskStore: Sendable {
    func save(_ task: TransferTask) async -> StoreResult
    func get(_ id: TransferId) async -> TransferTask?
    func unfinished() async -> [TransferTask]
    func addPendingCancel(peer: DeviceId, transferId: TransferId) async -> StoreResult
    func takePendingCancels(peer: DeviceId) async -> [TransferId]
}

public protocol SettingsStore: Sendable {
    func current() async -> Settings
    func update(_ transform: @Sendable (Settings) -> Settings) async -> StoreResult
}

// MARK: - C5.4 系统服务端口

public enum ClipRead: Hashable, Sendable {
    case text(String)
    case noText
    case failed
}

public enum ClipWrite: Hashable, Sendable {
    case ok
    case failed
}

public protocol ClipboardPort: Sendable {
    func readText() async -> ClipRead
    func writeText(_ text: String) async -> ClipWrite
}

public enum LocateResult: Hashable, Sendable {
    case ok
    case notFound
    case noHandler
    case noFileManager
    case noAccess
}

public protocol FileLocator: Sendable {
    func openFile(_ ref: StoredFileRef, mime: String) async -> LocateResult
    func revealInFolder(_ ref: StoredFileRef) async -> LocateResult
    func openDirectory(_ dir: DirRef) async -> LocateResult
}

public enum NotifyKey: Hashable, Sendable {
    case text(peer: DeviceId, messageId: MessageId)
    case pairingRequest(peer: DeviceId)
    case transfer(transferId: TransferId)
    case file(transferId: TransferId, index: Int)
}

public enum RequestKind: Hashable, Sendable {
    case pairing
    case fileOffer
}

public protocol Notifier: Sendable {
    func isEnabled() async -> Bool
    func notifyText(key: NotifyKey, peerName: String, preview: String) async
    func notifyFileCompleted(key: NotifyKey, peerName: String, fileName: String, file: StoredFileRef) async
    func notifyRequest(key: NotifyKey, peerName: String, kind: RequestKind, fileCount: Int?) async
    func notifyProblem(key: NotifyKey, peerName: String, fileName: String?, status: String) async
    func cancel(key: NotifyKey) async
}

public protocol AppLifecycle: Sendable {
    func isInBackground() async -> Bool
    func setTransferActive(_ active: Bool) async
}

public protocol ClockPort: Sendable {
    func monotonicMs() -> Int64
    func wallMs() -> Int64
    func localNow() -> LocalDateTime
    func tzOffsetMinutes() -> Int
    func sleep(milliseconds: Int64) async throws
}

/// 生产环境系统时钟。
public struct SystemClock: ClockPort, Sendable {
    public init() {}

    public func monotonicMs() -> Int64 {
        Int64(ProcessInfo.processInfo.systemUptime * 1000.0)
    }

    public func wallMs() -> Int64 {
        Int64(Date().timeIntervalSince1970 * 1000.0)
    }

    public func localNow() -> LocalDateTime {
        LocalDateTime.from(date: Date())
    }

    public func tzOffsetMinutes() -> Int {
        TimeZone.current.secondsFromGMT() / 60
    }

    public func sleep(milliseconds: Int64) async throws {
        guard milliseconds > 0 else { return }
        try await Task.sleep(nanoseconds: UInt64(milliseconds) * 1_000_000)
    }
}

/// 生产环境密码学安全随机数生成器（Apple SecRandomCopyBytes）。
public struct SystemRng: RngPort, Sendable {
    public init() {}

    public func nextBytes(count: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        let status = SecRandomCopyBytes(kSecRandomDefault, count, &bytes)
        if status == errSecSuccess {
            return Data(bytes)
        }
        var gen = SystemRandomNumberGenerator()
        for i in 0..<count {
            bytes[i] = UInt8.random(in: 0...255, using: &gen)
        }
        return Data(bytes)
    }
}

/// 内存发出文本缓存（超长转存或重发兜底）。
public actor InMemoryOutgoingTextCache: OutgoingTextCache {
    private var entries: [TextKey: Data] = [:]

    public init() {}

    public func put(peer: DeviceId, messageId: MessageId, utf8: Data) {
        entries[TextKey(peer: peer, messageId: messageId)] = utf8
    }

    public func get(peer: DeviceId, messageId: MessageId) -> Data? {
        entries[TextKey(peer: peer, messageId: messageId)]
    }

    public func remove(peer: DeviceId, messageId: MessageId) {
        entries.removeValue(forKey: TextKey(peer: peer, messageId: messageId))
    }

    public func clear() {
        entries.removeAll()
    }
}
