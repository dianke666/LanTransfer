import Foundation
import XCTest
@testable import LanTransferProtocol
import LanTransferTesting

/// 跨端一致性测试：直接读取共享仓库根目录 `protocol/test-vectors/` 的全部 9 份协议测试向量，逐字节比对 Swift + CryptoKit 实现。
final class ProtocolVectorConformanceTests: XCTestCase {
    private func hex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    private func fromHex(_ hexString: String) -> Data {
        let chars = Array(hexString.utf8)
        XCTAssertEqual(chars.count % 2, 0)
        var bytes: [UInt8] = []
        bytes.reserveCapacity(chars.count / 2)
        func nibble(_ c: UInt8) -> UInt8 {
            switch c {
            case 48...57: return c - 48
            case 97...102: return c - 97 + 10
            case 65...70: return c - 65 + 10
            default: fatalError("Invalid hex char: \(c)")
            }
        }
        var i = 0
        while i < chars.count {
            bytes.append((nibble(chars[i]) << 4) | nibble(chars[i + 1]))
            i += 2
        }
        return Data(bytes)
    }

    private func loadJsonObject(_ fileName: String) throws -> [String: Any] {
        let data = try VectorLocator.loadVectorData(named: fileName)
        let obj = try JSONSerialization.jsonObject(with: data)
        return try XCTUnwrap(obj as? [String: Any])
    }

    private func parseVersion(_ s: String) -> ProtocolVersion {
        let parts = s.split(separator: ".")
        return ProtocolVersion(major: UInt8(parts[0])!, minor: UInt8(parts[1])!)
    }

    private func decodeErrorName(_ e: DecodeError) -> String {
        switch e {
        case .badMagic: return "BAD_MAGIC"
        case .unknownType: return "UNKNOWN_TYPE"
        case .payloadTooLarge: return "PAYLOAD_TOO_LARGE"
        case .malformedPayload: return "MALFORMED_PAYLOAD"
        }
    }

    private func parseDeviceType(_ s: String) -> DeviceType {
        switch s {
        case "ANDROID": return .android
        case "WINDOWS": return .windows
        case "IOS": return .ios
        default: fatalError("Unknown device type: \(s)")
        }
    }

    private func parseIntentName(_ intent: Intent) -> String {
        switch intent {
        case .session: return "SESSION"
        case .pair: return "PAIR"
        case .qrPair: return "QR_PAIR"
        }
    }

