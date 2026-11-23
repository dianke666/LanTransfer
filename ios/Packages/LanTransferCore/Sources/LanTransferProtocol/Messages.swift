import Foundation

/// Frame 的 type（规范第 9 节），全部 25 种消息在 1.0 中引入。
public enum MessageType: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case sessionResult = 0x01
    case sessionClose = 0x02
    case heartbeat = 0x03
    case pairRequest = 0x10
    case pairNonce = 0x11
    case pairReveal = 0x12
    case pairDecision = 0x13
    case pairCancel = 0x14
    case pairComplete = 0x15
    case qrPairRequest = 0x16
    case qrPairResult = 0x17
    case text = 0x20
    case textAck = 0x21
    case textCorrupt = 0x22
    case fileOffer = 0x30
    case fileOfferReply = 0x31
    case fileChunk = 0x32
    case fileEnd = 0x33
    case fileAck = 0x34
    case fileResult = 0x35
    case fileReadFailed = 0x36
    case transferCancel = 0x37
    case resumeRequest = 0x38
    case resumeState = 0x39
    case resumePlan = 0x3A
}

public enum SessionResultCode: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case accepted = 0
    case notTrusted = 1
    case keyMismatch = 2
    case duplicate = 3
}

public enum SessionCloseReason: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case normal = 0
    case unpaired = 1
    case appExit = 2
    case background = 3
    case duplicate = 4
}

public enum PairDecisionCode: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case accepted = 0
    case rejected = 1
    case busy = 2
    case timeout = 3
}

public enum PairCancelReason: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case user = 0
    case timeout = 1
}

public enum QrPairResultCode: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case ok = 0
    case tokenInvalid = 1
}

public enum OfferDecision: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case accept = 0
    case reject = 1
    case timeout = 2
    case noSpace = 3
    case cannotSave = 4
    case invalidOffer = 5
}

public enum FileResultStatus: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case completed = 0
    case checksumFailed = 1
    case writeFailed = 2
}

public enum TransferCancelReason: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case user = 0
    case unknownTransfer = 1
    case unpaired = 2
}

public enum ResumeFileStatus: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case none = 0
    case partial = 1
    case completed = 2
    case terminal = 3
}

public enum ResumeAction: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case `continue` = 0
    case restart = 1
    case skip = 2
}

/// File_Offer 中的单个文件条目。
public struct FileEntry: Hashable, Sendable {
    public let name: String
    public let size: UInt64
    public let mime: String
    public let mtime: Int64?

    public init(name: String, size: UInt64, mime: String, mtime: Int64? = nil) {
        self.name = name
        self.size = size
        self.mime = mime
        self.mtime = mtime
    }
}

public struct ResumeFileState: Hashable, Sendable {
    public let fileIndex: UInt32
    public let state: ResumeFileStatus
    public let persisted: UInt64

    public init(fileIndex: UInt32, state: ResumeFileStatus, persisted: UInt64) {
        self.fileIndex = fileIndex
        self.state = state
        self.persisted = persisted
    }
}

public struct ResumeFilePlan: Hashable, Sendable {
    public let fileIndex: UInt32
    public let action: ResumeAction
    public let start: UInt64
    public let size: UInt64?
    public let mtime: Int64?

    public init(fileIndex: UInt32, action: ResumeAction, start: UInt64, size: UInt64? = nil, mtime: Int64? = nil) {
        self.fileIndex = fileIndex
        self.action = action
        self.start = start
        self.size = size
        self.mtime = mtime
    }
}

/// 协议消息联合体（规范第 9 节，对应 Android/Windows ProtocolMessage）。
public enum ProtocolMessage: Hashable, Sendable {
    case sessionResult(result: SessionResultCode)
    case sessionClose(reason: SessionCloseReason)
    case heartbeat
    case pairRequest(commitment: Data)
    case pairNonce(nonce: Data)
    case pairReveal(nonce: Data)
    case pairDecision(decision: PairDecisionCode)
    case pairCancel(reason: PairCancelReason)
    case pairComplete
    case qrPairRequest(token: Data)
    case qrPairResult(result: QrPairResultCode)
    case text(messageId: MessageId, sentAt: Int64, body: Data)
    case textAck(messageId: MessageId)
    case textCorrupt(messageId: MessageId)
    case fileOffer(transferId: TransferId, files: [FileEntry])
    case fileOfferReply(transferId: TransferId, decision: OfferDecision)
    case fileChunk(transferId: TransferId, fileIndex: UInt32, offset: UInt64, data: Data)
    case fileEnd(transferId: TransferId, fileIndex: UInt32, sha256: Data)
    case fileAck(transferId: TransferId, fileIndex: UInt32, persisted: UInt64)
    case fileResult(transferId: TransferId, fileIndex: UInt32, status: FileResultStatus, detail: String?)
    case fileReadFailed(transferId: TransferId, fileIndex: UInt32)
    case transferCancel(transferId: TransferId, reason: TransferCancelReason)
    case resumeRequest(transferId: TransferId)
    case resumeState(transferId: TransferId, files: [ResumeFileState])
    case resumePlan(transferId: TransferId, files: [ResumeFilePlan])

    public var type: MessageType {
        switch self {
        case .sessionResult: return .sessionResult
        case .sessionClose: return .sessionClose
        case .heartbeat: return .heartbeat
        case .pairRequest: return .pairRequest
        case .pairNonce: return .pairNonce
        case .pairReveal: return .pairReveal
        case .pairDecision: return .pairDecision
        case .pairCancel: return .pairCancel
        case .pairComplete: return .pairComplete
        case .qrPairRequest: return .qrPairRequest
        case .qrPairResult: return .qrPairResult
        case .text: return .text
        case .textAck: return .textAck
        case .textCorrupt: return .textCorrupt
        case .fileOffer: return .fileOffer
        case .fileOfferReply: return .fileOfferReply
        case .fileChunk: return .fileChunk
        case .fileEnd: return .fileEnd
        case .fileAck: return .fileAck
        case .fileResult: return .fileResult
        case .fileReadFailed: return .fileReadFailed
        case .transferCancel: return .transferCancel
        case .resumeRequest: return .resumeRequest
        case .resumeState: return .resumeState
        case .resumePlan: return .resumePlan
        }
    }
}
