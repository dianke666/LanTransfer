import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 带缓冲的 TCP 读取器：保留握手阶段多读到的后续 record 字节并移交给 Connection 读循环。
public actor ConnReader {
    private let conn: any TcpConn
    private var buffer: [UInt8] = []
    private var offset: Int = 0

    public init(conn: any TcpConn) {
        self.conn = conn
    }

    public func readExactly(_ count: Int) async -> Data? {
        if count == 0 { return Data() }
        var out: [UInt8] = []
        out.reserveCapacity(count)
        while out.count < count {
            if offset < buffer.count {
                let take = min(count - out.count, buffer.count - offset)
                out.append(contentsOf: buffer[offset..<(offset + take)])
                offset += take
                if offset == buffer.count {
                    buffer.removeAll(keepingCapacity: true)
                    offset = 0
                }
            } else {
                guard let chunk = await conn.read(maxBytes: 64 * 1024), !chunk.isEmpty else {
                    return nil
                }
                buffer = [UInt8](chunk)
                offset = 0
            }
        }
        return Data(out)
    }

    public func readSome(maxBytes: Int = 64 * 1024) async -> Data? {
        if offset < buffer.count {
            let take = min(maxBytes, buffer.count - offset)
            let data = Data(buffer[offset..<(offset + take)])
            offset += take
            if offset == buffer.count {
                buffer.removeAll(keepingCapacity: true)
                offset = 0
            }
            return data
        }
        return await conn.read(maxBytes: maxBytes)
    }
}

internal struct LocalHandshakeIdentity: Sendable {
    let deviceId: DeviceId
    let keyPair: X25519KeyPair
    let displayName: String
    let deviceType: DeviceType
}

internal enum Msg2Decision: Sendable {
    case proceed(Intent)
    case stop(OpenFailure?)
}

internal enum HandshakeOutcome: Sendable {
    case done(
        peer: PeerIdentity,
        intent: Intent,
        version: ProtocolVersion,
        send: RecordCipher,
        receive: RecordCipher,
        reader: ConnReader
    )
    case failed(OpenFailure)
    case merged(PeerIdentity)
}

/// 连接前导（"LANT" || major || minor）与 Noise_XX_25519_AESGCM_SHA256 三步握手（TCP 帧封装 `len:u16_be || noise_message`）。
internal enum Handshake {
    static func initiate(
        conn: any TcpConn,
        local: LocalHandshakeIdentity,
        ephemeral: any EphemeralKeySource,
        version: ProtocolVersion = .current,
        decide: @Sendable (PeerIdentity) async -> Msg2Decision
    ) async -> HandshakeOutcome {
        let reader = ConnReader(conn: conn)
        let mine = PreambleCodec.encode(version)
        guard await conn.write(mine) else {
            return .failed(.protocolError)
        }
        guard let theirs = await reader.readExactly(PreambleCodec.size) else {
            return .failed(.noVersionResponse)
        }
        guard let remoteVer = PreambleCodec.decode(theirs) else {
            return .failed(.protocolError)
        }
        let negotiated: ProtocolVersion
        switch PreambleCodec.negotiate(local: version, remote: remoteVer) {
        case .compatible(let v):
            negotiated = v
        case .incompatible(let loc, let rem):
            return .failed(.versionIncompatible(local: loc, remote: rem))
        }

        let noise = NoiseXX(
            role: .initiator,
            localStatic: local.keyPair,
            prologue: NoiseXX.prologue(preambleI: mine, preambleR: theirs),
            ephemeral: ephemeral
        )

        do {
            let msg1 = try noise.writeMessage(payload: Data())
            guard await writeFramed(conn: conn, message: msg1) else {
                return .failed(.protocolError)
            }
            guard let msg2 = await readFramed(reader: reader) else {
                return .failed(.protocolError)
            }
            let payload2 = try noise.readMessage(msg2)
            guard let id2 = HandshakeIdentityCodec.decode(payload2, expectIntent: false, version: negotiated),
                  DisplayNameRules.isAcceptableRemote(id2.displayName),
                  let remoteStatic = noise.remoteStatic else {
                return .failed(.protocolError)
            }
            let peer = PeerIdentity(
                deviceId: id2.deviceId,
                displayName: id2.displayName,
                deviceType: id2.deviceType,
                publicKey: remoteStatic,
                remoteAddr: conn.remote
            )

            switch await decide(peer) {
            case .stop(let failure):
                if let failure {
                    return .failed(failure)
                }
                return .merged(peer)
            case .proceed(let intent):
                let id3 = HandshakeIdentity(
                    deviceId: local.deviceId,
                    displayName: local.displayName,
                    deviceType: local.deviceType,
                    intent: intent
                )
                let msg3 = try noise.writeMessage(payload: HandshakeIdentityCodec.encode(id3))
                guard await writeFramed(conn: conn, message: msg3) else {
                    return .failed(.protocolError)
                }
                let (sendCipher, recvCipher) = noise.split()
                return .done(
                    peer: peer,
                    intent: intent,
                    version: negotiated,
                    send: sendCipher,
                    receive: recvCipher,
                    reader: reader
                )
            }
        } catch is HandshakeAuthError {
            return .failed(.handshakeAuthFailed)
        } catch {
            return .failed(.protocolError)
        }
    }

