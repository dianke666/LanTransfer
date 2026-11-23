package com.lantransfer.vectorgen

import com.lantransfer.protocol.AnnouncementCodec
import com.lantransfer.protocol.Announcement
import com.lantransfer.protocol.Base32
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.EphemeralKeySource
import com.lantransfer.protocol.FileAck
import com.lantransfer.protocol.FileChunk
import com.lantransfer.protocol.FileEnd
import com.lantransfer.protocol.FileEntry
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.FileOfferReply
import com.lantransfer.protocol.FileReadFailed
import com.lantransfer.protocol.FileResult
import com.lantransfer.protocol.FileResultStatus
import com.lantransfer.protocol.FrameEncoder
import com.lantransfer.protocol.HandshakeIdentity
import com.lantransfer.protocol.HandshakeIdentityCodec
import com.lantransfer.protocol.Heartbeat
import com.lantransfer.protocol.Intent
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.NoiseXX
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.PairCancel
import com.lantransfer.protocol.PairCancelReason
import com.lantransfer.protocol.PairComplete
import com.lantransfer.protocol.PairDecision
import com.lantransfer.protocol.PairDecisionCode
import com.lantransfer.protocol.PairNonce
import com.lantransfer.protocol.PairRequest
import com.lantransfer.protocol.PairReveal
import com.lantransfer.protocol.PreambleCodec
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.ProtocolVersion
import com.lantransfer.protocol.QrCodec
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
import com.lantransfer.protocol.Role
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
import com.lantransfer.protocol.VerificationCode
import com.lantransfer.protocol.X25519KeyPair
import com.lantransfer.testing.InvalidUtf8
import com.lantransfer.testing.TextCharClass
import com.lantransfer.testing.VectorJson
import com.lantransfer.testing.cps
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonObjectBuilder
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import com.lantransfer.domain.FileNameSanitizer
import com.lantransfer.domain.FsProfile
import java.io.File
import java.security.MessageDigest
import java.time.LocalDateTime

@OptIn(kotlinx.serialization.ExperimentalSerializationApi::class) // prettyPrintIndent
private val json = Json { prettyPrint = true; prettyPrintIndent = "  " }

private fun hex(b: ByteArray) = Bytes.of(b).toHex()

private fun seq(size: Int, seed: Int) = ByteArray(size) { (seed + it).toByte() }

private fun id16(seed: Int) = Bytes16.of(seq(16, seed))

private fun frame(m: ProtocolMessage) = FrameEncoder.encodeOrThrow(m)

private fun u32(v: Long) = byteArrayOf((v ushr 24).toByte(), (v ushr 16).toByte(), (v ushr 8).toByte(), v.toByte())

/** 手工拼一个 TLV 字段（构造非法或含未知 tag 的样例用）。 */
private fun field(tag: Int, value: ByteArray) = byteArrayOf(tag.toByte()) + u32(value.size.toLong()) + value

private fun header(type: Int, length: Long) = byteArrayOf(0x4C, 0x54, type.toByte()) + u32(length)

private fun doc(description: String, vectors: List<JsonObject>, extra: JsonObjectBuilder.() -> Unit = {}): JsonObject = buildJsonObject {
    put("description", description)
    put("protocol_version", "1.0")
    extra()
    put("vectors", JsonArray(vectors))
}

private fun frameVector(name: String, m: ProtocolMessage) = buildJsonObject {
    put("name", name)
    put("type", m.type.name)
    put("model", VectorJson.messageModel(m))
    put("bytes", hex(frame(m)))
}

private val sha256Empty = MessageDigest.getInstance("SHA-256").digest(ByteArray(0))

