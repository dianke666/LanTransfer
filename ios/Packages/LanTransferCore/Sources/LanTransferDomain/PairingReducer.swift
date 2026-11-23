import Foundation
import LanTransferProtocol

/// 引擎层分配的连接编号，用于归约器区分“当前配对连接”。
public struct ConnId: Hashable, Sendable {
    public let value: Int64
    public init(_ value: Int64) { self.value = value }
}

/// 握手后得到的对端信息。
public struct PeerInfo: Hashable, Sendable {
    public let deviceId: DeviceId
    public let displayName: String
    public let deviceType: DeviceType
    public let publicKey: Data
    public let ip: Ipv4?
    public let port: Int?

    public init(
        deviceId: DeviceId,
        displayName: String,
        deviceType: DeviceType,
        publicKey: Data,
        ip: Ipv4? = nil,
        port: Int? = nil
    ) {
        self.deviceId = deviceId
        self.displayName = displayName
        self.deviceType = deviceType
        self.publicKey = publicKey
        self.ip = ip
        self.port = port
    }
}

public enum PairingRole: Hashable, Sendable {
    case initiator
    case receiver
    case qrScanner
    case qrDisplay
}

public enum PairingSession: Hashable, Sendable {
    case connecting(peer: DeviceId, qrToken: Data?, deadline: Int64)
    case awaitingPeer(peer: DeviceId, conn: ConnId, info: PeerInfo, localPublicKey: Data, nI: Data, deadline: Int64)
    case showingSas(peer: DeviceId, conn: ConnId, info: PeerInfo, sas: String, deadline: Int64)
    case awaitingUser(peer: DeviceId, conn: ConnId, info: PeerInfo, localPublicKey: Data, commitment: Data, nR: Data, sas: String?, deadline: Int64)
    case awaitingQrResult(peer: DeviceId, conn: ConnId, info: PeerInfo, deadline: Int64)
    case awaitingComplete(peer: DeviceId, conn: ConnId, info: PeerInfo, role: PairingRole, lease: TokenLease?, deadline: Int64)
    case saving(peer: DeviceId, conn: ConnId, info: PeerInfo, role: PairingRole, lease: TokenLease?)

    public var peer: DeviceId {
        switch self {
        case .connecting(let peer, _, _),
             .awaitingPeer(let peer, _, _, _, _, _),
             .showingSas(let peer, _, _, _, _),
             .awaitingUser(let peer, _, _, _, _, _, _, _),
             .awaitingQrResult(let peer, _, _, _),
             .awaitingComplete(let peer, _, _, _, _, _),
             .saving(let peer, _, _, _, _):
            return peer
        }
    }

    public var role: PairingRole {
        switch self {
        case .connecting(_, let token, _): return token == nil ? .initiator : .qrScanner
        case .awaitingPeer, .showingSas: return .initiator
        case .awaitingUser: return .receiver
        case .awaitingQrResult: return .qrScanner
        case .awaitingComplete(_, _, _, let role, _, _),
             .saving(_, _, _, let role, _): return role
        }
    }

    public var conn: ConnId? {
        switch self {
        case .connecting: return nil
        case .awaitingPeer(_, let c, _, _, _, _),
             .showingSas(_, let c, _, _, _),
             .awaitingUser(_, let c, _, _, _, _, _, _),
             .awaitingQrResult(_, let c, _, _),
             .awaitingComplete(_, let c, _, _, _, _),
             .saving(_, let c, _, _, _):
            return c
        }
    }

    public var deadline: Int64? {
        switch self {
        case .connecting(_, _, let d),
             .awaitingPeer(_, _, _, _, _, let d),
             .showingSas(_, _, _, _, let d),
             .awaitingUser(_, _, _, _, _, _, _, let d),
             .awaitingQrResult(_, _, _, let d),
             .awaitingComplete(_, _, _, _, _, let d):
            return d
        case .saving:
            return nil
        }
    }
}

public struct PairingDone: Hashable, Sendable {
    public let peer: DeviceId
    public let role: PairingRole
    public let result: PairingResult

    public init(peer: DeviceId, role: PairingRole, result: PairingResult) {
        self.peer = peer
        self.role = role
        self.result = result
    }
}

public struct PairingState: Hashable, Sendable {
    public var session: PairingSession?
    public var rejectedAt: [DeviceId: Int64]
    public var last: PairingDone?

