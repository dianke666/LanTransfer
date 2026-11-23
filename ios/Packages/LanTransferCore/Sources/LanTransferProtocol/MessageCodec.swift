import Foundation

/// 各协议消息载荷（TLV Struct）编解码（规范第 9 节）。
internal enum MessageCodec {
    static func encodePayload(_ message: ProtocolMessage) -> [UInt8] {
        var w = TlvWriter()
        switch message {
        case .sessionResult(let result):
            w.protoEnum(1, result)
        case .sessionClose(let reason):
            w.protoEnum(1, reason)
        case .heartbeat:
            break
        case .pairRequest(let commitment):
            precondition(commitment.count == 32, "commitment must be 32 bytes")
            w.bytes(1, commitment)
        case .pairNonce(let nonce):
            precondition(nonce.count == 32, "nonce must be 32 bytes")
            w.bytes(1, nonce)
        case .pairReveal(let nonce):
            precondition(nonce.count == 32, "nonce must be 32 bytes")
            w.bytes(1, nonce)
        case .pairDecision(let decision):
            w.protoEnum(1, decision)
        case .pairCancel(let reason):
            w.protoEnum(1, reason)
        case .pairComplete:
            break
        case .qrPairRequest(let token):
            precondition(token.count == 16, "token must be 16 bytes")
            w.bytes(1, token)
        case .qrPairResult(let result):
            w.protoEnum(1, result)
        case .text(let messageId, let sentAt, let body):
            w.bytes(1, messageId.bytes)
            w.i64(2, sentAt)
            w.bytes(3, body)
        case .textAck(let messageId):
            w.bytes(1, messageId.bytes)
        case .textCorrupt(let messageId):
            w.bytes(1, messageId.bytes)
        case .fileOffer(let transferId, let files):
            w.bytes(1, transferId.bytes)
            w.list(2, files) { e, f in
                e.string(1, f.name)
                e.u64(2, f.size)
                e.string(3, f.mime)
                if let mtime = f.mtime {
                    e.i64(4, mtime)
                }
            }
        case .fileOfferReply(let transferId, let decision):
            w.bytes(1, transferId.bytes)
            w.protoEnum(2, decision)
        case .fileChunk(let transferId, let fileIndex, let offset, let data):
            precondition(data.count >= 1 && data.count <= ProtocolConstants.maxChunk, "chunk size out of range")
            w.bytes(1, transferId.bytes)
            w.u32(2, fileIndex)
            w.u64(3, offset)
            w.bytes(4, data)
        case .fileEnd(let transferId, let fileIndex, let sha256):
            precondition(sha256.count == 32, "sha256 must be 32 bytes")
            w.bytes(1, transferId.bytes)
            w.u32(2, fileIndex)
            w.bytes(3, sha256)
        case .fileAck(let transferId, let fileIndex, let persisted):
            w.bytes(1, transferId.bytes)
            w.u32(2, fileIndex)
            w.u64(3, persisted)
        case .fileResult(let transferId, let fileIndex, let status, let detail):
            w.bytes(1, transferId.bytes)
            w.u32(2, fileIndex)
            w.protoEnum(3, status)
            if let detail {
                w.string(4, detail)
            }
        case .fileReadFailed(let transferId, let fileIndex):
            w.bytes(1, transferId.bytes)
            w.u32(2, fileIndex)
        case .transferCancel(let transferId, let reason):
            w.bytes(1, transferId.bytes)
            w.protoEnum(2, reason)
        case .resumeRequest(let transferId):
            w.bytes(1, transferId.bytes)
        case .resumeState(let transferId, let files):
            w.bytes(1, transferId.bytes)
            w.list(2, files) { e, f in
                e.u32(1, f.fileIndex)
                e.protoEnum(2, f.state)
                e.u64(3, f.persisted)
            }
        case .resumePlan(let transferId, let files):
            w.bytes(1, transferId.bytes)
            w.list(2, files) { e, f in
                e.u32(1, f.fileIndex)
                e.protoEnum(2, f.action)
                e.u64(3, f.start)
                if let size = f.size {
                    e.u64(4, size)
                }
                if let mtime = f.mtime {
                    e.i64(5, mtime)
                }
            }
        }
        return w.toBytes()
    }

