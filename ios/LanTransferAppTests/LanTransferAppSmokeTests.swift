import SwiftUI
import XCTest
@testable import LanTransferApp
import LanTransferDomain
import LanTransferProtocol

final class LanTransferAppSmokeTests: XCTestCase {
    func testUiTextAlwaysResolvesSimplifiedChinese() {
        XCTAssertEqual(UiText.appName, "局域网传输")
        XCTAssertEqual(UiText.deviceTypeIos, "iOS")
        XCTAssertEqual(UiText.iosBtnPickPhotos, "相册")
    }

    func testDeviceTypeThemeMapping() {
        XCTAssertEqual(DeviceType.android.sfSymbolName, "smartphone")
        XCTAssertEqual(DeviceType.windows.sfSymbolName, "desktopcomputer")
        XCTAssertEqual(DeviceType.ios.sfSymbolName, "iphone")
    }

    func testThemeModeColorSchemeMapping() {
        XCTAssertNil(ThemeMode.system.preferredColorScheme)
        XCTAssertEqual(ThemeMode.light.preferredColorScheme, .light)
        XCTAssertEqual(ThemeMode.dark.preferredColorScheme, .dark)
    }
}