    public init(session: PairingSession? = nil, rejectedAt: [DeviceId: Int64] = [:], last: PairingDone? = nil) {
        self.session = session
        self.rejectedAt = rejectedAt
        self.last = last
    }
}

public enum PairingEvent: Hashable, Sendable {
    case startPair(peer: DeviceId, now: Int64)
    case startQrPair(peer: DeviceId, token: Data, now: Int64)
    case connected(conn: ConnId, info: PeerInfo, localPublicKey: Data, nI: Data, now: Int64)
    case connectFailed(now: Int64)
    case pairRequestReceived(conn: ConnId, info: PeerInfo, localPublicKey: Data, commitment: Data, nR: Data, now: Int64)
    case qrRequestChecked(conn: ConnId, info: PeerInfo, lease: TokenLease?, now: Int64)
    case messageReceived(conn: ConnId, msg: ProtocolMessage, now: Int64)
    case userDecision(accept: Bool, now: Int64)
    case userCancel(now: Int64)
    case trustStored(peer: DeviceId, ok: Bool)
    case disconnected(conn: ConnId, now: Int64)
    case tick(now: Int64)
}

public enum PairingEffect: Hashable, Sendable {
    case send(conn: ConnId, msg: ProtocolMessage)
    case close(conn: ConnId)
    case cancelConnect
    case armTimer(atMono: Int64)
    case showRequest(PeerInfo)
    case showSas(String)
    case dismissRequest
    case saveTrust(PeerInfo)
    case stageTrust(PeerInfo)
    case commitTrust(DeviceId)
    case discardStagedTrust(DeviceId)
    case deleteTrust(DeviceId)
    case markTokenUsed(TokenLease)
    case releaseToken(TokenLease)
    case promoteSession(ConnId)
    case autoRejected(peer: DeviceId, result: PairingResult)
    case finished(PairingDone)
}

/// 配对纯状态机归约器（M3.4，需求 7、8），与 Android/Windows 逐分支一致。
public enum PairingReducer: Sendable {
    public static let connectTimeoutMs: Int64 = 10_000
    public static let qrConnectTimeoutMs: Int64 = 4 * 5_000
    public static let requestTimeoutMs: Int64 = 60_000
    public static let cooldownMs: Int64 = 60_000
    public static let completeTimeoutMs: Int64 = 15_000

    public static func reduce(_ state: PairingState, _ event: PairingEvent) -> Step<PairingState, PairingEffect> {
        switch event {
        case .startPair(let peer, let now):
            return start(state, .connecting(peer: peer, qrToken: nil, deadline: now + connectTimeoutMs))
        case .startQrPair(let peer, let token, let now):
            return start(state, .connecting(peer: peer, qrToken: token, deadline: now + qrConnectTimeoutMs))
        case .connected(let conn, let info, let localPk, let nI, let now):
            return onConnected(state, conn: conn, info: info, localPublicKey: localPk, nI: nI, now: now)
        case .connectFailed:
            guard case .connecting = state.session, let session = state.session else {
                return Step(state: state)
            }
            return done(state, session: session, result: .connectFailed)
        case .pairRequestReceived(let conn, let info, let localPk, let commitment, let nR, let now):
            return onPairRequest(state, conn: conn, info: info, localPublicKey: localPk, commitment: commitment, nR: nR, now: now)
        case .qrRequestChecked(let conn, let info, let lease, let now):
            return onQrRequest(state, conn: conn, info: info, lease: lease, now: now)
        case .messageReceived(let conn, let msg, _):
            return onMessage(state, conn: conn, msg: msg)
        case .userDecision(let accept, let now):
            return onDecision(state, accept: accept, now: now)
        case .userCancel:
            return onCancel(state)
        case .trustStored(let peer, let ok):
            return onTrustStored(state, peer: peer, ok: ok)
        case .disconnected(let conn, _):
            return onDisconnected(state, conn: conn)
        case .tick(let now):
            return onTick(state, now: now)
        }
    }

    private static func start(_ state: PairingState, _ connecting: PairingSession) -> Step<PairingState, PairingEffect> {
        guard state.session == nil, let deadline = connecting.deadline else {
            return Step(state: state)
        }
        var next = state
        next.session = connecting
        return Step(state: next, effects: [.armTimer(atMono: deadline)])
    }

