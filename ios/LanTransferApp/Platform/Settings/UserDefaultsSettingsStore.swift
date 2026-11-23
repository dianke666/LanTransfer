import Foundation
import LanTransferDomain
import LanTransferEngine

/// 基于 UserDefaults（支持 App Group）的应用设置存储。
public actor UserDefaultsSettingsStore: SettingsStore {
    private static let receiveDirKey = "lan.receiveDir"
    private static let autoCopyKey = "lan.autoCopyText"
    private static let themeModeKey = "lan.themeMode"

    private let suiteName: String?

    public init(suiteName: String? = AppGroupConfig.groupId) {
        self.suiteName = suiteName
    }

    private func defaults() -> UserDefaults {
        if let suiteName {
            return UserDefaults(suiteName: suiteName) ?? .standard
        }
        return .standard
    }

    public func current() -> Settings {
        let ud = defaults()
        let dir = ud.string(forKey: Self.receiveDirKey).flatMap(DirRef.parse) ?? .default
        let autoCopy = ud.bool(forKey: Self.autoCopyKey)
        let theme = ud.string(forKey: Self.themeModeKey).flatMap(ThemeMode.init(rawValue:)) ?? .system
        return Settings(receiveDir: dir, autoCopyText: autoCopy, themeMode: theme)
    }

    public func update(_ transform: @Sendable (Settings) -> Settings) -> StoreResult {
        let next = transform(current())
        let ud = defaults()
        ud.set(next.receiveDir.serialized, forKey: Self.receiveDirKey)
        ud.set(next.autoCopyText, forKey: Self.autoCopyKey)
        ud.set(next.themeMode.rawValue, forKey: Self.themeModeKey)
        return .ok
    }
}
