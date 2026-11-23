import XCTest
@testable import LanTransferEngine
import LanTransferProtocol

final class EnginePortsTests: XCTestCase {
    func testSocketAddrFormatting() {
        let addr = SocketAddr(ip: Ipv4(192, 168, 1, 100), port: ProtocolConstants.defaultPort)
        XCTAssertEqual(addr.description, "192.168.1.100:42580")
    }

    func testSystemClockReturnsPositiveTimestamps() {
        let clock = SystemClock()
        XCTAssertGreaterThan(clock.monotonicMs(), 0)
        XCTAssertGreaterThan(clock.wallMs(), 0)
    }
}