    private func parseMessage(type: String, model: [String: Any]) -> ProtocolMessage {
        func str(_ k: String) -> String { model[k] as! String }
        func optStr(_ k: String) -> String? { model[k] as? String }
        func u32(_ k: String) -> UInt32 { UInt32((model[k] as! NSNumber).uint64Value) }
        func u64Str(_ k: String) -> UInt64 { UInt64(str(k))! }
        func i64Str(_ k: String) -> Int64 { Int64(str(k))! }
        func msgId() -> MessageId { MessageId(bytes: fromHex(str("message_id")))! }
        func tid() -> TransferId { TransferId(bytes: fromHex(str("transfer_id")))! }

        switch type {
        case "SESSION_RESULT":
            let map: [String: SessionResultCode] = ["ACCEPTED": .accepted, "NOT_TRUSTED": .notTrusted, "KEY_MISMATCH": .keyMismatch, "DUPLICATE": .duplicate]
            return .sessionResult(result: map[str("result")]!)
        case "SESSION_CLOSE":
            let map: [String: SessionCloseReason] = ["NORMAL": .normal, "UNPAIRED": .unpaired, "APP_EXIT": .appExit, "BACKGROUND": .background, "DUPLICATE": .duplicate]
            return .sessionClose(reason: map[str("reason")]!)
        case "HEARTBEAT":
            return .heartbeat
        case "PAIR_REQUEST":
            return .pairRequest(commitment: fromHex(str("commitment")))
        case "PAIR_NONCE":
            return .pairNonce(nonce: fromHex(str("nonce")))
        case "PAIR_REVEAL":
            return .pairReveal(nonce: fromHex(str("nonce")))
        case "PAIR_DECISION":
            let map: [String: PairDecisionCode] = ["ACCEPTED": .accepted, "REJECTED": .rejected, "BUSY": .busy, "TIMEOUT": .timeout]
            return .pairDecision(decision: map[str("decision")]!)
        case "PAIR_CANCEL":
            let map: [String: PairCancelReason] = ["USER": .user, "TIMEOUT": .timeout]
            return .pairCancel(reason: map[str("reason")]!)
        case "PAIR_COMPLETE":
            return .pairComplete
        case "QR_PAIR_REQUEST":
            return .qrPairRequest(token: fromHex(str("token")))
        case "QR_PAIR_RESULT":
            let map: [String: QrPairResultCode] = ["OK": .ok, "TOKEN_INVALID": .tokenInvalid]
            return .qrPairResult(result: map[str("result")]!)
        case "TEXT":
            return .text(messageId: msgId(), sentAt: i64Str("sent_at"), body: fromHex(str("text")))
        case "TEXT_ACK":
            return .textAck(messageId: msgId())
        case "TEXT_CORRUPT":
            return .textCorrupt(messageId: msgId())
        case "FILE_OFFER":
            let files = (model["files"] as! [[String: Any]]).map { f in
                FileEntry(
                    name: f["name"] as! String,
                    size: UInt64(f["size"] as! String)!,
                    mime: f["mime"] as! String,
                    mtime: (f["mtime"] as? String).flatMap(Int64.init)
                )
            }
            return .fileOffer(transferId: tid(), files: files)
        case "FILE_OFFER_REPLY":
            let map: [String: OfferDecision] = ["ACCEPT": .accept, "REJECT": .reject, "TIMEOUT": .timeout, "NO_SPACE": .noSpace, "CANNOT_SAVE": .cannotSave, "INVALID_OFFER": .invalidOffer]
            return .fileOfferReply(transferId: tid(), decision: map[str("decision")]!)
        case "FILE_CHUNK":
            return .fileChunk(transferId: tid(), fileIndex: u32("file_index"), offset: u64Str("offset"), data: fromHex(str("data")))
        case "FILE_END":
            return .fileEnd(transferId: tid(), fileIndex: u32("file_index"), sha256: fromHex(str("sha256")))
        case "FILE_ACK":
            return .fileAck(transferId: tid(), fileIndex: u32("file_index"), persisted: u64Str("persisted"))
        case "FILE_RESULT":
            let map: [String: FileResultStatus] = ["COMPLETED": .completed, "CHECKSUM_FAILED": .checksumFailed, "WRITE_FAILED": .writeFailed]
            return .fileResult(transferId: tid(), fileIndex: u32("file_index"), status: map[str("status")]!, detail: optStr("detail"))
        case "FILE_READ_FAILED":
            return .fileReadFailed(transferId: tid(), fileIndex: u32("file_index"))
        case "TRANSFER_CANCEL":
            let map: [String: TransferCancelReason] = ["USER": .user, "UNKNOWN_TRANSFER": .unknownTransfer, "UNPAIRED": .unpaired]
            return .transferCancel(transferId: tid(), reason: map[str("reason")]!)
        case "RESUME_REQUEST":
            return .resumeRequest(transferId: tid())
        case "RESUME_STATE":
            let map: [String: ResumeFileStatus] = ["NONE": .none, "PARTIAL": .partial, "COMPLETED": .completed, "TERMINAL": .terminal]
            let files = (model["files"] as! [[String: Any]]).map { f in
                ResumeFileState(
                    fileIndex: UInt32((f["file_index"] as! NSNumber).uint64Value),
                    state: map[f["state"] as! String]!,
                    persisted: UInt64(f["persisted"] as! String)!
                )
            }
            return .resumeState(transferId: tid(), files: files)
        case "RESUME_PLAN":
            let map: [String: ResumeAction] = ["CONTINUE": .continue, "RESTART": .restart, "SKIP": .skip]
            let files = (model["files"] as! [[String: Any]]).map { f in
                ResumeFilePlan(
                    fileIndex: UInt32((f["file_index"] as! NSNumber).uint64Value),
                    action: map[f["action"] as! String]!,
                    start: UInt64(f["start"] as! String)!,
                    size: (f["size"] as? String).flatMap(UInt64.init),
                    mtime: (f["mtime"] as? String).flatMap(Int64.init)
                )
            }
            return .resumePlan(transferId: tid(), files: files)
        default:
            fatalError("Unknown message type: \(type)")
        }
    }

