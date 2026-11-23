import Foundation

/// 握手认证或格式失败（需求 10.6）。
public struct HandshakeAuthError: Error, Hashable, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
}

public enum HandshakeRole: Hashable, Sendable {
    case initiator
    case responder
}

private struct NoiseCipherState {
    private let key: Data?
    private var n: UInt64 = 0

    init(key: Data? = nil) {
        self.key = key
    }

    mutating func encryptWithAd(_ ad: Data, plain: Data) -> Data {
        guard let key else { return plain }
        let ct = CryptoPrimitives.aesGcmEncrypt(key: key, n: n, ad: ad, plain: plain)
        n += 1
        return ct
    }

    mutating func decryptWithAd(_ ad: Data, cipher: Data) throws -> Data {
        guard let key else { return cipher }
        guard let plain = CryptoPrimitives.aesGcmDecrypt(key: key, n: n, ad: ad, cipher: cipher) else {
            throw HandshakeAuthError("decryption failed")
        }
        n += 1
        return plain
    }
}

private struct NoiseSymmetricState {
    private var ck: Data
    private var cipher = NoiseCipherState()
    private(set) var h: Data

    init(protocolName: String) {
        let name = Data(protocolName.utf8)
        if name.count <= CryptoPrimitives.hashLen {
            var padded = name
            padded.append(Data(repeating: 0, count: CryptoPrimitives.hashLen - name.count))
            self.h = padded
        } else {
            self.h = CryptoPrimitives.sha256(name)
        }
        self.ck = self.h
    }

    mutating func mixKey(_ ikm: Data) {
        let (newCk, k) = CryptoPrimitives.hkdf2(chainingKey: ck, ikm: ikm)
        ck = newCk
        cipher = NoiseCipherState(key: k)
    }

    mutating func mixHash(_ data: Data) {
        h = CryptoPrimitives.sha256(h, data)
    }

    mutating func encryptAndHash(_ plain: Data) -> Data {
        let ct = cipher.encryptWithAd(h, plain: plain)
        mixHash(ct)
        return ct
    }

    mutating func decryptAndHash(_ ct: Data) throws -> Data {
        let plain = try cipher.decryptWithAd(h, cipher: ct)
        mixHash(ct)
        return plain
    }

    func split() -> (Data, Data) {
        CryptoPrimitives.hkdf2(chainingKey: ck, ikm: Data())
    }
}

/// Noise_XX_25519_AESGCM_SHA256 握手状态机（规范第 6 节）：
/// msg1 -> e; msg2 <- e, ee, s, es; msg3 -> s, se。
public final class NoiseXX: @unchecked Sendable {
    public static let protocolName = "Noise_XX_25519_AESGCM_SHA256"
    private static let prologuePrefix = Data("LanTransfer-Noise-v1".utf8)

    private let role: HandshakeRole
    private let localStatic: X25519KeyPair
    private let ephemeralSource: any EphemeralKeySource
    private var ss: NoiseSymmetricState
    private var e: X25519KeyPair?
    private var re: Data?
    private var rs: Data?
    private var step: Int = 0
    private var failed: Bool = false

    /// 仅用于测试模拟冒充者。
    internal var advertisedStaticOverride: Data?

    public init(
        role: HandshakeRole,
        localStatic: X25519KeyPair,
        prologue: Data,
        ephemeral: any EphemeralKeySource = EphemeralKeySources.secure
    ) {
        self.role = role
        self.localStatic = localStatic
        self.ephemeralSource = ephemeral
        var state = NoiseSymmetricState(protocolName: Self.protocolName)
        state.mixHash(prologue)
        self.ss = state
    }

    public static func prologue(preambleI: Data, preambleR: Data) -> Data {
        var out = Data(capacity: prologuePrefix.count + preambleI.count + preambleR.count)
        out.append(prologuePrefix)
        out.append(preambleI)
        out.append(preambleR)
        return out
    }

    public var remoteStatic: Data? { rs }
    public var handshakeHash: Data { ss.h }
    public var isComplete: Bool { step == 3 }

    private func advertisedStatic() -> Data {
        advertisedStaticOverride ?? localStatic.publicKey
    }

    private func dh(_ local: X25519KeyPair, _ remote: Data) throws -> Data {
        guard let shared = local.agree(remotePublic: remote) else {
            throw HandshakeAuthError("invalid DH result")
        }
        return shared
    }

    public func writeMessage(payload: Data) throws -> Data {
        precondition(!failed, "handshake failed")
        var out = Data()
        switch (step, role) {
        case (0, .initiator):
            let eph = ephemeralSource.next()
            self.e = eph
            out.append(eph.publicKey)
            ss.mixHash(eph.publicKey)
        case (1, .responder):
            let eph = ephemeralSource.next()
            self.e = eph
            out.append(eph.publicKey)
            ss.mixHash(eph.publicKey)
            ss.mixKey(try dh(eph, re!)) // ee
            out.append(ss.encryptAndHash(advertisedStatic())) // s
            ss.mixKey(try dh(localStatic, re!)) // es
        case (2, .initiator):
            out.append(ss.encryptAndHash(advertisedStatic())) // s
            ss.mixKey(try dh(localStatic, re!)) // se
        default:
            preconditionFailure("not this side's turn to write (step \(step), \(role))")
        }
        out.append(ss.encryptAndHash(payload))
        step += 1
        return out
    }

    public func readMessage(_ message: Data) throws -> Data {
        precondition(!failed, "handshake failed")
        let bytes = [UInt8](message)
        var p = 0

        func take(_ n: Int) throws -> Data {
            guard bytes.count - p >= n else {
                throw HandshakeAuthError("handshake message too short")
            }
            let slice = Data(bytes[p..<(p + n)])
            p += n
            return slice
        }

        do {
            switch (step, role) {
            case (0, .responder):
                let remoteE = try take(32)
                re = remoteE
                ss.mixHash(remoteE)
            case (1, .initiator):
                let remoteE = try take(32)
                re = remoteE
                ss.mixHash(remoteE)
                ss.mixKey(try dh(e!, remoteE)) // ee
                let staticKey = try ss.decryptAndHash(try take(32 + CryptoPrimitives.tagLen)) // s
                rs = staticKey
                ss.mixKey(try dh(e!, staticKey)) // es
            case (2, .responder):
                let staticKey = try ss.decryptAndHash(try take(32 + CryptoPrimitives.tagLen)) // s
                rs = staticKey
                ss.mixKey(try dh(e!, staticKey)) // se
            default:
                preconditionFailure("not this side's turn to read (step \(step), \(role))")
            }

            let remaining = Data(bytes[p..<bytes.count])
            let payload = try ss.decryptAndHash(remaining)
            step += 1
            return payload
        } catch let error as HandshakeAuthError {
            failed = true
            throw error
        }
    }

    public func split() -> (send: RecordCipher, receive: RecordCipher) {
        precondition(isComplete, "handshake not complete")
        let (k1, k2) = ss.split()
        switch role {
        case .initiator:
            return (RecordCipher(key: k1), RecordCipher(key: k2))
        case .responder:
            return (RecordCipher(key: k2), RecordCipher(key: k1))
        }
    }
}
