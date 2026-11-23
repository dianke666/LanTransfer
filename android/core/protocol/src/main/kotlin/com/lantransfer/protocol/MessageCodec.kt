package com.lantransfer.protocol

/** 各消息类型的载荷（Struct）编解码（规范第 9 节）。 */
internal object MessageCodec {
    fun encodePayload(msg: ProtocolMessage): ByteArray {
        val w = TlvWriter()
        when (msg) {
            is SessionResult -> w.enum(1, msg.result)
            is SessionClose -> w.enum(1, msg.reason)
            Heartbeat -> Unit
            is PairRequest -> w.bytes(1, msg.commitment)
            is PairNonce -> w.bytes(1, msg.nonce)
            is PairReveal -> w.bytes(1, msg.nonce)
            is PairDecision -> w.enum(1, msg.decision)
            is PairCancel -> w.enum(1, msg.reason)
            PairComplete -> Unit
            is QrPairRequest -> w.bytes16(1, msg.token)
            is QrPairResult -> w.enum(1, msg.result)
            is Text -> {
                w.bytes16(1, msg.messageId.value)
                w.i64(2, msg.sentAt)
                w.bytes(3, msg.text)
            }
            is TextAck -> w.bytes16(1, msg.messageId.value)
            is TextCorrupt -> w.bytes16(1, msg.messageId.value)
            is FileOffer -> {
                w.bytes16(1, msg.transferId.value)
                w.list(2, msg.files) { f ->
                    string(1, f.name)
                    u64(2, f.size)
                    string(3, f.mime)
                    f.mtime?.let { i64(4, it) }
                }
            }
            is FileOfferReply -> {
                w.bytes16(1, msg.transferId.value)
                w.enum(2, msg.decision)
            }
            is FileChunk -> {
                w.bytes16(1, msg.transferId.value)
                w.u32(2, msg.fileIndex)
                w.u64(3, msg.offset)
                w.bytes(4, msg.data)
            }
            is FileEnd -> {
                w.bytes16(1, msg.transferId.value)
                w.u32(2, msg.fileIndex)
                w.bytes(3, msg.sha256)
            }
            is FileAck -> {
                w.bytes16(1, msg.transferId.value)
                w.u32(2, msg.fileIndex)
                w.u64(3, msg.persisted)
            }
            is FileResult -> {
                w.bytes16(1, msg.transferId.value)
                w.u32(2, msg.fileIndex)
                w.enum(3, msg.status)
                msg.detail?.let { w.string(4, it) }
            }
            is FileReadFailed -> {
                w.bytes16(1, msg.transferId.value)
                w.u32(2, msg.fileIndex)
            }
            is TransferCancel -> {
                w.bytes16(1, msg.transferId.value)
                w.enum(2, msg.reason)
            }
            is ResumeRequest -> w.bytes16(1, msg.transferId.value)
            is ResumeState -> {
                w.bytes16(1, msg.transferId.value)
                w.list(2, msg.files) { f ->
                    u32(1, f.fileIndex)
                    enum(2, f.state)
                    u64(3, f.persisted)
                }
            }
            is ResumePlan -> {
                w.bytes16(1, msg.transferId.value)
                w.list(2, msg.files) { f ->
                    u32(1, f.fileIndex)
                    enum(2, f.action)
                    u64(3, f.start)
                    f.size?.let { u64(4, it) }
                    f.mtime?.let { i64(5, it) }
                }
            }
        }
        return w.toByteArray()
    }