    static func respond(
        conn: any TcpConn,
        local: LocalHandshakeIdentity,
        ephemeral: any EphemeralKeySource,
        version: ProtocolVersion = .current
    ) async -> HandshakeOutcome {
        let reader = ConnReader(conn: conn)
        guard let theirs = await reader.readExactly(PreambleCodec.size) else {
            return .failed(.protocolError)
        }
        guard let remoteVer = PreambleCodec.decode(theirs) else {
            return .failed(.protocolError)
        }
        let mine = PreambleCodec.encode(version)
        guard await conn.write(mine) else {
            return .failed(.protocolError)
        }
        let negotiated: ProtocolVersion
        switch PreambleCodec.negotiate(local: version, remote: remoteVer) {
        case .compatible(let v):
            negotiated = v
        case .incompatible(let loc, let rem):
            return .failed(.versionIncompatible(local: loc, remote: rem))
        }

        let noise = NoiseXX(
            role: .responder,
            localStatic: local.keyPair,
            prologue: NoiseXX.prologue(preambleI: theirs, preambleR: mine),
            ephemeral: ephemeral
        )

        do {
            guard let msg1 = await readFramed(reader: reader) else {
                return .failed(.protocolError)
            }
            let payload1 = try noise.readMessage(msg1)
            guard payload1.isEmpty else {
                return .failed(.protocolError)
            }
            let id2 = HandshakeIdentity(
                deviceId: local.deviceId,
                displayName: local.displayName,
                deviceType: local.deviceType,
                intent: nil
            )
            let msg2 = try noise.writeMessage(payload: HandshakeIdentityCodec.encode(id2))
            guard await writeFramed(conn: conn, message: msg2) else {
                return .failed(.protocolError)
            }
            guard let msg3 = await readFramed(reader: reader) else {
                return .failed(.protocolError)
            }
            let payload3 = try noise.readMessage(msg3)
            guard let id3 = HandshakeIdentityCodec.decode(payload3, expectIntent: true, version: negotiated),
                  let intent = id3.intent,
                  DisplayNameRules.isAcceptableRemote(id3.displayName),
                  let remoteStatic = noise.remoteStatic else {
                return .failed(.protocolError)
            }
            let peer = PeerIdentity(
                deviceId: id3.deviceId,
                displayName: id3.displayName,
                deviceType: id3.deviceType,
                publicKey: remoteStatic,
                remoteAddr: conn.remote
            )
            let (sendCipher, recvCipher) = noise.split()
            return .done(
                peer: peer,
                intent: intent,
                version: negotiated,
                send: sendCipher,
                receive: recvCipher,
                reader: reader
            )
        } catch is HandshakeAuthError {
            return .failed(.handshakeAuthFailed)
        } catch {
            return .failed(.protocolError)
        }
    }

    private static func writeFramed(conn: any TcpConn, message: Data) async -> Bool {
        guard message.count <= Int(UInt16.max) else { return false }
        let len = UInt16(message.count)
        var out = Data(capacity: 2 + message.count)
        out.append(UInt8((len >> 8) & 0xFF))
        out.append(UInt8(len & 0xFF))
        out.append(message)
        return await conn.write(out)
    }

    private static func readFramed(reader: ConnReader) async -> Data? {
        guard let lenData = await reader.readExactly(2) else { return nil }
        let b = [UInt8](lenData)
        let len = (Int(b[0]) << 8) | Int(b[1])
        guard len > 0 else { return nil }
        return await reader.readExactly(len)
    }
}