    private static func done(
        _ state: PairingState,
        session: PairingSession,
        result: PairingResult,
        effects: [PairingEffect] = []
    ) -> Step<PairingState, PairingEffect> {
        let finished = PairingDone(peer: session.peer, role: session.role, result: result)
        var next = state
        next.session = nil
        next.last = finished
        return Step(state: next, effects: effects + [.finished(finished)])
    }

    private static func onConnected(
        _ state: PairingState,
        conn: ConnId,
        info: PeerInfo,
        localPublicKey: Data,
        nI: Data,
        now: Int64
    ) -> Step<PairingState, PairingEffect> {
        guard case .connecting(_, let qrToken, _) = state.session else {
            return Step(state: state, effects: [.close(conn: conn)])
        }
        var next = state
        if let token = qrToken {
            let deadline = now + completeTimeoutMs
            next.session = .awaitingQrResult(peer: info.deviceId, conn: conn, info: info, deadline: deadline)
            return Step(state: next, effects: [
                .send(conn: conn, msg: .qrPairRequest(token: token)),
                .armTimer(atMono: deadline),
            ])
        }
        let commitment = VerificationCode.commitment(pkI: localPublicKey, pkR: info.publicKey, nI: nI)
        let deadline = now + requestTimeoutMs
        next.session = .awaitingPeer(peer: info.deviceId, conn: conn, info: info, localPublicKey: localPublicKey, nI: nI, deadline: deadline)
        return Step(state: next, effects: [
            .send(conn: conn, msg: .pairRequest(commitment: commitment)),
            .armTimer(atMono: deadline),
        ])
    }

    private static func onPairRequest(
        _ state: PairingState,
        conn: ConnId,
        info: PeerInfo,
        localPublicKey: Data,
        commitment: Data,
        nR: Data,
        now: Int64
    ) -> Step<PairingState, PairingEffect> {
        let peer = info.deviceId
        if let rejected = state.rejectedAt[peer], now - rejected < cooldownMs {
            return Step(state: state, effects: autoReject(conn: conn, peer: peer, code: .rejected, result: .autoRejected))
        }
        if state.session != nil {
            return Step(state: state, effects: autoReject(conn: conn, peer: peer, code: .busy, result: .busy))
        }
        var next = state
        let deadline = now + requestTimeoutMs
        next.rejectedAt.removeValue(forKey: peer)
        next.session = .awaitingUser(
            peer: peer,
            conn: conn,
            info: info,
            localPublicKey: localPublicKey,
            commitment: commitment,
            nR: nR,
            sas: nil,
            deadline: deadline
        )
        return Step(state: next, effects: [
            .send(conn: conn, msg: .pairNonce(nonce: nR)),
            .showRequest(info),
            .armTimer(atMono: deadline),
        ])
    }

    private static func autoReject(conn: ConnId, peer: DeviceId, code: PairDecisionCode, result: PairingResult) -> [PairingEffect] {
        [
            .send(conn: conn, msg: .pairDecision(decision: code)),
            .close(conn: conn),
            .autoRejected(peer: peer, result: result),
        ]
    }

    private static func onQrRequest(
        _ state: PairingState,
        conn: ConnId,
        info: PeerInfo,
        lease: TokenLease?,
        now: Int64
    ) -> Step<PairingState, PairingEffect> {
        guard let lease, state.session == nil else {
            var effects: [PairingEffect] = []
            if let lease { effects.append(.releaseToken(lease)) }
            effects.append(.send(conn: conn, msg: .qrPairResult(result: .tokenInvalid)))
            effects.append(.close(conn: conn))
            return Step(state: state, effects: effects)
        }
        var next = state
        let deadline = now + completeTimeoutMs
        next.session = .awaitingComplete(peer: info.deviceId, conn: conn, info: info, role: .qrDisplay, lease: lease, deadline: deadline)
        return Step(state: next, effects: [
            .send(conn: conn, msg: .qrPairResult(result: .ok)),
            .stageTrust(info),
            .armTimer(atMono: deadline),
        ])
    }

