import XCTest
@testable import LanTransferProtocol
import LanTransferTesting

final class ProtocolBaselineTests: XCTestCase {
    func testProtocolConstantsMatchSpecification() {
        XCTAssertEqual(ProtocolConstants.defaultPort, 42580)
        XCTAssertEqual(ProtocolConstants.discoveryPort, 42581)
        XCTAssertEqual(ProtocolConstants.multicastGroup, "239.255.42.58")
        XCTAssertEqual(ProtocolConstants.frameMagic, 0x4C54)
        XCTAssertEqual(ProtocolConstants.maxTextBytes, 1_048_576)
        XCTAssertEqual(ProtocolConstants.maxFilesPerOffer, 1000)
        XCTAssertEqual(ProtocolConstants.maxFileSize, 1 << 40)
    }

    func testDeviceTypeCodes() {
        XCTAssertEqual(DeviceType.android.rawValue, 1)
        XCTAssertEqual(DeviceType.windows.rawValue, 2)
        XCTAssertEqual(DeviceType.ios.rawValue, 3)
        XCTAssertEqual(DeviceType.fromCode(3), .ios)
        XCTAssertNil(DeviceType.fromCode(99))
    }

    func testIpv4ParsingAndFormatting() {
        let ip = Ipv4(parsing: "192.168.1.28")
        XCTAssertNotNil(ip)
        XCTAssertEqual(ip?.description, "192.168.1.28")
        XCTAssertNil(Ipv4(parsing: "192.168.01.28"))
        XCTAssertNil(Ipv4(parsing: "256.0.0.1"))
    }

    func testSharedVectorManifestAccessible() throws {
        let manifest = try VectorLocator.loadManifest()
        XCTAssertEqual(manifest.formatVersion, 1)
        XCTAssertEqual(manifest.protocolVersion, "1.0")
        XCTAssertEqual(manifest.files.count, 10)
        for file in manifest.files {
            let data = try VectorLocator.loadVectorData(named: file)
            XCTAssertFalse(data.isEmpty, "Vector file \(file) should not be empty")
        }
    }
}
