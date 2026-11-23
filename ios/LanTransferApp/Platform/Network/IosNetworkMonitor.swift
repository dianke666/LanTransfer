import Darwin
import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import Network

/// iOS 本地网络接口监视器：`NWPathMonitor` 事件触发 + `getifaddrs()` 枚举，严格剔除蜂窝（`pdp_ip*`）、VPN（`utun*`）及 Apple 内部虚拟网卡（`awdl0`/`llw0`）。
public actor IosNetworkMonitor: NetworkMonitor {
    public nonisolated let interfacesStream: AsyncStream<[LocalInterface]>

    private let continuation: AsyncStream<[LocalInterface]>.Continuation
    private let pathMonitor = NWPathMonitor()
    private let monitorQueue = DispatchQueue(label: "com.lantransfer.netmonitor", qos: .utility)
    private var current: [LocalInterface] = []
    private var fastPolling: Bool = false
    private var pollTask: Task<Void, Never>?

    public init() {
        let (stream, cont) = AsyncStream<[LocalInterface]>.makeStream()
        self.interfacesStream = stream
        self.continuation = cont
        self.current = Self.enumerateInterfaces()
    }

    public func start() {
        refreshNow()
        pathMonitor.pathUpdateHandler = { [weak self] _ in
            guard let monitor = self else { return }
            Task { await monitor.refreshNow() }
        }
        pathMonitor.start(queue: monitorQueue)

        pollTask?.cancel()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                let fast = await self?.fastPolling ?? false
                let ms: UInt64 = fast ? 1_000 : 2_000
                do {
                    try await Task.sleep(nanoseconds: ms * 1_000_000)
                } catch {
                    break
                }
                await self?.refreshNow()
            }
        }
    }

    public func stop() {
        pollTask?.cancel()
        pollTask = nil
        pathMonitor.cancel()
    }

    public func currentInterfaces() -> [LocalInterface] {
        current
    }

    public func setFastPolling(_ enabled: Bool) {
        fastPolling = enabled
    }

    private func refreshNow() {
        let next = Self.enumerateInterfaces()
        if next != current {
            current = next
            continuation.yield(next)
        }
    }

    /// 枚举当前所有满足本地局域网约束的私有 IPv4 接口并按优先级排序。
    public static func enumerateInterfaces() -> [LocalInterface] {
        var ifaddrPtr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddrPtr) == 0, let firstAddr = ifaddrPtr else {
            return []
        }
        defer { freeifaddrs(ifaddrPtr) }

        var results: [LocalInterface] = []
        var seen = Set<String>()
        var cursor: UnsafeMutablePointer<ifaddrs>? = firstAddr

        while let ptr = cursor {
            defer { cursor = ptr.pointee.ifa_next }
            let flags = Int32(ptr.pointee.ifa_flags)
            guard (flags & IFF_UP) != 0,
                  (flags & IFF_LOOPBACK) == 0,
                  (flags & IFF_POINTOPOINT) == 0,
                  let addrPtr = ptr.pointee.ifa_addr,
                  addrPtr.pointee.sa_family == UInt8(AF_INET) else {
                continue
            }

            let name = String(cString: ptr.pointee.ifa_name)
            guard isAllowedInterfaceName(name) else { continue }

            let sin = addrPtr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
            let ipBits = UInt32(bigEndian: sin.sin_addr.s_addr)
            let ip = Ipv4(rawValue: ipBits)
            guard AddressRules.isPrivate(ip) else { continue }

            var prefixLen = 24
            if let maskPtr = ptr.pointee.ifa_netmask, maskPtr.pointee.sa_family == UInt8(AF_INET) {
                let maskSin = maskPtr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
                let maskBits = UInt32(bigEndian: maskSin.sin_addr.s_addr)
                prefixLen = maskBits.nonzeroBitCount
            }

            let dedupKey = "\(name)-\(ipBits)"
            guard seen.insert(dedupKey).inserted else { continue }

            let kind = classifyKind(name)
            results.append(LocalInterface(name: name, kind: kind, address: ip, prefixLength: prefixLen))
        }

        results.sort { a, b in
            if a.kind.rawValue != b.kind.rawValue { return a.kind.rawValue < b.kind.rawValue }
            if a.prefixLength != b.prefixLength { return a.prefixLength > b.prefixLength }
            return a.name < b.name
        }
        return results
    }

    private static func isAllowedInterfaceName(_ name: String) -> Bool {
        let excludedPrefixes = ["pdp_ip", "utun", "ipsec", "ppp", "awdl", "llw", "anpi", "lo", "ap"]
        return !excludedPrefixes.contains(where: { name.hasPrefix($0) })
    }

    private static func classifyKind(_ name: String) -> IfaceKind {
        if name == "en0" { return .wifi }
        if name.hasPrefix("bridge") { return .hotspot }
        if name.hasPrefix("en") { return .ethernet }
        return .other
    }
}