    private static func onMessage(_ state: PairingState, conn: ConnId, msg: ProtocolMessage) -> Step<PairingState, PairingEffect> {
        guard let session = state.session, session.conn == conn else {
            return Step(state: state)
        }
        switch (session, msg) {
        case (.awaitingPeer(let peer, let c, let info, let localPk, let nI, let deadline), .pairNonce(let nR)):
            let sas = VerificationCode.sas(pkI: localPk, pkR: info.publicKey, nI: nI, nR: nR)
            var next = state
            next.session = .showingSas(peer: peer, conn: c, info: info, sas: sas, deadline: deadline)
            return Step(state: next, effects: [.send(conn: c, msg: .pairReveal(nonce: nI)), .showSas(sas)])

        case (.awaitingPeer(_, let c, _, _, _, _), .pairDecision(let decision)):
            return done(state, session: session, result: decisionResult(decision) ?? .disconnected, effects: [.close(conn: c)])

        case (.showingSas(let peer, let c, let info, _, _), .pairDecision(let decision)):
            if let r = decisionResult(decision) {
                return done(state, session: session, result: r, effects: [.close(conn: c)])
            }
            var next = state
            next.session = .saving(peer: peer, conn: c, info: info, role: .initiator, lease: nil)
            return Step(state: next, effects: [.saveTrust(info)])

        case (.awaitingUser(let peer, let c, let info, let localPk, let commitment, let nR, let existingSas, let deadline), .pairReveal(let nI)):
            guard existingSas == nil else { return Step(state: state) }
            guard VerificationCode.verifyCommitment(commitment, pkI: info.publicKey, pkR: localPk, nI: nI) else {
                return done(state, session: session, result: .codeMismatch, effects: [.dismissRequest, .close(conn: c)])
            }
            let sas = VerificationCode.sas(pkI: info.publicKey, pkR: localPk, nI: nI, nR: nR)
            var next = state
            next.session = .awaitingUser(
                peer: peer, conn: c, info: info, localPublicKey: localPk,
                commitment: commitment, nR: nR, sas: sas, deadline: deadline
            )
            return Step(state: next, effects: [.showSas(sas)])

        case (.awaitingUser(_, let c, _, _, _, _, _, _), .pairCancel(let reason)):
            return done(state, session: session, result: cancelResult(reason), effects: [.dismissRequest, .close(conn: c)])

        case (.awaitingQrResult(let peer, let c, let info, _), .qrPairResult(let result)):
            if result == .ok {
                var next = state
                next.session = .saving(peer: peer, conn: c, info: info, role: .qrScanner, lease: nil)
                return Step(state: next, effects: [.saveTrust(info)])
            }
            return done(state, session: session, result: .tokenInvalid, effects: [.close(conn: c)])

        case (.awaitingComplete(let peer, let c, let info, let role, let lease, _), .pairComplete):
            var next = state
            next.session = .saving(peer: peer, conn: c, info: info, role: role, lease: lease)
            return Step(state: next, effects: [.commitTrust(peer)])

        case (.awaitingComplete(let peer, let c, _, _, let lease, _), .pairCancel(let reason)):
            return done(state, session: session, result: cancelResult(reason), effects: abortStaged(peer: peer, lease: lease) + [.close(conn: c)])

        default:
            return Step(state: state)
        }
    }

    private static func decisionResult(_ code: PairDecisionCode) -> PairingResult? {
        switch code {
        case .accepted: return nil
        case .rejected: return .rejected
        case .busy: return .busy
        case .timeout: return .timeout
        }
    }

    private static func cancelResult(_ reason: PairCancelReason) -> PairingResult {
        reason == .timeout ? .timeout : .cancelled
    }

    private static func abortStaged(peer: DeviceId, lease: TokenLease?) -> [PairingEffect] {
        if let lease {
            return [.discardStagedTrust(peer), .releaseToken(lease)]
        }
        return [.discardStagedTrust(peer)]
    }

    private static func onDecision(_ state: PairingState, accept: Bool, now: Int64) -> Step<PairingState, PairingEffect> {
        guard let session = state.session,
              case .awaitingUser(let peer, let c, let info, _, _, _, let sas, _) = session,
              sas != nil else {
            return Step(state: state)
        }
        if accept {
            var next = state
            let deadline = now + completeTimeoutMs
            next.session = .awaitingComplete(peer: peer, conn: c, info: info, role: .receiver, lease: nil, deadline: deadline)
            return Step(state: next, effects: [
                .stageTrust(info),
                .send(conn: c, msg: .pairDecision(decision: .accepted)),
                .armTimer(atMono: deadline),
            ])
        }
        var step = done(
            state,
            session: session,
            result: .rejected,
            effects: [.send(conn: c, msg: .pairDecision(decision: .rejected)), .close(conn: c)]
        )
        var nextState = step.state
        nextState.rejectedAt[peer] = now
        step = Step(state: nextState, effects: step.effects)
        return step
    }

