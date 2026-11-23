import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol

/// 将领域/引擎状态与错误映射为统一简体中文文案（全部来源于 `UiText`）。
public enum UiTextMapper: Sendable {
    public static func textStatus(_ status: TextStatus) -> String {
        switch status {
        case .sending: return UiText.statusTextSending
        case .delivered: return UiText.statusTextDelivered
        case .failed: return UiText.statusTextFailed
        case .received: return UiText.statusTextReceived
        }
    }

    public static func taskState(_ state: TaskState, direction: Direction, interrupt: InterruptInfo? = nil) -> String {
        switch state {
        case .offering: return UiText.statusTaskOffering
        case .awaitingUser: return UiText.statusTaskAwaitingUser
        case .running: return UiText.statusTaskRunning
        case .interrupted:
            if let interrupt {
                return "\(UiText.statusTaskInterrupted)（\(interruptReason(interrupt))）"
            }
            return UiText.statusTaskInterrupted
        case .resuming: return UiText.statusTaskResuming
        case .completed: return UiText.statusTaskCompleted
        case .rejected: return direction == .send ? UiText.statusTaskRejectedSender : UiText.statusTaskRejectedReceiver
        case .noResponse: return direction == .send ? UiText.statusTaskNoResponseSender : UiText.statusTaskTimeoutReceiver
        case .noSpace: return UiText.statusTaskNoSpace
        case .cannotSave: return direction == .send ? UiText.statusTaskCannotSaveSender : UiText.statusTaskCannotSaveReceiver
        case .connectionLost: return UiText.statusTaskConnectionLost
        case .cancelled: return UiText.statusTaskCancelled
        case .peerCancelled: return UiText.statusTaskPeerCancelled
        case .offerInvalid: return UiText.statusTaskOfferInvalid
        }
    }

    public static func interruptReason(_ info: InterruptInfo) -> String {
        switch info.reason {
        case .peerUnresponsive: return UiText.statusInterruptPeerUnresponsive
        case .peerClosed: return UiText.statusInterruptPeerClosed
        case .localNetworkLost: return UiText.statusInterruptLocalNetworkLost
        case .integrityFailed: return UiText.statusInterruptIntegrityFailed
        case .protocolError: return UiText.statusInterruptProtocolError
        case .writeFailed: return UiText.statusInterruptWriteFailed(detail: info.detail ?? "未知错误")
        case .appExit: return UiText.statusInterruptAppExit
        case .appKilled: return UiText.statusInterruptAppKilled
        }
    }

    public static func fileState(_ state: FileState) -> String {
        switch state {
        case .waiting: return UiText.statusFileWaiting
        case .transferring: return UiText.statusFileTransferring
        case .verifying: return UiText.statusFileVerifying
        case .writeFailed: return UiText.statusFileWriteFailed
        case .completed: return UiText.statusFileCompleted
        case .readFailed: return UiText.statusFileReadFailed
        case .checksumFailed: return UiText.statusFileChecksumFailed
        }
    }

    public static func openFailure(_ failure: OpenFailure) -> String {
        switch failure {
        case .notLocal: return UiText.errorNotLocal
        case .selfConnection: return UiText.errorConnectSelf
        case .bindFailed(let vpn): return vpn ? UiText.errorBindFailedVpn : UiText.errorBindFailed
        case .unreachable, .timeout: return UiText.errorConnectFailed
        case .noVersionResponse: return UiText.errorNoVersionResponse
        case .versionIncompatible(let loc, let rem):
            return "\(UiText.errorVersionIncompatible)\n\(UiText.labelVersionDetail(local: loc.description, remote: rem.description))"
        case .handshakeAuthFailed: return UiText.securityAuthFailed
        case .keyMismatch(let peerName): return UiText.securityKeyMismatch(peer: peerName)
        case .fingerprintMismatch: return UiText.securityQrMismatch
        case .notTrusted: return UiText.errorPeerUnpaired
        case .protocolError: return UiText.statusInterruptProtocolError
        }
    }

    public static func pairingResult(_ done: PairingDone, peerName: String?, failure: OpenFailure?) -> String {
        switch done.result {
        case .success:
            if done.role == .qrScanner, let peerName {
                return UiText.toastPairSuccessPeer(peer: peerName)
            }
            return UiText.toastPairSuccess
        case .rejected, .autoRejected:
            return UiText.toastPairRejected
        case .busy:
            return UiText.toastPairBusy
        case .timeout:
            return UiText.toastPairTimeout
        case .cancelled:
            return UiText.statusTaskCancelled
        case .tokenInvalid:
            return UiText.errorQrTokenInvalid
        case .connectFailed:
            return failure.map(openFailure) ?? UiText.errorConnectFailed
        case .codeMismatch, .disconnected, .storeFailed:
            return UiText.toastPairFailed
        }
    }

    public static func formatRecordTime(_ time: RecordTime) -> String {
        let date = Date(timeIntervalSince1970: TimeInterval(time.utcMs) / 1000.0)
        let tz = TimeZone(secondsFromGMT: time.offsetMin * 60) ?? .current
        let local = LocalDateTime.from(date: date, timeZone: tz)
        return String(format: "%04d-%02d-%02d %02d:%02d", local.year, local.month, local.day, local.hour, local.minute)
    }

    public static func formatClockTime(_ time: RecordTime) -> String {
        let date = Date(timeIntervalSince1970: TimeInterval(time.utcMs) / 1000.0)
        let tz = TimeZone(secondsFromGMT: time.offsetMin * 60) ?? .current
        let local = LocalDateTime.from(date: date, timeZone: tz)
        return String(format: "%02d:%02d", local.hour, local.minute)
    }

    public static func formatEta(_ etaSec: Int64?) -> String {
        guard let sec = etaSec else { return UiText.labelEtaUnknown }
        if sec < 60 { return "\(sec) 秒" }
        let mins = sec / 60
        let rem = sec % 60
        if mins < 60 { return "\(mins) 分 \(rem) 秒" }
        return "\(mins / 60) 小时 \(mins % 60) 分"
    }

    public static func formatGroupedSas(_ sas: String) -> String {
        guard sas.count == 6 else { return sas }
        let idx = sas.index(sas.startIndex, offsetBy: 3)
        return "\(sas[..<idx]) \(sas[idx...])"
    }
}