private fun frames(): JsonObject {
    val t = TransferId(id16(0x40))
    val samples = listOf(
        "SESSION_RESULT ACCEPTED" to SessionResult(SessionResultCode.ACCEPTED),
        "SESSION_RESULT DUPLICATE" to SessionResult(SessionResultCode.DUPLICATE),
        "SESSION_CLOSE UNPAIRED" to SessionClose(SessionCloseReason.UNPAIRED),
        "HEARTBEAT" to Heartbeat,
        "PAIR_REQUEST" to PairRequest(Bytes.of(seq(32, 0x10))),
        "PAIR_NONCE" to PairNonce(Bytes.of(seq(32, 0x30))),
        "PAIR_REVEAL" to PairReveal(Bytes.of(seq(32, 0x50))),
        "PAIR_DECISION BUSY" to PairDecision(PairDecisionCode.BUSY),
        "PAIR_CANCEL TIMEOUT" to PairCancel(PairCancelReason.TIMEOUT),
        "PAIR_COMPLETE" to PairComplete,
        "QR_PAIR_REQUEST" to QrPairRequest(id16(0x70)),
        "QR_PAIR_RESULT TOKEN_INVALID" to QrPairResult(QrPairResultCode.TOKEN_INVALID),
        "TEXT 含 CRLF、NUL 与表情" to Text(MessageId(id16(0x20)), 1_735_693_805_000L, Bytes.of(StrictUtf8.encode("Hello\r\n世界\u0000" + cps(0x1F600)))),
        "TEXT 空文本" to Text(MessageId(id16(0x21)), 0L, Bytes.EMPTY),
        "TEXT_ACK" to TextAck(MessageId(id16(0x20))),
        "TEXT_CORRUPT" to TextCorrupt(MessageId(id16(0x22))),
        "FILE_OFFER 大小 0、2³²、1 TiB" to FileOffer(t, listOf(
            FileEntry("照片.jpg", 0uL, "image/jpeg", 1_735_693_805_000L),
            FileEntry("big.bin", 1uL shl 32, "application/octet-stream"),
            FileEntry("max.iso", 1uL shl 40, "", -1L),
        )),
        "FILE_OFFER_REPLY INVALID_OFFER" to FileOfferReply(t, OfferDecision.INVALID_OFFER),
        "FILE_CHUNK" to FileChunk(t, 2u, 1uL shl 32, Bytes.of(byteArrayOf(0, 1, 2, -1, -2))),
        "FILE_END 空文件" to FileEnd(t, 0u, Bytes.of(sha256Empty)),
        "FILE_ACK" to FileAck(t, 1u, 262_144uL),
        "FILE_RESULT WRITE_FAILED 带说明" to FileResult(t, 1u, FileResultStatus.WRITE_FAILED, "磁盘已满"),
        "FILE_RESULT COMPLETED" to FileResult(t, 0u, FileResultStatus.COMPLETED),
        "FILE_READ_FAILED" to FileReadFailed(t, UInt.MAX_VALUE),
        "TRANSFER_CANCEL UNKNOWN_TRANSFER" to TransferCancel(t, TransferCancelReason.UNKNOWN_TRANSFER),
        "RESUME_REQUEST" to ResumeRequest(t),
        "RESUME_STATE" to ResumeState(t, listOf(
            ResumeFileState(0u, ResumeFileStatus.COMPLETED, 100uL),
            ResumeFileState(1u, ResumeFileStatus.PARTIAL, 65_536uL),
            ResumeFileState(2u, ResumeFileStatus.NONE, 0uL),
            ResumeFileState(3u, ResumeFileStatus.TERMINAL, 0uL),
        )),
        "RESUME_PLAN" to ResumePlan(t, listOf(
            ResumeFilePlan(0u, ResumeAction.SKIP, 0uL),
            ResumeFilePlan(1u, ResumeAction.CONTINUE, 65_536uL),
            ResumeFilePlan(2u, ResumeAction.RESTART, 0uL, 4096uL, 1_735_693_805_000L),
        )),
    )
    val vectors = samples.map { (n, m) -> frameVector(n, m) }.toMutableList()

    // 含未知 tag 的样例：顶层与 List 元素内部各插入未定义字段
    val close = SessionClose(SessionCloseReason.NORMAL)
    val closePayload = field(0, byteArrayOf(9)) + field(1, byteArrayOf(0)) + field(200, seq(3, 1))
    vectors += buildJsonObject {
        put("name", "SESSION_CLOSE 含未知 tag 0 与 200")
        put("type", close.type.name)
        put("model", VectorJson.messageModel(close))
        put("bytes", hex(header(0x02, closePayload.size.toLong()) + closePayload))
        put("canonical_bytes", hex(frame(close)))
    }
    val offer = FileOffer(t, listOf(FileEntry("a.txt", 3uL, "text/plain")))
    val entry = field(1, "a.txt".encodeToByteArray()) + field(2, byteArrayOf(0, 0, 0, 0, 0, 0, 0, 3)) + field(3, "text/plain".encodeToByteArray()) + field(9, byteArrayOf(1))
    val list = u32(1) + u32(entry.size.toLong()) + entry
    val offerPayload = field(1, t.value.toByteArray()) + field(2, list) + field(3, byteArrayOf(7, 7))
    vectors += buildJsonObject {
        put("name", "FILE_OFFER 顶层与 FileEntry 内含未知 tag")
        put("type", offer.type.name)
        put("model", VectorJson.messageModel(offer))
        put("bytes", hex(header(0x30, offerPayload.size.toLong()) + offerPayload))
        put("canonical_bytes", hex(frame(offer)))
    }
    return doc("每种 Protocol_Message 的有效 Frame；含未知 tag 的样例另给出规范编码 canonical_bytes", vectors)
}

