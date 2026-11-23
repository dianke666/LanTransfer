import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 连接目标：已发现/已信任设备、手动 IPv4 地址、或扫码解析的 QR_Payload。
public enum ConnectTarget: Hashable, Sendable {
    case device(DeviceId)
    case address(ip: Ipv4, port: Int)
    case qr(QrPayload)
}

/// 握手后验证得到的对端身份。
public struct PeerIdentity: Hashable, Sendable {
    public let deviceId: DeviceId
    public let displayName: String
    public let deviceType: DeviceType
    public let publicKey: Data
    public let remoteAddr: SocketAddr

    public init(deviceId: DeviceId, displayName: String, deviceType: DeviceType, publicKey: Data, remoteAddr: SocketAddr) {
        self.deviceId = deviceId
        self.displayName = displayName
        self.deviceType = deviceType
        self.publicKey = publicKey
        self.remoteAddr = remoteAddr
    }

    public func toPeerInfo() -> PeerInfo {
        PeerInfo(
            deviceId: deviceId,
            displayName: displayName,
            deviceType: deviceType,
            publicKey: publicKey,
            ip: remoteAddr.ip,
            port: remoteAddr.port
        )
    }
}

/// 建立连接失败原因（驱动 UI 错误提示）。
public enum OpenFailure: Error, Hashable, Sendable {
    case notLocal
    case selfConnection
    case bindFailed(vpnSuspected: Bool)
    case unreachable
    case timeout
    case noVersionResponse
    case versionIncompatible(local: ProtocolVersion, remote: ProtocolVersion)
    case handshakeAuthFailed
    case keyMismatch(peerName: String)
    case fingerprintMismatch
    case notTrusted(peer: DeviceId)
    case protocolError
}

/// 连接关闭原因。
public enum CloseReason: Hashable, Sendable {
    case local(SessionCloseReason)
    case peerClosed(SessionCloseReason?)
    case peerUnresponsive
    case localNetworkLost
    case integrityFailed
    case protocolError
    case pendingViolation
    case superseded

    public func toInterruptReason() -> InterruptReason? {
        switch self {
        case .local(let r):
            switch r {
            case .normal, .appExit, .background: return .appExit
            case .duplicate, .unpaired: return nil
            }
        case .peerClosed(let r):
            if r == .duplicate { return nil }
            return .peerClosed
        case .superseded:
            return .peerClosed
        case .peerUnresponsive:
            return .peerUnresponsive
        case .localNetworkLost:
            return .localNetworkLost
        case .integrityFailed:
            return .integrityFailed
        case .protocolError, .pendingViolation:
            return .protocolError
        }
    }
}

public struct SessionTimings: Hashable, Sendable {
    public let heartbeatMs: Int64
    public let idleTimeoutMs: Int64
    public let connectBudgetMs: Int64
    public let qrPerAddressMs: Int64

    public static let `default` = SessionTimings(
        heartbeatMs: 5_000,
        idleTimeoutMs: 15_000,
        connectBudgetMs: 10_000,
        qrPerAddressMs: 5_000
    )

    public init(heartbeatMs: Int64, idleTimeoutMs: Int64, connectBudgetMs: Int64, qrPerAddressMs: Int64) {
        self.heartbeatMs = heartbeatMs
        self.idleTimeoutMs = idleTimeoutMs
        self.connectBudgetMs = connectBudgetMs
        self.qrPerAddressMs = qrPerAddressMs
    }
}

public enum InboundFailure: Hashable, Sendable {
    case versionIncompatible(local: ProtocolVersion, remote: ProtocolVersion)
    case handshakeAuthFailed
    case keyMismatch(peerName: String)
}

public enum DiscoveryStatus: Hashable, Sendable {
    case stopped
    case running
    case unavailable(reason: String)
}

public enum PairingUiState: Hashable, Sendable {
    case idle
    case connecting(peer: DeviceId)
    case waitingForPeer(peer: PeerInfo)
    case showingCode(peer: PeerInfo, sas: String)
    case incomingRequest(peer: PeerInfo, sas: String?)
    case completing(peer: PeerInfo)
}

public enum EngineNotice: Hashable, Sendable {
    case pairingFinished(PairingDone, OpenFailure?)
    case pairingAutoRejected(DeviceId, PairingResult)
    case inboundFailure(InboundFailure)
    case storeFailed(detail: String)
    case peerUnpaired(DeviceId)
}