    func testFramesVectorConformance() throws {
        let doc = try loadJsonObject("frames.json")
        let vectors = try XCTUnwrap(doc["vectors"] as? [[String: Any]])
        var seenTypes = Set<MessageType>()
        for v in vectors {
            let typeName = v["type"] as! String
            let model = parseMessage(type: typeName, model: v["model"] as! [String: Any])
            seenTypes.insert(model.type)
            let bytes = fromHex(v["bytes"] as! String)
            let canonical = (v["canonical_bytes"] as? String).map(fromHex) ?? bytes
            XCTAssertEqual(hex(FrameEncoder.encodeOrPrecondition(model)), hex(canonical), "Encode mismatch for \(v["name"] ?? "")")
            let batch = FrameDecoder().feed(bytes)
            XCTAssertNil(batch.error, "Unexpected decode error for \(v["name"] ?? "")")
            XCTAssertEqual(batch.messages, [model], "Decode mismatch for \(v["name"] ?? "")")
        }
        XCTAssertEqual(seenTypes, Set(MessageType.allCases))
    }

    func testInvalidFramesVectorConformance() throws {
        let doc = try loadJsonObject("frames-invalid.json")
        let vectors = try XCTUnwrap(doc["vectors"] as? [[String: Any]])
        for v in vectors {
            let decoder = FrameDecoder(version: parseVersion(v["version"] as! String))
            let batch = decoder.feed(fromHex(v["bytes"] as! String))
            XCTAssertEqual(batch.messages.count, (v["messages_before_error"] as! NSNumber).intValue)
            let err = try XCTUnwrap(batch.error)
            XCTAssertEqual(decodeErrorName(err), v["error"] as! String, "Error mismatch for \(v["name"] ?? "")")
        }
    }

    func testTextMessagesAndInvalidUtf8VectorConformance() throws {
        let doc = try loadJsonObject("text-messages.json")
        let msgId = try XCTUnwrap(MessageId(bytes: fromHex(doc["message_id"] as! String)))
        let sentAt = Int64(doc["sent_at"] as! String)!
        for v in try XCTUnwrap(doc["vectors"] as? [[String: Any]]) {
            let utf8 = fromHex(v["text_utf8"] as! String)
            let decoded = try XCTUnwrap(StrictUtf8.decode(utf8))
            XCTAssertEqual(hex(StrictUtf8.encode(decoded)), hex(utf8))
            let expectedFrame = v["frame"] as! String
            XCTAssertEqual(hex(FrameEncoder.encodeOrPrecondition(.text(messageId: msgId, sentAt: sentAt, body: utf8))), expectedFrame)
            let batch = FrameDecoder().feed(fromHex(expectedFrame))
            XCTAssertEqual(batch.messages.count, 1)
            if case .text(_, _, let body) = batch.messages[0] {
                XCTAssertEqual(StrictUtf8.decode(body), decoded)
            } else {
                XCTFail("Expected text message")
            }
        }

        let invalidDoc = try loadJsonObject("utf8-invalid.json")
        for v in try XCTUnwrap(invalidDoc["vectors"] as? [[String: Any]]) {
            let badUtf8 = fromHex(v["text_utf8"] as! String)
            XCTAssertNil(StrictUtf8.decode(badUtf8), "Should reject invalid UTF-8: \(v["name"] ?? "")")
            let batch = FrameDecoder().feed(fromHex(v["frame"] as! String))
            XCTAssertEqual(batch.messages.count, 1)
            if case .text(_, _, let body) = batch.messages[0] {
                XCTAssertEqual(hex(body), v["text_utf8"] as! String)
            } else {
                XCTFail("Expected text frame carrying raw invalid UTF-8")
            }
        }
    }

    func testAnnouncementsVectorConformance() throws {
        let doc = try loadJsonObject("announcements.json")
        for v in try XCTUnwrap(doc["vectors"] as? [[String: Any]]) {
            let bytes = fromHex(v["bytes"] as! String)
            if v["error"] != nil {
                XCTAssertNil(AnnouncementCodec.decode(bytes), "Should drop announcement: \(v["name"] ?? "")")
            } else {
                let m = v["model"] as! [String: Any]
                let expected = Announcement(
                    deviceId: DeviceId(bytes: fromHex(m["device_id"] as! String))!,
                    displayName: m["display_name"] as! String,
                    deviceType: parseDeviceType(m["device_type"] as! String),
                    version: ProtocolVersion(major: UInt8((m["proto_major"] as! NSNumber).uint8Value), minor: UInt8((m["proto_minor"] as! NSNumber).uint8Value)),
                    port: UInt16((m["port"] as! NSNumber).uint16Value)
                )
                XCTAssertEqual(hex(AnnouncementCodec.encode(expected)), v["bytes"] as! String)
                XCTAssertEqual(AnnouncementCodec.decode(bytes), expected)
            }
        }
    }