private fun framesInvalid(): JsonObject {
    fun v(name: String, bytes: ByteArray, error: String, before: Int = 0) = buildJsonObject {
        put("name", name); put("version", "1.0"); put("bytes", hex(bytes)); put("error", error); put("messages_before_error", before)
    }
    val hb = frame(Heartbeat)
    fun malformed(name: String, type: Int, payload: ByteArray) = v(name, header(type, payload.size.toLong()) + payload, "MALFORMED_PAYLOAD")
    val badName = field(1, seq(16, 0x40)) + field(2, u32(1) + u32(20) + field(1, byteArrayOf(0xC0.toByte(), 0x80.toByte())) + field(2, ByteArray(8)) + field(3, ByteArray(0)))
    val trailing = field(1, seq(16, 0x40)) + field(2, u32(0) + byteArrayOf(1))
    return doc("各类解码错误；version 为解码器使用的协商版本", listOf(
        v("帧头 magic 错误", byteArrayOf(0x4C, 0x55, 0x03) + u32(0), "BAD_MAGIC"),
        v("合法 HEARTBEAT 之后 magic 错误", hb + byteArrayOf(0x00, 0x00, 0x03) + u32(0), "BAD_MAGIC", 1),
        v("未定义的类型 0x04", header(0x04, 0), "UNKNOWN_TYPE"),
        v("未定义的类型 0xFF", header(0xFF, 0), "UNKNOWN_TYPE"),
        v("length 为 MAX_PAYLOAD + 1（只有帧头）", header(0x20, 2_097_153), "PAYLOAD_TOO_LARGE"),
        v("length 为 0xFFFFFFFF（只有帧头）", header(0x03, 0xFFFF_FFFFL), "PAYLOAD_TOO_LARGE"),
        malformed("SESSION_RESULT 缺少必需字段", 0x01, ByteArray(0)),
        malformed("tag 不递增", 0x02, field(5, byteArrayOf(0)) + field(1, byteArrayOf(0))),
        malformed("同一 tag 出现两次", 0x02, field(1, byteArrayOf(0)) + field(1, byteArrayOf(0))),
        malformed("枚举值未定义", 0x01, field(1, byteArrayOf(9))),
        malformed("定长字段长度不符", 0x21, field(1, seq(15, 0))),
        malformed("字段长度越过缓冲区", 0x01, byteArrayOf(1) + u32(5) + byteArrayOf(0)),
        malformed("字段头被截断", 0x01, byteArrayOf(1, 0, 0)),
        malformed("TEXT 缺少 text 字段", 0x20, field(1, seq(16, 0)) + field(2, ByteArray(8))),
        malformed("FILE_OFFER 文件名不是合法 UTF-8", 0x30, badName),
        malformed("List 元素之后有多余字节", 0x30, trailing),
        malformed("FILE_CHUNK data 为空", 0x32, field(1, seq(16, 0)) + field(2, u32(0)) + field(3, ByteArray(8)) + field(4, ByteArray(0))),
    ))
}

