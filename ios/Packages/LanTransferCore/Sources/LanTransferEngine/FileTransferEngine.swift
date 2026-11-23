import CryptoKit
import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 接收待确认文件任务项（多个并发 Offer 排队展示）。
public struct PendingIncomingOffer: Identifiable, Hashable, Sendable {
    public var id: TransferId { transferId }
    public let transferId: TransferId
    public let peer: DeviceId
    public let peerName: String
    public let files: [FileEntry]
    public let totalSize: Int64

    public init(transferId: TransferId, peer: DeviceId, peerName: String, files: [FileEntry], totalSize: Int64) {
        self.transferId = transferId
        self.peer = peer
        self.peerName = peerName
        self.files = files
        self.totalSize = totalSize
    }
}

public enum SendFilesResult: Hashable, Sendable {
    case started(TransferId)
    case empty
    case tooManyFiles
    case foldersNotSupported
    case unreadable(detail: String)
    case connectFailed(OpenFailure)
}

/// 文件传输核心引擎（actor）：驱动 `TransferReducer`，负责 64 KiB 分片流式读写、SHA-256 校验、断点续传、取消清理与防熄屏管理。
public actor FileTransferEngine {
    private static let ackStepBytes: Int64 = 256 * 1024
    private static let ackIntervalMs: Int64 = 200

    private let sessionManager: SessionManager
    private let receiveStorage: any ReceiveStorage
    private let sourceFiles: any SourceFiles
    private let taskStore: any TaskStore
    private let trustStore: any TrustStore
    private let settingsStore: any SettingsStore
    private let notifier: any Notifier
    private let lifecycle: any AppLifecycle
    private let clock: any ClockPort
    private let rng: any RngPort

    private var tasks: [TransferId: TransferTask] = [:]
    private var incomingOfferQueue: [PendingIncomingOffer] = []
    private var progressCalculators: [TransferId: ProgressCalculator] = [:]
    private var progressSnapshots: [TransferId: TransferProgress] = [:]
    private var offerTimers: [TransferId: Task<Void, Never>] = [:]
    private var sendLoops: [TransferId: Task<Void, Never>] = [:]
    private var openWriters: [TransferId: (fileIndex: Int, writer: any TempWriter, lastAckBytes: Int64, lastAckMono: Int64)] = [:]

    private var onTasksChanged: (@Sendable () async -> Void)?
    private var onNotice: (@Sendable (EngineNotice) async -> Void)?

    public init(
        sessionManager: SessionManager,
        receiveStorage: any ReceiveStorage,
        sourceFiles: any SourceFiles,
        taskStore: any TaskStore,
        trustStore: any TrustStore,
        settingsStore: any SettingsStore,
        notifier: any Notifier,
        lifecycle: any AppLifecycle,
        clock: any ClockPort,
        rng: any RngPort
    ) {
        self.sessionManager = sessionManager
        self.receiveStorage = receiveStorage
        self.sourceFiles = sourceFiles
        self.taskStore = taskStore
        self.trustStore = trustStore
        self.settingsStore = settingsStore
        self.notifier = notifier
        self.lifecycle = lifecycle
        self.clock = clock
        self.rng = rng
    }

    public func configure(
        onTasksChanged: @escaping @Sendable () async -> Void,
        onNotice: @escaping @Sendable (EngineNotice) async -> Void
    ) {
        self.onTasksChanged = onTasksChanged
        self.onNotice = onNotice
    }

    public func allTasks() -> [TransferTask] {
        Array(tasks.values).sorted { $0.recordedAt.utcMs > $1.recordedAt.utcMs }
    }

    public func currentIncomingOffers() -> [PendingIncomingOffer] {
        incomingOfferQueue
    }

    public func currentProgress() -> [TransferId: TransferProgress] {
        progressSnapshots
    }

    public func unfinishedCount() -> Int {
        tasks.values.filter { !$0.state.isTerminal }.count
    }

    /// 启动恢复：把上次未正常结束的 RUNNING/RESUMING 转为 INTERRUPTED(APP_KILLED)，并清理超过 24 h 的孤立临时文件。
    public func restoreAndCleanup() async {
        let loaded = await taskStore.unfinished()
        for t in loaded {
            let step = TransferReducer.reduce(t, .restored)
            var updated = step.state
            updated.updatedAtUtcMs = clock.wallMs()
            tasks[updated.transferId] = updated
            if updated.state != t.state {
                _ = await taskStore.save(updated)
            }
        }
        let activeIds = Set(tasks.values.filter { $0.state.isActive }.map(\.transferId))
        let temps = await receiveStorage.listTemps()
        let expired = TempFileSelector.select(temps: temps, active: activeIds, nowWall: clock.wallMs())
        for t in expired {
            await receiveStorage.deleteTemp(t.ref)
        }
        await syncActiveTransferLifecycle()
        await onTasksChanged?()
    }

    /// 发起多文件发送任务。
    public func sendFiles(peer: DeviceId, sources: [SourceRef]) async -> SendFilesResult {
        guard !sources.isEmpty else { return .empty }
        guard sources.count <= ProtocolConstants.maxFilesPerOffer else { return .tooManyFiles }

        var metas: [(SourceRef, SourceMeta)] = []
        for ref in sources {
            switch await sourceFiles.stat(ref) {
            case .failure(let err):
                return .unreadable(detail: err.detail)
            case .success(let meta):
                if meta.isDirectory {
                    return .foldersNotSupported
                }
                metas.append((ref, meta))
            }
        }

        let openRes = await sessionManager.open(target: .device(peer), forcePair: false)
        let session: ActiveSession
        switch openRes {
        case .ok(let s):
            session = s
        case .failed(let f):
            if case .notTrusted(let p) = f {
                await onNotice?(.peerUnpaired(p))
            }
            return .connectFailed(f)
        case .pairing:
            return .connectFailed(.notTrusted(peer: peer))
        }

        let tid = TransferId(bytes: rng.nextBytes(count: 16))!
        let nowWall = clock.wallMs()
        let recTime = RecordTime(utcMs: nowWall, offsetMin: clock.tzOffsetMinutes())
        let transferFiles = metas.enumerated().map { idx, pair in
            TransferFile(
                transferId: tid,
                index: idx,
                seq: 0,
                originalName: pair.1.name,
                size: pair.1.size,
                mime: pair.1.mime,
                mtime: pair.1.mtime,
                state: .waiting,
                sourceRef: pair.0
            )
        }
        let task = TransferTask(
            transferId: tid,
            peer: peer,
            peerName: session.peer.displayName,
            direction: .send,
            state: .offering,
            recordedAt: recTime,
            updatedAtUtcMs: nowWall,
            files: transferFiles
        )
        tasks[tid] = task
        _ = await taskStore.save(task)

        let wireEntries = metas.map { FileEntry(name: $0.1.name, size: UInt64($0.1.size), mime: $0.1.mime, mtime: $0.1.mtime) }
        await session.connection.send(.fileOffer(transferId: tid, files: wireEntries), priority: .control)
        startOfferTimer(transferId: tid, ms: TransferReducer.senderOfferTimeoutMs)
        await onTasksChanged?()
        return .started(tid)
    }

    public func respondToOffer(transferId: TransferId, accept: Bool) async {
        incomingOfferQueue.removeAll { $0.transferId == transferId }
        await notifier.cancel(key: .transfer(transferId: transferId))
        guard tasks[transferId] != nil else { return }
        if accept {
            let dir = await settingsStore.current().receiveDir
            let probeStatus = await receiveStorage.probe(dir)
            let usable: Bool
            if probeStatus == .usable {
                usable = await receiveStorage.ensureDirectory(dir)
            } else {
                usable = false
            }
            await apply(transferId: transferId, event: .userAccepted(dir: dir, dirUsable: usable))
        } else {
            await apply(transferId: transferId, event: .userRejected)
        }
    }

    public func cancelTask(transferId: TransferId, reason: TransferCancelReason = .user) async {
        incomingOfferQueue.removeAll { $0.transferId == transferId }
        guard let task = tasks[transferId] else { return }
        let hasSession = await sessionManager.session(for: task.peer) != nil
        await apply(transferId: transferId, event: .localCancel(sessionActive: hasSession, reason: reason))
    }

    public func cancelAllForPeer(_ peer: DeviceId, reason: TransferCancelReason) async {
        let matching = tasks.values.filter { $0.peer == peer && !$0.state.isTerminal }.map(\.transferId)
        for tid in matching {
            await cancelTask(transferId: tid, reason: reason)
        }
    }

    /// 断点续传：重新建连后交换 `RESUME_REQUEST` -> `RESUME_STATE` -> `RESUME_PLAN`。
    public func resumeTask(transferId: TransferId) async {
        guard let task = tasks[transferId], task.state == .interrupted else { return }
        await apply(transferId: transferId, event: .resumeRequested)

        let openRes = await sessionManager.open(target: .device(task.peer), forcePair: false)
        switch openRes {
        case .ok(let session):
            if task.direction == .send {
                await session.connection.send(.resumeRequest(transferId: transferId), priority: .control)
            } else {
                let states = await buildResumeStates(for: task)
                await session.connection.send(.resumeState(transferId: transferId, files: states), priority: .control)
            }
        case .failed:
            await apply(transferId: transferId, event: .resumeFailed(reason: .peerUnresponsive))
        case .pairing:
            await apply(transferId: transferId, event: .resumeFailed(reason: .peerClosed))
        }
    }

    /// 会话建立后主动投递离线期间缓存的取消通知。
    public func handleSessionOpened(_ session: ActiveSession) async {
        let cancels = await taskStore.takePendingCancels(peer: session.peer.deviceId)
        for tid in cancels {
            await session.connection.send(.transferCancel(transferId: tid, reason: .user), priority: .control)
        }
    }

    /// 会话断开时将关联进行中任务转入已中断并保留断点。
    public func handleSessionClosed(peer: DeviceId, reason: CloseReason) async {
        guard let interruptReason = reason.toInterruptReason() else { return }
        let affected = tasks.values.filter { $0.peer == peer && !$0.state.isTerminal }.map(\.transferId)
        for tid in affected {
            await closeWriter(for: tid)
            incomingOfferQueue.removeAll { $0.transferId == tid }
            await apply(transferId: tid, event: .disconnected(reason: interruptReason))
        }
    }

    /// 处理会话上收到的全部文件协议消息。
    public func handleInbound(peer: DeviceId, message: ProtocolMessage) async {
        switch message {
        case .fileOffer(let tid, let files):
            await handleFileOffer(peer: peer, transferId: tid, files: files)
        case .fileOfferReply(let tid, let decision):
            await apply(transferId: tid, event: .offerReplied(decision))
        case .fileChunk(let tid, let fileIndex, let offset, let data):
            await handleFileChunk(transferId: tid, fileIndex: Int(fileIndex), offset: Int64(offset), data: data)
        case .fileEnd(let tid, let fileIndex, let sha256):
            await closeWriter(for: tid)
            await apply(transferId: tid, event: .endReceived(index: Int(fileIndex), sha256: sha256, localNow: clock.localNow()))
        case .fileAck(let tid, let fileIndex, let persisted):
            await apply(transferId: tid, event: .ackReceived(index: Int(fileIndex), persisted: Int64(persisted)))
            updateProgress(transferId: tid)
        case .fileResult(let tid, let fileIndex, let status, let detail):
            await apply(transferId: tid, event: .resultReceived(index: Int(fileIndex), status: status, detail: detail))
            updateProgress(transferId: tid)
        case .fileReadFailed(let tid, let fileIndex):
            await closeWriter(for: tid)
            await apply(transferId: tid, event: .peerReadFailed(index: Int(fileIndex)))
        case .transferCancel(let tid, let reason):
            await closeWriter(for: tid)
            incomingOfferQueue.removeAll { $0.transferId == tid }
            await apply(transferId: tid, event: .peerCancelled(reason: reason))
        case .resumeRequest(let tid):
            await handleResumeRequest(peer: peer, transferId: tid)
        case .resumeState(let tid, let fileStates):
            await handleResumeState(peer: peer, transferId: tid, states: fileStates)
        case .resumePlan(let tid, let wirePlans):
            let entries = wirePlans.map {
                ResumePlanEntry(
                    index: Int($0.fileIndex),
                    action: $0.action,
                    start: Int64($0.start),
                    size: $0.size.map(Int64.init),
                    mtime: $0.mtime
                )
            }
            if tasks[tid]?.state == .interrupted {
                await apply(transferId: tid, event: .resumeRequested)
            }
            await apply(transferId: tid, event: .resumePlanned(entries))
        default:
            break
        }
    }

    // MARK: - Inbound Handlers

    private func handleFileOffer(peer: DeviceId, transferId: TransferId, files: [FileEntry]) async {
        guard let session = await sessionManager.session(for: peer) else { return }
        let settings = await settingsStore.current()
        let trusted = await trustStore.get(peer)
        let dir = settings.receiveDir
        let dirStatus = await receiveStorage.probe(dir)
        let freeSpace = await receiveStorage.freeSpace(dir)
        let decision = OfferPrecheck.decide(files: files, dir: dirStatus, freeBytes: freeSpace, autoAccept: trusted?.autoAccept ?? false)

        switch decision {
        case .reject(let reply):
            await session.connection.send(.fileOfferReply(transferId: transferId, decision: reply), priority: .control)
            return
        case .autoAccept, .askUser:
            let nowWall = clock.wallMs()
            let recTime = RecordTime(utcMs: nowWall, offsetMin: clock.tzOffsetMinutes())
            let transferFiles = files.enumerated().map { idx, f in
                TransferFile(
                    transferId: transferId,
                    index: idx,
                    seq: 0,
                    originalName: f.name,
                    size: Int64(f.size),
                    mime: f.mime,
                    mtime: f.mtime,
                    state: .waiting
                )
            }
            let task = TransferTask(
                transferId: transferId,
                peer: peer,
                peerName: session.peer.displayName,
                direction: .receive,
                state: .awaitingUser,
                receiveDir: dir,
                recordedAt: recTime,
                updatedAtUtcMs: nowWall,
                files: transferFiles
            )
            tasks[transferId] = task
            _ = await taskStore.save(task)

            if case .autoAccept = decision {
                let ensured = await receiveStorage.ensureDirectory(dir)
                await apply(transferId: transferId, event: .userAccepted(dir: dir, dirUsable: ensured))
            } else {
                let offer = PendingIncomingOffer(
                    transferId: transferId,
                    peer: peer,
                    peerName: session.peer.displayName,
                    files: files,
                    totalSize: OfferPrecheck.totalSize(files: files)
                )
                incomingOfferQueue.append(offer)
                startOfferTimer(transferId: transferId, ms: TransferReducer.receiverOfferTimeoutMs)
                if await lifecycle.isInBackground() {
                    await notifier.notifyRequest(
                        key: .transfer(transferId: transferId),
                        peerName: session.peer.displayName,
                        kind: .fileOffer,
                        fileCount: files.count
                    )
                }
                await onTasksChanged?()
            }
        }
    }

    private func handleFileChunk(transferId: TransferId, fileIndex: Int, offset: Int64, data: Data) async {
        guard var task = tasks[transferId],
              task.direction == .receive,
              task.state == .running,
              let pos = task.files.firstIndex(where: { $0.index == fileIndex }) else {
            return
        }
        let dir = task.receiveDir ?? .default
        let tempRef: TempRef
        if let existing = task.files[pos].tempRef {
            tempRef = existing
        } else {
            let created = await receiveStorage.createTemp(dir: dir, task: transferId, index: fileIndex)
            task.files[pos].tempRef = created
            tasks[transferId] = task
            tempRef = created
        }

        let writer: any TempWriter
        var lastAckBytes: Int64
        var lastAckMono: Int64
        if let open = openWriters[transferId], open.fileIndex == fileIndex {
            writer = open.writer
            lastAckBytes = open.lastAckBytes
            lastAckMono = open.lastAckMono
        } else {
            await closeWriter(for: transferId)
            switch await receiveStorage.openTempForWrite(tempRef, offset: offset) {
            case .failure(let err):
                await apply(transferId: transferId, event: .writeFailed(index: fileIndex, detail: err.detail))
                return
            case .success(let w):
                writer = w
                lastAckBytes = offset
                lastAckMono = clock.monotonicMs()
            }
        }

        if case .failed(let detail) = await writer.write(data) {
            await closeWriter(for: transferId)
            await apply(transferId: transferId, event: .writeFailed(index: fileIndex, detail: detail))
            return
        }

        let newPersisted = offset + Int64(data.count)
        let nowMono = clock.monotonicMs()
        if (newPersisted - lastAckBytes) >= Self.ackStepBytes || (nowMono - lastAckMono) >= Self.ackIntervalMs {
            if let session = await sessionManager.session(for: task.peer) {
                await session.connection.send(
                    .fileAck(transferId: transferId, fileIndex: UInt32(fileIndex), persisted: UInt64(newPersisted)),
                    priority: .control
                )
            }
            lastAckBytes = newPersisted
            lastAckMono = nowMono
        }
        openWriters[transferId] = (fileIndex: fileIndex, writer: writer, lastAckBytes: lastAckBytes, lastAckMono: lastAckMono)

        await apply(
            transferId: transferId,
            event: .chunkWritten(index: fileIndex, persisted: newPersisted, localNow: clock.localNow(), nowWall: clock.wallMs())
        )
        updateProgress(transferId: transferId)
    }

    private func handleResumeRequest(peer: DeviceId, transferId: TransferId) async {
        guard let session = await sessionManager.session(for: peer) else { return }
        guard let task = tasks[transferId], !task.state.isTerminal, task.direction == .receive else {
            await session.connection.send(.transferCancel(transferId: transferId, reason: .unknownTransfer), priority: .control)
            return
        }
        if task.state == .interrupted {
            await apply(transferId: transferId, event: .resumeRequested)
        }
        let states = await buildResumeStates(for: task)
        await session.connection.send(.resumeState(transferId: transferId, files: states), priority: .control)
    }

    private func handleResumeState(peer: DeviceId, transferId: TransferId, states: [ResumeFileState]) async {
        guard let session = await sessionManager.session(for: peer) else { return }
        guard let task = tasks[transferId], !task.state.isTerminal, task.direction == .send else {
            await session.connection.send(.transferCancel(transferId: transferId, reason: .unknownTransfer), priority: .control)
            return
        }
        if task.state == .interrupted {
            await apply(transferId: transferId, event: .resumeRequested)
        }
        var stats: [SourceStat?] = []
        stats.reserveCapacity(task.files.count)
        for f in task.files {
            if let ref = f.sourceRef, case .success(let meta) = await sourceFiles.stat(ref) {
                stats.append(SourceStat(size: meta.size, mtime: meta.mtime))
            } else {
                stats.append(nil)
            }
        }
        let plan = ResumePlanner.plan(task: task, state: states, stats: stats)
        await apply(transferId: transferId, event: .resumePlanned(plan))
    }

    private func buildResumeStates(for task: TransferTask) async -> [ResumeFileState] {
        var result: [ResumeFileState] = []
        for f in task.files {
            let wireStatus: ResumeFileStatus
            let persisted: UInt64
            if f.state == .completed {
                wireStatus = .completed
                persisted = UInt64(f.size)
            } else if f.state.isTerminal {
                wireStatus = .terminal
                persisted = 0
            } else if let ref = f.tempRef, let tempLen = await receiveStorage.tempSize(ref), tempLen > 0 {
                wireStatus = .partial
                persisted = UInt64(tempLen)
            } else {
                wireStatus = .none
                persisted = 0
            }
            result.append(ResumeFileState(fileIndex: UInt32(f.index), state: wireStatus, persisted: persisted))
        }
        return result
    }

    // MARK: - Reducer & Effect Execution

    private func apply(transferId: TransferId, event: TransferEvent) async {
        guard let existing = tasks[transferId] else { return }
        let step = TransferReducer.reduce(existing, event)
        var updated = step.state
        updated.updatedAtUtcMs = clock.wallMs()
        tasks[transferId] = updated

        if updated.state == .running && existing.state != .running {
            var calc = ProgressCalculator()
            calc.reset(nowMono: clock.monotonicMs(), transferred: totalTransferred(updated))
            progressCalculators[transferId] = calc
        }
        if updated.state.isTerminal {
            await closeWriter(for: transferId)
            sendLoops.removeValue(forKey: transferId)?.cancel()
            offerTimers.removeValue(forKey: transferId)?.cancel()
            progressSnapshots.removeValue(forKey: transferId)
            progressCalculators.removeValue(forKey: transferId)
        }

        if case .failed(let detail) = await taskStore.save(updated) {
            await onNotice?(.storeFailed(detail: detail))
        }

        for fx in step.effects {
            await execute(transferId: transferId, task: updated, effect: fx)
        }
        await syncActiveTransferLifecycle()
        await onTasksChanged?()
    }

    private func execute(transferId: TransferId, task: TransferTask, effect: TransferEffect) async {
        switch effect {
        case .send(let msg):
            if let session = await sessionManager.session(for: task.peer) {
                await session.connection.send(msg, priority: .control)
            }
        case .startFile(let index, let offset):
            sendLoops[transferId]?.cancel()
            sendLoops[transferId] = Task { [weak self] in
                await self?.streamFile(transferId: transferId, fileIndex: index, offset: offset)
            }
        case .stopSending:
            sendLoops.removeValue(forKey: transferId)?.cancel()
        case .verify(let index, let expectedSha256):
            Task { [weak self] in
                await self?.verifyAndCommit(transferId: transferId, fileIndex: index, expectedSha256: expectedSha256)
            }
        case .deleteTemp(let index):
            if let f = task.files.first(where: { $0.index == index }), let ref = f.tempRef {
                await receiveStorage.deleteTemp(ref)
            }
        case .addPendingCancel:
            _ = await taskStore.addPendingCancel(peer: task.peer, transferId: transferId)
        case .startOfferTimer(let ms):
            startOfferTimer(transferId: transferId, ms: ms)
        case .cancelOfferTimer:
            offerTimers.removeValue(forKey: transferId)?.cancel()
        case .startResume, .abortResume:
            break
        case .protocolViolation:
            await sessionManager.closeSession(peer: task.peer, reason: .normal)
        }
    }

    private func streamFile(transferId: TransferId, fileIndex: Int, offset: Int64) async {
        guard let task = tasks[transferId],
              let file = task.files.first(where: { $0.index == fileIndex }),
              let sourceRef = file.sourceRef,
              let session = await sessionManager.session(for: task.peer) else {
            await apply(transferId: transferId, event: .sourceReadFailed(index: fileIndex))
            return
        }

        var hasher = SHA256()
        // 续传时先对前缀 [0, offset) 计算增量 SHA-256
        if offset > 0 {
            guard case .success(let prefixReader) = await sourceFiles.openForRead(sourceRef, offset: 0) else {
                await apply(transferId: transferId, event: .sourceReadFailed(index: fileIndex))
                return
            }
            var remaining = offset
            while remaining > 0 && !Task.isCancelled {
                let toRead = Int(min(Int64(ProtocolConstants.maxChunk), remaining))
                switch await prefixReader.read(maxBytes: toRead) {
                case .success(let chunk?):
                    hasher.update(data: chunk)
                    remaining -= Int64(chunk.count)
                case .success(nil), .failure:
                    await prefixReader.close()
                    await apply(transferId: transferId, event: .sourceReadFailed(index: fileIndex))
                    return
                }
            }
            await prefixReader.close()
        }

        guard case .success(let reader) = await sourceFiles.openForRead(sourceRef, offset: offset) else {
            await apply(transferId: transferId, event: .sourceReadFailed(index: fileIndex))
            return
        }
        defer { Task { await reader.close() } }

        var cursor = offset
        while !Task.isCancelled {
            switch await reader.read(maxBytes: ProtocolConstants.maxChunk) {
            case .failure:
                await apply(transferId: transferId, event: .sourceReadFailed(index: fileIndex))
                return
            case .success(nil):
                let digest = Data(hasher.finalize())
                await session.connection.send(
                    .fileEnd(transferId: transferId, fileIndex: UInt32(fileIndex), sha256: digest),
                    priority: .fileChunk
                )
                await apply(transferId: transferId, event: .fileEndSent(index: fileIndex, sha256: digest))
                return
            case .success(let chunk?):
                if chunk.isEmpty { continue }
                hasher.update(data: chunk)
                await session.connection.send(
                    .fileChunk(transferId: transferId, fileIndex: UInt32(fileIndex), offset: UInt64(cursor), data: chunk),
                    priority: .fileChunk
                )
                cursor += Int64(chunk.count)
                await Task.yield()
            }
        }
    }

    private func verifyAndCommit(transferId: TransferId, fileIndex: Int, expectedSha256: Data) async {
        guard let task = tasks[transferId],
              let file = task.files.first(where: { $0.index == fileIndex }) else {
            return
        }
        let dir = task.receiveDir ?? .default
        let tempRef: TempRef
        if let ref = file.tempRef {
            tempRef = ref
        } else {
            // 0 字节文件尚未收到任何 chunk，直接创建空临时文件
            tempRef = await receiveStorage.createTemp(dir: dir, task: transferId, index: fileIndex)
            if case .success(let w) = await receiveStorage.openTempForWrite(tempRef, offset: 0) {
                _ = await w.flushSync()
                await w.close()
            }
        }

        guard case .success(let reader) = await receiveStorage.openTempForRead(tempRef) else {
            await apply(transferId: transferId, event: .verifyDone(index: fileIndex, outcome: .commitFailed(detail: "无法读取临时文件")))
            return
        }

        var hasher = SHA256()
        var actualSize: Int64 = 0
        while true {
            switch await reader.read(maxBytes: ProtocolConstants.maxChunk) {
            case .failure(let err):
                await reader.close()
                await apply(transferId: transferId, event: .verifyDone(index: fileIndex, outcome: .commitFailed(detail: err.detail)))
                return
            case .success(nil):
                break
            case .success(let chunk?):
                hasher.update(data: chunk)
                actualSize += Int64(chunk.count)
                continue
            }
            break
        }
        await reader.close()

        let actualHash = Data(hasher.finalize())
        guard actualSize == file.size && actualHash == expectedSha256 else {
            await apply(transferId: transferId, event: .verifyDone(index: fileIndex, outcome: .checksumMismatch))
            return
        }

        let started = file.startedAt ?? clock.localNow()
        let sanitized = FileNameSanitizer.sanitize(file.originalName, profile: receiveStorage.fsProfile, now: started)
        let finalName = await resolveCollision(name: sanitized, dir: dir)

        switch await receiveStorage.commitTemp(tempRef, dir: dir, finalName: finalName, mtime: file.mtime) {
        case .failed(let detail):
            await apply(transferId: transferId, event: .verifyDone(index: fileIndex, outcome: .commitFailed(detail: detail)))
        case .ok(let committedName, let fullPath, let storedRef):
            await apply(
                transferId: transferId,
                event: .verifyDone(index: fileIndex, outcome: .committed(finalName: committedName, fullPath: fullPath, storedRef: storedRef))
            )
            if await lifecycle.isInBackground() {
                await notifier.notifyFileCompleted(
                    key: .file(transferId: transferId, index: fileIndex),
                    peerName: task.peerName,
                    fileName: committedName,
                    file: storedRef
                )
            }
        }
    }

    private func resolveCollision(name: String, dir: DirRef) async -> String {
        if !(await receiveStorage.exists(dir, name: name)) {
            return name
        }
        let profile = receiveStorage.fsProfile
        var n = 1
        while true {
            let probeName = NameCollisionResolver.resolve(name, profile: profile, startAt: n) { $0 == name }
            if !(await receiveStorage.exists(dir, name: probeName)) {
                return probeName
            }
            n += 1
        }
    }

    private func startOfferTimer(transferId: TransferId, ms: Int64) {
        offerTimers[transferId]?.cancel()
        offerTimers[transferId] = Task { [weak self] in
            do {
                try await self?.clock.sleep(milliseconds: ms)
                await self?.handleOfferTimeout(transferId: transferId)
            } catch {}
        }
    }

    private func handleOfferTimeout(transferId: TransferId) async {
        incomingOfferQueue.removeAll { $0.transferId == transferId }
        await apply(transferId: transferId, event: .offerTimedOut)
    }

    private func closeWriter(for transferId: TransferId) async {
        if let open = openWriters.removeValue(forKey: transferId) {
            _ = await open.writer.flushSync()
            await open.writer.close()
        }
    }

    private func totalTransferred(_ task: TransferTask) -> Int64 {
        task.files.reduce(Int64(0)) { acc, f in
            acc + (f.state == .completed ? f.size : f.persisted)
        }
    }

    private func totalSize(_ task: TransferTask) -> Int64 {
        task.files.reduce(Int64(0)) { $0 + $1.size }
    }

    private func updateProgress(transferId: TransferId) {
        guard let task = tasks[transferId], task.state == .running else { return }
        let now = clock.monotonicMs()
        let transferred = totalTransferred(task)
        var calc = progressCalculators[transferId] ?? ProgressCalculator()
        calc.sample(nowMono: now, transferred: transferred)
        progressCalculators[transferId] = calc
        progressSnapshots[transferId] = calc.snapshot(nowMono: now, total: totalSize(task), completed: task.state == .completed)
    }

    private func syncActiveTransferLifecycle() async {
        let hasActive = tasks.values.contains { $0.state.isActive }
        await lifecycle.setTransferActive(hasActive)
    }
}
