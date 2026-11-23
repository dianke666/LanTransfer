package com.lantransfer.testing

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileAck
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.FileEnd
import com.lantransfer.protocol.FileEntry
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.FileReadFailed
import com.lantransfer.protocol.FileResult
import com.lantransfer.protocol.FileResultStatus
import com.lantransfer.protocol.Heartbeat
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.MessageType
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.PairCancel
import com.lantransfer.protocol.PairCancelReason
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairDecision
import com.lantransfer.protocol.PairDecisionCode
import com.lantransfer.protocol.PairNonce
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.PairReveal
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.QrPairRequest
import com.lantransfer.protocol.QrPairResult
import com.lantransfer.protocol.QrPairResultCode
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumeFilePlan
import com.lantransfer.protocol.ResumeFileState
import com.lantransfer.protocol.ResumeFileStatus
import com.lantransfer.protocol.ResumePlan
import com.lantransfer.protocol.ResumeRequest
import com.lantransfer.protocol.ResumeState
import com.lantransfer.protocol.SessionClose
import com.lantransfer.protocol.SessionCloseReason
import com.lantransfer.protocol.SessionResult
import com.lantransfer.protocol.SessionResultCode
import com.lantransfer.protocol.StrictUtf8
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TextCorrupt
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.TransferId
import io.kotest.property.Arb
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.byte
import io.kotest.property.arbitrary.byteArray
import io.kotest.property.arbitrary.choice
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.constant
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.enum
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import io.kotest.property.arbitrary.orNull
import io.kotest.property.arbitrary.uInt
import io.kotest.property.arbitrary.uLong

// C1.9 每种消息类型的生成器（Property 1–4 等使用）。C# 侧同名生成器在 LanTransfer.Testing/MessageGens.cs。

/** 任意字节串，长度 0..[maxLength]。 */
fun Gens.bytes(maxLength: Int = 64): Arb<Bytes> =
    Arb.byteArray(Arb.int(0..maxLength), Arb.byte()).map { Bytes.of(it) }

/** 恰好 [size] 字节的字节串。 */
fun Gens.bytesOfSize(size: Int): Arb<Bytes> = Arb.byteArray(Arb.constant(size), Arb.byte()).map { Bytes.of(it) }

fun Gens.bytes16(): Arb<Bytes16> = Arb.byteArray(Arb.constant(16), Arb.byte()).map { Bytes16.of(it) }

fun Gens.transferId(): Arb<TransferId> = bytes16().map { TransferId(it) }

fun Gens.messageId(): Arb<MessageId> = bytes16().map { MessageId(it) }

/** u32：偏向 0、小值与上界。 */
fun Gens.u32(): Arb<UInt> = Arb.choose(3 to Arb.uInt(0u..1000u), 1 to Arb.uInt(), 1 to Arb.element(0u, UInt.MAX_VALUE))

/** u64：偏向 0、2³²、1 TiB 等边界。 */
fun Gens.u64(): Arb<ULong> = Arb.choose(
    3 to Arb.uLong(0uL..(1uL shl 20)),
    2 to Arb.element(0uL, (1uL shl 32) - 1uL, 1uL shl 32, (1uL shl 32) + 1uL, 1uL shl 40, (1uL shl 40) + 1uL, ULong.MAX_VALUE),
    1 to Arb.uLong(),
)

/** i64（时间戳等）：偏向常见毫秒时间与边界。 */
fun Gens.i64(): Arb<Long> = Arb.choose(
    3 to Arb.long(1_600_000_000_000L..2_000_000_000_000L),
    1 to Arb.element(0L, -1L, Long.MIN_VALUE, Long.MAX_VALUE),
    1 to Arb.long(),
)

/** 协议 String 字段：不含 Unpaired_Surrogate 的 Unicode 文本。 */
fun Gens.protocolString(maxFragments: Int = 8): Arb<String> = unicodeText(maxFragments)