/** 每个字符类的样例文本；边缘空白类放在首尾。 */
private fun textMessages(): JsonObject {
    val id = id16(0x60)
    val sentAt = 1_735_693_805_000L
    val vectors = ArrayList<JsonObject>()
    fun add(name: String, text: String, classes: List<String>) {
        val utf8 = StrictUtf8.encode(text)
        vectors += buildJsonObject {
            put("name", name)
            put("class", JsonArray(classes.map { JsonPrimitive(it) }))
            put("text_utf8", hex(utf8))
            put("frame", hex(frame(Text(MessageId(id), sentAt, Bytes.of(utf8)))))
        }
    }
    TextCharClass.entries.forEach { c ->
        val text = if (c == TextCharClass.EDGE_WHITESPACE) c.samples.joinToString("") + "中" + c.samples.reversed().joinToString("") else "a" + c.samples.joinToString("|") + "b"
        add("字符类 ${c.label}", text, listOf(c.label))
    }
    add("开头的 U+FEFF 与结尾的 U+3000", "\uFEFF开头\u3000", listOf("bom", "edge-whitespace", "ideographic-space"))
    add("混合：CRLF、单独 CR、LF、TAB、NUL", "第一行\r\n第二行\r第三行\n\t缩进\u0000之后", listOf("crlf", "cr", "lf", "tab", "nul"))
    add("空文本", "", emptyList())
    return doc("需求 12.2–12.4 每个字符类的 TEXT；frame 以 message_id、sent_at 编码", vectors) {
        put("message_id", id.toHex())
        put("sent_at", sentAt.toString())
    }
}

private fun utf8Invalid(): JsonObject {
    val id = id16(0x61)
    val samples = listOf(
        Triple("截断的 3 字节序列", InvalidUtf8.TRUNCATED, "61e282"),
        Triple("截断的 4 字节序列", InvalidUtf8.TRUNCATED, "f09f98"),
        Triple("非最短形式 C0 80", InvalidUtf8.OVERLONG, "c080"),
        Triple("非最短形式 E0 80 80", InvalidUtf8.OVERLONG, "e08080"),
        Triple("编码 U+D800", InvalidUtf8.SURROGATE, "eda080"),
        Triple("编码 U+DFFF", InvalidUtf8.SURROGATE, "61edbfbf62"),
        Triple("大于 U+10FFFF", InvalidUtf8.ABOVE_MAX, "f4908080"),
        Triple("F5 开头", InvalidUtf8.ABOVE_MAX, "f5808080"),
        Triple("非法字节 FF", InvalidUtf8.INVALID_BYTE, "ff"),
        Triple("没有前导字节的续字节", InvalidUtf8.UNEXPECTED_CONTINUATION, "6180"),
        Triple("前导字节后缺少续字节", InvalidUtf8.MISSING_CONTINUATION, "e228a1"),
    )
    return doc("非法 UTF-8 的 TEXT：能作为 TEXT 解码，但严格 UTF-8 解码失败，应回复 TEXT_CORRUPT", samples.map { (name, kind, bytes) ->
        buildJsonObject {
            put("name", name); put("kind", kind); put("text_utf8", bytes)
            put("frame", hex(frame(Text(MessageId(id), 0L, Bytes.ofHex(bytes)))))
            put("expected", "TEXT_CORRUPT")
        }
    })
}

/** 32 个码点、含中文和表情的名称。 */
private val name32 = "小明的手机" + cps(0x1F600, 0x1F4F1) + "ABCDEFGHIJKLMNOPQRSTUVWXY"

