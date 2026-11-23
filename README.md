# LanTransfer

在同一局域网（Wi‑Fi 局域网或 Android 手机热点）内，在 Android 手机、Windows 电脑及后续 iOS 设备之间点对点传输无损文字和大文件。全部数据只在本地网络内传输，端到端加密（Noise_XX_25519_AESGCM_SHA256），不经过互联网或蜂窝数据。

- **Android_App**：Android 8.0（API 26）及以上（Material 3 / Jetpack Compose，支持浅色 / 深色 / 跟随系统）
- **Windows_Client**：Windows 10 版本 1809 及以上的 x64 系统（Fluent Win11 风格 WPF，支持浅色 / 深色 / 跟随系统与原生沉浸式暗色标题栏）
- **iOS_App（Wave 0–5 已通过 GitHub Actions macOS-15 / Xcode 16.4 全量编译、单元测试、模拟器测试与免签名 IPA 构建；待真机联调）**：iOS 17.0+（原生 SwiftUI / Apple HIG / 零第三方依赖）；涵盖纯 `CryptoKit` Noise_XX 协议层、领域层与 3 大归约器、全套并发引擎、iOS 原生平台适配层（Network/Keychain/SQLite M2/Files/ShareExtension）、9 大 SwiftUI 界面及 10 份共享向量单元测试

需求、设计与实施计划见 `.kiro/specs/lan-file-transfer/`（iOS 分阶段任务清单见 [ios-tasks.md](.kiro/specs/lan-file-transfer/ios-tasks.md)）。

## 仓库结构

| 目录 | 内容 |
|---|---|
| `protocol/spec/` | 平台无关的协议规范与变更记录，是各端之间唯一的契约 |
| `protocol/test-vectors/` | JSON 测试向量，Android / Windows / iOS 测试套件均直接读取验证 |
| `shared/ui-text/` | 简体中文界面文案唯一来源 `zh-CN.tsv` 与生成器 `generate.py`，生成 Android `strings.xml`/`UiText.kt`、Windows `Strings.resx`/`UiText.g.cs` 与 iOS `Localizable.strings`/`UiText.g.swift` |
| `shared/design-tokens.json` | 跨平台统一设计令牌（浅色 / 深色色板、圆角、间距、iOS SF Symbols/触觉反馈与 Android / Windows / iOS 平台徽章定义） |
| `android/` | Android 端：Kotlin + Jetpack Compose + Room，Gradle 多模块工程 |
| `windows/` | Windows 端：.NET 10 + WPF + SQLite 解决方案与 Inno Setup 6 安装程序 |
| `ios/` | iOS 端：Swift 6 + SwiftUI + 零第三方依赖 SwiftPM 核心包（`LanTransferCore`）、XcodeGen `project.yml` 与系统分享扩展 |
| `docs/` | 架构与 UI 文档（含 [iOS 架构指南](docs/ios-architecture-and-ui-guide.md)、[iOS UI 规范](docs/ios-ui-design-spec.md) 与 [交互式 UI 原型](docs/ios-ui-preview.html)） |
| `interop-tests/` | 规划中的 JVM `peer-cli` 与 .NET `PeerCli` 跨端自动化互通测试目录 |
| `.github/workflows/` | CI 工作流（含 Android、Windows、Installer 与 macOS iOS 工作流） |

各端不共享运行时代码，只共享协议规范、测试向量、界面文案表与视觉设计令牌。

## 快速构建与运行

### Windows 端（.NET 10 SDK + Inno Setup 6）
```powershell
dotnet build windows/LanTransfer.sln --no-restore
dotnet publish windows/src/LanTransfer.App -c Release -o windows/publish/LanTransfer --no-restore
iscc windows/installer/LanTransfer.iss
# 调试产物：windows/src/LanTransfer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/LanTransfer.exe
# 安装包：windows/installer/Output/LanTransfer-Setup-1.0.0-win-x64.exe
```

### Android 端（JDK 17 + Android SDK 36）
```powershell
./android/gradlew -p android :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --offline
# 产物：android/app/build/outputs/apk/debug/app-debug.apk
```

### iOS 端（macOS 14+ / Xcode 16+ / Swift 6，或直接通过 GitHub Actions 云端构建）
```bash
# 运行纯 Swift 核心单元测试（含共享协议测试向量校验）
swift test --package-path ios/Packages/LanTransferCore

# 生成 Xcode 工程并运行 iOS 模拟器测试（免签名）
cd ios && xcodegen generate
xcodebuild -project LanTransfer.xcodeproj -scheme LanTransferApp -destination 'generic/platform=iOS Simulator' CODE_SIGNING_ALLOWED=NO build
```
> 无本地 macOS 时，推送到 GitHub 后 `.github/workflows/ios.yml` 会在 `macos-15`（Xcode 16.4 / Swift 6.0）自动执行文案同步校验、`swift test`、模拟器测试并产出免签名真机包 Artifact **`LanTransfer-iOS-unsigned-ipa`**（`LanTransfer-1.0.0-unsigned.ipa`），可在 Windows 上通过 Sideloadly / AltStore 侧载到真机。

### 重新生成三端共享文案
```powershell
python shared/ui-text/generate.py
```

## 约定

- 文本文件一律使用 UTF‑8（无 BOM），换行为 LF；`.bat`、`.cmd`、`.sln` 使用 CRLF。见 `.editorconfig` 与 `.gitattributes`。
- `protocol/test-vectors/` 下的文件和 `*.tsv` 由 Git 按字节原样存取，不做换行转换；请勿手工调整其中的换行或行尾空白。
- Android release 签名使用的 keystore（`*.jks`、`*.keystore`）、`keystore.properties` 和全部密码属于机密，不得提交到仓库（已写入 `.gitignore`）。keystore 必须离线备份：丢失后无法再发布可覆盖安装的升级包，用户只能卸载重装，并因此丢失设备身份和已配对设备。
