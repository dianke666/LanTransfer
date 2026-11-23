import Foundation
import LanTransferProtocol

public enum VerifyOutcome: Hashable, Sendable {
    case committed(finalName: String, fullPath: String, storedRef: StoredFileRef)
    case checksumMismatch
    case commitFailed(detail: String)
}

public enum TransferEvent: Hashable, Sendable {
    // 发送方
    case offerReplied(OfferDecision)
    case sourceReadFailed(index: Int)
    case fileEndSent(index: Int, sha256: Data)
    case ackReceived(index: Int, persisted: Int64)
    case resultReceived(index: Int, status: FileResultStatus, detail: String?)

    // 接收方
    case userAccepted(dir: DirRef, dirUsable: Bool)
    case userRejected
    case chunkWritten(index: Int, persisted: Int64, localNow: LocalDateTime, nowWall: Int64)
    case endReceived(index: Int, sha256: Data, localNow: LocalDateTime)
    case verifyDone(index: Int, outcome: VerifyOutcome)
    case writeFailed(index: Int, detail: String)
    case peerReadFailed(index: Int)

    // 双方
    case offerTimedOut
    case disconnected(reason: InterruptReason, detail: String? = nil)
    case localCancel(sessionActive: Bool, reason: TransferCancelReason = .user)
    case peerCancelled(reason: TransferCancelReason)
    case resumeRequested
    case resumeFailed(reason: InterruptReason)
    case resumePlanned([ResumePlanEntry])
    case restored
}

public enum TransferEffect: Hashable, Sendable {
    case send(ProtocolMessage)
    case startFile(index: Int, offset: Int64)
    case stopSending
    case verify(index: Int, sha256: Data)
    case deleteTemp(index: Int)
    case addPendingCancel(reason: TransferCancelReason)
    case startOfferTimer(ms: Int64)
    case cancelOfferTimer
    case startResume
    case abortResume
    case protocolViolation
}

/// 传输任务纯状态机归约器（M3.2、M3.3，需求 14–18），与 Android/Windows 逐分支一致。
public enum TransferReducer: Sendable {
    public static let receiverOfferTimeoutMs: Int64 = 60_000
    public static let senderOfferTimeoutMs: Int64 = 65_000

    public static func reduce(_ task: TransferTask, _ event: TransferEvent) -> Step<TransferTask, TransferEffect> {
        if task.state.isTerminal {
            return Step(state: task)
        }
        switch event {
        case .offerReplied(let decision):
            return onOfferReplied(task, decision: decision)
        case .sourceReadFailed(let index):
            return senderOnly(task) { onSourceReadFailed(task, index: index) }
        case .fileEndSent(let index, let sha256):
            return senderOnly(task) { onFileEndSent(task, index: index, sha256: sha256) }
        case .ackReceived(let index, let persisted):
            return senderOnly(task) { onAck(task, index: index, persisted: persisted) }
        case .resultReceived(let index, let status, let detail):
            return senderOnly(task) { onResult(task, index: index, status: status, detail: detail) }
        case .userAccepted(let dir, let dirUsable):
            return onUserAccepted(task, dir: dir, dirUsable: dirUsable)
        case .userRejected:
            return onUserRejected(task)
        case .chunkWritten(let index, let persisted, let localNow, let nowWall):
            return receiverOnly(task) { onChunk(task, index: index, persisted: persisted, localNow: localNow, nowWall: nowWall) }
        case .endReceived(let index, let sha256, let localNow):
            return receiverOnly(task) { onEnd(task, index: index, sha256: sha256, localNow: localNow) }
        case .verifyDone(let index, let outcome):
            return receiverOnly(task) { onVerifyDone(task, index: index, outcome: outcome) }
        case .writeFailed(let index, let detail):
            return receiverOnly(task) { onWriteFailed(task, index: index, detail: detail) }
        case .peerReadFailed(let index):
            return receiverOnly(task) { onPeerReadFailed(task, index: index) }
        case .offerTimedOut:
            return onOfferTimedOut(task)
        case .disconnected(let reason, let detail):
            return onDisconnected(task, reason: reason, detail: detail)
        case .localCancel(let sessionActive, let reason):
            return onLocalCancel(task, sessionActive: sessionActive, reason: reason)
        case .peerCancelled:
            return onPeerCancelled(task)
        case .resumeRequested:
            guard task.state == .interrupted else { return Step(state: task) }
            var next = task
            next.state = .resuming
            next.interrupt = nil
            return Step(state: next, effects: [.startResume])
        case .resumeFailed(let reason):
            guard task.state == .resuming else { return Step(state: task) }
            return Step(state: interrupted(task, reason: reason))
        case .resumePlanned(let plan):
            return onResumePlanned(task, plan: plan)
        case .restored:
            return onRestored(task)
        }
    }

