# LanTransfer iOS 端实施任务清单（Swift 6 / SwiftUI / iOS 17+）

> 架构与平台关键难题方案见 [docs/ios-architecture-and-ui-guide.md](../../../docs/ios-architecture-and-ui-guide.md)，UI 与交互规范见 [docs/ios-ui-design-spec.md](../../../docs/ios-ui-design-spec.md)，交互式视觉原型见 [docs/ios-ui-preview.html](../../../docs/ios-ui-preview.html)。

---

## Wave 0：架构设计、UI 规范与工程前置准备（已完成）

- [x] **0.1** 架构方案与 iOS 专项技术决策定稿（[docs/ios-architecture-and-ui-guide.md](../../../docs/ios-architecture-and-ui-guide.md)）
- [x] **0.2** Apple HIG 原生 SwiftUI UI/UX 设计规范定稿（[docs/ios-ui-design-spec.md](../../../docs/ios-ui-design-spec.md)）
- [x] **0.3** 离线可交互 iOS 视觉原型制作（[docs/ios-ui-preview.html](../../../docs/ios-ui-preview.html)）
- [x] **0.4** 扩展 `shared/design-tokens.json`、`shared/ui-text/zh-CN.tsv` 与 `generate.py`，生成 `Localizable.strings` 与 `UiText.g.swift`
- [x] **0.5** 搭建零第三方依赖 SwiftPM 核心包 `ios/Packages/LanTransferCore`、XcodeGen `ios/project.yml`、M2 `Schema.sql`、Entitlements / Info.plist、主题基座 `LanTheme.swift` 及主应用 / 分享扩展骨架
- [x] **0.6** 配置 GitHub Actions macOS 工作流 `.github/workflows/ios.yml` 与 `.gitignore`

---

## Wave 1：`LanTransferProtocol` 协议层（已完成源码与向量测试）

- [x] **1.1** TLV 编解码（`Tlv.swift`）、Strict UTF-8 校验（`StrictUtf8.swift`）、帧头（`0x4C54`、`Frame.swift`）与 25 种消息体编解码（`Messages.swift`、`MessageCodec.swift`）
- [x] **1.2** 前导版本协商（`PreambleCodec`）、UDP 局域网发现公告（`AnnouncementCodec`）、Base32 与 `LT1:` 二维码编解码（`Discovery.swift`、`Qr.swift`、`HandshakeIdentity.swift`）
- [x] **1.3** `Noise_XX_25519_AESGCM_SHA256` 握手状态机（`NoiseXX.swift`）、加密记录层（`Record.swift`）、6 位 SAS 验证码与承诺校验（`Crypto.swift`，纯 Apple `CryptoKit`）
- [x] **1.4** 全量 9 份协议共享测试向量一致性测试（`ProtocolVectorConformanceTests.swift` 直接加载 `protocol/test-vectors/manifest.json`）

---

## Wave 2：`LanTransferDomain` 领域层与纯状态机（已完成源码与向量测试）

- [x] **2.1** `FileNameSanitizer`（`FsProfile.ios`：255 UTF-8 字节限制、重名序号与碰撞解析）及 `filename-sanitizer.json` 向量测试（`FileNames.swift`、`DomainVectorAndReducerTests.swift`）
- [x] **2.2** `DisplayNameRules`（1–32 码点、空白与控制字符校验）与 `AddressRules`（RFC1918 私有子网判定、接口排序、二维码候选连接顺序）
- [x] **2.3** `TextRules`、`TextDisplayMapper`（兼容 `UITextView` UTF-16 选区还原映射）、`SizeFormatter`、`ProgressCalculator` 与 `DeviceListReducer`
- [x] **2.4** 三个纯归约器：`PairingReducer`、`TextReducer`、`TransferReducer` 及状态迁移单元测试

---

## Wave 3：`LanTransferEngine` 引擎层（已完成源码与内存端到端测试）

