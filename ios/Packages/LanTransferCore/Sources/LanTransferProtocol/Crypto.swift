import CryptoKit
import Foundation

/// X25519 密钥对（纯 Apple CryptoKit 实现，零第三方依赖）。
public struct X25519KeyPair: Hashable, Sendable {
    public static let keySize = 32

    private let rawPrivateKey: Data
    public let publicKey: Data

    private init(rawPrivateKey: Data, publicKey: Data) {
        self.rawPrivateKey = rawPrivateKey
        self.publicKey = publicKey
    }

    public func privateKeyBytes() -> Data {
        rawPrivateKey
    }

    public static func fromPrivateKey(_ privateKey: Data) throws -> X25519KeyPair {
        guard privateKey.count == keySize else {
            throw HandshakeAuthError("X25519 private key must be 32 bytes")
        }
        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: privateKey)
        return X25519KeyPair(rawPrivateKey: privateKey, publicKey: key.publicKey.rawRepresentation)
    }

    public static func generate() -> X25519KeyPair {
        let key = Curve25519.KeyAgreement.PrivateKey()
        return X25519KeyPair(rawPrivateKey: key.rawRepresentation, publicKey: key.publicKey.rawRepresentation)
    }

    /// 与对端 32 字节公钥计算 DH 共享密钥；对端公钥无效或产生全零（小阶点）时返回 nil。
    public func agree(remotePublic: Data) -> Data? {
        guard remotePublic.count == Self.keySize,
              let priv = try? Curve25519.KeyAgreement.PrivateKey(rawRepresentation: rawPrivateKey),
              let pub = try? Curve25519.KeyAgreement.PublicKey(rawRepresentation: remotePublic),
              let secret = try? priv.sharedSecretFromKeyAgreement(with: pub) else {
            return nil
        }
        let shared = secret.withUnsafeBytes { Data($0) }
        guard shared.count == Self.keySize, !shared.allSatisfy({ $0 == 0 }) else {
            return nil
        }
        return shared
    }
}

/// Noise 临时密钥来源端口：生产环境用 CSPRNG，测试注入固定向量。
public protocol EphemeralKeySource: Sendable {
    func next() -> X25519KeyPair
}

public enum EphemeralKeySources: Sendable {
    private struct SecureSource: EphemeralKeySource {
        func next() -> X25519KeyPair { .generate() }
    }

    private final class FixedSource: EphemeralKeySource, @unchecked Sendable {
        private let lock = NSLock()
        private var keys: [Data]
        private var index = 0

        init(keys: [Data]) {
            self.keys = keys
        }

        func next() -> X25519KeyPair {
            lock.withLock {
                precondition(index < keys.count, "Fixed ephemeral key source exhausted")
                let raw = keys[index]
                index += 1
                return try! X25519KeyPair.fromPrivateKey(raw)
            }
        }
    }

    public static let secure: any EphemeralKeySource = SecureSource()

    public static func fixed(_ privateKeys: [Data]) -> any EphemeralKeySource {
        FixedSource(keys: privateKeys)
    }
}

/// 平台密码学原语：CryptoKit SHA-256、HMAC-SHA256、Noise HKDF2、AES-256-GCM。
internal enum CryptoPrimitives {
    static let hashLen = 32
    static let tagLen = 16

    static func sha256(_ parts: Data...) -> Data {
        var hasher = SHA256()
        for part in parts {
            hasher.update(data: part)
        }
        return Data(hasher.finalize())
    }

    static func hmac(key: Data, parts: [Data]) -> Data {
        var mac = HMAC<SHA256>(key: SymmetricKey(data: key))
        for part in parts {
            mac.update(data: part)
        }
        return Data(mac.finalize())
    }

    static func hkdf2(chainingKey: Data, ikm: Data) -> (Data, Data) {
        let temp = hmac(key: chainingKey, parts: [ikm])
        let o1 = hmac(key: temp, parts: [Data([1])])
        let o2 = hmac(key: temp, parts: [o1, Data([2])])
        return (o1, o2)
    }

    static func makeNonce(_ n: UInt64) -> Data {
        var bytes = [UInt8](repeating: 0, count: 12)
        for i in 0..<8 {
            bytes[4 + i] = UInt8((n >> UInt64(56 - i * 8)) & 0xFF)
        }
        return Data(bytes)
    }

    static func aesGcmEncrypt(key: Data, n: UInt64, ad: Data, plain: Data) -> Data {
        let nonce = try! AES.GCM.Nonce(data: makeNonce(n))
        let box = try! AES.GCM.seal(plain, using: SymmetricKey(data: key), nonce: nonce, authenticating: ad)
        var out = Data(capacity: box.ciphertext.count + box.tag.count)
        out.append(box.ciphertext)
        out.append(box.tag)
        return out
    }

    static func aesGcmDecrypt(key: Data, n: UInt64, ad: Data, cipher: Data) -> Data? {
        guard cipher.count >= tagLen else { return nil }
        let split = cipher.index(cipher.endIndex, offsetBy: -tagLen)
        let ct = cipher[cipher.startIndex..<split]
        let tag = cipher[split..<cipher.endIndex]
        guard let nonce = try? AES.GCM.Nonce(data: makeNonce(n)),
              let box = try? AES.GCM.SealedBox(nonce: nonce, ciphertext: ct, tag: tag),
              let plain = try? AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: ad) else {
            return nil
        }
        return plain
    }
}

/// 公钥指纹 = SHA-256(32 字节公钥)。
public enum Fingerprint: Sendable {
    public static func of(_ publicKey: Data) -> Data {
        Data(SHA256.hash(data: publicKey))
    }
}

/// 配对 6 位验证码与承诺派生（规范第 10 节）。
public enum VerificationCode: Sendable {
    private static let commitPrefix = Data("LanTransfer-Commit-v1".utf8)
    private static let sasPrefix = Data("LanTransfer-SAS-v1".utf8)

    public static func commitment(pkI: Data, pkR: Data, nI: Data) -> Data {
        CryptoPrimitives.sha256(commitPrefix, pkI, pkR, nI)
    }

    public static func verifyCommitment(_ commitment: Data, pkI: Data, pkR: Data, nI: Data) -> Bool {
        let expected = Self.commitment(pkI: pkI, pkR: pkR, nI: nI)
        guard expected.count == commitment.count else { return false }
        var diff: UInt8 = 0
        for (a, b) in zip(expected, commitment) {
            diff |= a ^ b
        }
        return diff == 0
    }

    public static func sas(pkI: Data, pkR: Data, nI: Data, nR: Data) -> String {
        let hash = [UInt8](CryptoPrimitives.sha256(sasPrefix, pkI, pkR, nI, nR))
        var v: UInt64 = 0
        for i in 0..<8 {
            v = (v << 8) | UInt64(hash[i])
        }
        return String(format: "%06llu", v % 1_000_000)
    }
}
