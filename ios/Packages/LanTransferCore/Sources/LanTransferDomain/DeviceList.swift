import Foundation
import LanTransferProtocol

public struct DeviceEntry: Hashable, Sendable {
    public let announcement: Announcement
    public let ip: Ipv4
    public let lastSeenMono: Int64
    public var trusted: Bool

    public var deviceId: DeviceId { announcement.deviceId }

    public init(announcement: Announcement, ip: Ipv4, lastSeenMono: Int64, trusted: Bool) {
        self.announcement = announcement
        self.ip = ip
        self.lastSeenMono = lastSeenMono
        self.trusted = trusted
    }
}

public struct DeviceListState: Hashable, Sendable {
    public let selfId: DeviceId
    public var interfaces: [LocalInterface]
    public var trusted: Set<DeviceId>
    public var entries: [DeviceEntry]

    public init(
        selfId: DeviceId,
        interfaces: [LocalInterface] = [],
        trusted: Set<DeviceId> = [],
        entries: [DeviceEntry] = []
    ) {
        self.selfId = selfId
        self.interfaces = interfaces
        self.trusted = trusted
        self.entries = entries
    }
}

public enum DeviceListEvent: Hashable, Sendable {
    case announced(Announcement, src: Ipv4, via: LocalInterface?, nowMono: Int64)
    case tick(nowMono: Int64)
    case trustChanged(Set<DeviceId>)
    case interfacesChanged([LocalInterface])
}

/// 设备列表归约（需求 5.2–5.5、5.8）。
public enum DeviceListReducer: Sendable {
    public static let expiryMs: Int64 = 10_000

    public static func reduce(_ state: DeviceListState, _ event: DeviceListEvent) -> DeviceListState {
        var s = state
        switch event {
        case .announced(let a, let src, let via, let nowMono):
            guard a.deviceId != s.selfId,
                  AddressRules.acceptsInbound(src: src, via: via, interfaces: s.interfaces) else {
                return s
            }
            let entry = DeviceEntry(announcement: a, ip: src, lastSeenMono: nowMono, trusted: s.trusted.contains(a.deviceId))
            if let idx = s.entries.firstIndex(where: { $0.deviceId == a.deviceId }) {
                s.entries[idx] = entry
            } else {
                s.entries.append(entry)
            }
            return s

        case .tick(let nowMono):
            let cutoff = nowMono - expiryMs
            s.entries.removeAll(where: { $0.lastSeenMono < cutoff })
            return s

        case .trustChanged(let trusted):
            s.trusted = trusted
            for i in 0..<s.entries.count {
                s.entries[i].trusted = trusted.contains(s.entries[i].deviceId)
            }
            return s

        case .interfacesChanged(let interfaces):
            s.interfaces = interfaces
            return s
        }
    }

    public static func onDatagram(
        _ state: DeviceListState,
        datagram: Data,
        src: Ipv4,
        via: LocalInterface?,
        nowMono: Int64
    ) -> DeviceListState {
        guard AddressRules.acceptsInbound(src: src, via: via, interfaces: state.interfaces),
              let announcement = AnnouncementCodec.decode(datagram) else {
            return state
        }
        return reduce(state, .announced(announcement, src: src, via: via, nowMono: nowMono))
    }
}