private fun announcements(): JsonObject {
    check(name32.codePointCount(0, name32.length) == 32)
    val id = DeviceId(id16(0x80))
    fun valid(name: String, a: Announcement) = buildJsonObject {
        put("name", name); put("model", VectorJson.announcementModel(a)); put("bytes", hex(AnnouncementCodec.encode(a)))
    }
    fun raw(nameBytes: ByteArray, type: Int, port: Int, magic: ByteArray = "LTDA".encodeToByteArray(), omitPort: Boolean = false): ByteArray =
        magic + field(1, id.value.toByteArray()) + field(2, nameBytes) + field(3, byteArrayOf(type.toByte())) + field(4, byteArrayOf(1)) +
            field(5, byteArrayOf(0)) + (if (omitPort) ByteArray(0) else field(6, byteArrayOf((port ushr 8).toByte(), port.toByte())))
    fun drop(name: String, bytes: ByteArray) = buildJsonObject { put("name", name); put("bytes", hex(bytes)); put("error", "DROP") }
    val good = raw("Pixel".encodeToByteArray(), 1, 42580)
    return doc("Discovery_Announcement：有效样例断言往返，含 error 的样例应被丢弃", listOf(
        valid("32 个码点、含中文和表情的名称", Announcement(id, name32, DeviceType.ANDROID, ProtocolVersion.V1_0, 42580)),
        valid("32 个表情组成的名称", Announcement(id, cps(*IntArray(32) { 0x1F600 + it }), DeviceType.WINDOWS, ProtocolVersion(1, 3), 42599)),
        valid("ASCII 名称", Announcement(id, "Pixel 9", DeviceType.ANDROID, ProtocolVersion.V1_0, 1)),
        valid("iOS 设备", Announcement(id, "iPhone 16 Pro", DeviceType.IOS, ProtocolVersion.V1_0, 42580)),
        drop("超过 512 字节", good + field(9, ByteArray(512))),
        drop("magic 不符", raw("Pixel".encodeToByteArray(), 1, 42580, "LTDB".encodeToByteArray())),
        drop("缺少端口字段", raw("Pixel".encodeToByteArray(), 1, 42580, omitPort = true)),
        drop("名称不是合法 UTF-8", raw(byteArrayOf(0x61, 0xC0.toByte(), 0x80.toByte()), 1, 42580)),
        drop("名称为空", raw(ByteArray(0), 1, 42580)),
        drop("名称 33 个码点", raw(StrictUtf8.encode(name32 + "Z"), 1, 42580)),
        drop("设备类型 4", raw("Pixel".encodeToByteArray(), 4, 42580)),
        drop("端口为 0", raw("Pixel".encodeToByteArray(), 1, 0)),
        drop("截断", good.copyOf(good.size - 1)),
    ))
}

private fun qrPayloads(): JsonObject {
    val id = DeviceId(id16(0x90))
    val fp = Bytes.of(seq(32, 0xA0))
    val token = id16(0xC0)
    fun ip(a: Int, b: Int, c: Int, d: Int) = (a shl 24) or (b shl 16) or (c shl 8) or d
    fun valid(name: String, p: QrPayload) = buildJsonObject { put("name", name); put("model", VectorJson.qrModel(p)); put("text", QrCodec.encode(p)) }
    fun bad(name: String, text: String) = buildJsonObject { put("name", name); put("text", text); put("error", "PARSE_ERROR") }
    fun rawStruct(ips: ByteArray, port: Int, name: String = "PC") = QrCodec.PREFIX + Base32.encode(
        field(1, id.value.toByteArray()) + field(2, StrictUtf8.encode(name)) + field(3, ips) + field(4, byteArrayOf((port ushr 8).toByte(), port.toByte())) +
            field(5, fp.toByteArray()) + field(6, token.toByteArray()),
    )
    val two = QrPayload(id, "办公室电脑", listOf(ip(192, 168, 1, 20), ip(10, 0, 0, 5)), 42580, fp, token)
    val four = QrPayload(id, name32, listOf(ip(192, 168, 1, 20), ip(10, 0, 0, 5), ip(172, 16, 0, 1), ip(192, 168, 43, 1)), 42599, fp, token)
    val text = QrCodec.encode(two)
    val body = text.substring(QrCodec.PREFIX.length)
    val firstLetter = body.indexOfFirst { it in 'A'..'Z' }
    val lowered = QrCodec.PREFIX + body.substring(0, firstLetter) + body[firstLetter].lowercaseChar() + body.substring(firstLetter + 1)
    val badLength = QrCodec.PREFIX + body.dropLast(generateSequence(1) { it + 1 }.first { (body.length - it) % 8 in setOf(1, 3, 6) })
    val alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    check(body.length % 8 != 0)
    val badPad = QrCodec.PREFIX + body.dropLast(1) + alphabet[alphabet.indexOf(body.last()) or 1]
    return doc("QR_Payload 文本：有效样例断言往返，含 error 的样例应整体解析失败", listOf(
        valid("2 个 IP", two),
        valid("4 个 IP、32 个码点的名称", four),
        bad("含小写字母", lowered),
        bad("含填充符 =", "$text="),
        bad("前缀错误", "LT2:$body"),
        bad("长度对 8 取模不合法", badLength),
        bad("末尾多余比特不为 0", badPad),
        bad("超过 512 字节", QrCodec.PREFIX + "A".repeat(509)),
        bad("没有 IP", rawStruct(ByteArray(0), 42580)),
        bad("5 个 IP", rawStruct(ByteArray(20) { (it / 4 + 1).toByte() }, 42580)),
        bad("IP 重复", rawStruct(ByteArray(8) { 7 }, 42580)),
        bad("IP 列表长度不是 4 的倍数", rawStruct(ByteArray(6) { 7 }, 42580)),
        bad("端口为 0", rawStruct(ByteArray(4) { 7 }, 0)),
        bad("名称为空", rawStruct(ByteArray(4) { 7 }, 42580, "")),
    ))
}

