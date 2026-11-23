package com.lantransfer.testing

import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
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
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.QrPairRequest
import com.lantransfer.protocol.QrPairResult
import com.lantransfer.protocol.QrPairResultCode
import com.lantransfer.protocol.QrPayload
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
import com.lantransfer.protocol.Text
import com.lantransfer.protocol.TextAck
import com.lantransfer.protocol.TextCorrupt
import com.lantransfer.protocol.TransferCancel
import com.lantransfer.protocol.TransferCancelReason
import com.lantransfer.protocol.TransferId
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonObjectBuilder
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/**
 * 测试向量中的模型 ↔ 协议模型（写法见 protocol/test-vectors/README.md）。向量生成工具与两端一致性测试共用这一套映射；
 * C# 侧的同名映射在 LanTransfer.Testing/VectorJson.cs。
 */
object VectorJson {
    fun ipv4(v: Int): String = "${(v ushr 24) and 0xFF}.${(v ushr 16) and 0xFF}.${(v ushr 8) and 0xFF}.${v and 0xFF}"

    fun parseIpv4(s: String): Int {
        val p = s.split('.').map { it.toInt() }
        require(p.size == 4 && p.all { it in 0..255 }) { "bad IPv4 $s" }
        return (p[0] shl 24) or (p[1] shl 16) or (p[2] shl 8) or p[3]
    }

    fun version(v: ProtocolVersion): String = "${v.major}.${v.minor}"

    fun parseVersion(s: String): ProtocolVersion = s.split('.').let { ProtocolVersion(it[0].toInt(), it[1].toInt()) }

    private fun JsonObjectBuilder.hex(key: String, b: Bytes) = put(key, b.toHex())

    private fun JsonObjectBuilder.hex(key: String, b: Bytes16) = put(key, b.toHex())

    private fun JsonObjectBuilder.u64(key: String, v: ULong) = put(key, v.toString())

    private fun JsonObjectBuilder.i64(key: String, v: Long) = put(key, v.toString())

    private fun JsonObject.bytes(key: String) = Bytes.ofHex(string(key))

    private fun JsonObject.b16(key: String) = Bytes16.ofHex(string(key))

    private fun JsonObject.u64(key: String) = string(key).toULong()

    private fun JsonObject.i64(key: String) = string(key).toLong()

    private fun JsonObject.u32(key: String) = getValue(key).jsonPrimitive.content.toUInt()

    private inline fun <reified E : Enum<E>> JsonObject.enumOf(key: String): E = enumValueOf(string(key))

    fun messageModel(m: ProtocolMessage): JsonObject = buildJsonObject {
        when (m) {
            is SessionResult -> put("result", m.result.name)
            is SessionClose -> put("reason", m.reason.name)
            Heartbeat, PairComplete -> Unit
            is PairRequest -> hex("commitment", m.commitment)
            is PairNonce -> hex("nonce", m.nonce)
            is PairReveal -> hex("nonce", m.nonce)
            is PairDecision -> put("decision", m.decision.name)
            is PairCancel -> put("reason", m.reason.name)
            is QrPairRequest -> hex("token", m.token)
            is QrPairResult -> put("result", m.result.name)
            is Text -> { hex("message_id", m.messageId.value); i64("sent_at", m.sentAt); hex("text", m.text) }
            is TextAck -> hex("message_id", m.messageId.value)
            is TextCorrupt -> hex("message_id", m.messageId.value)
            is FileOffer -> {
                hex("transfer_id", m.transferId.value)
                put("files", JsonArray(m.files.map { f ->
                    buildJsonObject {
                        put("name", f.name); u64("size", f.size); put("mime", f.mime); f.mtime?.let { i64("mtime", it) }
                    }
                }))
            }
            is FileOfferReply -> { hex("transfer_id", m.transferId.value); put("decision", m.decision.name) }
            is FileChunk -> { hex("transfer_id", m.transferId.value); put("file_index", m.fileIndex.toLong()); u64("offset", m.offset); hex("data", m.data) }
            is FileEnd -> { hex("transfer_id", m.transferId.value); put("file_index", m.fileIndex.toLong()); hex("sha256", m.sha256) }
            is FileAck -> { hex("transfer_id", m.transferId.value); put("file_index", m.fileIndex.toLong()); u64("persisted", m.persisted) }
            is FileResult -> {
                hex("transfer_id", m.transferId.value); put("file_index", m.fileIndex.toLong()); put("status", m.status.name)
                m.detail?.let { put("detail", it) }
            }
            is FileReadFailed -> { hex("transfer_id", m.transferId.value); put("file_index", m.fileIndex.toLong()) }
            is TransferCancel -> { hex("transfer_id", m.transferId.value); put("reason", m.reason.name) }
            is ResumeRequest -> hex("transfer_id", m.transferId.value)
            is ResumeState -> {
                hex("transfer_id", m.transferId.value)
                put("files", JsonArray(m.files.map { f ->
                    buildJsonObject { put("file_index", f.fileIndex.toLong()); put("state", f.state.name); u64("persisted", f.persisted) }
                }))
            }
            is ResumePlan -> {
                hex("transfer_id", m.transferId.value)
                put("files", JsonArray(m.files.map { f ->
                    buildJsonObject {
                        put("file_index", f.fileIndex.toLong()); put("action", f.action.name); u64("start", f.start)
                        f.size?.let { u64("size", it) }; f.mtime?.let { i64("mtime", it) }
                    }
                }))
            }
        }
    }

