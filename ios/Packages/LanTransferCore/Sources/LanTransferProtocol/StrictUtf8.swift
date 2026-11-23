import Foundation

/// 严格 UTF-8 编解码（规范 2.1、2.4），按 Unicode 标准表 3-7 实现，拒绝截断、非最短形式、代理项及 > U+10FFFF。
public enum StrictUtf8: Sendable {
    /// 编码 Swift 字符串为标准 UTF-8（Swift String 仅含合法 Unicode 标量值）。
    public static func encode(_ string: String) -> Data {
        Data(string.utf8)
    }

    public static func encodedLength(_ string: String) -> Int {
        string.utf8.count
    }

    /// 严格校验字节序列是否为合法标准 UTF-8。
    public static func isValid(_ data: Data) -> Bool {
        decode(data) != nil
    }

    /// 严格解码；不合法时返回 nil。
    public static func decode(_ data: Data) -> String? {
        decode([UInt8](data))
    }

    public static func decode(_ bytes: [UInt8]) -> String? {
        var scalars = String.UnicodeScalarView()
        scalars.reserveCapacity(bytes.count)

        var i = 0
        let count = bytes.count
        while i < count {
            let b0 = bytes[i]
            if b0 < 0x80 {
                scalars.append(UnicodeScalar(b0))
                i += 1
                continue
            }

            let need: Int
            let lo: UInt8
            let hi: UInt8
            switch b0 {
            case 0xC2...0xDF:
                need = 1; lo = 0x80; hi = 0xBF
            case 0xE0:
                need = 2; lo = 0xA0; hi = 0xBF
            case 0xE1...0xEC, 0xEE, 0xEF:
                need = 2; lo = 0x80; hi = 0xBF
            case 0xED:
                need = 2; lo = 0x80; hi = 0x9F
            case 0xF0:
                need = 3; lo = 0x90; hi = 0xBF
            case 0xF1...0xF3:
                need = 3; lo = 0x80; hi = 0xBF
            case 0xF4:
                need = 3; lo = 0x80; hi = 0x8F
            default:
                return nil
            }

            if count - i - 1 < need {
                return nil
            }

            let b1 = bytes[i + 1]
            if b1 < lo || b1 > hi {
                return nil
            }

            var cp: UInt32 = (UInt32(b0 & UInt8(0x3F >> need)) << 6) | UInt32(b1 & 0x3F)
            if need >= 2 {
                for k in 2...need {
                    let bk = bytes[i + k]
                    if (bk & 0xC0) != 0x80 {
                        return nil
                    }
                    cp = (cp << 6) | UInt32(bk & 0x3F)
                }
            }

            guard let scalar = UnicodeScalar(cp) else {
                return nil
            }
            scalars.append(scalar)
            i += need + 1
        }

        return String(scalars)
    }
}
