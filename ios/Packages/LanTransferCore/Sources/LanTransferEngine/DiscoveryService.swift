import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 局域网设备自动发现服务（UDP 42581 + 组播 239.255.42.58 + 定向广播，支持个人证书无组播权限时优雅降级）。
public actor DiscoveryService {
    public static let announceIntervalMs: Int64 = 2_000
    public static let tickIntervalMs: Int64 = 1_000

    private let transport: any Transport
    private let networkMonitor: any NetworkMonitor
    private let clock: any ClockPort
    private let deviceType: DeviceType

    private var selfId: DeviceId?
    private var displayName: String = DisplayNameRules.defaultIosName
    private var listenPort: UInt16?
    private var state: DeviceListState?
    private var status: DiscoveryStatus = .stopped
    private var udp: (any UdpEndpoint)?
    private var receiveTask: Task<Void, Never>?
    private var announceTask: Task<Void, Never>?
    private var tickTask: Task<Void, Never>?
    private var onChange: (@Sendable ([DeviceEntry], DiscoveryStatus) async -> Void)?

    public init(
        transport: any Transport,
        networkMonitor: any NetworkMonitor,
        clock: any ClockPort,
        deviceType: DeviceType = .ios
    ) {
        self.transport = transport
        self.networkMonitor = networkMonitor
        self.clock = clock
        self.deviceType = deviceType
    }

    public func configure(
        selfId: DeviceId,
        displayName: String,
        trustedIds: Set<DeviceId>,
        onChange: @escaping @Sendable ([DeviceEntry], DiscoveryStatus) async -> Void
    ) async {
        self.selfId = selfId
        self.displayName = displayName
        self.onChange = onChange
        let interfaces = await networkMonitor.currentInterfaces()
        self.state = DeviceListState(selfId: selfId, interfaces: interfaces, trusted: trustedIds)
    }

    public func updateDisplayName(_ name: String) {
        self.displayName = name
    }

    public func updateTrusted(_ ids: Set<DeviceId>) async {
        guard let cur = state else { return }
        let next = DeviceListReducer.reduce(cur, .trustChanged(ids))
        state = next
        await notify()
    }

    public func updateInterfaces(_ interfaces: [LocalInterface]) async {
        guard let cur = state else { return }
        let next = DeviceListReducer.reduce(cur, .interfacesChanged(interfaces))
        state = next
        await notify()
    }

    public func currentDevices() -> [DeviceEntry] {
        state?.entries ?? []
    }

    public func currentStatus() -> DiscoveryStatus {
        status
    }

    public func addressOf(_ deviceId: DeviceId) -> (Ipv4, Int)? {
        guard let entry = state?.entries.first(where: { $0.deviceId == deviceId }) else { return nil }
        return (entry.ip, Int(entry.announcement.port))
    }

    public func start(listenPort: Int) async {
        self.listenPort = UInt16(exactly: listenPort)
        await stopLoopsOnly()

        let interfaces = await networkMonitor.currentInterfaces()
        await updateInterfaces(interfaces)
        guard !interfaces.isEmpty else {
            status = .stopped
            await notify()
            return
        }

        let multicastIp = Ipv4(parsing: ProtocolConstants.multicastGroup)!
        let opened = await transport.openUdp(
            port: ProtocolConstants.discoveryPort,
            multicastGroup: multicastIp,
            interfaces: interfaces
        )
        switch opened {
        case .failed(let reason):
            status = .unavailable(reason: reason)
            await notify()
            return
        case .ok(let endpoint):
            self.udp = endpoint
            self.status = .running
            await notify()

            receiveTask = Task { [weak self] in
                while !Task.isCancelled {
                    guard let datagram = await endpoint.receive() else { break }
                    await self?.handleDatagram(datagram)
                }
            }

            announceTask = Task { [weak self] in
                while !Task.isCancelled {
                    await self?.broadcastOnce()
                    do {
                        try await self?.clock.sleep(milliseconds: Self.announceIntervalMs)
                    } catch {
                        break
                    }
                }
            }

            tickTask = Task { [weak self] in
                while !Task.isCancelled {
                    do {
                        try await self?.clock.sleep(milliseconds: Self.tickIntervalMs)
                    } catch {
                        break
                    }
                    await self?.handleTick()
                }
            }
        }
    }

    public func stop() async {
        await stopLoopsOnly()
        status = .stopped
        await notify()
    }

    private func stopLoopsOnly() async {
        receiveTask?.cancel()
        announceTask?.cancel()
        tickTask?.cancel()
        receiveTask = nil
        announceTask = nil
        tickTask = nil
        await udp?.close()
        udp = nil
    }

    private func broadcastOnce() async {
        guard let udp, let selfId, let port = listenPort, port != 0 else { return }
        let announcement = Announcement(
            deviceId: selfId,
            displayName: displayName,
            deviceType: deviceType,
            version: .current,
            port: port
        )
        let payload = AnnouncementCodec.encode(announcement)
        let multicastIp = Ipv4(parsing: ProtocolConstants.multicastGroup)!
        let interfaces = await networkMonitor.currentInterfaces()
        for iface in interfaces {
            _ = await udp.send(via: iface, dst: multicastIp, port: ProtocolConstants.discoveryPort, data: payload)
            _ = await udp.send(via: iface, dst: iface.broadcast, port: ProtocolConstants.discoveryPort, data: payload)
        }
    }

    private func handleDatagram(_ datagram: Datagram) async {
        guard let cur = state else { return }
        let next = DeviceListReducer.onDatagram(
            cur,
            datagram: datagram.data,
            src: datagram.src,
            via: datagram.via,
            nowMono: clock.monotonicMs()
        )
        if next != cur {
            state = next
            await notify()
        }
    }

    private func handleTick() async {
        guard let cur = state else { return }
        let next = DeviceListReducer.reduce(cur, .tick(nowMono: clock.monotonicMs()))
        if next != cur {
            state = next
            await notify()
        }
    }

    private func notify() async {
        await onChange?(state?.entries ?? [], status)
    }
}