private fun preamble(): JsonObject {
    fun enc(v: ProtocolVersion) = buildJsonObject { put("name", "编码 ${VectorJson.version(v)}"); put("version", VectorJson.version(v)); put("bytes", hex(PreambleCodec.encode(v))) }
    fun bad(name: String, bytes: String) = buildJsonObject { put("name", name); put("bytes", bytes); put("error", "INVALID") }
    fun neg(local: String, remote: String, result: String) = buildJsonObject {
        put("name", "协商 $local / $remote"); put("local", local); put("remote", remote); put("negotiated", result)
    }
    return doc("连接前导与版本协商", listOf(
        enc(ProtocolVersion.V1_0), enc(ProtocolVersion(1, 7)), enc(ProtocolVersion(255, 255)),
        bad("magic 错误", "4c414e550100"), bad("长度 5 字节", "4c414e5401"), bad("长度 7 字节", "4c414e54010000"),
        neg("1.0", "1.0", "1.0"), neg("1.2", "1.0", "1.0"), neg("1.0", "1.2", "1.0"), neg("2.5", "2.3", "2.3"),
        neg("1.0", "2.0", "INCOMPATIBLE"), neg("2.0", "1.9", "INCOMPATIBLE"),
    ))
}

private fun noise(): JsonObject {
    val official = buildJsonObject {
        put("name", "cacophony Noise_XX_25519_AESGCM_SHA256")
        put("protocol_name", "Noise_XX_25519_AESGCM_SHA256")
        put("init_prologue", "4a6f686e2047616c74")
        put("init_static", "e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1")
        put("init_ephemeral", "893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a")
        put("resp_prologue", "4a6f686e2047616c74")
        put("resp_static", "4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893")
        put("resp_ephemeral", "bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b")
        put("handshake_hash", "1b7aefb1125762aa21a252890d00af54519638b76437444538f9a52f21e2e0dc")
        put("messages", JsonArray(listOf(
            "4c756477696720766f6e204d69736573" to "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c79444c756477696720766f6e204d69736573",
            "4d757272617920526f746862617264" to "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f144808843757117acceb05bd7a45733bc22015c97a9d0cbaf41b80446d5988ff5127235d76b79eade70f473d6a4ef521fdcbeda5340d01e028ba793fc059f2724a83af05f12dda0448a7621a926b379a92477fd",
            "462e20412e20486179656b" to "c90f1cf77eba4e50edb038991565e36c9758943a989229b6051244dc4fbecb6946744b401af2ee1a5881b65fbb87fd07cb6a328ececc9ce6ce84c399dc332d4fd521fa4bb7f467ce909395",
            "4361726c204d656e676572" to "bc3fa77f6aca3e8466d7dc6bea10013e88a6a29add5132b461806c",
            "4a65616e2d426170746973746520536179" to "250b01074cdfe0df2ecf8ccbf1737b15a2ddb5b52fd9a396604e9c793cee3b3bb9",
        ).map { (p, c) -> buildJsonObject { put("payload", p); put("ciphertext", c) } }))
    }

    // 本协议的固定密钥握手转录
    val sI = seq(32, 0x01); val eI = seq(32, 0x41); val sR = seq(32, 0x81); val eR = seq(32, 0xC1)
    val preI = PreambleCodec.encode(ProtocolVersion.V1_0)
    val preR = PreambleCodec.encode(ProtocolVersion.V1_0)
    val prologue = NoiseXX.prologue(preI, preR)
    val i = NoiseXX(Role.INITIATOR, X25519KeyPair.fromPrivateKey(sI), prologue, EphemeralKeySource.fixed(eI))
    val r = NoiseXX(Role.RESPONDER, X25519KeyPair.fromPrivateKey(sR), prologue, EphemeralKeySource.fixed(eR))
    val idR = HandshakeIdentity(DeviceId(id16(0xD0)), "办公室电脑", DeviceType.WINDOWS)
    val idI = HandshakeIdentity(DeviceId(id16(0xE0)), "小明的手机", DeviceType.ANDROID, Intent.PAIR)
    val p2 = HandshakeIdentityCodec.encode(idR)
    val p3 = HandshakeIdentityCodec.encode(idI)
    val m1 = i.writeMessage(ByteArray(0)); r.readMessage(m1)
    val m2 = r.writeMessage(p2); i.readMessage(m2)
    val m3 = i.writeMessage(p3); r.readMessage(m3)
    val (iSend, _) = i.split()
    val (rSend, _) = r.split()
    val f1 = frame(Heartbeat)
    val f2 = frame(PairNonce(Bytes.of(seq(32, 0x30))))
    val f3 = frame(Text(MessageId(id16(0x20)), 1_735_693_805_000L, Bytes.of(StrictUtf8.encode("你好"))))
    fun identity(h: HandshakeIdentity) = buildJsonObject {
        put("device_id", h.deviceId.value.toHex()); put("display_name", h.displayName); put("device_type", h.deviceType.name)
        h.intent?.let { put("intent", it.name) }
    }
    val transcript = buildJsonObject {
        put("name", "LanTransfer 1.0 点选配对握手（固定临时密钥）")
        put("preamble_i", hex(preI)); put("preamble_r", hex(preR)); put("prologue", hex(prologue))
        put("init_static", hex(sI)); put("init_ephemeral", hex(eI)); put("resp_static", hex(sR)); put("resp_ephemeral", hex(eR))
        put("init_static_public", X25519KeyPair.fromPrivateKey(sI).publicKey.toHex())
        put("resp_static_public", X25519KeyPair.fromPrivateKey(sR).publicKey.toHex())
        put("msg1", buildJsonObject { put("payload", ""); put("message", hex(m1)) })
        put("msg2", buildJsonObject { put("identity", identity(idR)); put("payload", hex(p2)); put("message", hex(m2)) })
        put("msg3", buildJsonObject { put("identity", identity(idI)); put("payload", hex(p3)); put("message", hex(m3)) })
        put("handshake_hash", hex(i.handshakeHash))
        put("records_i_to_r", JsonArray(listOf(f1, f3).map { f -> buildJsonObject { put("plaintext", hex(f)); put("record", hex(iSend.seal(f))) } }))
        put("records_r_to_i", JsonArray(listOf(f2).map { f -> buildJsonObject { put("plaintext", hex(f)); put("record", hex(rSend.seal(f))) } }))
    }
    return doc("Noise_XX_25519_AESGCM_SHA256：官方向量子集（official）与本协议的固定密钥转录（transcripts）", emptyList()) {
        put("official", JsonArray(listOf(official)))
        put("transcripts", JsonArray(listOf(transcript)))
    }
}

