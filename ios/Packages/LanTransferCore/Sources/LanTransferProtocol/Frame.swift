import Foundation

/// FrameEncoder.encode 的结果。
public enum EncodeResult: Hashable, Sendable {
    case ok(Data)
    case payloadTooLarge(Int)
}

/// Frame 编码器（规范第 8 节）。
public enum FrameEncoder: Sendable {
    public static func encode(
        _ message: ProtocolMessage,
        version: ProtocolVersion = .current,
        maxPayload: Int = ProtocolConstants.maxPayload
    ) -> EncodeResult {
        precondition(message.type.introducedIn.definedIn(version), "\(message.type) is not defined in \(version)")
        let payload = MessageCodec.encodePayload(message)
        if payload.count > maxPayload {
            return .payloadTooLarge(payload.count)
        }
        var out: [UInt8] = []
        out.reserveCapacity(ProtocolConstants.frameHeaderSize + payload.count)
        out.append(UInt8((ProtocolConstants.frameMagic >> 8) & 0xFF))
        out.append(UInt8(ProtocolConstants.frameMagic & 0xFF))
        out.append(message.type.rawValue)
        let len = UInt32(payload.count)
        out.append(UInt8((len >> 24) & 0xFF))
        out.append(UInt8((len >> 16) & 0xFF))
        out.append(UInt8((len >> 8) & 0xFF))
        out.append(UInt8(len & 0xFF))
        out.append(contentsOf: payload)
        return .ok(Data(out))
    }

    public static func encodeOrPrecondition(_ message: ProtocolMessage, version: ProtocolVersion = .current) -> Data {
        switch encode(message, version: version) {
        case .ok(let data):
            return data
        case .payloadTooLarge(let count):
            preconditionFailure("payload too large: \(count)")
        }
    }
}

/// Frame 解码错误（规范第 8 节）。
public enum DecodeError: Error, Hashable, Sendable {
    case badMagic
    case unknownType(Int)
    case payloadTooLarge(UInt32)
    case malformedPayload(type: Int, detail: String)
}

/// 一次流式输入解码得到的完整消息批次及可选错误。
public struct DecodeBatch: Hashable, Sendable {
    public let messages: [ProtocolMessage]
    public let error: DecodeError?

    public init(messages: [ProtocolMessage], error: DecodeError?) {
        self.messages = messages
        self.error = error
    }
}

/// 流式 Frame 解码器（帧头到齐即检查；出错或关闭后不再接受输入；内存有界）。
public final class FrameDecoder: @unchecked Sendable {
    private let lock = NSLock()
    private let version: ProtocolVersion
    private let maxPayload: Int
    private var header = [UInt8](repeating: 0, count: ProtocolConstants.frameHeaderSize)
    private var headerFill: Int = 0
    private var currentType: MessageType = .heartbeat
    private var payload: [UInt8]?
    private var payloadFill: Int = 0
    private var failedError: DecodeError?
    private var closed: Bool = false

    public init(version: ProtocolVersion = .current, maxPayload: Int = ProtocolConstants.maxPayload) {
        self.version = version
        self.maxPayload = maxPayload
    }

    public var error: DecodeError? {
        lock.withLock { failedError }
    }

    public var isClosed: Bool {
        lock.withLock { closed }
    }

    public var bufferedBytes: Int {
        lock.withLock { headerFill + payloadFill }
    }

    public func feed(_ data: Data) -> DecodeBatch {
        feed([UInt8](data))
    }

    public func feed(_ bytes: [UInt8]) -> DecodeBatch {
        lock.withLock {
            if failedError != nil || closed {
                return DecodeBatch(messages: [], error: nil)
            }

            var output: [ProtocolMessage] = []
            var i = 0
            let total = bytes.count

            while true {
                if payload == nil {
                    if i == total { break }
                    let take = min(ProtocolConstants.frameHeaderSize - headerFill, total - i)
                    for k in 0..<take {
                        header[headerFill + k] = bytes[i + k]
                    }
                    headerFill += take
                    i += take
                    if headerFill < ProtocolConstants.frameHeaderSize { break }

                    if let failure = startFrame() {
                        return fail(&output, failure)
                    }
                }

                if payload != nil {
                    let needed = payload!.count - payloadFill
                    if needed > 0 {
                        if i == total { break }
                        let take = min(needed, total - i)
                        for k in 0..<take {
                            payload![payloadFill + k] = bytes[i + k]
                        }
                        payloadFill += take
                        i += take
                        if payloadFill < payload!.count { break }
                    }
                } else {
                    break
                }

                do {
                    let msg = try MessageCodec.decodePayload(type: currentType, bytes: payload!, version: version)
                    output.append(msg)
                    reset()
                } catch let e as MalformedError {
                    return fail(&output, .malformedPayload(type: Int(currentType.rawValue), detail: e.detail))
                } catch {
                    return fail(&output, .malformedPayload(type: Int(currentType.rawValue), detail: "\(error)"))
                }
            }

            return DecodeBatch(messages: output, error: nil)
        }
    }

    public func close() {
        lock.withLock {
            reset()
            closed = true
        }
    }

    private func startFrame() -> DecodeError? {
        let magic = (UInt16(header[0]) << 8) | UInt16(header[1])
        if magic != ProtocolConstants.frameMagic {
            return .badMagic
        }
        let code = header[2]
        guard let msgType = MessageType.decode(code: code, in: version) else {
            return .unknownType(Int(code))
        }
        let len = (UInt32(header[3]) << 24)
            | (UInt32(header[4]) << 16)
            | (UInt32(header[5]) << 8)
            | UInt32(header[6])
        if UInt64(len) > UInt64(maxPayload) {
            return .payloadTooLarge(len)
        }
        currentType = msgType
        payload = [UInt8](repeating: 0, count: Int(len))
        payloadFill = 0
        return nil
    }

    private func fail(_ output: inout [ProtocolMessage], _ err: DecodeError) -> DecodeBatch {
        failedError = err
        reset()
        return DecodeBatch(messages: output, error: err)
    }

    private func reset() {
        headerFill = 0
        payload = nil
        payloadFill = 0
    }
}
