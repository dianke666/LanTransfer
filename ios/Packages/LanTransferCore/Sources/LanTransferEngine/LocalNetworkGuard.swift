import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 本地网络守卫：唯一允许调用 `Transport.connect` 的入口，确保所有出站连接均通过 `AddressRules.classify` 校验，绝不访问外网或触发 DNS。
public actor LocalNetworkGuard {
    private let transport: any Transport
    private let networkMonitor: any NetworkMonitor

    public init(transport: any Transport, networkMonitor: any NetworkMonitor) {
        self.transport = transport
        self.networkMonitor = networkMonitor
    }

    public enum GuardConnectResult: Sendable {
        case ok(any TcpConn)
        case failed(OpenFailure)
    }

    public func connect(ip: Ipv4, port: Int, timeoutMs: Int64) async -> GuardConnectResult {
        let interfaces = await networkMonitor.currentInterfaces()
        switch AddressRules.classify(ip, interfaces: interfaces) {
        case .self:
            return .failed(.selfConnection)
        case .notLocal:
            return .failed(.notLocal)
        case .local(let iface):
            let res = await transport.connect(
                via: iface,
                ip: ip,
                port: port,
                timeoutSeconds: TimeInterval(max(timeoutMs, 100)) / 1000.0
            )
            switch res {
            case .ok(let conn):
                return .ok(conn)
            case .bindFailed(let vpn):
                return .failed(.bindFailed(vpnSuspected: vpn))
            case .refused, .unreachable:
                return .failed(.unreachable)
            case .timeout:
                return .failed(.timeout)
            }
        }
    }

    /// 按二维码候选顺序依次尝试所有本地子网地址。
    public func connectQr(
        ips: [Ipv4],
        port: Int,
        perAddressTimeoutMs: Int64
    ) async -> GuardConnectResult {
        let interfaces = await networkMonitor.currentInterfaces()
        var sawSelf = false
        var candidates: [(Ipv4, LocalInterface)] = []
        for ip in ips {
            switch AddressRules.classify(ip, interfaces: interfaces) {
            case .self:
                sawSelf = true
            case .local(let iface):
                candidates.append((ip, iface))
            case .notLocal:
                break
            }
        }
        if candidates.isEmpty {
            return .failed(sawSelf ? .selfConnection : .notLocal)
        }
        var lastFailure: OpenFailure = .unreachable
        for (ip, iface) in candidates {
            let res = await transport.connect(
                via: iface,
                ip: ip,
                port: port,
                timeoutSeconds: TimeInterval(max(perAddressTimeoutMs, 100)) / 1000.0
            )
            switch res {
            case .ok(let conn):
                return .ok(conn)
            case .bindFailed(let vpn):
                lastFailure = .bindFailed(vpnSuspected: vpn)
            case .refused, .unreachable:
                lastFailure = .unreachable
            case .timeout:
                lastFailure = .timeout
            }
        }
        return .failed(lastFailure)
    }
}