    static func decodePayload(type: MessageType, bytes: [UInt8], version: ProtocolVersion) throws -> ProtocolMessage {
        let s = try TlvStruct(bytes: bytes)
        switch type {
        case .sessionResult:
            return .sessionResult(result: try s.protoEnum(1, "result", version: version))
        case .sessionClose:
            return .sessionClose(reason: try s.protoEnum(1, "reason", version: version))
        case .heartbeat:
            return .heartbeat
        case .pairRequest:
            return .pairRequest(commitment: try s.fixedData(1, "commitment", size: 32))
        case .pairNonce:
            return .pairNonce(nonce: try s.fixedData(1, "nonce", size: 32))
        case .pairReveal:
            return .pairReveal(nonce: try s.fixedData(1, "nonce", size: 32))
        case .pairDecision:
            return .pairDecision(decision: try s.protoEnum(1, "decision", version: version))
        case .pairCancel:
            return .pairCancel(reason: try s.protoEnum(1, "reason", version: version))
        case .pairComplete:
            return .pairComplete
        case .qrPairRequest:
            return .qrPairRequest(token: try s.fixedData(1, "token", size: 16))
        case .qrPairResult:
            return .qrPairResult(result: try s.protoEnum(1, "result", version: version))
        case .text:
            let id = try messageId(from: s)
            return .text(messageId: id, sentAt: try s.i64(2, "sent_at"), body: try s.data(3, "text"))
        case .textAck:
            return .textAck(messageId: try messageId(from: s))
        case .textCorrupt:
            return .textCorrupt(messageId: try messageId(from: s))
        case .fileOffer:
            let tid = try transferId(from: s)
            let entries = try s.list(2, "files").map { e in
                FileEntry(
                    name: try e.string(1, "name"),
                    size: try e.u64(2, "size"),
                    mime: try e.string(3, "mime"),
                    mtime: try e.optI64(4, "mtime")
                )
            }
            return .fileOffer(transferId: tid, files: entries)
        case .fileOfferReply:
            return .fileOfferReply(transferId: try transferId(from: s), decision: try s.protoEnum(2, "decision", version: version))
        case .fileChunk:
            if s.has(4) {
                let len = s.length(of: 4)
                if len < 1 || len > ProtocolConstants.maxChunk {
                    throw MalformedError("chunk data length \(len) out of range")
                }
            }
            return .fileChunk(
                transferId: try transferId(from: s),
                fileIndex: try s.u32(2, "file_index"),
                offset: try s.u64(3, "offset"),
                data: try s.data(4, "data")
            )
        case .fileEnd:
            return .fileEnd(
                transferId: try transferId(from: s),
                fileIndex: try s.u32(2, "file_index"),
                sha256: try s.fixedData(3, "sha256", size: 32)
            )
        case .fileAck:
            return .fileAck(
                transferId: try transferId(from: s),
                fileIndex: try s.u32(2, "file_index"),
                persisted: try s.u64(3, "persisted")
            )
        case .fileResult:
            return .fileResult(
                transferId: try transferId(from: s),
                fileIndex: try s.u32(2, "file_index"),
                status: try s.protoEnum(3, "status", version: version),
                detail: try s.optString(4, "detail")
            )
        case .fileReadFailed:
            return .fileReadFailed(transferId: try transferId(from: s), fileIndex: try s.u32(2, "file_index"))
        case .transferCancel:
            return .transferCancel(transferId: try transferId(from: s), reason: try s.protoEnum(2, "reason", version: version))
        case .resumeRequest:
            return .resumeRequest(transferId: try transferId(from: s))
        case .resumeState:
            let tid = try transferId(from: s)
            let files = try s.list(2, "files").map { e in
                ResumeFileState(
                    fileIndex: try e.u32(1, "file_index"),
                    state: try e.protoEnum(2, "state", version: version),
                    persisted: try e.u64(3, "persisted")
                )
            }
            return .resumeState(transferId: tid, files: files)
        case .resumePlan:
            let tid = try transferId(from: s)
            let files = try s.list(2, "files").map { e in
                ResumeFilePlan(
                    fileIndex: try e.u32(1, "file_index"),
                    action: try e.protoEnum(2, "action", version: version),
                    start: try e.u64(3, "start"),
                    size: try e.optU64(4, "size"),
                    mtime: try e.optI64(5, "mtime")
                )
            }
            return .resumePlan(transferId: tid, files: files)
        }
    }

    private static func messageId(from s: TlvStruct) throws -> MessageId {
        let raw = try s.fixedData(1, "message_id", size: 16)
        guard let id = MessageId(bytes: raw) else {
            throw MalformedError("invalid message_id")
        }
        return id
    }

    private static func transferId(from s: TlvStruct) throws -> TransferId {
        let raw = try s.fixedData(1, "transfer_id", size: 16)
        guard let id = TransferId(bytes: raw) else {
            throw MalformedError("invalid transfer_id")
        }
        return id
    }
}
