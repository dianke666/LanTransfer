// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "LanTransferCore",
    platforms: [
        .iOS(.v17),
        .macOS(.v14),
    ],
    products: [
        .library(name: "LanTransferProtocol", targets: ["LanTransferProtocol"]),
        .library(name: "LanTransferDomain", targets: ["LanTransferDomain"]),
        .library(name: "LanTransferEngine", targets: ["LanTransferEngine"]),
        .library(name: "LanTransferTesting", targets: ["LanTransferTesting"]),
    ],
    dependencies: [],
    targets: [
        .target(
            name: "LanTransferProtocol"
        ),
        .target(
            name: "LanTransferDomain",
            dependencies: ["LanTransferProtocol"]
        ),
        .target(
            name: "LanTransferEngine",
            dependencies: ["LanTransferProtocol", "LanTransferDomain"]
        ),
        .target(
            name: "LanTransferTesting",
            dependencies: ["LanTransferProtocol", "LanTransferDomain", "LanTransferEngine"]
        ),
        .testTarget(
            name: "LanTransferProtocolTests",
            dependencies: ["LanTransferProtocol", "LanTransferTesting"]
        ),
        .testTarget(
            name: "LanTransferDomainTests",
            dependencies: ["LanTransferDomain", "LanTransferTesting"]
        ),
        .testTarget(
            name: "LanTransferEngineTests",
            dependencies: ["LanTransferEngine", "LanTransferTesting"]
        ),
    ]
)
