import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol

/// 内存 TCP 管道（用于无真实套接字的引擎端到端互通测试）。
public actor InMemoryPipe {
    private var chunks: [Data] = []
    private var waiter: CheckedContinuation<Data?, Never>?
    private var closed = false

    public init() {}

    public func push(_ data: Data) -> Bool {
        if closed { return false }
        if data.isEmpty { return true }
        if let w = waiter {
            waiter = nil
            w.resume(returning: data)
        } else {
            chunks.append(data)
        }
        return true
    }

    public func pop(maxBytes: Int) async -> Data? {
        if !chunks.isEmpty {
            let first = chunks.removeFirst()
            if first.count <= maxBytes {
                return first
            }
            let head = Data(first.prefix(maxBytes))
            let tail = Data(first.dropFirst(maxBytes))
            chunks.insert(tail, at: 0)
            return head
        }
        if closed { return nil }
        return await withCheckedContinuation { cont in
            self.waiter = cont
        }
    }

    public func close() {
        closed = true
        if let w = waiter {
            waiter = nil
            w.resume(returning: nil)
        }
    }
}

public final class InMemoryTcpConn: TcpConn, @unchecked Sendable {
    public let local: SocketAddr
    public let remote: SocketAddr
    private let rx: InMemoryPipe
    private let tx: InMemoryPipe

    public init(local: SocketAddr, remote: SocketAddr, rx: InMemoryPipe, tx: InMemoryPipe) {
        self.local = local
        self.remote = remote
        self.rx = rx
        self.tx = tx
    }

    public static func pair(a: SocketAddr, b: SocketAddr) -> (InMemoryTcpConn, InMemoryTcpConn) {
        let aToB = InMemoryPipe()
        let bToA = InMemoryPipe()
        return (
            InMemoryTcpConn(local: a, remote: b, rx: bToA, tx: aToB),
            InMemoryTcpConn(local: b, remote: a, rx: aToB, tx: bToA)
        )
    }

    public func read(maxBytes: Int) async -> Data? {
        await rx.pop(maxBytes: maxBytes)
    }

    public func write(_ data: Data) async -> Bool {
        await tx.push(data)
    }

    public func close() async {
        await rx.close()
        await tx.close()
    }
}

public actor InMemoryTcpListener: TcpListener {
    public nonisolated let port: Int
    private var queue: [any TcpConn] = []
    private var waiter: CheckedContinuation<(any TcpConn)?, Never>?
    private var closed = false

    public init(port: Int) {
        self.port = port
    }

    public func enqueue(_ conn: any TcpConn) {
        guard !closed else { return }
        if let w = waiter {
            waiter = nil
            w.resume(returning: conn)
        } else {
            queue.append(conn)
        }
    }

    public func accept() async -> (any TcpConn)? {
        if !queue.isEmpty {
            return queue.removeFirst()
        }
        if closed { return nil }
        return await withCheckedContinuation { cont in
            self.waiter = cont
        }
    }

    public func close() {
        closed = true
        if let w = waiter {
            waiter = nil
            w.resume(returning: nil)
        }
    }
}

public actor InMemoryNetworkHub {
    private var listeners: [SocketAddr: InMemoryTcpListener] = [:]
    private var nextEphemeralPort = 50000

    public init() {}

    public func register(ip: Ipv4, candidatePorts: [Int]) -> InMemoryTcpListener? {
        for candidate in candidatePorts {
            let port: Int
            if candidate == 0 {
                port = nextEphemeralPort
                nextEphemeralPort += 1
            } else {
                port = candidate
            }
            let addr = SocketAddr(ip: ip, port: port)
            if listeners[addr] == nil {
                let listener = InMemoryTcpListener(port: port)
                listeners[addr] = listener
                return listener
            }
        }
        return nil
    }

    public func connect(from srcIp: Ipv4, to dst: SocketAddr) async -> ConnectResult {
        guard let listener = listeners[dst] else {
            return .refused
        }
        let clientPort = nextEphemeralPort
        nextEphemeralPort += 1
        let (client, server) = InMemoryTcpConn.pair(a: SocketAddr(ip: srcIp, port: clientPort), b: dst)
        await listener.enqueue(server)
        return .ok(client)
    }
}

