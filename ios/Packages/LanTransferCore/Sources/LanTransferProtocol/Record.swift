import Foundation

/// 安全记录层（规范第 7 节）：`len:u16_be || AES-256-GCM(k, nonce = 0 || n, ad = [], plain)`。
public final class RecordCipher: @unchecked Sendable {
    internal static let lastNonce: UInt64 = UInt64.max

    private let lock = NSLock()
    private let key: Data
    private var n: UInt64 = 0
    private var reader: RecordReader?

    internal init(key: Data) {
        self.key = key
    }

    public func seal(_ plain: Data) -> Data {
        lock.withLock {
            if plain.isEmpty { return Data() }
            let bytes = [UInt8](plain)
            var out = Data(capacity: bytes.count + ((bytes.count / ProtocolConstants.recordMaxPlaintext) + 1) * 18)
            var p = 0
            while p < bytes.count {
                let chunkLen = min(ProtocolConstants.recordMaxPlaintext, bytes.count - p)
                precondition(n != Self.lastNonce, "record counter exhausted")
                let chunk = Data(bytes[p..<(p + chunkLen)])
                let ct = CryptoPrimitives.aesGcmEncrypt(key: key, n: n, ad: Data(), plain: chunk)
                n += 1
                let len = UInt16(ct.count)
                out.append(UInt8((len >> 8) & 0xFF))
                out.append(UInt8(len & 0xFF))
                out.append(ct)
                p += chunkLen
            }
            return out
        }
    }

    public func openStream() -> RecordReader {
        lock.withLock {
            precondition(reader == nil, "stream already opened")
            let r = RecordReader(key: key)
            reader = r
            return r
        }
    }
}

public struct RecordFeedResult: Hashable, Sendable {
    public let chunks: [Data]
    public let integrityError: Bool

    public init(chunks: [Data], integrityError: Bool) {
        self.chunks = chunks
        self.integrityError = integrityError
    }
}

/// 流式记录层解密器；任何长度或 AEAD 校验失败后立即置 failed，不再输出明文。
public final class RecordReader: @unchecked Sendable {
    private let lock = NSLock()
    private let key: Data
    private var lenBuf: [UInt8] = [0, 0]
    private var lenFill: Int = 0
    private var n: UInt64 = 0
    private var body: [UInt8]?
    private var bodyFill: Int = 0
    private var failed: Bool = false

    internal init(key: Data) {
        self.key = key
    }

    public var isFailed: Bool {
        lock.withLock { failed }
    }

    public var bufferedBytes: Int {
        lock.withLock { lenFill + bodyFill }
    }

    public func feed(_ data: Data) -> RecordFeedResult {
        feed([UInt8](data))
    }

    public func feed(_ bytes: [UInt8]) -> RecordFeedResult {
        lock.withLock {
            if failed {
                return RecordFeedResult(chunks: [], integrityError: true)
            }
            var output: [Data] = []
            var i = 0
            let total = bytes.count
            while i < total {
                if body == nil {
                    let take = min(2 - lenFill, total - i)
                    for k in 0..<take {
                        lenBuf[lenFill + k] = bytes[i + k]
                    }
                    lenFill += take
                    i += take
                    if lenFill < 2 { break }

                    let recLen = (Int(lenBuf[0]) << 8) | Int(lenBuf[1])
                    if recLen < ProtocolConstants.recordMinCiphertext {
                        return fail(&output)
                    }
                    body = [UInt8](repeating: 0, count: recLen)
                    bodyFill = 0
                    continue
                }

                let needed = body!.count - bodyFill
                let take = min(needed, total - i)
                for k in 0..<take {
                    body![bodyFill + k] = bytes[i + k]
                }
                bodyFill += take
                i += take
                if bodyFill < body!.count { break }

                if n == RecordCipher.lastNonce {
                    return fail(&output)
                }
                guard let plain = CryptoPrimitives.aesGcmDecrypt(key: key, n: n, ad: Data(), cipher: Data(body!)) else {
                    return fail(&output)
                }
                n += 1
                output.append(plain)
                body = nil
                bodyFill = 0
                lenFill = 0
            }
            return RecordFeedResult(chunks: output, integrityError: false)
        }
    }

    private func fail(_ output: inout [Data]) -> RecordFeedResult {
        failed = true
        body = nil
        bodyFill = 0
        lenFill = 0
        return RecordFeedResult(chunks: output, integrityError: true)
    }
}
