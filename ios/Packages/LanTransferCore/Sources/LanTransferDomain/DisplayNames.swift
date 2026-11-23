import Foundation
import LanTransferProtocol

public enum NameCheck: Hashable, Sendable {
    case ok(cleaned: String, removedAny: Bool)
    case empty
    case tooLong
    case hasControl
}

/// 设备显示名称规则（需求 4，设计 C3.2）。
public enum DisplayNameRules: Sendable {
    public static let defaultAndroidName = "Android 设备"
    public static let defaultWindowsName = "Windows 电脑"
    public static let defaultIosName = "iOS 设备"

    /// 显式列出 Unicode White_Space 属性码点，保证跨端一致。
    public static func isWhiteSpace(_ scalar: UnicodeScalar) -> Bool {
        let v = scalar.value
        return (v >= 0x0009 && v <= 0x000D)
            || v == 0x0020
            || v == 0x0085
            || v == 0x00A0
            || v == 0x1680
            || (v >= 0x2000 && v <= 0x200A)
            || v == 0x2028
            || v == 0x2029
            || v == 0x202F
            || v == 0x205F
            || v == 0x3000
    }

    public static func trimWhiteSpace(_ s: String) -> String {
        let scalars = Array(s.unicodeScalars)
        var start = 0
        var end = scalars.count
        while start < end && isWhiteSpace(scalars[start]) {
            start += 1
        }
        while end > start && isWhiteSpace(scalars[end - 1]) {
            end -= 1
        }
        return String(String.UnicodeScalarView(scalars[start..<end]))
    }

    public static func cleaned(_ s: String) -> String {
        trimWhiteSpace(s)
    }

    private static func isControl(_ scalar: UnicodeScalar) -> Bool {
        let v = scalar.value
        return v <= 0x001F || (v >= 0x007F && v <= 0x009F)
    }

    public static func defaultName(modelOrComputerName: String?, type: DeviceType) -> String {
        let c = cleaned(modelOrComputerName ?? "")
        let head = String(String.UnicodeScalarView(c.unicodeScalars.prefix(ProtocolConstants.maxNameCodePoints)))
        if !head.isEmpty {
            return head
        }
        switch type {
        case .android: return defaultAndroidName
        case .windows: return defaultWindowsName
        case .ios: return defaultIosName
        }
    }

    public static func validateUserInput(_ input: String) -> NameCheck {
        let c = cleaned(input)
        if c.isEmpty {
            return .empty
        }
        if c.unicodeScalars.count > ProtocolConstants.maxNameCodePoints {
            return .tooLong
        }
        if c.unicodeScalars.contains(where: isControl) {
            return .hasControl
        }
        return .ok(cleaned: c, removedAny: false)
    }

    public static func isAcceptableRemote(_ name: String) -> Bool {
        let n = name.unicodeScalars.count
        return n >= ProtocolConstants.minNameCodePoints && n <= ProtocolConstants.maxNameCodePoints
    }
}
