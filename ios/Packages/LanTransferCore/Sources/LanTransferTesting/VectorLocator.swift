import Foundation

/// 定位共享协议测试向量目录 `protocol/test-vectors/`，确保 Android / Windows / iOS 共用同一事实来源。
public enum VectorLocator: Sendable {
    public struct Manifest: Decodable, Sendable {
        public let formatVersion: Int
        public let protocolVersion: String
        public let files: [String]

        private enum CodingKeys: String, CodingKey {
            case formatVersion = "format_version"
            case protocolVersion = "protocol_version"
            case files
        }
    }

    public static func testVectorsDirectory(filePath: StaticString = #filePath) -> URL {
        // ios/Packages/LanTransferCore/Sources/LanTransferTesting/VectorLocator.swift -> repo root (6 levels up)
        var url = URL(fileURLWithPath: "\(filePath)")
        for _ in 0..<6 {
            url.deleteLastPathComponent()
        }
        return url.appendingPathComponent("protocol/test-vectors", isDirectory: true)
    }

    public static func loadManifest() throws -> Manifest {
        let manifestURL = testVectorsDirectory().appendingPathComponent("manifest.json")
        let data = try Data(contentsOf: manifestURL)
        return try JSONDecoder().decode(Manifest.self, from: data)
    }

    public static func loadVectorData(named fileName: String) throws -> Data {
        let fileURL = testVectorsDirectory().appendingPathComponent(fileName)
        return try Data(contentsOf: fileURL)
    }
}