    private static func senderOnly(_ t: TransferTask, _ body: () -> Step<TransferTask, TransferEffect>) -> Step<TransferTask, TransferEffect> {
        (t.direction == .send && t.state == .running) ? body() : Step(state: t)
    }

    private static func receiverOnly(_ t: TransferTask, _ body: () -> Step<TransferTask, TransferEffect>) -> Step<TransferTask, TransferEffect> {
        (t.direction == .receive && t.state == .running) ? body() : Step(state: t)
    }

    private static func currentPos(_ t: TransferTask) -> Int? {
        t.files.firstIndex(where: { !$0.state.isTerminal })
    }

    private static func posOf(_ t: TransferTask, index: Int) -> Int? {
        t.files.firstIndex(where: { $0.index == index })
    }

    private static func interrupted(_ t: TransferTask, reason: InterruptReason, detail: String? = nil) -> TransferTask {
        var next = t
        next.state = .interrupted
        next.interrupt = InterruptInfo(reason: reason, detail: detail)
        return next
    }

    private static func startNextSender(_ t: TransferTask, effects: [TransferEffect] = []) -> Step<TransferTask, TransferEffect> {
        var next = t
        guard let pos = currentPos(next) else {
            next.state = .completed
            next.interrupt = nil
            return Step(state: next, effects: effects)
        }
        next.files[pos].state = .transferring
        let f = next.files[pos]
        return Step(state: next, effects: effects + [.startFile(index: f.index, offset: f.persisted)])
    }

    private static func afterReceiverFile(_ t: TransferTask, effects: [TransferEffect] = []) -> Step<TransferTask, TransferEffect> {
        var next = t
        if currentPos(next) == nil {
            next.state = .completed
            next.interrupt = nil
        }
        return Step(state: next, effects: effects)
    }

    // MARK: Offer 阶段

    private static func onOfferReplied(_ t: TransferTask, decision: OfferDecision) -> Step<TransferTask, TransferEffect> {
        guard t.direction == .send && t.state == .offering else { return Step(state: t) }
        var next = t
        if decision == .accept {
            next.state = .running
            return startNextSender(next, effects: [.cancelOfferTimer])
        }
        switch decision {
        case .reject: next.state = .rejected
        case .timeout: next.state = .noResponse
        case .noSpace: next.state = .noSpace
        case .cannotSave: next.state = .cannotSave
        default: next.state = .offerInvalid
        }
        return Step(state: next, effects: [.cancelOfferTimer])
    }

    private static func replyEffect(_ t: TransferTask, _ decision: OfferDecision) -> TransferEffect {
        .send(.fileOfferReply(transferId: t.transferId, decision: decision))
    }

    private static func onUserAccepted(_ t: TransferTask, dir: DirRef, dirUsable: Bool) -> Step<TransferTask, TransferEffect> {
        guard t.direction == .receive && t.state == .awaitingUser else { return Step(state: t) }
        var next = t
        if !dirUsable {
            next.state = .cannotSave
            return Step(state: next, effects: [.cancelOfferTimer, replyEffect(t, .cannotSave)])
        }
        next.state = .running
        next.receiveDir = dir
        return afterReceiverFile(next, effects: [.cancelOfferTimer, replyEffect(t, .accept)])
    }

