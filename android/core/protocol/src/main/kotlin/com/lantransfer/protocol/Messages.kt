package com.lantransfer.protocol

/** Frame 的 type（规范第 9 节）。 */
enum class MessageType(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    SESSION_RESULT(0x01),
    SESSION_CLOSE(0x02),
    HEARTBEAT(0x03),
    PAIR_REQUEST(0x10),
    PAIR_NONCE(0x11),
    PAIR_REVEAL(0x12),
    PAIR_DECISION(0x13),
    PAIR_CANCEL(0x14),
    PAIR_COMPLETE(0x15),
    QR_PAIR_REQUEST(0x16),
    QR_PAIR_RESULT(0x17),
    TEXT(0x20),
    TEXT_ACK(0x21),
    TEXT_CORRUPT(0x22),
    FILE_OFFER(0x30),
    FILE_OFFER_REPLY(0x31),
    FILE_CHUNK(0x32),
    FILE_END(0x33),
    FILE_ACK(0x34),
    FILE_RESULT(0x35),
    FILE_READ_FAILED(0x36),
    TRANSFER_CANCEL(0x37),
    RESUME_REQUEST(0x38),
    RESUME_STATE(0x39),
    RESUME_PLAN(0x3A),
    ;

    companion object {
        fun fromCode(code: Int): MessageType? = entries.firstOrNull { it.code == code }
    }
}

enum class SessionResultCode(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    ACCEPTED(0), NOT_TRUSTED(1), KEY_MISMATCH(2), DUPLICATE(3)
}

enum class SessionCloseReason(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    NORMAL(0), UNPAIRED(1), APP_EXIT(2), BACKGROUND(3), DUPLICATE(4)
}

enum class PairDecisionCode(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    ACCEPTED(0), REJECTED(1), BUSY(2), TIMEOUT(3)
}

enum class PairCancelReason(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    USER(0), TIMEOUT(1)
}

enum class QrPairResultCode(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    OK(0), TOKEN_INVALID(1)
}

enum class OfferDecision(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    ACCEPT(0), REJECT(1), TIMEOUT(2), NO_SPACE(3), CANNOT_SAVE(4), INVALID_OFFER(5)
}

enum class FileResultStatus(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    COMPLETED(0), CHECKSUM_FAILED(1), WRITE_FAILED(2)
}

enum class TransferCancelReason(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    USER(0), UNKNOWN_TRANSFER(1), UNPAIRED(2)
}

enum class ResumeFileStatus(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    NONE(0), PARTIAL(1), COMPLETED(2), TERMINAL(3)
}

enum class ResumeAction(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    CONTINUE(0), RESTART(1), SKIP(2)
}

private fun requireSize(name: String, b: Bytes, size: Int) {
    require(b.size == size) { "$name must be $size bytes, got ${b.size}" }
}

/** C1.9 中的 Protocol_Message。可选字段用可空类型表示，保留“是否出现”的信息（规范 2.5）。 */
sealed interface ProtocolMessage {
    val type: MessageType
}

data class SessionResult(val result: SessionResultCode) : ProtocolMessage {
    override val type get() = MessageType.SESSION_RESULT
}

data class SessionClose(val reason: SessionCloseReason) : ProtocolMessage {
    override val type get() = MessageType.SESSION_CLOSE
}

data object Heartbeat : ProtocolMessage {
    override val type get() = MessageType.HEARTBEAT
}

data class PairRequest(val commitment: Bytes) : ProtocolMessage {
    init { requireSize("commitment", commitment, 32) }
    override val type get() = MessageType.PAIR_REQUEST
}

data class PairNonce(val nonce: Bytes) : ProtocolMessage {
    init { requireSize("nonce", nonce, 32) }
    override val type get() = MessageType.PAIR_NONCE
}

data class PairReveal(val nonce: Bytes) : ProtocolMessage {
    init { requireSize("nonce", nonce, 32) }
    override val type get() = MessageType.PAIR_REVEAL
}

