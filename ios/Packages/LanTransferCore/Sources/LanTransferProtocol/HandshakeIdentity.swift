import Foundation

/// 握手 msg3 中的连接意图（规范第 6 节）。
public enum Intent: UInt8, CaseIterable, ProtocolEnum, Hashable, Sendable {
    case session = 1
    case pair = 2
    case qrPair = 3
}

/// msg2 / msg3 的握手身份载荷；`intent` 仅在 msg3 中出现。
public struct HandshakeIdentity: Hashable, Sendable {
    public let deviceId: DeviceId
    public let displayName: String
    public let deviceType: DeviceType
    public let intent: Intent?

    public init(deviceId: DeviceId, displayName: String, deviceType: DeviceType, intent: Intent? = nil) {
        self.deviceId = deviceId
        self.displayName = displayName
        self.deviceType = deviceType
        self.intent = intent
    }
}

/// HandshakeIdentity TLV 编解码（1 device_id、2 display_name、3 device_type、4 intent）。
public enum HandshakeIdentityCodec: Sendable {
    public static func encode(_ identity: HandshakeIdentity) -> Data {
        var w = TlvWriter()
        w.bytes(1, identity.deviceId.bytes)
        w.string(2, identity.displayName)
        w.u8(3, identity.deviceType.rawValue)
        if let intent = identity.intent {
            w.protoEnum(4, intent)
        }
        return w.toData()
    }

    public static func decode(_ data: Data, expectIntent: Bool, version: ProtocolVersion = .current) -> HandshakeIdentity? {
        do {
            let s = try TlvStruct(bytes: [UInt8](data))
            let typeCode = try s.u8(3, "device_type")
            guard let deviceType = DeviceType(rawValue: typeCode),
                  s.has(4) == expectIntent,
                  let deviceId = DeviceId(bytes: try s.fixedData(1, "device_id", size: 16)) else {
                return nil
            }
            let intent: Intent? = expectIntent ? try s.protoEnum(4, "intent", version: version) : nil
            return HandshakeIdentity(
                deviceId: deviceId,
                displayName: try s.string(2, "display_name"),
                deviceType: deviceType,
                intent: intent
            )
        } catch {
            return nil
        }
    }
}