    private static func onUserRejected(_ t: TransferTask) -> Step<TransferTask, TransferEffect> {
        guard t.direction == .receive && t.state == .awaitingUser else { return Step(state: t) }
        var next = t
        next.state = .rejected
        return Step(state: next, effects: [.cancelOfferTimer, replyEffect(t, .reject)])
    }

    private static func onOfferTimedOut(_ t: TransferTask) -> Step<TransferTask, TransferEffect> {
        var next = t
        if t.direction == .send && t.state == .offering {
            next.state = .noResponse
            return Step(state: next)
        }
        if t.direction == .receive && t.state == .awaitingUser {
            next.state = .noResponse
            return Step(state: next, effects: [replyEffect(t, .timeout)])
        }
        return Step(state: t)
    }

    // MARK: 发送方

    private static func onSourceReadFailed(_ t: TransferTask, index: Int) -> Step<TransferTask, TransferEffect> {
        guard let pos = currentPos(t), t.files[pos].index == index else { return Step(state: t) }
        var next = t
        next.files[pos].state = .readFailed
        return startNextSender(next, effects: [.send(.fileReadFailed(transferId: t.transferId, fileIndex: UInt32(index)))])
    }

    private static func onFileEndSent(_ t: TransferTask, index: Int, sha256: Data) -> Step<TransferTask, TransferEffect> {
        guard let pos = currentPos(t), t.files[pos].index == index, t.files[pos].state == .transferring else {
            return Step(state: t)
        }
        var next = t
        next.files[pos].state = .verifying
        next.files[pos].sha256 = sha256
        return Step(state: next)
    }

    private static func onAck(_ t: TransferTask, index: Int, persisted: Int64) -> Step<TransferTask, TransferEffect> {
        guard let pos = currentPos(t), t.files[pos].index == index else { return Step(state: t) }
        let f = t.files[pos]
        let clamped = max(f.persisted, min(persisted, f.size))
        guard clamped != f.persisted else { return Step(state: t) }
        var next = t
        next.files[pos].persisted = clamped
        return Step(state: next)
    }

    private static func onResult(_ t: TransferTask, index: Int, status: FileResultStatus, detail: String?) -> Step<TransferTask, TransferEffect> {
        guard let pos = currentPos(t), t.files[pos].index == index else { return Step(state: t) }
        var next = t
        let wasTransferring = next.files[pos].state == .transferring
        switch status {
        case .completed:
            next.files[pos].state = .completed
            next.files[pos].persisted = next.files[pos].size
            return startNextSender(next)
        case .checksumFailed:
            next.files[pos].state = .checksumFailed
            return startNextSender(next, effects: wasTransferring ? [.stopSending] : [])
        case .writeFailed:
            next.files[pos].state = .writeFailed
            return Step(state: interrupted(next, reason: .writeFailed, detail: detail), effects: [.stopSending])
        }
    }

    // MARK: 接收方

    private static func violation(_ t: TransferTask) -> Step<TransferTask, TransferEffect> {
        Step(state: interrupted(t, reason: .protocolError), effects: [.protocolViolation])
    }

    private enum TargetMatch {
        case current(Int)
        case ignoreTerminal
        case outOfOrder
    }

    private static func targetMatch(_ t: TransferTask, index: Int) -> TargetMatch {
        guard let pos = posOf(t, index: index) else { return .outOfOrder }
        if t.files[pos].state.isTerminal { return .ignoreTerminal }
        return pos == currentPos(t) ? .current(pos) : .outOfOrder
    }

    private static func resultEffect(_ t: TransferTask, index: Int, status: FileResultStatus, detail: String? = nil) -> TransferEffect {
        .send(.fileResult(transferId: t.transferId, fileIndex: UInt32(index), status: status, detail: detail))
    }

