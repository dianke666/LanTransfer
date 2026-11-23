import Foundation
import XCTest
@testable import LanTransferEngine
import LanTransferDomain
import LanTransferProtocol
import LanTransferTesting

/// 引擎层端到端集成测试：在内存管道上验证 QR 快速配对、加密无损文字传输与文件分片 SHA-256 校验落盘。
final class EngineFlowTests: XCTestCase {
    func testQrPairTextAndFileTransferEndToEnd() async throws {
        let hub = InMemoryNetworkHub()
        let ipA = Ipv4(192, 168, 1, 10)
        let ipB = Ipv4(192, 168, 1, 20)
        let ifaceA = LocalInterface(name: "en0", kind: .wifi, address: ipA, prefixLength: 24)
        let ifaceB = LocalInterface(name: "en0", kind: .wifi, address: ipB, prefixLength: 24)

        let idA = DeviceIdentity(deviceId: DeviceId(bytes: Data(repeating: 0xAA, count: 16))!, keyPair: .generate(), displayName: "iPhone-A")
        let idB = DeviceIdentity(deviceId: DeviceId(bytes: Data(repeating: 0xBB, count: 16))!, keyPair: .generate(), displayName: "Win-B")

        let storeA = InMemoryHistoryAndTaskStore()
        let storeB = InMemoryHistoryAndTaskStore()
        let trustA = InMemoryTrustStore()
        let trustB = InMemoryTrustStore()
        let recvA = InMemoryReceiveStorage()
        let recvB = InMemoryReceiveStorage()
        let srcA = InMemorySourceFiles()
        let srcB = InMemorySourceFiles()

        let hostA = EngineHost(
            networkMonitor: FakeNetworkMonitor(interfaces: [ifaceA]),
            transport: InMemoryTransport(hub: hub, primaryIp: ipA),
            receiveStorage: recvA,
            sourceFiles: srcA,
            identityStore: InMemoryIdentityStore(identity: idA),
            trustStore: trustA,
            historyStore: storeA,
            taskStore: storeA,
            settingsStore: InMemorySettingsStore(),
            clipboard: InMemoryClipboard(),
            notifier: InMemoryNotifier(),
            lifecycle: FakeAppLifecycle(),
            deviceType: .ios
        )
        let hostB = EngineHost(
            networkMonitor: FakeNetworkMonitor(interfaces: [ifaceB]),
            transport: InMemoryTransport(hub: hub, primaryIp: ipB),
            receiveStorage: recvB,
            sourceFiles: srcB,
            identityStore: InMemoryIdentityStore(identity: idB),
            trustStore: trustB,
            historyStore: storeB,
            taskStore: storeB,
            settingsStore: InMemorySettingsStore(),
            clipboard: InMemoryClipboard(),
            notifier: InMemoryNotifier(),
            lifecycle: FakeAppLifecycle(),
            deviceType: .windows
        )

        await hostA.start()
        await hostB.start()

        // 1. B 展示二维码，A 扫码配对
        let rawQr = await hostB.currentQrText()
        let qrText = try XCTUnwrap(rawQr)
        let qrErr = await hostA.pairingService.pairByQr(text: qrText)
        XCTAssertNil(qrErr)

        // 等待双方完成信任写入与会话升级
        try await Task.sleep(nanoseconds: 250_000_000)
        let trustedOnA = await trustA.get(idB.deviceId)
        let trustedOnB = await trustB.get(idA.deviceId)
        XCTAssertNotNil(trustedOnA)
        XCTAssertNotNil(trustedOnB)

        // 2. A 向 B 发送无损文字（含 CRLF、NUL、Emoji）
        let payloadText = "Hello\r\n世界\u{0000}🚀"
        let textRes = await hostA.textService.sendText(peer: idB.deviceId, text: payloadText)
        guard case .sent(let msgId) = textRes else {
            return XCTFail("Expected text sent")
        }
        try await Task.sleep(nanoseconds: 250_000_000)
        let receivedTextRecord = await storeB.findText(peer: idA.deviceId, id: msgId)
        XCTAssertNotNil(receivedTextRecord)
        XCTAssertEqual(receivedTextRecord.flatMap { StrictUtf8.decode($0.textUtf8) }, payloadText)

        // 3. B 开启自动接收，A 发送 150 KiB 二进制文件（跨多个 64 KiB 分片）
        await hostB.setAutoAccept(peer: idA.deviceId, enabled: true)
        let fileData = Data((0..<(150 * 1024)).map { UInt8($0 & 0xFF) })
        let srcRef = SourceRef("mem://photo.bin")
        await srcA.add(ref: srcRef, name: "photo.bin", data: fileData, mime: "application/octet-stream")
        let sendFileRes = await hostA.fileTransferEngine.sendFiles(peer: idB.deviceId, sources: [srcRef])
        guard case .started = sendFileRes else {
            return XCTFail("Expected file transfer started, got \(sendFileRes)")
        }

        try await Task.sleep(nanoseconds: 500_000_000)
        let committed = await recvB.committedFile("photo.bin")
        XCTAssertEqual(committed, fileData)

        await hostA.stopNetworking(reason: .normal)
        await hostB.stopNetworking(reason: .normal)
    }
}
