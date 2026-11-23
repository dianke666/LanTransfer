import XCTest
@testable import LanTransferDomain

final class DomainBaselineTests: XCTestCase {
    func testTaskStateCodesAndHelpers() {
        XCTAssertEqual(TaskState.offering.rawValue, 0)
        XCTAssertEqual(TaskState.running.rawValue, 2)
        XCTAssertEqual(TaskState.completed.rawValue, 10)
        XCTAssertEqual(TaskState.offerInvalid.rawValue, 18)

        XCTAssertTrue(TaskState.running.isActive)
        XCTAssertTrue(TaskState.resuming.isActive)
        XCTAssertFalse(TaskState.interrupted.isActive)
        XCTAssertFalse(TaskState.interrupted.isTerminal)
        XCTAssertTrue(TaskState.completed.isTerminal)
        XCTAssertTrue(TaskState.cancelled.isTerminal)
    }

    func testFileStateCodes() {
        XCTAssertFalse(FileState.writeFailed.isTerminal)
        XCTAssertTrue(FileState.completed.isTerminal)
        XCTAssertTrue(FileState.checksumFailed.isTerminal)
    }

    func testDirRefSerializationRoundTrip() {
        XCTAssertEqual(DirRef.default.serialized, "default")
        XCTAssertEqual(DirRef.parse("default"), .default)
        XCTAssertEqual(DirRef.parse("path:/var/mobile/Documents/LanTransfer"), .path("/var/mobile/Documents/LanTransfer"))
        XCTAssertEqual(DirRef.parse("tree:content://example"), .tree("content://example"))
        XCTAssertNil(DirRef.parse("unknown:foo"))
    }

    func testFsProfileIncludesIos() {
        XCTAssertTrue(FsProfile.allCases.contains(.ios))
    }
}