    private static func onChunk(_ t: TransferTask, index: Int, persisted: Int64, localNow: LocalDateTime, nowWall: Int64) -> Step<TransferTask, TransferEffect> {
        switch targetMatch(t, index: index) {
        case .ignoreTerminal:
            return Step(state: t)
        case .outOfOrder:
            return violation(t)
        case .current(let pos):
            if t.files[pos].state == .verifying {
                return violation(t)
            }
            var next = t
            let f = next.files[pos]
            if persisted > f.size {
                next.files[pos].state = .checksumFailed
                return afterReceiverFile(next, effects: [.deleteTemp(index: f.index), resultEffect(t, index: f.index, status: .checksumFailed)])
            }
            next.files[pos].state = .transferring
            next.files[pos].persisted = max(f.persisted, persisted)
            if next.files[pos].startedAt == nil {
                next.files[pos].startedAt = localNow
            }
            next.files[pos].lastWriteAtUtcMs = nowWall
            return Step(state: next)
        }
    }

    private static func onEnd(_ t: TransferTask, index: Int, sha256: Data, localNow: LocalDateTime) -> Step<TransferTask, TransferEffect> {
        switch targetMatch(t, index: index) {
        case .ignoreTerminal:
            return Step(state: t)
        case .outOfOrder:
            return violation(t)
        case .current(let pos):
            if t.files[pos].state == .verifying {
                return violation(t)
            }
            var next = t
            next.files[pos].state = .verifying
            next.files[pos].sha256 = sha256
            if next.files[pos].startedAt == nil {
                next.files[pos].startedAt = localNow
            }
            return Step(state: next, effects: [.verify(index: next.files[pos].index, sha256: sha256)])
        }
    }

    private static func onVerifyDone(_ t: TransferTask, index: Int, outcome: VerifyOutcome) -> Step<TransferTask, TransferEffect> {
        guard let pos = currentPos(t), t.files[pos].index == index, t.files[pos].state == .verifying else {
            return Step(state: t)
        }
        var next = t
        let fIndex = next.files[pos].index
        switch outcome {
        case .committed(let finalName, let fullPath, let storedRef):
            next.files[pos].state = .completed
            next.files[pos].persisted = next.files[pos].size
            next.files[pos].finalName = finalName
            next.files[pos].fullPath = fullPath
            next.files[pos].storedRef = storedRef
            return afterReceiverFile(next, effects: [resultEffect(t, index: fIndex, status: .completed)])
        case .commitFailed(let detail):
            next.files[pos].state = .writeFailed
            return Step(state: interrupted(next, reason: .writeFailed, detail: detail), effects: [resultEffect(t, index: fIndex, status: .writeFailed, detail: detail)])
        case .checksumMismatch:
            next.files[pos].state = .checksumFailed
            return afterReceiverFile(next, effects: [.deleteTemp(index: fIndex), resultEffect(t, index: fIndex, status: .checksumFailed)])
        }
    }

    private static func onWriteFailed(_ t: TransferTask, index: Int, detail: String) -> Step<TransferTask, TransferEffect> {
        guard let pos = currentPos(t), t.files[pos].index == index else { return Step(state: t) }
        var next = t
        next.files[pos].state = .writeFailed
        return Step(state: interrupted(next, reason: .writeFailed, detail: detail), effects: [resultEffect(t, index: index, status: .writeFailed, detail: detail)])
    }

    private static func onPeerReadFailed(_ t: TransferTask, index: Int) -> Step<TransferTask, TransferEffect> {
        switch targetMatch(t, index: index) {
        case .ignoreTerminal:
            return Step(state: t)
        case .outOfOrder:
            return violation(t)
        case .current(let pos):
            var next = t
            next.files[pos].state = .readFailed
            return afterReceiverFile(next, effects: [.deleteTemp(index: next.files[pos].index)])
        }
    }

    // MARK: 双方事件

    private static func onDisconnected(_ t: TransferTask, reason: InterruptReason, detail: String?) -> Step<TransferTask, TransferEffect> {
        switch t.state {
        case .offering, .awaitingUser:
            var next = t
            next.state = .connectionLost
            return Step(state: next, effects: [.cancelOfferTimer])
        case .running:
            return Step(state: interrupted(t, reason: reason, detail: detail), effects: t.direction == .send ? [.stopSending] : [])
        case .resuming:
            return Step(state: interrupted(t, reason: reason, detail: detail), effects: [.abortResume])
        default:
            return Step(state: t)
        }
    }

