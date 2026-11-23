import Foundation
import LanTransferProtocol

/// 一次 Issue 产生的 Pairing_Token（16 字节）。
public struct PairingToken: Hashable, Sendable {
    public let value: Data
    public let issuedAtMono: Int64

    public init(value: Data, issuedAtMono: Int64) {
        self.value = value
        self.issuedAtMono = issuedAtMono
    }
}

public struct TokenLease: Hashable, Sendable {
    internal let id: Int64
    internal init(id: Int64) { self.id = id }
}

public enum TokenCheck: Hashable, Sendable {
    case ok(TokenLease)
    case invalid
}

/// Pairing_Token 注册表（需求 8.6、8.7、8.9）：5 min 有效、1 次使用作废、5 次失败失效、常数时间比较。
public final class PairingTokenRegistry: @unchecked Sendable {
    public static let tokenSize = 16
    public static let lifetimeMs: Int64 = 5 * 60_000
    public static let maxFailures = 5

    private let lock = NSLock()
    private let rng: any RngPort
    private var current: Data?
    private var issuedAt: Int64 = 0
    private var failures: Int = 0
    private var used: Bool = false
    private var activeLease: Int64?
    private var nextLeaseId: Int64 = 1

    public init(rng: any RngPort) {
        self.rng = rng
    }

    public func issue(nowMono: Int64) -> PairingToken {
        lock.withLock {
            let v = rng.nextBytes(count: Self.tokenSize)
            precondition(v.count == Self.tokenSize, "RngPort returned \(v.count) bytes")
            current = v
            issuedAt = nowMono
            failures = 0
            used = false
            activeLease = nil
            return PairingToken(value: v, issuedAtMono: nowMono)
        }
    }

    public func check(token: Data, nowMono: Int64) -> TokenCheck {
        lock.withLock {
            let matches: Bool
            if let cur = current, cur.count == token.count {
                var diff: UInt8 = 0
                for (a, b) in zip(cur, token) {
                    diff |= a ^ b
                }
                matches = (diff == 0)
            } else {
                matches = false
            }
            if matches && activeLease == nil && isUsableLocked(nowMono: nowMono) {
                let lease = TokenLease(id: nextLeaseId)
                nextLeaseId += 1
                activeLease = lease.id
                return .ok(lease)
            }
            failures += 1
            return .invalid
        }
    }

    public func markUsed(_ lease: TokenLease) {
        lock.withLock {
            if activeLease == lease.id {
                used = true
                activeLease = nil
            }
        }
    }

    public func release(_ lease: TokenLease) {
        lock.withLock {
            if activeLease == lease.id {
                activeLease = nil
            }
        }
    }

    public func needsRefresh(nowMono: Int64) -> Bool {
        lock.withLock {
            current == nil || !isUsableLocked(nowMono: nowMono)
        }
    }

    private func isUsableLocked(nowMono: Int64) -> Bool {
        !used && failures < Self.maxFailures && (nowMono - issuedAt) < Self.lifetimeMs
    }
}

public struct PickedItem: Hashable, Sendable {
    public let ref: SourceRef
    public let isDirectory: Bool

    public init(ref: SourceRef, isDirectory: Bool) {
        self.ref = ref
        self.isDirectory = isDirectory
    }
}

public struct PendingAddResult: Hashable, Sendable {
    public let list: [SourceRef]
    public let foldersSkipped: Bool
    public let tooMany: Bool

    public init(list: [SourceRef], foldersSkipped: Bool, tooMany: Bool) {
        self.list = list
        self.foldersSkipped = foldersSkipped
        self.tooMany = tooMany
    }
}

/// 待发送列表规则（需求 14.1、14.8、14.9）。
public enum PendingListRules: Sendable {
    public static let maxFiles = ProtocolConstants.maxFilesPerOffer

    public static func add(current: [SourceRef], incoming: [PickedItem]) -> PendingAddResult {
        let files = incoming.filter { !$0.isDirectory }.map(\.ref)
        let foldersSkipped = files.count != incoming.count
        if current.count + files.count > maxFiles {
            return PendingAddResult(list: current, foldersSkipped: foldersSkipped, tooMany: true)
        }
        return PendingAddResult(list: current + files, foldersSkipped: foldersSkipped, tooMany: false)
    }
}

public enum PrecheckDecision: Hashable, Sendable {
    case reject(OfferDecision)
    case autoAccept
    case askUser
}

