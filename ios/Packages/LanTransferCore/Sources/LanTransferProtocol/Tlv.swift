import Foundation

/// 载荷不符合 TLV Struct 规则（规范 2.2、2.3）。
internal struct MalformedError: Error, Sendable {
    let detail: String
    init(_ detail: String) { self.detail = detail }
}

/// 协议枚举版本校验。
public protocol ProtocolEnum: RawRepresentable, Sendable where RawValue == UInt8 {
    var introducedIn: ProtocolVersion { get }
}

public extension ProtocolEnum {
    var introducedIn: ProtocolVersion { .v1_0 }

    static func decode(code: UInt8, in version: ProtocolVersion) -> Self? {
        guard let value = Self(rawValue: code), value.introducedIn.definedIn(version) else {
            return nil
        }
        return value
    }
}

/// TLV Struct 编码器：字段必须按 tag 严格升序写入（规范 2.2 第 6 条）。
internal struct TlvWriter {
    private var out: [UInt8] = []
    private var lastTag: Int = -1

    private mutating func writeHeader(tag: UInt8, length: Int) {
        let tagInt = Int(tag)
        precondition(tagInt > lastTag, "TLV tags must be strictly increasing: \(tagInt) after \(lastTag)")
        precondition(length >= 0 && length <= Int(UInt32.max), "TLV field length out of range")
        lastTag = tagInt
        out.append(tag)
        let u32 = UInt32(length)
        out.append(UInt8((u32 >> 24) & 0xFF))
        out.append(UInt8((u32 >> 16) & 0xFF))
        out.append(UInt8((u32 >> 8) & 0xFF))
        out.append(UInt8(u32 & 0xFF))
    }

    mutating func u8(_ tag: UInt8, _ value: UInt8) {
        writeHeader(tag: tag, length: 1)
        out.append(value)
    }

    mutating func u16(_ tag: UInt8, _ value: UInt16) {
        writeHeader(tag: tag, length: 2)
        out.append(UInt8((value >> 8) & 0xFF))
        out.append(UInt8(value & 0xFF))
    }

    mutating func u32(_ tag: UInt8, _ value: UInt32) {
        writeHeader(tag: tag, length: 4)
        out.append(UInt8((value >> 24) & 0xFF))
        out.append(UInt8((value >> 16) & 0xFF))
        out.append(UInt8((value >> 8) & 0xFF))
        out.append(UInt8(value & 0xFF))
    }

    mutating func u64(_ tag: UInt8, _ value: UInt64) {
        writeHeader(tag: tag, length: 8)
        for shift in stride(from: 56, through: 0, by: -8) {
            out.append(UInt8((value >> UInt64(shift)) & 0xFF))
        }
    }

    mutating func i64(_ tag: UInt8, _ value: Int64) {
        u64(tag, UInt64(bitPattern: value))
    }

    mutating func protoEnum<E: ProtocolEnum>(_ tag: UInt8, _ value: E) {
        u8(tag, value.rawValue)
    }

    mutating func bytes(_ tag: UInt8, _ data: Data) {
        writeHeader(tag: tag, length: data.count)
        out.append(contentsOf: data)
    }

    mutating func bytes(_ tag: UInt8, _ array: [UInt8]) {
        writeHeader(tag: tag, length: array.count)
        out.append(contentsOf: array)
    }

    mutating func string(_ tag: UInt8, _ value: String) {
        bytes(tag, StrictUtf8.encode(value))
    }

    mutating func list<T>(_ tag: UInt8, _ items: [T], element: (inout TlvWriter, T) -> Void) {
        var body: [UInt8] = []
        let count = UInt32(items.count)
        body.append(UInt8((count >> 24) & 0xFF))
        body.append(UInt8((count >> 16) & 0xFF))
        body.append(UInt8((count >> 8) & 0xFF))
        body.append(UInt8(count & 0xFF))
        for item in items {
            var elemWriter = TlvWriter()
            element(&elemWriter, item)
            let elemBytes = elemWriter.toBytes()
            let elemLen = UInt32(elemBytes.count)
            body.append(UInt8((elemLen >> 24) & 0xFF))
            body.append(UInt8((elemLen >> 16) & 0xFF))
            body.append(UInt8((elemLen >> 8) & 0xFF))
            body.append(UInt8(elemLen & 0xFF))
            body.append(contentsOf: elemBytes)
        }
        bytes(tag, body)
    }

    func toBytes() -> [UInt8] { out }
    func toData() -> Data { Data(out) }
}

/// 已解析的 TLV Struct：校验 tag 严格递增、无越界、恰好填满缓冲区，读取时跳过未知 tag。
internal struct TlvStruct {
    private let buf: [UInt8]
    private var starts: [Int]
    private var lengths: [Int]

    init(bytes: [UInt8], offset: Int = 0, length: Int? = nil) throws {
        let len = length ?? (bytes.count - offset)
        guard offset >= 0, len >= 0, offset + len <= bytes.count else {
            throw MalformedError("invalid slice range")
        }
        self.buf = bytes
        self.starts = [Int](repeating: -1, count: 256)
        self.lengths = [Int](repeating: 0, count: 256)

        var pos = offset
        let end = offset + len
        var lastTag = -1
        while pos < end {
            if end - pos < 5 {
                throw MalformedError("truncated field header")
            }
            let tag = Int(bytes[pos])
            let fieldLen = Self.readU32BE(bytes, at: pos + 1)
            if UInt64(fieldLen) > UInt64(end - pos - 5) {
                throw MalformedError("field \(tag) exceeds buffer")
            }
            if tag <= lastTag {
                throw MalformedError("tag \(tag) not strictly increasing")
            }
            lastTag = tag
            starts[tag] = pos + 5
            lengths[tag] = Int(fieldLen)
            pos += 5 + Int(fieldLen)
        }
    }