    func testQrPayloadsVectorConformance() throws {
        let doc = try loadJsonObject("qr-payloads.json")
        for v in try XCTUnwrap(doc["vectors"] as? [[String: Any]]) {
            let text = v["text"] as! String
            if v["error"] != nil {
                XCTAssertNil(QrCodec.decode(text).payload, "Should reject QR: \(v["name"] ?? "")")
            } else {
                let m = v["model"] as! [String: Any]
                let ips = (m["ipv4_list"] as! [String]).map { Ipv4(parsing: $0)!.rawValue }
                let expected = QrPayload(
                    deviceId: DeviceId(bytes: fromHex(m["device_id"] as! String))!,
                    displayName: m["display_name"] as! String,
                    ipv4List: ips,
                    port: UInt16((m["port"] as! NSNumber).uint16Value),
                    fingerprint: fromHex(m["fingerprint"] as! String),
                    token: fromHex(m["token"] as! String)
                )
                XCTAssertEqual(QrCodec.encode(expected), text)
                XCTAssertEqual(QrCodec.decode(text), .ok(expected))
            }
        }
    }

    func testPreambleVectorConformance() throws {
        let doc = try loadJsonObject("preamble.json")
        for v in try XCTUnwrap(doc["vectors"] as? [[String: Any]]) {
            if let expectedNeg = v["negotiated"] as? String {
                let result = PreambleCodec.negotiate(local: parseVersion(v["local"] as! String), remote: parseVersion(v["remote"] as! String))
                if expectedNeg == "INCOMPATIBLE" {
                    if case .incompatible = result {} else { XCTFail("Expected incompatible") }
                } else {
                    XCTAssertEqual(result, .compatible(parseVersion(expectedNeg)))
                }
            } else if v["error"] != nil {
                XCTAssertNil(PreambleCodec.decode(fromHex(v["bytes"] as! String)))
            } else {
                let ver = parseVersion(v["version"] as! String)
                XCTAssertEqual(hex(PreambleCodec.encode(ver)), v["bytes"] as! String)
                XCTAssertEqual(PreambleCodec.decode(fromHex(v["bytes"] as! String)), ver)
            }
        }
    }