data class PairDecision(val decision: PairDecisionCode) : ProtocolMessage {
    override val type get() = MessageType.PAIR_DECISION
}

data class PairCancel(val reason: PairCancelReason) : ProtocolMessage {
    override val type get() = MessageType.PAIR_CANCEL
}

data object PairComplete : ProtocolMessage {
    override val type get() = MessageType.PAIR_COMPLETE
}

data class QrPairRequest(val token: Bytes16) : ProtocolMessage {
    override val type get() = MessageType.QR_PAIR_REQUEST
}

data class QrPairResult(val result: QrPairResultCode) : ProtocolMessage {
    override val type get() = MessageType.QR_PAIR_RESULT
}

/** 文字消息。[text] 是 UTF‑8 字节（类型为 Bytes），是否合法由文字规则检查（规范第 13 节）。 */
data class Text(val messageId: MessageId, val sentAt: Long, val text: Bytes) : ProtocolMessage {
    override val type get() = MessageType.TEXT
}

data class TextAck(val messageId: MessageId) : ProtocolMessage {
    override val type get() = MessageType.TEXT_ACK
}

data class TextCorrupt(val messageId: MessageId) : ProtocolMessage {
    override val type get() = MessageType.TEXT_CORRUPT
}

/** File_Offer 中的一个文件。[size] 与 [mime] 的语义约束（≤ 1 TiB、可打印 ASCII ≤ 255 字节）由接收方校验，不属于解码错误。 */
data class FileEntry(val name: String, val size: ULong, val mime: String, val mtime: Long? = null)

data class FileOffer(val transferId: TransferId, val files: List<FileEntry>) : ProtocolMessage {
    override val type get() = MessageType.FILE_OFFER
}

data class FileOfferReply(val transferId: TransferId, val decision: OfferDecision) : ProtocolMessage {
    override val type get() = MessageType.FILE_OFFER_REPLY
}

data class FileChunk(val transferId: TransferId, val fileIndex: UInt, val offset: ULong, val data: Bytes) : ProtocolMessage {
    init { require(data.size in 1..ProtocolConstants.MAX_CHUNK) { "chunk size must be 1..${ProtocolConstants.MAX_CHUNK}" } }
    override val type get() = MessageType.FILE_CHUNK
}

data class FileEnd(val transferId: TransferId, val fileIndex: UInt, val sha256: Bytes) : ProtocolMessage {
    init { requireSize("sha256", sha256, 32) }
    override val type get() = MessageType.FILE_END
}

data class FileAck(val transferId: TransferId, val fileIndex: UInt, val persisted: ULong) : ProtocolMessage {
    override val type get() = MessageType.FILE_ACK
}

data class FileResult(val transferId: TransferId, val fileIndex: UInt, val status: FileResultStatus, val detail: String? = null) : ProtocolMessage {
    override val type get() = MessageType.FILE_RESULT
}

data class FileReadFailed(val transferId: TransferId, val fileIndex: UInt) : ProtocolMessage {
    override val type get() = MessageType.FILE_READ_FAILED
}

data class TransferCancel(val transferId: TransferId, val reason: TransferCancelReason) : ProtocolMessage {
    override val type get() = MessageType.TRANSFER_CANCEL
}

data class ResumeRequest(val transferId: TransferId) : ProtocolMessage {
    override val type get() = MessageType.RESUME_REQUEST
}

data class ResumeFileState(val fileIndex: UInt, val state: ResumeFileStatus, val persisted: ULong)

data class ResumeState(val transferId: TransferId, val files: List<ResumeFileState>) : ProtocolMessage {
    override val type get() = MessageType.RESUME_STATE
}

data class ResumeFilePlan(val fileIndex: UInt, val action: ResumeAction, val start: ULong, val size: ULong? = null, val mtime: Long? = null)

data class ResumePlan(val transferId: TransferId, val files: List<ResumeFilePlan>) : ProtocolMessage {
    override val type get() = MessageType.RESUME_PLAN
}
