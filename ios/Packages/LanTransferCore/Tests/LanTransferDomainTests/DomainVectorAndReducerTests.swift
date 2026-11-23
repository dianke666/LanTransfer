import Foundation
import XCTest
@testable import LanTransferDomain
import LanTransferProtocol
import LanTransferTesting

final class DomainVectorAndReducerTests: XCTestCase {
    private func parseUtf16Hex(_ hexString: String) -> [UInt16] {
        let chars = Array(hexString.utf8)
        func nib(_ c: UInt8) -> UInt16 {
            switch c {
            case 48...57: return UInt16(c - 48)
            case 97...102: return UInt16(c - 97 + 10)
            case 65...70: return UInt16(c - 65 + 10)
            default: fatalError("bad hex")
            }
        }
        var units: [UInt16] = []
        var i = 0
        while i + 3 < chars.count {
            let u = (nib(chars[i]) << 12) | (nib(chars[i + 1]) << 8) | (nib(chars[i + 2]) << 4) | nib(chars[i + 3])
            units.append(u)
            i += 4
        }
        return units
    }

    func testFileNameSanitizerVectorConformance() throws {
        let data = try VectorLocator.loadVectorData(named: "filename-sanitizer.json")
        let doc = try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        let vectors = try XCTUnwrap(doc["vectors"] as? [[String: Any]])
        XCTAssertFalse(vectors.isEmpty)

        for v in vectors {
            let now = try XCTUnwrap(LocalDateTime.parseIso(v["now"] as! String))
            let expectedAndroid = v["android"] as! String
            let expectedWindows = v["windows"] as! String

            let actualAndroid: String
            let actualIos: String
            let actualWindows: String
            if let hex16 = v["input_utf16"] as? String {
                let units = parseUtf16Hex(hex16)
                actualAndroid = FileNameSanitizer.sanitize(utf16Units: units, profile: .android, now: now)
                actualIos = FileNameSanitizer.sanitize(utf16Units: units, profile: .ios, now: now)
                actualWindows = FileNameSanitizer.sanitize(utf16Units: units, profile: .windows, now: now)
            } else {
                let input = v["input"] as! String
                actualAndroid = FileNameSanitizer.sanitize(input, profile: .android, now: now)
                actualIos = FileNameSanitizer.sanitize(input, profile: .ios, now: now)
                actualWindows = FileNameSanitizer.sanitize(input, profile: .windows, now: now)
            }

            XCTAssertEqual(actualAndroid, expectedAndroid, "Android mismatch for \(v["name"] ?? "")")
            XCTAssertEqual(actualIos, expectedAndroid, "iOS should match POSIX Android profile for \(v["name"] ?? "")")
            XCTAssertEqual(actualWindows, expectedWindows, "Windows mismatch for \(v["name"] ?? "")")
        }
    }

    func testTextDisplayMapperLosslessSelectionRoundTrip() {
        let original = "A\r\nB\u{0000}C\u{007F}😀"
        let mapped = TextDisplayMapper.map(original)
        XCTAssertEqual(mapped.display, "A\nB\u{2400}C\u{2421}😀")
        // Selecting across the mapped LF recovers the original CRLF
        XCTAssertEqual(mapped.extractOriginal(from: original, selStart: 0, selEnd: 3), "A\r\nB")
        // Selecting the NUL symbol recovers U+0000
        XCTAssertEqual(mapped.extractOriginal(from: original, selStart: 3, selEnd: 4), "\u{0000}")
        // Selecting full string recovers exact original
        XCTAssertEqual(mapped.extractOriginal(from: original, selStart: 0, selEnd: mapped.display.utf16.count), original)
    }

    func testAddressRulesClassifyAndQrPriority() {
        let wifi = LocalInterface(name: "en0", kind: .wifi, address: Ipv4(192, 168, 1, 28), prefixLength: 24)
        let hotspot = LocalInterface(name: "bridge100", kind: .hotspot, address: Ipv4(172, 20, 10, 1), prefixLength: 28)
        let ifaces = [wifi, hotspot]

        XCTAssertEqual(AddressRules.classify(Ipv4(192, 168, 1, 28), interfaces: ifaces), .self)
        XCTAssertEqual(AddressRules.classify(Ipv4(192, 168, 1, 100), interfaces: ifaces), .local(wifi))
        XCTAssertEqual(AddressRules.classify(Ipv4(172, 20, 10, 2), interfaces: ifaces), .local(hotspot))
        XCTAssertEqual(AddressRules.classify(Ipv4(8, 8, 8, 8), interfaces: ifaces), .notLocal)
        XCTAssertEqual(AddressRules.parsePort(""), .default(42580))
        XCTAssertEqual(AddressRules.parsePort("42590"), .ok(42590))
        XCTAssertEqual(AddressRules.parsePort("0"), .invalid)
    }

    func testTextReducerSendAckRetryAndTimeout() {
        let peer = DeviceId(bytes: Data(repeating: 1, count: 16))!
        let mid = MessageId(bytes: Data(repeating: 2, count: 16))!
        let key = TextKey(peer: peer, messageId: mid)
        let body = Data("hello".utf8)

        let s1 = TextReducer.reduce(TextState(), .send(key: key, body: body, sentAtUtcMs: 1000, now: 100))
        XCTAssertEqual(s1.state.outgoing[key]?.status, .sending)

        let s2 = TextReducer.reduce(s1.state, .tick(now: 10_100))
        XCTAssertEqual(s2.state.outgoing[key]?.status, .failed)
        XCTAssertTrue(s2.state.outgoing[key]?.showRetry == true)

        let s3 = TextReducer.reduce(s2.state, .retry(key: key, now: 12_000))
        XCTAssertEqual(s3.state.outgoing[key]?.status, .sending)

        let s4 = TextReducer.reduce(s3.state, .ackReceived(key: key))
        XCTAssertEqual(s4.state.outgoing[key]?.status, .delivered)
    }

