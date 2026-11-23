import Foundation

/// 协议常量（规范第 1 节），与 Android Constants.kt 和 Windows Constants.cs 保持逐项一致。
public enum ProtocolConstants: Sendable {
    public static let defaultPort: Int = 42580
    public static let fallbackPortRange: ClosedRange<Int> = 42590...42599
    public static let discoveryPort: Int = 42581
    public static let multicastGroup: String = "239.255.42.58"

    public static let frameMagic: UInt16 = 0x4C54
    public static let frameHeaderSize: Int = 7
    public static let maxPayload: Int = 2 * 1024 * 1024

    public static let maxTextBytes: Int = 1_048_576
    public static let maxChunk: Int = 65_536
    public static let maxFilesPerOffer: Int = 1000
    public static let maxFileSize: Int64 = 1 << 40 // 1 TiB
    public static let maxMimeBytes: Int = 255

    public static let recordMinCiphertext: Int = 17
    public static let recordMaxCiphertext: Int = 65_535
    public static let recordMaxPlaintext: Int = recordMaxCiphertext - 16

    public static let maxAnnouncement: Int = 512
    public static let maxQrText: Int = 512
    public static let maxQrIps: Int = 4
    public static let minNameCodePoints: Int = 1
    public static let maxNameCodePoints: Int = 32
}

/// Protocol_Version：主版本号.次版本号。
public struct ProtocolVersion: Comparable, Hashable, Sendable, CustomStringConvertible {
    public let major: UInt8
    public let minor: UInt8

    public init(major: UInt8, minor: UInt8) {
        self.major = major
        self.minor = minor
    }

    public static let v1_0 = ProtocolVersion(major: 1, minor: 0)

    /// 本实现支持的最新版本。
    public static let current = v1_0

    public var description: String { "\(major).\(minor)" }

    /// 本版本引入的特性是否在 `negotiated` 版本中可用。
    public func definedIn(_ negotiated: ProtocolVersion) -> Bool {
        major == negotiated.major && minor <= negotiated.minor
    }

    public static func < (lhs: ProtocolVersion, rhs: ProtocolVersion) -> Bool {
        if lhs.major != rhs.major {
            return lhs.major < rhs.major
        }
        return lhs.minor < rhs.minor
    }
}

/// 设备类型（公告、握手身份中出现；与 Android/Windows 协议码值完全一致）。
public enum DeviceType: UInt8, CaseIterable, Hashable, Sendable {
    case android = 1
    case windows = 2
    case ios = 3

    public static func fromCode(_ code: Int) -> DeviceType? {
        guard let raw = UInt8(exactly: code) else { return nil }
        return DeviceType(rawValue: raw)
    }
}

/// 16 字节设备标识。
public struct DeviceId: Hashable, Sendable, CustomStringConvertible {
    public static let byteCount = 16
    public let bytes: Data

    public init?(bytes: Data) {
        guard bytes.count == Self.byteCount else { return nil }
        self.bytes = bytes
    }

    public var hexString: String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    public var description: String { hexString }
}

/// 16 字节消息标识。
public struct MessageId: Hashable, Sendable, CustomStringConvertible {
    public static let byteCount = 16
    public let bytes: Data

    public init?(bytes: Data) {
        guard bytes.count == Self.byteCount else { return nil }
        self.bytes = bytes
    }

    public var hexString: String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    public var description: String { hexString }
}

/// 16 字节传输任务标识。
public struct TransferId: Hashable, Sendable, CustomStringConvertible {
    public static let byteCount = 16
    public let bytes: Data

    public init?(bytes: Data) {
        guard bytes.count == Self.byteCount else { return nil }
        self.bytes = bytes
    }

    public var hexString: String {
        bytes.map { String(format: "%02x", $0) }.joined()
    }

    public var description: String { hexString }
}

/// IPv4 地址（大端 32 位存储，与数据库 last_ip / 网络字节序一致）。
public struct Ipv4: Hashable, Comparable, Sendable, CustomStringConvertible {
    public let rawValue: UInt32

    public init(rawValue: UInt32) {
        self.rawValue = rawValue
    }

    public init(_ a: UInt8, _ b: UInt8, _ c: UInt8, _ d: UInt8) {
        self.rawValue = (UInt32(a) << 24) | (UInt32(b) << 16) | (UInt32(c) << 8) | UInt32(d)
    }

    public init?(parsing string: String) {
        let parts = string.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var octets: [UInt8] = []
        octets.reserveCapacity(4)
        for part in parts {
            guard !part.isEmpty, part.allSatisfy(\.isASCII), part.allSatisfy(\.isNumber) else {
                return nil
            }
            if part.count > 1 && part.first == "0" {
                return nil
            }
            guard let value = UInt8(part) else { return nil }
            octets.append(value)
        }
        self.init(octets[0], octets[1], octets[2], octets[3])
    }

    public var octets: (UInt8, UInt8, UInt8, UInt8) {
        (
            UInt8((rawValue >> 24) & 0xFF),
            UInt8((rawValue >> 16) & 0xFF),
            UInt8((rawValue >> 8) & 0xFF),
            UInt8(rawValue & 0xFF)
        )
    }

    public var description: String {
        let o = octets
        return "\(o.0).\(o.1).\(o.2).\(o.3)"
    }

    public static func < (lhs: Ipv4, rhs: Ipv4) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}
