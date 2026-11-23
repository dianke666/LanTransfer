import Foundation

/// QR_Payload 模型（规范第 4 节）。
public struct QrPayload: Hashable, Sendable {
    public let deviceId: DeviceId
    public let displayName: String
    public let ipv4List: [UInt32]
    public let port: UInt16
    public let fingerprint: Data
    public let token: Data

    public init(
        deviceId: DeviceId,
        displayName: String,
        ipv4List: [UInt32],
        port: UInt16,
        fingerprint: Data,
        token: Data
    ) {
        self.deviceId = deviceId
        self.displayName = displayName
        self.ipv4List = ipv4List
        self.port = port
        self.fingerprint = fingerprint
        self.token = token
    }
}

public enum QrError: Error, Hashable, Sendable {
    case tooLong
    case badPrefix
    case badBase32
    case malformed
    case invalidField
}

public enum QrDecodeResult: Hashable, Sendable {
    case ok(QrPayload)
    case fail(QrError)

    public var payload: QrPayload? {
        if case .ok(let p) = self { return p }
        return nil
    }
}

/// RFC 4648 Base32（大写、无填充、严格末尾零比特校验）。
public enum Base32: Sendable {
    private static let alphabet: [UInt8] = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".utf8)

    public static func encode(_ data: Data) -> String {
        let bytes = [UInt8](data)
        var out: [UInt8] = []
        out.reserveCapacity((bytes.count * 8 + 4) / 5)
        var buffer = 0
        var bits = 0
        for x in bytes {
            buffer = (buffer << 8) | Int(x)
            bits += 8
            while bits >= 5 {
                out.append(alphabet[(buffer >> (bits - 5)) & 0x1F])
                bits -= 5
            }
            buffer &= 0xFF
        }
        if bits > 0 {
            out.append(alphabet[(buffer << (5 - bits)) & 0x1F])
        }
        return String(decoding: out, as: UTF8.self)
    }

    public static func decode(_ string: String) -> Data? {
        let chars = Array(string.utf8)
        let rem = chars.count % 8
        guard rem == 0 || rem == 2 || rem == 4 || rem == 5 || rem == 7 else {
            return nil
        }
        var out = [UInt8](repeating: 0, count: chars.count * 5 / 8)
        var buffer = 0
        var bits = 0
        var p = 0
        for c in chars {
            let v: Int
            if c >= 65 && c <= 90 { // A-Z
                v = Int(c - 65)
            } else if c >= 50 && c <= 55 { // 2-7
                v = Int(c - 50) + 26
            } else {
                return nil
            }
            buffer = ((buffer << 5) | v) & 0xFFFF
            bits += 5
            if bits >= 8 {
                out[p] = UInt8((buffer >> (bits - 8)) & 0xFF)
                p += 1
                bits -= 8
            }
        }
        if bits > 0 && (buffer & ((1 << bits) - 1)) != 0 {
            return nil
        }
        return Data(out)
    }
}

/// QR_Payload 文本编解码：`LT1:` || Base32(QrStruct)。
public enum QrCodec: Sendable {
    public static let prefix = "LT1:"

    public static func encode(_ payload: QrPayload) -> String {
        precondition(isValidFields(payload), "invalid QR payload fields")
        var w = TlvWriter()
        w.bytes(1, payload.deviceId.bytes)
        w.string(2, payload.displayName)
        var ipBytes: [UInt8] = []
        ipBytes.reserveCapacity(payload.ipv4List.count * 4)
        for ip in payload.ipv4List {
            ipBytes.append(UInt8((ip >> 24) & 0xFF))
            ipBytes.append(UInt8((ip >> 16) & 0xFF))
            ipBytes.append(UInt8((ip >> 8) & 0xFF))
            ipBytes.append(UInt8(ip & 0xFF))
        }
        w.bytes(3, ipBytes)
        w.u16(4, payload.port)
        w.bytes(5, payload.fingerprint)
        w.bytes(6, payload.token)
        let text = prefix + Base32.encode(w.toData())
        precondition(text.utf8.count <= ProtocolConstants.maxQrText, "QR text exceeds 512 bytes")
        return text
    }

    public static func decode(_ text: String) -> QrDecodeResult {
        if text.utf8.count > ProtocolConstants.maxQrText {
            return .fail(.tooLong)
        }
        guard text.hasPrefix(prefix) else {
            return .fail(.badPrefix)
        }
        let encoded = String(text.dropFirst(prefix.count))
        guard let raw = Base32.decode(encoded) else {
            return .fail(.badBase32)
        }
        let payload: QrPayload
        do {
            let s = try TlvStruct(bytes: [UInt8](raw))
            let ipData = [UInt8](try s.data(3, "ipv4_list"))
            if ipData.count % 4 != 0 {
                return .fail(.invalidField)
            }
            var ips: [UInt32] = []
            ips.reserveCapacity(ipData.count / 4)
            var idx = 0
            while idx < ipData.count {
                let ip = (UInt32(ipData[idx]) << 24)
                    | (UInt32(ipData[idx + 1]) << 16)
                    | (UInt32(ipData[idx + 2]) << 8)
                    | UInt32(ipData[idx + 3])
                ips.append(ip)
                idx += 4
            }
            guard let deviceId = DeviceId(bytes: try s.fixedData(1, "device_id", size: 16)) else {
                return .fail(.malformed)
            }
            payload = QrPayload(
                deviceId: deviceId,
                displayName: try s.string(2, "display_name"),
                ipv4List: ips,
                port: try s.u16(4, "port"),
                fingerprint: try s.fixedData(5, "fingerprint", size: 32),
                token: try s.fixedData(6, "token", size: 16)
            )
        } catch {
            return .fail(.malformed)
        }
        return isValidFields(payload) ? .ok(payload) : .fail(.invalidField)
    }

    private static func isValidFields(_ p: QrPayload) -> Bool {
        guard AnnouncementCodec.isNameValid(p.displayName),
              p.ipv4List.count >= 1,
              p.ipv4List.count <= ProtocolConstants.maxQrIps,
              Set(p.ipv4List).count == p.ipv4List.count,
              p.port != 0,
              p.fingerprint.count == 32,
              p.token.count == 16 else {
            return false
        }
        return true
    }
}