    private static func deleteUnfinishedTemps(_ t: TransferTask) -> [TransferEffect] {
        guard t.direction == .receive else { return [] }
        return t.files.filter { !$0.state.isTerminal }.map { .deleteTemp(index: $0.index) }
    }

    private static func cancelled(_ t: TransferTask, _ state: TaskState) -> TransferTask {
        var next = t
        next.state = state
        next.interrupt = nil
        return next
    }

    private static func onLocalCancel(_ t: TransferTask, sessionActive: Bool, reason: TransferCancelReason) -> Step<TransferTask, TransferEffect> {
        let notify = TransferEffect.send(.transferCancel(transferId: t.transferId, reason: reason))
        let stop: [TransferEffect] = t.direction == .send ? [.stopSending] : []
        switch t.state {
        case .offering:
            return Step(state: cancelled(t, .cancelled), effects: [.cancelOfferTimer, notify])
        case .awaitingUser:
            return Step(state: cancelled(t, .rejected), effects: [.cancelOfferTimer, replyEffect(t, .reject)])
        case .running:
            return Step(state: cancelled(t, .cancelled), effects: stop + [notify] + deleteUnfinishedTemps(t))
        case .interrupted:
            return Step(state: cancelled(t, .cancelled), effects: [.addPendingCancel(reason: reason)] + deleteUnfinishedTemps(t))
        case .resuming:
            let cancelFx: TransferEffect = sessionActive ? notify : .addPendingCancel(reason: reason)
            return Step(state: cancelled(t, .cancelled), effects: [.abortResume] + stop + [cancelFx] + deleteUnfinishedTemps(t))
        default:
            return Step(state: t)
        }
    }

    private static func onPeerCancelled(_ t: TransferTask) -> Step<TransferTask, TransferEffect> {
        var effects: [TransferEffect] = []
        if t.state == .offering || t.state == .awaitingUser {
            effects.append(.cancelOfferTimer)
        } else if t.state == .resuming {
            effects.append(.abortResume)
        }
        if t.direction == .send && t.state == .running {
            effects.append(.stopSending)
        }
        if t.state != .offering && t.state != .awaitingUser {
            effects.append(contentsOf: deleteUnfinishedTemps(t))
        }
        return Step(state: cancelled(t, .peerCancelled), effects: effects)
    }

    private static func onResumePlanned(_ t: TransferTask, plan: [ResumePlanEntry]) -> Step<TransferTask, TransferEffect> {
        guard t.state == .resuming else { return Step(state: t) }
        var byIndex: [Int: ResumePlanEntry] = [:]
        for p in plan { byIndex[p.index] = p }

        var effects: [TransferEffect] = []
        var next = t
        for i in 0..<next.files.count {
            guard let p = byIndex[next.files[i].index] else { continue }
            switch p.action {
            case .skip:
                if !next.files[i].state.isTerminal {
                    next.files[i].state = .completed
                    next.files[i].persisted = next.files[i].size
                }
            case .continue:
                if !next.files[i].state.isTerminal {
                    next.files[i].state = .waiting
                    next.files[i].persisted = min(p.start, next.files[i].size)
                }
            case .restart:
                if t.direction == .receive {
                    effects.append(.deleteTemp(index: next.files[i].index))
                }
                if !next.files[i].state.isTerminal {
                    next.files[i].state = .waiting
                    next.files[i].persisted = 0
                    if let newSize = p.size {
                        next.files[i].size = newSize
                        next.files[i].mtime = p.mtime
                    }
                    next.files[i].sha256 = nil
                }
            }
        }
        next.state = .running
        next.interrupt = nil
        if t.direction == .send {
            effects.append(.send(.resumePlan(transferId: t.transferId, files: plan.map { $0.toWire() })))
            return startNextSender(next, effects: effects)
        }
        return afterReceiverFile(next, effects: effects)
    }

    private static func onRestored(_ t: TransferTask) -> Step<TransferTask, TransferEffect> {
        switch t.state {
        case .running, .resuming:
            return Step(state: interrupted(t, reason: .appKilled))
        case .offering, .awaitingUser:
            var next = t
            next.state = .connectionLost
            return Step(state: next)
        default:
            return Step(state: t)
        }
    }
}