/** 可打印 ASCII 的 MIME 类型。 */
fun Gens.mime(): Arb<String> = Arb.element("application/octet-stream", "text/plain", "image/jpeg", "video/mp4", "application/pdf", "")

/** FileEntry：size 偏向 0、2³²、1 TiB；mtime 随机出现或缺省。 */
fun Gens.fileEntry(): Arb<FileEntry> = Arb.bind(protocolString(4), u64(), mime(), i64().orNull(0.3)) { name, size, mime, mtime ->
    FileEntry(name, size, mime, mtime)
}

fun Gens.resumeFileState(): Arb<ResumeFileState> =
    Arb.bind(u32(), Arb.enum<ResumeFileStatus>(), u64()) { i, s, p -> ResumeFileState(i, s, p) }

fun Gens.resumeFilePlan(): Arb<ResumeFilePlan> =
    Arb.bind(u32(), Arb.enum<ResumeAction>(), u64(), u64().orNull(0.5), i64().orNull(0.5)) { i, a, start, size, mtime ->
        ResumeFilePlan(i, a, start, size, mtime)
    }

/** 文字正文：大多为合法 UTF‑8，偶尔为任意字节（正文类型是 Bytes）。 */
fun Gens.textBody(): Arb<Bytes> = Arb.choose(
    4 to unicodeText().map { Bytes.of(StrictUtf8.encode(it)) },
    1 to bytes(32),
)

/** C1.9 中任意一种消息的任意实例。 */
fun Gens.protocolMessage(): Arb<ProtocolMessage> = Arb.choice(
    Arb.enum<SessionResultCode>().map { SessionResult(it) },
    Arb.enum<SessionCloseReason>().map { SessionClose(it) },
    Arb.constant(Heartbeat),
    bytesOfSize(32).map { PairRequest(it) },
    bytesOfSize(32).map { PairNonce(it) },
    bytesOfSize(32).map { PairReveal(it) },
    Arb.enum<PairDecisionCode>().map { PairDecision(it) },
    Arb.enum<PairCancelReason>().map { PairCancel(it) },
    Arb.constant(PairComplete),
    bytes16().map { QrPairRequest(it) },
    Arb.enum<QrPairResultCode>().map { QrPairResult(it) },
    Arb.bind(messageId(), i64(), textBody()) { id, t, body -> Text(id, t, body) },
    messageId().map { TextAck(it) },
    messageId().map { TextCorrupt(it) },
    Arb.bind(transferId(), Arb.list(fileEntry(), 0..5)) { id, files -> FileOffer(id, files) },
    Arb.bind(transferId(), Arb.enum<OfferDecision>()) { id, d -> FileOfferReply(id, d) },
    Arb.bind(transferId(), u32(), u64(), Arb.byteArray(Arb.choose(4 to Arb.int(1..512), 1 to Arb.constant(ProtocolConstants.MAX_CHUNK)), Arb.byte())) { id, i, off, data ->
        FileChunk(id, i, off, Bytes.of(data))
    },
    Arb.bind(transferId(), u32(), bytesOfSize(32)) { id, i, h -> FileEnd(id, i, h) },
    Arb.bind(transferId(), u32(), u64()) { id, i, p -> FileAck(id, i, p) },
    Arb.bind(transferId(), u32(), Arb.enum<FileResultStatus>(), protocolString(3).orNull(0.5)) { id, i, s, d -> FileResult(id, i, s, d) },
    Arb.bind(transferId(), u32()) { id, i -> FileReadFailed(id, i) },
    Arb.bind(transferId(), Arb.enum<TransferCancelReason>()) { id, r -> TransferCancel(id, r) },
    transferId().map { ResumeRequest(it) },
    Arb.bind(transferId(), Arb.list(resumeFileState(), 0..5)) { id, f -> ResumeState(id, f) },
    Arb.bind(transferId(), Arb.list(resumeFilePlan(), 0..5)) { id, f -> ResumePlan(id, f) },
)

/** 全部消息类型（覆盖检查用）。 */
val allMessageTypes: List<MessageType> = MessageType.entries