private fun verificationCodes(): JsonObject {
    val sets = listOf(
        listOf(seq(32, 0x01), seq(32, 0x21), seq(32, 0x41), seq(32, 0x61)),
        listOf(ByteArray(32), ByteArray(32) { -1 }, ByteArray(32) { 7 }, ByteArray(32) { 9 }),
        listOf(seq(32, 0x99), seq(32, 0x33), seq(32, 0x11), seq(32, 0xEE)),
    )
    return doc("配对验证码：commitment 与 SAS（6 位，左侧补零）", sets.mapIndexed { k, (pkI, pkR, nI, nR) ->
        buildJsonObject {
            put("name", "样例 ${k + 1}")
            put("pk_i", hex(pkI)); put("pk_r", hex(pkR)); put("n_i", hex(nI)); put("n_r", hex(nR))
            put("commitment", hex(VerificationCode.commitment(pkI, pkR, nI)))
            put("sas", VerificationCode.sas(pkI, pkR, nI, nR))
        }
    })
}

/** 文件名处理向量（任务 8.3）：两套期望输出与触发的步骤。含 Unpaired_Surrogate 的输入只写 input_utf16。 */
private fun filenameSanitizer(): JsonObject {
    val now = LocalDateTime.of(2025, 1, 1, 9, 30, 5)
    val inputs = listOf(
        "合法名称原样保留" to "report.pdf",
        "保留名 con.txt" to "con.txt",
        "保留名 Nul.tar.gz" to "Nul.tar.gz",
        "保留名 COM1" to "COM1",
        "保留名后跟空格 CON .txt" to "CON .txt",
        "保留名 aux .log" to "aux .log",
        "保留名 con." to "con.",
        "非保留名 Com0.txt" to "Com0.txt",
        "保留名 lpt9.log" to "lpt9.log",
        "点点 .." to "..",
        "路径 a/../b" to "a/../b",
        "Windows 路径" to "C:\\Users\\a\\b.txt",
        "以分隔符结尾 dir/" to "dir/",
        "末尾的点 report." to "report.",
        "末尾的空格和点" to "trailing. . ",
        "只有空格" to "  ",
        "三个点" to "...",
        "非法字符" to "a<b>c:d\"e|f?g*h.txt",
        "控制字符与 DEL" to "tab\there\u0001x\u007F.txt",
        "255 字节的中文名称" to "中".repeat(85),
        "超过 255 字节的中文名称" to "中".repeat(86) + ".txt",
        "超过 255 个 UTF-16 码元" to "x".repeat(300) + ".txt",
        "表情组成的长名称" to "😀".repeat(70) + ".png",
        "超长扩展名" to "a." + "x".repeat(300),
        "回退名形式的输入保持不变" to "file_20250101_093005.txt",
        "以点开头无扩展名" to ".bashrc",
        "多重扩展名" to "name.with.many.dots.tar.gz",
        "中文扩展名" to "名字.中文扩展名",
        "空串" to "",
    )
    fun utf16(s: String) = s.map { "%04x".format(it.code) }.joinToString("")
    fun vector(name: String, input: String, lone: Boolean) = buildJsonObject {
        val a = FileNameSanitizer.trace(input, FsProfile.ANDROID, now)
        val w = FileNameSanitizer.trace(input, FsProfile.WINDOWS, now)
        put("name", name)
        if (lone) put("input_utf16", utf16(input)) else put("input", input)
        put("now", "2025-01-01T09:30:05")
        put("android", a.result)
        put("windows", w.result)
        put("steps", buildJsonObject {
            put("android", JsonArray(a.steps.map { JsonPrimitive(it) }))
            put("windows", JsonArray(w.steps.map { JsonPrimitive(it) }))
        })
    }
    val vectors = inputs.map { (n, s) -> vector(n, s, false) } +
        vector("单独的高代理", "a\uD800b.txt", true) +
        vector("单独的低代理", "\uDC00", true) +
        vector("次序颠倒的代理对", "\uDE00\uD83D.txt", true)
    return doc("文件名处理（S0–S2、L1–L4）：android / windows 两套期望；input_utf16 为 UTF-16 码元（大端十六进制）", vectors)
}

