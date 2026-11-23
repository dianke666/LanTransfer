# LanTransfer iOS 端（SwiftUI / iOS 17+）

> 与 Android、Windows 共用同一套协议（`Noise_XX_25519_AESGCM_SHA256` + UDP `42581` 局域网发现 + TCP `42580` 分片与断点续传）和共享测试向量 `protocol/test-vectors/`，**零第三方依赖**（全部采用 Apple 原生框架：`CryptoKit`、`Network`、`SQLite3`、`Security`、`AVFoundation`、`CoreImage`、`PhotosUI`、`QuickLook`、`SwiftUI`）。

## 1. 环境前置要求

- **macOS 14+** 与 **Xcode 16+**（Swift 6.0 / iOS 17.0 SDK+）
- **XcodeGen**（用于从 `project.yml` 生成 `.xcodeproj`）：
  ```bash
  brew install xcodegen
  ```
- **Python 3.10+**（修改 `shared/ui-text/zh-CN.tsv` 后重新生成三端文案时使用）

## 2. 快速开始

### 2.1 运行纯 Swift 核心单元测试（macOS 无界面即可跑通）
```bash
cd ios/Packages/LanTransferCore
swift test
```
核心包 `LanTransferCore` 同时声明了 `.iOS(.v17)` 与 `.macOS(.v14)`，可直接加载仓库根目录的 `protocol/test-vectors/` 全部 10 份协议向量。

### 2.2 生成并打开 Xcode 工程
```bash
cd ios
xcodegen generate
open LanTransfer.xcodeproj
```

### 2.3 命令行构建 iOS 模拟器包（无需签名）
```bash
cd ios
xcodegen generate
xcodebuild -project LanTransfer.xcodeproj \
  -scheme LanTransferApp \
  -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO \
  build
```

## 3. 签名与权限配置说明

| Entitlements 文件 | 适用账号 | 能力说明 |
| --- | --- | --- |
| `LanTransferApp/LanTransferApp.entitlements`（默认） | 个人免费 Apple ID / Simulator / CI | 仅含 App Group（`group.com.lantransfer.app`）。未获批组播权限时，可通过**扫描二维码**与**手动输入 IPv4**与 Android / Windows 完整互通。 |
| `LanTransferApp/LanTransferApp-Multicast.entitlements` | 付费 Apple Developer 账号（已获批 Multicast Networking） | 额外声明 `com.apple.developer.networking.multicast`，支持 UDP 组播 `239.255.42.58:42581` 与定向广播自动发现。启用时在 `project.yml` 将 `entitlements.path` 切换为此文件即可。 |

> **App Store 出口合规提示**：本应用采用纯本地局域网点对点加密传输（`Noise_XX_25519_AESGCM_SHA256`），上架 TestFlight / App Store 时请按 Apple App Store Connect 最新加密合规流程如实声明，切勿硬编码豁免标记。

## 4. 共享中文文案与设计令牌同步

修改 [shared/ui-text/zh-CN.tsv](../shared/ui-text/zh-CN.tsv) 后，在仓库根目录执行：
```bash
python shared/ui-text/generate.py
```
会自动同步更新：
- `ios/LanTransferApp/Resources/zh-Hans.lproj/Localizable.strings`
- `ios/LanTransferApp/UI/Text/UiText.g.swift`
（以及 Android 与 Windows 对应文案文件）

## 5. 目录结构

```text
ios/
├── project.yml                           # XcodeGen 工程描述
├── README.md
├── Packages/LanTransferCore/             # 零第三方依赖 SwiftPM 核心包
│   ├── Package.swift
│   ├── Sources/
│   │   ├── LanTransferProtocol/          # 协议常量、帧编解码、Noise_XX、QR、发现公告
│   │   ├── LanTransferDomain/            # M1/M3 实体与纯状态机归约器
│   │   ├── LanTransferEngine/            # C5 端口契约、Session/Transfer/Pairing/Discovery 协调器
│   │   └── LanTransferTesting/           # 共享测试向量定位器与测试桩
│   └── Tests/                            # Protocol / Domain / Engine 单元测试
├── LanTransferApp/                       # SwiftUI 主应用（iOS 17+）
│   ├── App/LanTransferApp.swift
│   ├── Info.plist
│   ├── LanTransferApp.entitlements
│   ├── LanTransferApp-Multicast.entitlements
│   ├── Platform/                         # Network/Security/Storage(M2 SQLite)/Settings/Files/SystemServices
│   ├── Resources/
│   │   ├── Assets.xcassets/
│   │   └── zh-Hans.lproj/Localizable.strings
│   └── UI/
│       ├── Components/                   # LosslessTextView（UITextView 桥接）、QuickLook、CommonComponents
│       ├── Screens/                      # Devices/Conversation/ConnectionSheets/History/Settings
│       ├── Text/                         # UiText.g.swift + UiTextMapper.swift
│       ├── Theme/LanTheme.swift
│       └── ViewModels/AppController.swift
├── LanTransferShareExtension/            # iOS 系统分享扩展（App Group 交接）
└── LanTransferAppTests/                  # 主应用冒烟与平台适配测试
```

## 6. 相关设计文档

- 架构与核心挑战方案：[docs/ios-architecture-and-ui-guide.md](../docs/ios-architecture-and-ui-guide.md)
- UI 与交互设计规范：[docs/ios-ui-design-spec.md](../docs/ios-ui-design-spec.md)
- 交互式 UI 视觉原型：[docs/ios-ui-preview.html](../docs/ios-ui-preview.html)
- 分阶段开发任务清单：[ios-tasks.md](../.kiro/specs/lan-file-transfer/ios-tasks.md)