    fun message(type: MessageType, o: JsonObject): ProtocolMessage = when (type) {
        MessageType.SESSION_RESULT -> SessionResult(o.enumOf<SessionResultCode>("result"))
        MessageType.SESSION_CLOSE -> SessionClose(o.enumOf<SessionCloseReason>("reason"))
        MessageType.HEARTBEAT -> Heartbeat
        MessageType.PAIR_REQUEST -> PairRequest(o.bytes("commitment"))
        MessageType.PAIR_NONCE -> PairNonce(o.bytes("nonce"))
        MessageType.PAIR_REVEAL -> PairReveal(o.bytes("nonce"))
        MessageType.PAIR_DECISION -> PairDecision(o.enumOf<PairDecisionCode>("decision"))
        MessageType.PAIR_CANCEL -> PairCancel(o.enumOf<PairCancelReason>("reason"))
        MessageType.PAIR_COMPLETE -> PairComplete
        MessageType.QR_PAIR_REQUEST -> QrPairRequest(o.b16("token"))
        MessageType.QR_PAIR_RESULT -> QrPairResult(o.enumOf<QrPairResultCode>("result"))
        MessageType.TEXT -> Text(MessageId(o.b16("message_id")), o.i64("sent_at"), o.bytes("text"))
        MessageType.TEXT_ACK -> TextAck(MessageId(o.b16("message_id")))
        MessageType.TEXT_CORRUPT -> TextCorrupt(MessageId(o.b16("message_id")))
        MessageType.FILE_OFFER -> FileOffer(TransferId(o.b16("transfer_id")), o.array("files").map {
            val f = it.jsonObject
            FileEntry(f.string("name"), f.u64("size"), f.string("mime"), f["mtime"]?.let { _ -> f.i64("mtime") })
        })
        MessageType.FILE_OFFER_REPLY -> FileOfferReply(TransferId(o.b16("transfer_id")), o.enumOf<OfferDecision>("decision"))
        MessageType.FILE_CHUNK -> FileChunk(TransferId(o.b16("transfer_id")), o.u32("file_index"), o.u64("offset"), o.bytes("data"))
        MessageType.FILE_END -> FileEnd(TransferId(o.b16("transfer_id")), o.u32("file_index"), o.bytes("sha256"))
        MessageType.FILE_ACK -> FileAck(TransferId(o.b16("transfer_id")), o.u32("file_index"), o.u64("persisted"))
        MessageType.FILE_RESULT -> FileResult(TransferId(o.b16("transfer_id")), o.u32("file_index"), o.enumOf<FileResultStatus>("status"), o["detail"]?.jsonPrimitive?.content)
        MessageType.FILE_READ_FAILED -> FileReadFailed(TransferId(o.b16("transfer_id")), o.u32("file_index"))
        MessageType.TRANSFER_CANCEL -> TransferCancel(TransferId(o.b16("transfer_id")), o.enumOf<TransferCancelReason>("reason"))
        MessageType.RESUME_REQUEST -> ResumeRequest(TransferId(o.b16("transfer_id")))
        MessageType.RESUME_STATE -> ResumeState(TransferId(o.b16("transfer_id")), o.array("files").map {
            val f = it.jsonObject
            ResumeFileState(f.u32("file_index"), f.enumOf<ResumeFileStatus>("state"), f.u64("persisted"))
        })
        MessageType.RESUME_PLAN -> ResumePlan(TransferId(o.b16("transfer_id")), o.array("files").map {
            val f = it.jsonObject
            ResumeFilePlan(f.u32("file_index"), f.enumOf<ResumeAction>("action"), f.u64("start"), f["size"]?.let { _ -> f.u64("size") }, f["mtime"]?.let { _ -> f.i64("mtime") })
        })
    }

    fun announcementModel(a: Announcement): JsonObject = buildJsonObject {
        put("device_id", a.deviceId.value.toHex()); put("display_name", a.displayName); put("device_type", a.deviceType.name)
        put("proto_major", a.version.major); put("proto_minor", a.version.minor); put("port", a.port)
    }

    fun announcement(o: JsonObject): Announcement = Announcement(
        DeviceId(o.b16("device_id")), o.string("display_name"), enumValueOf<DeviceType>(o.string("device_type")),
        ProtocolVersion(o.getValue("proto_major").jsonPrimitive.int, o.getValue("proto_minor").jsonPrimitive.int), o.getValue("port").jsonPrimitive.int,
    )

    fun qrModel(p: QrPayload): JsonObject = buildJsonObject {
        put("device_id", p.deviceId.value.toHex()); put("display_name", p.displayName)
        put("ipv4_list", JsonArray(p.ipv4List.map { JsonPrimitive(ipv4(it)) })); put("port", p.port)
        put("fingerprint", p.fingerprint.toHex()); put("token", p.token.toHex())
    }

    fun qr(o: JsonObject): QrPayload = QrPayload(
        DeviceId(o.b16("device_id")), o.string("display_name"), o.getValue("ipv4_list").jsonArray.map { parseIpv4(it.jsonPrimitive.content) },
        o.getValue("port").jsonPrimitive.int, o.bytes("fingerprint"), o.b16("token"),
    )
}