/// File_Offer 预检（需求 15.1、15.2、15.5、15.7）。
public enum OfferPrecheck: Sendable {
    public static func decide(files: [FileEntry], dir: DirStatus, freeBytes: Int64?, autoAccept: Bool) -> PrecheckDecision {
        guard isSemanticallyValid(files: files) else {
            return .reject(.invalidOffer)
        }
        guard dir == .usable else {
            return .reject(.cannotSave)
        }
        if let free = freeBytes, free < totalSize(files: files) {
            return .reject(.noSpace)
        }
        return autoAccept ? .autoAccept : .askUser
    }

    public static func isSemanticallyValid(files: [FileEntry]) -> Bool {
        guard files.count >= 1 && files.count <= ProtocolConstants.maxFilesPerOffer else {
            return false
        }
        return files.allSatisfy { $0.size <= UInt64(ProtocolConstants.maxFileSize) && isValidMime($0.mime) }
    }

    public static func isValidMime(_ mime: String) -> Bool {
        mime.utf8.count <= ProtocolConstants.maxMimeBytes
            && mime.unicodeScalars.allSatisfy { $0.value >= 0x20 && $0.value <= 0x7E }
    }

    public static func totalSize(files: [FileEntry]) -> Int64 {
        files.reduce(Int64(0)) { $0 + Int64($1.size) }
    }
}

public struct SourceStat: Hashable, Sendable {
    public let size: Int64
    public let mtime: Int64?

    public init(size: Int64, mtime: Int64?) {
        self.size = size
        self.mtime = mtime
    }
}

public struct ResumePlanEntry: Hashable, Sendable {
    public let index: Int
    public let action: ResumeAction
    public let start: Int64
    public let size: Int64?
    public let mtime: Int64?

    public init(index: Int, action: ResumeAction, start: Int64, size: Int64? = nil, mtime: Int64? = nil) {
        self.index = index
        self.action = action
        self.start = start
        self.size = size
        self.mtime = mtime
    }

    public func toWire() -> ResumeFilePlan {
        ResumeFilePlan(
            fileIndex: UInt32(index),
            action: action,
            start: UInt64(start),
            size: size.map(UInt64.init),
            mtime: mtime
        )
    }
}

/// 断点续传计划生成器（需求 17.4、17.5）。
public enum ResumePlanner: Sendable {
    public static func plan(
        task: TransferTask,
        state: [ResumeFileState],
        stats: [SourceStat?]
    ) -> [ResumePlanEntry] {
        precondition(stats.count == task.files.count, "stats must match task files")
        var byIndex: [UInt32: ResumeFileState] = [:]
        for s in state where byIndex[s.fileIndex] == nil {
            byIndex[s.fileIndex] = s
        }
        var result: [ResumePlanEntry] = []
        result.reserveCapacity(task.files.count)
        for k in 0..<task.files.count {
            let f = task.files[k]
            let r = byIndex[UInt32(f.index)]
            let stat = stats[k]
            let entry: ResumePlanEntry
            if f.state.isTerminal || r?.state == .completed || r?.state == .terminal {
                entry = ResumePlanEntry(index: f.index, action: .skip, start: 0)
            } else if let stat, (stat.size != f.size || stat.mtime != f.mtime) {
                entry = ResumePlanEntry(index: f.index, action: .restart, start: 0, size: stat.size, mtime: stat.mtime)
            } else if r == nil || r?.state == .none {
                entry = ResumePlanEntry(index: f.index, action: .continue, start: 0)
            } else if let r, r.persisted <= UInt64(f.size) {
                entry = ResumePlanEntry(index: f.index, action: .continue, start: Int64(r.persisted))
            } else {
                entry = ResumePlanEntry(index: f.index, action: .restart, start: 0)
            }
            result.append(entry)
        }
        return result
    }
}

public struct TempInfo: Hashable, Sendable {
    public let ref: TempRef
    public let transferId: TransferId?
    public let lastWriteUtcMs: Int64

    public init(ref: TempRef, transferId: TransferId?, lastWriteUtcMs: Int64) {
        self.ref = ref
        self.transferId = transferId
        self.lastWriteUtcMs = lastWriteUtcMs
    }
}

/// 过期临时文件选择（需求 17.7：超过 24 小时且不属于进行中任务）。
public enum TempFileSelector: Sendable {
    public static let maxAgeMs: Int64 = 24 * 60 * 60 * 1000

    public static func select(temps: [TempInfo], active: Set<TransferId>, nowWall: Int64) -> [TempInfo] {
        temps.filter { t in
            let isActive = t.transferId.map { active.contains($0) } ?? false
            return !isActive && (nowWall - t.lastWriteUtcMs) > maxAgeMs
        }
    }
}