    /** 解析载荷；不符合 Struct 规则时抛出 [MalformedException]。 */
    fun decodePayload(type: MessageType, buf: ByteArray, offset: Int, length: Int, v: ProtocolVersion): ProtocolMessage {
        val s = TlvStruct(buf, offset, length)
        return when (type) {
            MessageType.SESSION_RESULT -> SessionResult(s.enum(1, "result", SessionResultCode.entries, v))
            MessageType.SESSION_CLOSE -> SessionClose(s.enum(1, "reason", SessionCloseReason.entries, v))
            MessageType.HEARTBEAT -> Heartbeat
            MessageType.PAIR_REQUEST -> PairRequest(s.bytesFixed(1, "commitment", 32))
            MessageType.PAIR_NONCE -> PairNonce(s.bytesFixed(1, "nonce", 32))
            MessageType.PAIR_REVEAL -> PairReveal(s.bytesFixed(1, "nonce", 32))
            MessageType.PAIR_DECISION -> PairDecision(s.enum(1, "decision", PairDecisionCode.entries, v))
            MessageType.PAIR_CANCEL -> PairCancel(s.enum(1, "reason", PairCancelReason.entries, v))
            MessageType.PAIR_COMPLETE -> PairComplete
            MessageType.QR_PAIR_REQUEST -> QrPairRequest(s.bytes16(1, "token"))
            MessageType.QR_PAIR_RESULT -> QrPairResult(s.enum(1, "result", QrPairResultCode.entries, v))
            MessageType.TEXT -> Text(MessageId(s.bytes16(1, "message_id")), s.i64(2, "sent_at"), s.bytes(3, "text"))
            MessageType.TEXT_ACK -> TextAck(MessageId(s.bytes16(1, "message_id")))
            MessageType.TEXT_CORRUPT -> TextCorrupt(MessageId(s.bytes16(1, "message_id")))
            MessageType.FILE_OFFER -> FileOffer(
                transferId(s),
                s.list(2, "files").map { e ->
                    FileEntry(e.string(1, "name"), e.u64(2, "size"), e.string(3, "mime"), e.optI64(4, "mtime"))
                },
            )
            MessageType.FILE_OFFER_REPLY -> FileOfferReply(transferId(s), s.enum(2, "decision", OfferDecision.entries, v))
            MessageType.FILE_CHUNK -> {
                val len = if (s.has(4)) s.length(4) else 0
                if (s.has(4) && len !in 1..ProtocolConstants.MAX_CHUNK) throw MalformedException("chunk data length $len out of range")
                FileChunk(transferId(s), s.u32(2, "file_index"), s.u64(3, "offset"), s.bytes(4, "data"))
            }
            MessageType.FILE_END -> FileEnd(transferId(s), s.u32(2, "file_index"), s.bytesFixed(3, "sha256", 32))
            MessageType.FILE_ACK -> FileAck(transferId(s), s.u32(2, "file_index"), s.u64(3, "persisted"))
            MessageType.FILE_RESULT -> FileResult(
                transferId(s),
                s.u32(2, "file_index"),
                s.enum(3, "status", FileResultStatus.entries, v),
                s.optString(4, "detail"),
            )
            MessageType.FILE_READ_FAILED -> FileReadFailed(transferId(s), s.u32(2, "file_index"))
            MessageType.TRANSFER_CANCEL -> TransferCancel(transferId(s), s.enum(2, "reason", TransferCancelReason.entries, v))
            MessageType.RESUME_REQUEST -> ResumeRequest(transferId(s))
            MessageType.RESUME_STATE -> ResumeState(
                transferId(s),
                s.list(2, "files").map { e ->
                    ResumeFileState(e.u32(1, "file_index"), e.enum(2, "state", ResumeFileStatus.entries, v), e.u64(3, "persisted"))
                },
            )
            MessageType.RESUME_PLAN -> ResumePlan(
                transferId(s),
                s.list(2, "files").map { e ->
                    ResumeFilePlan(
                        e.u32(1, "file_index"),
                        e.enum(2, "action", ResumeAction.entries, v),
                        e.u64(3, "start"),
                        e.optU64(4, "size"),
                        e.optI64(5, "mtime"),
                    )
                },
            )
        }
    }

    private fun transferId(s: TlvStruct) = TransferId(s.bytes16(1, "transfer_id"))
}