- [x] **3.1** 内存测试桩套件（`InMemoryEngineFixtures.swift`：`InMemoryNetworkHub`、`InMemoryTransport`、`InMemoryHistoryAndTaskStore`、`InMemoryReceiveStorage` 等）
- [x] **3.2** `LocalNetworkGuard`、`Handshake`、`Connection`（P0/P1/P2 优先队列与 5 s / 15 s 心跳超时）与 `SessionManager`（双向并发建连消重）
- [x] **3.3** `PairingService`（点选 SAS 配对、扫码配对、一次性令牌生命周期、60 s 拒绝冷却、解除配对）
- [x] **3.4** `TextService`（无损 UTF-8 发送、去重落库、失败重试、前台自动复制与后台通知）
- [x] **3.5** `FileTransferEngine`（Offer 预检、64 KiB 分片、增量 `CryptoKit.SHA256` 校验、断点续传、取消与 24 h 临时文件清理）
- [x] **3.6** `DiscoveryService`（组播/定向广播公告、10 s 过期与无组播权限降级）与 `EngineHost` 统一门面及 `EngineFlowTests.swift`

---

## Wave 4：`LanTransferApp/Platform` iOS 原生平台适配层（已完成源码）

- [x] **4.1** `IosNetworkMonitor`（`getifaddrs()` + `NWPathMonitor`，过滤蜂窝 `pdp_ip*` / VPN `utun*` / `awdl0`，支持 Wi-Fi `en0` 与个人热点 `bridge100`）
- [x] **4.2** `IosTransport`（`NWConnection` / `NWListener` 禁蜂窝+绑定源地址；POSIX BSD UDP 组播/定向广播 + 无组播权限优雅降级）
- [x] **4.3** `KeychainIdentityStore`（X25519 私钥仅存本机 Keychain，禁用 iCloud 同步，支持损坏重置提示）
- [x] **4.4** `SqliteDatabase`（原生 `libsqlite3` + WAL + App Group `group.com.lantransfer.app`，复用 `Schema.sql` 实现 Trust / History / Task 存储及 Share Extension 只读快照）
- [x] **4.5** `IosReceiveStorage`、`IosSourceFiles`（`Documents/LanTransfer/` 暴露至“文件”App、临时 `.part` 隔离在 `Library/Caches/lantransfer-tmp/`、安全作用域读取）
- [x] **4.6** `IosClipboard`（读回校验）、`IosFileLocator`（QuickLook + `shareddocuments://`）、`IosNotifier`（`UNUserNotificationCenter`）、`IosAppLifecycle`（`beginBackgroundTask` + `isIdleTimerDisabled` + 切后台 `SESSION_CLOSE(BACKGROUND)`）
- [x] **4.7** `LanTransferShareExtension` 完整实现（读取系统分享附件/文字 → 写入 App Group `share-inbox` → 通过 `lantransfer://share` 唤起主应用进入目标会话）

---

## Wave 5：`LanTransferApp/UI` 九大 SwiftUI 页面与真机联调

- [x] **5.1** `DevicesTabView`（状态横幅栈、本机英雄状态卡、可点击复制 IP 芯片、三联快捷按钮、局域网/已信任设备卡片与雷达空态）
- [x] **5.2** `ConversationView` 与 `LosslessTextView`（`UIViewRepresentable` 包装 `UITextView` 覆写 `copy(_:)` 无损选区还原、长文本折叠、多文件进度卡、待发送队列、`PhotosPicker` + `fileImporter`）
- [x] **5.3** `QrShowSheet`（`CoreImage` 无插值 QR、亮度提升/恢复、截屏温和提醒）与 `QrScanView`（`AVFoundation` 全屏取景框、手电筒、3 s 误码防抖、权限引导）
- [x] **5.4** `ManualConnectSheet`、`PairingSheet`（6 位超大等宽 SAS 确认弹窗）与 `IncomingOfferSheet`（并发 Offer 队列与文件清单）
- [x] **5.5** `HistoryTabView`（原生 `.searchable`、三段筛选、左滑三选一删除确认、QuickLook / 文件 App 定位）与 `SettingsTabView`（主题切换、设备改名、后台说明、已信任设备自动接收与解除配对）
- [x] **5.6** 云端 macOS / Xcode 16.4 / Swift 6.0 全量编译与测试通过（GitHub Actions `.github/workflows/ios.yml` 全部绿灯：共享文案同步校验、`LanTransferCore` 10 份跨端向量与 E2E 单元测试、`LanTransferApp` + `LanTransferShareExtension` + `LanTransferAppTests` 模拟器安装测试、真机 Release 免签名 `LanTransfer-1.0.0-unsigned.ipa` 构建）
- [ ] **5.7** 真机三端联调（Windows Sideloadly / AltStore 侧载或 Xcode 真机运行，验证 iOS ↔ Windows / Android 在 Wi-Fi 与手机热点下的扫码/手动连接、无损文字、多文件与断点续传全流程互通，暂缓待后续安排）