public final class InMemoryTransport: Transport, @unchecked Sendable {
    private let hub: InMemoryNetworkHub
    private let primaryIp: Ipv4

    public init(hub: InMemoryNetworkHub, primaryIp: Ipv4) {
        self.hub = hub
        self.primaryIp = primaryIp
    }

    public func connect(via: LocalInterface, ip: Ipv4, port: Int, timeoutSeconds: TimeInterval) async -> ConnectResult {
        await hub.connect(from: via.address, to: SocketAddr(ip: ip, port: port))
    }

    public func listen(candidatePorts: [Int]) async -> (any TcpListener)? {
        await hub.register(ip: primaryIp, candidatePorts: candidatePorts)
    }

    public func openUdp(port: Int, multicastGroup: Ipv4, interfaces: [LocalInterface]) async -> UdpOpenResult {
        .failed(reason: "in-memory udp disabled")
    }
}

public actor FakeNetworkMonitor: NetworkMonitor {
    public nonisolated let interfacesStream: AsyncStream<[LocalInterface]>
    private let continuation: AsyncStream<[LocalInterface]>.Continuation
    private var interfaces: [LocalInterface]

    public init(interfaces: [LocalInterface]) {
        self.interfaces = interfaces
        let (stream, cont) = AsyncStream<[LocalInterface]>.makeStream()
        self.interfacesStream = stream
        self.continuation = cont
    }

    public func currentInterfaces() -> [LocalInterface] { interfaces }
    public func setFastPolling(_ enabled: Bool) {}

    public func setInterfaces(_ next: [LocalInterface]) {
        interfaces = next
        continuation.yield(next)
    }
}

public actor InMemoryIdentityStore: IdentityStore {
    private var identity: DeviceIdentity

    public init(identity: DeviceIdentity) {
        self.identity = identity
    }

    public func loadOrCreate() -> IdentityLoad { .loaded(identity) }
    public func displayName() -> String { identity.displayName }
    public func setDisplayName(_ name: String) -> StoreResult {
        identity.displayName = name
        return .ok
    }
}

public actor InMemoryTrustStore: TrustStore {
    private var devices: [DeviceId: TrustedDevice] = [:]

    public init() {}

    public func all() -> [TrustedDevice] { Array(devices.values) }
    public func get(_ id: DeviceId) -> TrustedDevice? { devices[id] }
    public func upsert(_ device: TrustedDevice) -> StoreResult {
        devices[device.deviceId] = device
        return .ok
    }
    public func delete(_ id: DeviceId) -> StoreResult {
        devices.removeValue(forKey: id)
        return .ok
    }
    public func setAutoAccept(_ id: DeviceId, enabled: Bool) -> StoreResult {
        devices[id]?.autoAccept = enabled
        return .ok
    }
    public func updateName(_ id: DeviceId, name: String) -> StoreResult {
        devices[id]?.displayName = name
        return .ok
    }
    public func recordAddress(_ id: DeviceId, ip: Ipv4, port: Int) -> StoreResult {
        devices[id]?.lastAddress = ip
        devices[id]?.lastPort = port
        return .ok
    }
}