private fun write(dir: File, name: String, element: JsonElement) {
    val text = json.encodeToString(JsonElement.serializer(), element).replace("\r\n", "\n") + "\n"
    File(dir, name).writeBytes(text.encodeToByteArray())
}

fun main(args: Array<String>) {
    val dir = File(args.firstOrNull() ?: "protocol/test-vectors")
    require(dir.isDirectory) { "output directory not found: ${dir.absolutePath}" }
    val files = linkedMapOf(
        "frames.json" to frames(),
        "frames-invalid.json" to framesInvalid(),
        "text-messages.json" to textMessages(),
        "utf8-invalid.json" to utf8Invalid(),
        "announcements.json" to announcements(),
        "qr-payloads.json" to qrPayloads(),
        "preamble.json" to preamble(),
        "noise-xx.json" to noise(),
        "verification-code.json" to verificationCodes(),
        "filename-sanitizer.json" to filenameSanitizer(),
    )
    files.forEach { (name, doc) -> write(dir, name, doc) }
    val listed = (files.keys + extraFiles(dir)).distinct()
    write(dir, "manifest.json", buildJsonObject {
        put("format_version", 1)
        put("protocol_version", "1.0")
        put("files", JsonArray(listed.map { JsonPrimitive(it) }))
    })
    println("wrote ${files.size} vector files to ${dir.absolutePath}")
}

/** 目录中已有、但不由本工具生成的向量文件（保留在 manifest 中）。 */
private fun extraFiles(dir: File): List<String> =
    dir.listFiles { f -> f.isFile && f.name.endsWith(".json") && f.name != "manifest.json" }.orEmpty().map { it.name }.sorted()