    func testNoiseXXOfficialAndTranscriptVectors() throws {
        let doc = try loadJsonObject("noise-xx.json")
        for v in try XCTUnwrap(doc["official"] as? [[String: Any]]) {
            let i = NoiseXX(
                role: .initiator,
                localStatic: try X25519KeyPair.fromPrivateKey(fromHex(v["init_static"] as! String)),
                prologue: fromHex(v["init_prologue"] as! String),
                ephemeral: EphemeralKeySources.fixed([fromHex(v["init_ephemeral"] as! String)])
            )
            let r = NoiseXX(
                role: .responder,
                localStatic: try X25519KeyPair.fromPrivateKey(fromHex(v["resp_static"] as! String)),
                prologue: fromHex(v["resp_prologue"] as! String),
                ephemeral: EphemeralKeySources.fixed([fromHex(v["resp_ephemeral"] as! String)])
            )
            let msgs = v["messages"] as! [[String: Any]]
            XCTAssertEqual(hex(try i.writeMessage(payload: fromHex(msgs[0]["payload"] as! String))), msgs[0]["ciphertext"] as! String)
            XCTAssertEqual(hex(try r.readMessage(fromHex(msgs[0]["ciphertext"] as! String))), msgs[0]["payload"] as! String)
            XCTAssertEqual(hex(try r.writeMessage(payload: fromHex(msgs[1]["payload"] as! String))), msgs[1]["ciphertext"] as! String)
            XCTAssertEqual(hex(try i.readMessage(fromHex(msgs[1]["ciphertext"] as! String))), msgs[1]["payload"] as! String)
            XCTAssertEqual(hex(try i.writeMessage(payload: fromHex(msgs[2]["payload"] as! String))), msgs[2]["ciphertext"] as! String)
            XCTAssertEqual(hex(try r.readMessage(fromHex(msgs[2]["ciphertext"] as! String))), msgs[2]["payload"] as! String)
            XCTAssertEqual(hex(i.handshakeHash), v["handshake_hash"] as! String)

            let (iSend, iRecv) = i.split()
            let (rSend, rRecv) = r.split()
            let iIn = iRecv.openStream()
            let rIn = rRecv.openStream()
            for k in 3..<msgs.count {
                let fromResponder = (k - 3) % 2 == 0
                let plain = fromHex(msgs[k]["payload"] as! String)
                let record = (fromResponder ? rSend : iSend).seal(plain)
                XCTAssertEqual(hex(record.dropFirst(2)), msgs[k]["ciphertext"] as! String)
                let fed = (fromResponder ? iIn : rIn).feed(record)
                XCTAssertFalse(fed.integrityError)
                XCTAssertEqual(fed.chunks.count, 1)
                XCTAssertEqual(hex(fed.chunks[0]), msgs[k]["payload"] as! String)
            }
        }

        for v in try XCTUnwrap(doc["transcripts"] as? [[String: Any]]) {
            let prologue = NoiseXX.prologue(preambleI: fromHex(v["preamble_i"] as! String), preambleR: fromHex(v["preamble_r"] as! String))
            XCTAssertEqual(hex(prologue), v["prologue"] as! String)
            let iKey = try X25519KeyPair.fromPrivateKey(fromHex(v["init_static"] as! String))
            let rKey = try X25519KeyPair.fromPrivateKey(fromHex(v["resp_static"] as! String))
            XCTAssertEqual(hex(iKey.publicKey), v["init_static_public"] as! String)
            let i = NoiseXX(role: .initiator, localStatic: iKey, prologue: prologue, ephemeral: EphemeralKeySources.fixed([fromHex(v["init_ephemeral"] as! String)]))
            let r = NoiseXX(role: .responder, localStatic: rKey, prologue: prologue, ephemeral: EphemeralKeySources.fixed([fromHex(v["resp_ephemeral"] as! String)]))
            let m1 = v["msg1"] as! [String: Any]
            let m2 = v["msg2"] as! [String: Any]
            let m3 = v["msg3"] as! [String: Any]
            XCTAssertEqual(hex(try i.writeMessage(payload: fromHex(m1["payload"] as! String))), m1["message"] as! String)
            XCTAssertTrue(try r.readMessage(fromHex(m1["message"] as! String)).isEmpty)
            XCTAssertEqual(hex(try r.writeMessage(payload: fromHex(m2["payload"] as! String))), m2["message"] as! String)
            let id2 = try XCTUnwrap(HandshakeIdentityCodec.decode(try i.readMessage(fromHex(m2["message"] as! String)), expectIntent: false))
            XCTAssertEqual(id2.displayName, (m2["identity"] as! [String: Any])["display_name"] as! String)
            XCTAssertEqual(hex(try i.writeMessage(payload: fromHex(m3["payload"] as! String))), m3["message"] as! String)
            let id3 = try XCTUnwrap(HandshakeIdentityCodec.decode(try r.readMessage(fromHex(m3["message"] as! String)), expectIntent: true))
            XCTAssertEqual(parseIntentName(id3.intent!), (m3["identity"] as! [String: Any])["intent"] as! String)
            XCTAssertEqual(hex(i.handshakeHash), v["handshake_hash"] as! String)
            XCTAssertEqual(hex(try XCTUnwrap(r.remoteStatic)), v["init_static_public"] as! String)
            XCTAssertEqual(hex(try XCTUnwrap(i.remoteStatic)), v["resp_static_public"] as! String)

            let (iSend, _) = i.split()
            let (rSend, rRecv) = r.split()
            let rIn = rRecv.openStream()
            for rec in v["records_i_to_r"] as! [[String: Any]] {
                XCTAssertEqual(hex(iSend.seal(fromHex(rec["plaintext"] as! String))), rec["record"] as! String)
                let fed = rIn.feed(fromHex(rec["record"] as! String))
                XCTAssertFalse(fed.integrityError)
                XCTAssertEqual(hex(fed.chunks[0]), rec["plaintext"] as! String)
            }
            for rec in v["records_r_to_i"] as! [[String: Any]] {
                XCTAssertEqual(hex(rSend.seal(fromHex(rec["plaintext"] as! String))), rec["record"] as! String)
            }
        }
    }

    func testVerificationCodesVectorConformance() throws {
        let doc = try loadJsonObject("verification-code.json")
        for v in try XCTUnwrap(doc["vectors"] as? [[String: Any]]) {
            let pkI = fromHex(v["pk_i"] as! String)
            let pkR = fromHex(v["pk_r"] as! String)
            let nI = fromHex(v["n_i"] as! String)
            let nR = fromHex(v["n_r"] as! String)
            let commit = VerificationCode.commitment(pkI: pkI, pkR: pkR, nI: nI)
            XCTAssertEqual(hex(commit), v["commitment"] as! String)
            XCTAssertTrue(VerificationCode.verifyCommitment(commit, pkI: pkI, pkR: pkR, nI: nI))
            XCTAssertEqual(VerificationCode.sas(pkI: pkI, pkR: pkR, nI: nI, nR: nR), v["sas"] as! String)
        }
    }
}