    func testPairingReducerSasFlowEndToEnd() {
        let idI = DeviceId(bytes: Data(repeating: 0x11, count: 16))!
        let idR = DeviceId(bytes: Data(repeating: 0x22, count: 16))!
        let kI = X25519KeyPair.generate()
        let kR = X25519KeyPair.generate()
        let nI = Data(repeating: 0xAA, count: 32)
        let nR = Data(repeating: 0xBB, count: 32)
        let infoR = PeerInfo(deviceId: idR, displayName: "Win", deviceType: .windows, publicKey: kR.publicKey)
        let infoI = PeerInfo(deviceId: idI, displayName: "iPhone", deviceType: .ios, publicKey: kI.publicKey)
        let cI = ConnId(1)
        let cR = ConnId(2)

        var stI = PairingReducer.reduce(PairingState(), .startPair(peer: idR, now: 0)).state
        let stepConnI = PairingReducer.reduce(stI, .connected(conn: cI, info: infoR, localPublicKey: kI.publicKey, nI: nI, now: 10))
        stI = stepConnI.state
        guard case .send(_, .pairRequest(let commit)) = stepConnI.effects.first else {
            return XCTFail("Expected PAIR_REQUEST")
        }

        let stepReqR = PairingReducer.reduce(
            PairingState(),
            .pairRequestReceived(conn: cR, info: infoI, localPublicKey: kR.publicKey, commitment: commit, nR: nR, now: 20)
        )
        var stR = stepReqR.state
        guard case .send(_, let msgNonce) = stepReqR.effects.first else {
            return XCTFail("Expected PAIR_NONCE")
        }

        let stepNonceI = PairingReducer.reduce(stI, .messageReceived(conn: cI, msg: msgNonce, now: 30))
        stI = stepNonceI.state
        guard case .send(_, let msgReveal) = stepNonceI.effects.first,
              case .showSas(let sasI) = stepNonceI.effects[1] else {
            return XCTFail("Expected PAIR_REVEAL and ShowSas")
        }

        let stepRevealR = PairingReducer.reduce(stR, .messageReceived(conn: cR, msg: msgReveal, now: 40))
        stR = stepRevealR.state
        guard case .showSas(let sasR) = stepRevealR.effects.first else {
            return XCTFail("Expected receiver ShowSas")
        }
        XCTAssertEqual(sasI, sasR)

        let stepAcceptR = PairingReducer.reduce(stR, .userDecision(accept: true, now: 50))
        stR = stepAcceptR.state
        let stepDecisionI = PairingReducer.reduce(stI, .messageReceived(conn: cI, msg: .pairDecision(decision: .accepted), now: 60))
        stI = stepDecisionI.state
        let stepStoredI = PairingReducer.reduce(stI, .trustStored(peer: idR, ok: true))
        XCTAssertEqual(stepStoredI.state.last?.result, .success)

        let stepCompleteR = PairingReducer.reduce(stR, .messageReceived(conn: cR, msg: .pairComplete, now: 70))
        let stepStoredR = PairingReducer.reduce(stepCompleteR.state, .trustStored(peer: idI, ok: true))
        XCTAssertEqual(stepStoredR.state.last?.result, .success)
    }

    func testTransferReducerSingleFileHappyPath() {
        let tid = TransferId(bytes: Data(repeating: 0x33, count: 16))!
        let peer = DeviceId(bytes: Data(repeating: 0x44, count: 16))!
        let now = LocalDateTime(year: 2026, month: 11, day: 23, hour: 9, minute: 30, second: 0)
        let task = TransferTask(
            transferId: tid,
            peer: peer,
            peerName: "Win",
            direction: .receive,
            state: .awaitingUser,
            recordedAt: RecordTime(utcMs: 1000, offsetMin: 480),
            updatedAtUtcMs: 1000,
            files: [
                TransferFile(transferId: tid, index: 0, seq: 1, originalName: "a.bin", size: 100, mime: "application/octet-stream", state: .waiting)
            ]
        )
        let s1 = TransferReducer.reduce(task, .userAccepted(dir: .default, dirUsable: true))
        XCTAssertEqual(s1.state.state, .running)

        let s2 = TransferReducer.reduce(s1.state, .chunkWritten(index: 0, persisted: 100, localNow: now, nowWall: 2000))
        XCTAssertEqual(s2.state.files[0].persisted, 100)

        let sha = Data(repeating: 0xAB, count: 32)
        let s3 = TransferReducer.reduce(s2.state, .endReceived(index: 0, sha256: sha, localNow: now))
        XCTAssertEqual(s3.state.files[0].state, .verifying)

        let s4 = TransferReducer.reduce(s3.state, .verifyDone(index: 0, outcome: .committed(finalName: "a.bin", fullPath: "/docs/a.bin", storedRef: StoredFileRef("/docs/a.bin"))))
        XCTAssertEqual(s4.state.state, .completed)
        XCTAssertEqual(s4.state.files[0].state, .completed)
    }
}
