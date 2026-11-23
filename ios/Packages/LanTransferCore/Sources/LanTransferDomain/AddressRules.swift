import Foundation
import LanTransferProtocol

public enum Route: Hashable, Sendable {
    case `self`
    case local(LocalInterface)
    case notLocal
}

public enum PortParse: Hashable, Sendable {
    case `default`(Int)
    case ok(Int)
    case invalid
}

/// 局域网地址规则（需求 2、6、8，设计 C3.2），绝不调用 DNS。
public enum AddressRules: Sendable {
    public static func subnetMask(_ prefixLength: Int) -> UInt32 {
        precondition(prefixLength >= 0 && prefixLength <= 32)
        return prefixLength == 0 ? 0 : (UInt32.max << UInt32(32 - prefixLength))
    }

    public static func parseIpv4(_ input: String) -> Ipv4? {
        Ipv4(parsing: DisplayNameRules.trimWhiteSpace(input))
    }

    public static func parsePort(_ input: String) -> PortParse {
        let s = DisplayNameRules.trimWhiteSpace(input)
        if s.isEmpty {
            return .default(ProtocolConstants.defaultPort)
        }
        guard s.unicodeScalars.allSatisfy({ $0.value >= 0x30 && $0.value <= 0x39 }) else {
            return .invalid
        }
        let trimmed = s.drop(while: { $0 == "0" })
        if trimmed.count > 5 {
            return .invalid
        }
        let value = trimmed.isEmpty ? 0 : (Int(trimmed) ?? 0)
        return (1...65535).contains(value) ? .ok(value) : .invalid
    }

    /// 私有 IPv4：10/8、172.16/12、192.168/16、169.254/16。
    public static func isPrivate(_ ip: Ipv4) -> Bool {
        let b = ip.rawValue
        return (b >> 24) == 10
            || (b >> 20) == 0xAC1
            || (b >> 16) == 0xC0A8
            || (b >> 16) == 0xA9FE
    }

    public static func classify(_ target: Ipv4, interfaces: [LocalInterface]) -> Route {
        if interfaces.contains(where: { $0.address == target }) {
            return .self
        }
        guard isPrivate(target) else {
            return .notLocal
        }
        let candidates = interfaces.filter { $0.contains(target) }
        guard let best = candidates.min(by: { a, b in
            if a.prefixLength != b.prefixLength { return a.prefixLength > b.prefixLength }
            if a.kind.rawValue != b.kind.rawValue { return a.kind.rawValue < b.kind.rawValue }
            return a.name < b.name
        }) else {
            return .notLocal
        }
        return .local(best)
    }

    public static func acceptsInbound(src: Ipv4, via: LocalInterface?, interfaces: [LocalInterface]) -> Bool {
        guard isPrivate(src) else { return false }
        if let via {
            return via.contains(src)
        }
        return interfaces.contains(where: { $0.contains(src) })
    }

    public static func qrCandidates(ips: [Ipv4], interfaces: [LocalInterface]) -> [(ip: Ipv4, iface: LocalInterface)] {
        var result: [(Ipv4, LocalInterface)] = []
        for ip in ips {
            if case .local(let iface) = classify(ip, interfaces: interfaces) {
                result.append((ip, iface))
            }
        }
        return result
    }

    public static func qrAddressList(sortedInterfaces: [LocalInterface]) -> [Ipv4] {
        var seen = Set<Ipv4>()
        var result: [Ipv4] = []
        for iface in sortedInterfaces {
            if seen.insert(iface.address).inserted {
                result.append(iface.address)
                if result.count == ProtocolConstants.maxQrIps {
                    break
                }
            }
        }
        return result
    }
}