    private static func onCancel(_ state: PairingState) -> Step<PairingState, PairingEffect> {
        guard let session = state.session else { return Step(state: state) }
        switch session {
        case .connecting:
            return done(state, session: session, result: .cancelled, effects: [.cancelConnect])
        case .awaitingPeer(_, let c, _, _, _, _), .showingSas(_, let c, _, _, _):
            return done(state, session: session, result: .cancelled, effects: [.send(conn: c, msg: .pairCancel(reason: .user)), .close(conn: c)])
        case .awaitingQrResult(_, let c, _, _):
            return done(state, session: session, result: .cancelled, effects: [.close(conn: c)])
        default:
            return Step(state: state)
        }
    }

    private static func onTrustStored(_ state: PairingState, peer: DeviceId, ok: Bool) -> Step<PairingState, PairingEffect> {
        guard let session = state.session,
              case .saving(let sPeer, let c, _, let role, let lease) = session,
              sPeer == peer else {
            return Step(state: state)
        }
        if !ok {
            var cleanup: [PairingEffect] = []
            if role == .receiver || role == .qrDisplay {
                cleanup.append(.discardStagedTrust(sPeer))
            }
            if let lease {
                cleanup.append(.releaseToken(lease))
            }
            cleanup.append(.close(conn: c))
            return done(state, session: session, result: .storeFailed, effects: cleanup)
        }
        switch role {
        case .initiator, .qrScanner:
            return done(state, session: session, result: .success, effects: [.send(conn: c, msg: .pairComplete), .promoteSession(c)])
        case .receiver:
            return done(state, session: session, result: .success, effects: [.promoteSession(c)])
        case .qrDisplay:
            var effects: [PairingEffect] = []
            if let lease { effects.append(.markTokenUsed(lease)) }
            effects.append(.promoteSession(c))
            return done(state, session: session, result: .success, effects: effects)
        }
    }

    private static func onDisconnected(_ state: PairingState, conn: ConnId) -> Step<PairingState, PairingEffect> {
        guard let session = state.session, session.conn == conn else {
            return Step(state: state)
        }
        switch session {
        case .awaitingUser:
            return done(state, session: session, result: .disconnected, effects: [.dismissRequest])
        case .awaitingComplete(let peer, _, _, _, let lease, _):
            return done(state, session: session, result: .disconnected, effects: abortStaged(peer: peer, lease: lease))
        case .saving(let peer, _, _, let role, _) where role == .initiator || role == .qrScanner:
            return done(state, session: session, result: .disconnected, effects: [.deleteTrust(peer)])
        case .saving:
            return Step(state: state)
        default:
            return done(state, session: session, result: .disconnected)
        }
    }

    private static func onTick(_ state: PairingState, now: Int64) -> Step<PairingState, PairingEffect> {
        var next = state
        next.rejectedAt = state.rejectedAt.filter { now - $0.value < cooldownMs }
        guard let session = next.session, let deadline = session.deadline, now >= deadline else {
            return Step(state: next)
        }
        switch session {
        case .connecting:
            return done(next, session: session, result: .connectFailed, effects: [.cancelConnect])
        case .awaitingPeer(_, let c, _, _, _, _), .showingSas(_, let c, _, _, _):
            return done(next, session: session, result: .timeout, effects: [.send(conn: c, msg: .pairCancel(reason: .timeout)), .close(conn: c)])
        case .awaitingUser(_, let c, _, _, _, _, _, _):
            return done(next, session: session, result: .timeout, effects: [
                .send(conn: c, msg: .pairDecision(decision: .timeout)),
                .dismissRequest,
                .close(conn: c),
            ])
        case .awaitingQrResult(_, let c, _, _):
            return done(next, session: session, result: .timeout, effects: [.close(conn: c)])
        case .awaitingComplete(let peer, let c, _, _, let lease, _):
            return done(next, session: session, result: .disconnected, effects: abortStaged(peer: peer, lease: lease) + [.close(conn: c)])
        case .saving:
            return Step(state: next)
        }
    }
}