public actor InMemoryHistoryAndTaskStore: HistoryStore, TaskStore {
    private var nextSeq: Int64 = 1
    private var texts: [TextRecord] = []
    private var tasks: [TransferId: TransferTask] = [:]
    private var pendingCancels: [DeviceId: [TransferId]] = [:]

    public init() {}

    public func findText(peer: DeviceId, id: MessageId) -> TextRecord? {
        texts.first { $0.peer == peer && $0.messageId == id }
    }

    public func appendText(_ record: TextRecord) -> StoreResult {
        if findText(peer: record.peer, id: record.messageId) != nil {
            return .duplicate
        }
        var copy = record
        copy.seq = nextSeq
        nextSeq += 1
        texts.append(copy)
        return .ok
    }

    public func updateTextStatus(id: MessageId, peer: DeviceId, status: TextStatus) -> StoreResult {
        if let idx = texts.firstIndex(where: { $0.peer == peer && $0.messageId == id }) {
            texts[idx].status = status
        }
        return .ok
    }

    public func upsertFile(_ record: FileRecord) -> StoreResult { .ok }

    public func list(limit: Int) -> Result<[HistoryRecord], IoError> {
        var records: [HistoryRecord] = texts.map { .text($0) }
        for task in tasks.values {
            for f in task.files {
                records.append(.file(FileRecord(
                    file: f,
                    peer: task.peer,
                    peerName: task.peerName,
                    direction: task.direction,
                    recordedAt: task.recordedAt,
                    taskState: task.state,
                    interrupt: task.interrupt
                )))
            }
        }
        records.sort {
            if $0.recordedAt.utcMs != $1.recordedAt.utcMs { return $0.recordedAt.utcMs > $1.recordedAt.utcMs }
            return $0.seq > $1.seq
        }
        return .success(Array(records.prefix(limit)))
    }

    public func delete(_ id: RecordId) -> StoreResult {
        texts.removeAll { $0.seq == id.seq }
        return .ok
    }

    public func deleteFileRecord(transferId: TransferId, index: Int) -> StoreResult {
        if var task = tasks[transferId] {
            task.files.removeAll { $0.index == index }
            if task.files.isEmpty {
                tasks.removeValue(forKey: transferId)
            } else {
                tasks[transferId] = task
            }
        }
        return .ok
    }

    public func clearAll() -> StoreResult {
        texts.removeAll()
        tasks.removeAll()
        return .ok
    }

    public func save(_ task: TransferTask) -> StoreResult {
        var copy = task
        let existingSeqs = Dictionary(uniqueKeysWithValues: (tasks[task.transferId]?.files ?? []).map { ($0.index, $0.seq) })
        for i in 0..<copy.files.count {
            if let oldSeq = existingSeqs[copy.files[i].index], oldSeq > 0 {
                copy.files[i].seq = oldSeq
            } else if copy.files[i].seq == 0 {
                copy.files[i].seq = nextSeq
                nextSeq += 1
            }
        }
        tasks[task.transferId] = copy
        return .ok
    }

    public func get(_ id: TransferId) -> TransferTask? { tasks[id] }
    public func unfinished() -> [TransferTask] { tasks.values.filter { !$0.state.isTerminal } }
    public func addPendingCancel(peer: DeviceId, transferId: TransferId) -> StoreResult {
        pendingCancels[peer, default: []].append(transferId)
        return .ok
    }
    public func takePendingCancels(peer: DeviceId) -> [TransferId] {
        pendingCancels.removeValue(forKey: peer) ?? []
    }
}

public actor InMemorySettingsStore: SettingsStore {
    private var settings: Settings
    public init(settings: Settings = Settings()) { self.settings = settings }
    public func current() -> Settings { settings }
    public func update(_ transform: @Sendable (Settings) -> Settings) -> StoreResult {
        settings = transform(settings)
        return .ok
    }
}

public actor InMemoryClipboard: ClipboardPort {
    public var text: String?
    public init() {}
    public func readText() -> ClipRead { text.map(ClipRead.text) ?? .noText }
    public func writeText(_ text: String) -> ClipWrite {
        self.text = text
        return .ok
    }
}

public actor InMemoryNotifier: Notifier {
    public var enabled = true
    public init() {}
    public func isEnabled() -> Bool { enabled }
    public func notifyText(key: NotifyKey, peerName: String, preview: String) {}
    public func notifyFileCompleted(key: NotifyKey, peerName: String, fileName: String, file: StoredFileRef) {}
    public func notifyRequest(key: NotifyKey, peerName: String, kind: RequestKind, fileCount: Int?) {}
    public func notifyProblem(key: NotifyKey, peerName: String, fileName: String?, status: String) {}
    public func cancel(key: NotifyKey) {}
}

public actor FakeAppLifecycle: AppLifecycle {
    public var background = false
    public var transferActive = false
    public init() {}
    public func isInBackground() -> Bool { background }
    public func setTransferActive(_ active: Bool) { transferActive = active }
}

