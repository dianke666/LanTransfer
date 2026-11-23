import Foundation

/// App Group 共享配置与主应用 / Share Extension 交接模型（自动兼容个人开发账号无 App Group 容器场景）。
public enum AppGroupConfig: Sendable {
    public static let groupId = "group.com.lantransfer.app"
    public static let urlScheme = "lantransfer"

    public static func sharedContainerURL() -> URL {
        if let groupURL = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: groupId) {
            return groupURL
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
    }

    public static func databaseURL() -> URL {
        let dir = sharedContainerURL().appendingPathComponent("LanTransfer", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("lantransfer.db")
    }

    public static func shareInboxDirectoryURL() -> URL {
        let dir = sharedContainerURL().appendingPathComponent("share-inbox", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    public static func userDefaults() -> UserDefaults {
        UserDefaults(suiteName: groupId) ?? .standard
    }
}

public struct ShareHandoverManifest: Codable, Sendable {
    public let batchId: String
    public let peerHex: String
    public let text: String?
    public let filePaths: [String]
    public let createdAtUtcMs: Int64

    public init(batchId: String, peerHex: String, text: String?, filePaths: [String], createdAtUtcMs: Int64) {
        self.batchId = batchId
        self.peerHex = peerHex
        self.text = text
        self.filePaths = filePaths
        self.createdAtUtcMs = createdAtUtcMs
    }
}
