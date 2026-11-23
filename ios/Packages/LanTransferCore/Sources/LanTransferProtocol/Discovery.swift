import Foundation

/// Discovery_Announcement 数据模型（规范第 3 节）。
public struct Announcement: Hashable, Sendable {
    public let deviceId: DeviceId
    public let displayName: String
    public let deviceType: DeviceType
    public let version: ProtocolVersion
    public let port: UInt16

    public init(deviceId: DeviceId, displayName: String, deviceType: DeviceType, version: ProtocolVersion, port: UInt16) {
        self.deviceId = deviceId
        self.displayName = displayName
        self.deviceType = deviceType
        self.version = version
        self.port = port
    }
}

/// Discovery_Announcement 编解码（魔数 "LTDA"，最大 512 字节）。
public enum AnnouncementCodec: Sendable {
    private static let magic: [UInt8] = Array("LTDA".utf8)

    public static func isNameValid(_ name: String) -> Bool {
        let count = name.unicodeScalars.count
        return count >= ProtocolConstants.minNameCodePoints && count <= ProtocolConstants.maxNameCodePoints
    }

    public static func encode(_ announcement: Announcement) -> Data {
        precondition(isNameValid(announcement.displayName) && announcement.port != 0,
                     "display name must be 1..32 code points and port must be 1..65535")
        var w = TlvWriter()
        w.bytes(1, announcement.deviceId.bytes)
        w.string(2, announcement.displayName)
        w.u8(3, announcement.deviceType.rawValue)
        w.u8(4, announcement.version.major)
        w.u8(5, announcement.version.minor)
        w.u16(6, announcement.port)
        var out = magic
        out.append(contentsOf: w.toBytes())
        precondition(out.count <= ProtocolConstants.maxAnnouncement, "announcement exceeds 512 bytes")
        return Data(out)
    }

    public static func decode(_ datagram: Data) -> Announcement? {
        let bytes = [UInt8](datagram)
        guard bytes.count <= ProtocolConstants.maxAnnouncement,
              bytes.count >= magic.count,
              bytes[0] == magic[0],
              bytes[1] == magic[1],
              bytes[2] == magic[2],
              bytes[3] == magic[3] else {
            return nil
        }
        do {
            let s = try TlvStruct(bytes: bytes, offset: 4, length: bytes.count - 4)
            let name = try s.string(2, "display_name")
            let typeCode = try s.u8(3, "device_type")
            let port = try s.u16(6, "port")
            guard isNameValid(name),
                  let deviceType = DeviceType(rawValue: typeCode),
                  port != 0,
                  let deviceId = DeviceId(bytes: try s.fixedData(1, "device_id", size: 16)) else {
                return nil
            }
            let version = ProtocolVersion(
                major: try s.u8(4, "proto_major"),
                minor: try s.u8(5, "proto_minor")
            )
            return Announcement(
                deviceId: deviceId,
                displayName: name,
                deviceType: deviceType,
                version: version,
                port: port
            )
        } catch {
            return nil
        }
    }
}

/// 连接前导的版本协商结果（规范第 5 节）。
public enum Negotiation: Hashable, Sendable {
    case compatible(ProtocolVersion)
    case incompatible(local: ProtocolVersion, remote: ProtocolVersion)
}

/// 连接前导 "LANT" || major || minor（6 字节）。
public enum PreambleCodec: Sendable {
    public static let size = 6
    private static let magic: [UInt8] = Array("LANT".utf8)

    public static func encode(_ version: ProtocolVersion) -> Data {
        Data(magic + [version.major, version.minor])
    }

    public static func decode(_ data: Data) -> ProtocolVersion? {
        let bytes = [UInt8](data)
        guard bytes.count == size,
              bytes[0] == magic[0],
              bytes[1] == magic[1],
              bytes[2] == magic[2],
              bytes[3] == magic[3] else {
            return nil
        }
        return ProtocolVersion(major: bytes[4], minor: bytes[5])
    }

    public static func negotiate(local: ProtocolVersion, remote: ProtocolVersion) -> Negotiation {
        if local.major != remote.major {
            return .incompatible(local: local, remote: remote)
        }
        return .compatible(ProtocolVersion(major: local.major, minor: min(local.minor, remote.minor)))
    }
}