public actor InMemoryReceiveStorage: ReceiveStorage {
    public nonisolated let fsProfile: FsProfile = .ios
    private var temps: [String: Data] = [:]
    private var committed: [String: Data] = [:]

    public init() {}

    public func committedFile(_ name: String) -> Data? { committed[name] }

    public func probe(_ dir: DirRef) -> DirStatus { .usable }
    public func ensureDirectory(_ dir: DirRef) -> Bool { true }
    public func isWritable(_ dir: DirRef) -> Bool { true }
    public func freeSpace(_ dir: DirRef) -> Int64? { 100 * 1024 * 1024 }
    public func exists(_ dir: DirRef, name: String) -> Bool { committed[name] != nil }

    public func createTemp(dir: DirRef, task: TransferId, index: Int) -> TempRef {
        let key = "\(task.hexString)-\(index)"
        if temps[key] == nil { temps[key] = Data() }
        return TempRef(key)
    }

    public func openTempForWrite(_ ref: TempRef, offset: Int64) -> Result<any TempWriter, IoError> {
        var existing = temps[ref.value] ?? Data()
        if Int(offset) < existing.count {
            existing = Data(existing.prefix(Int(offset)))
            temps[ref.value] = existing
        }
        return .success(InMemoryTempWriter(storage: self, key: ref.value))
    }

    public func openTempForRead(_ ref: TempRef) -> Result<any ChunkReader, IoError> {
        guard let data = temps[ref.value] else { return .failure(IoError("missing temp")) }
        return .success(InMemoryChunkReader(data: data))
    }

    public func tempSize(_ ref: TempRef) -> Int64? { temps[ref.value].map { Int64($0.count) } }
    public func deleteTemp(_ ref: TempRef) { temps.removeValue(forKey: ref.value) }
    public func listTemps() -> [TempInfo] { [] }

    public func commitTemp(_ ref: TempRef, dir: DirRef, finalName: String, mtime: Int64?) -> CommitResult {
        guard let data = temps.removeValue(forKey: ref.value) else {
            return .failed(detail: "missing temp")
        }
        committed[finalName] = data
        return .ok(finalName: finalName, fullPath: "/Documents/LanTransfer/\(finalName)", storedRef: StoredFileRef(finalName))
    }

    public func deleteStoredFile(_ ref: StoredFileRef) -> Bool {
        committed.removeValue(forKey: ref.value) != nil
    }

    fileprivate func appendTemp(key: String, data: Data) {
        var cur = temps[key] ?? Data()
        cur.append(data)
        temps[key] = cur
    }
}

private actor InMemoryTempWriter: TempWriter {
    private let storage: InMemoryReceiveStorage
    private let key: String
    init(storage: InMemoryReceiveStorage, key: String) {
        self.storage = storage
        self.key = key
    }
    func write(_ data: Data) async -> IoResult {
        await storage.appendTemp(key: key, data: data)
        return .ok
    }
    func flushSync() -> IoResult { .ok }
    func close() {}
}

public actor InMemoryChunkReader: ChunkReader {
    private let bytes: [UInt8]
    private var offset: Int

    public init(data: Data, offset: Int = 0) {
        self.bytes = [UInt8](data)
        self.offset = min(offset, bytes.count)
    }

    public func read(maxBytes: Int) -> Result<Data?, IoError> {
        if offset >= bytes.count { return .success(nil) }
        let take = min(maxBytes, bytes.count - offset)
        let slice = Data(bytes[offset..<(offset + take)])
        offset += take
        return .success(slice)
    }

    public func close() {}
}

public actor InMemorySourceFiles: SourceFiles {
    private var files: [String: (SourceMeta, Data)] = [:]

    public init() {}

    public func add(ref: SourceRef, name: String, data: Data, mime: String = SourceMeta.octetStream) {
        files[ref.value] = (SourceMeta(name: name, size: Int64(data.count), mtime: 1_700_000_000_000, mime: mime, isDirectory: false), data)
    }

    public func stat(_ ref: SourceRef) -> Result<SourceMeta, IoError> {
        guard let pair = files[ref.value] else { return .failure(IoError("not found")) }
        return .success(pair.0)
    }

    public func openForRead(_ ref: SourceRef, offset: Int64) -> Result<any ChunkReader, IoError> {
        guard let pair = files[ref.value] else { return .failure(IoError("not found")) }
        return .success(InMemoryChunkReader(data: pair.1, offset: Int(offset)))
    }
}