    func has(_ tag: UInt8) -> Bool { starts[Int(tag)] >= 0 }
    func length(of tag: UInt8) -> Int { lengths[Int(tag)] }

    private func startOf(_ tag: UInt8, _ name: String) throws -> Int {
        let s = starts[Int(tag)]
        guard s >= 0 else {
            throw MalformedError("missing field \(tag) (\(name))")
        }
        return s
    }

    private func fixed(_ tag: UInt8, _ name: String, size: Int) throws -> Int {
        let s = try startOf(tag, name)
        let actual = lengths[Int(tag)]
        guard actual == size else {
            throw MalformedError("field \(tag) (\(name)) must be \(size) bytes, got \(actual)")
        }
        return s
    }

    func u8(_ tag: UInt8, _ name: String) throws -> UInt8 {
        let s = try fixed(tag, name, size: 1)
        return buf[s]
    }

    func u16(_ tag: UInt8, _ name: String) throws -> UInt16 {
        let s = try fixed(tag, name, size: 2)
        return (UInt16(buf[s]) << 8) | UInt16(buf[s + 1])
    }

    func u32(_ tag: UInt8, _ name: String) throws -> UInt32 {
        let s = try fixed(tag, name, size: 4)
        return Self.readU32BE(buf, at: s)
    }

    func u64(_ tag: UInt8, _ name: String) throws -> UInt64 {
        let s = try fixed(tag, name, size: 8)
        return Self.readU64BE(buf, at: s)
    }

    func i64(_ tag: UInt8, _ name: String) throws -> Int64 {
        Int64(bitPattern: try u64(tag, name))
    }

    func optU64(_ tag: UInt8, _ name: String) throws -> UInt64? {
        has(tag) ? try u64(tag, name) : nil
    }

    func optI64(_ tag: UInt8, _ name: String) throws -> Int64? {
        has(tag) ? try i64(tag, name) : nil
    }

    func data(_ tag: UInt8, _ name: String) throws -> Data {
        let s = try startOf(tag, name)
        let len = lengths[Int(tag)]
        return Data(buf[s..<(s + len)])
    }

    func fixedData(_ tag: UInt8, _ name: String, size: Int) throws -> Data {
        let s = try fixed(tag, name, size: size)
        return Data(buf[s..<(s + size)])
    }

    func string(_ tag: UInt8, _ name: String) throws -> String {
        let s = try startOf(tag, name)
        let len = lengths[Int(tag)]
        let slice = Array(buf[s..<(s + len)])
        guard let decoded = StrictUtf8.decode(slice) else {
            throw MalformedError("field \(tag) (\(name)) is not valid UTF-8")
        }
        return decoded
    }

    func optString(_ tag: UInt8, _ name: String) throws -> String? {
        has(tag) ? try string(tag, name) : nil
    }

    func protoEnum<E: ProtocolEnum>(_ tag: UInt8, _ name: String, version: ProtocolVersion) throws -> E {
        let code = try u8(tag, name)
        guard let value = E.decode(code: code, in: version) else {
            throw MalformedError("field \(tag) (\(name)) has undefined value \(code)")
        }
        return value
    }

    func list(_ tag: UInt8, _ name: String) throws -> [TlvStruct] {
        let p = try startOf(tag, name)
        let totalLen = lengths[Int(tag)]
        let end = p + totalLen
        guard totalLen >= 4 else {
            throw MalformedError("list \(tag) (\(name)) truncated")
        }
        let count = Self.readU32BE(buf, at: p)
        var pos = p + 4
        var items: [TlvStruct] = []
        // 防御极端大 count 导致无界内存预分配：至少每个元素占 4 字节长度头
        if UInt64(count) > UInt64((end - pos) / 4) {
            throw MalformedError("list \(tag) (\(name)) count exceeds buffer")
        }
        items.reserveCapacity(Int(count))
        for _ in 0..<count {
            if end - pos < 4 {
                throw MalformedError("list \(tag) (\(name)) element header truncated")
            }
            let elemLen = Self.readU32BE(buf, at: pos)
            if UInt64(elemLen) > UInt64(end - pos - 4) {
                throw MalformedError("list \(tag) (\(name)) element exceeds buffer")
            }
            items.append(try TlvStruct(bytes: buf, offset: pos + 4, length: Int(elemLen)))
            pos += 4 + Int(elemLen)
        }
        if pos != end {
            throw MalformedError("list \(tag) (\(name)) has trailing bytes")
        }
        return items
    }

    private static func readU32BE(_ b: [UInt8], at i: Int) -> UInt32 {
        (UInt32(b[i]) << 24)
            | (UInt32(b[i + 1]) << 16)
            | (UInt32(b[i + 2]) << 8)
            | UInt32(b[i + 3])
    }

    private static func readU64BE(_ b: [UInt8], at i: Int) -> UInt64 {
        var result: UInt64 = 0
        for k in 0..<8 {
            result = (result << 8) | UInt64(b[i + k])
        }
        return result
    }
}
