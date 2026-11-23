# lan-file-transfer 实施进度记录

最近更新：2026-11-23。

## 总览

| 任务 | 状态 | 说明 |
|---|---|---|
| 1.1 仓库骨架与通用配置 | 已完成 | |
| 1.2 Android Gradle 多模块工程 | 已完成 | 安装 Android SDK 后 `gradlew build :app:assembleDebugAndroidTest`（全部模块，含 `:app` lint）通过，锁文件已生成；lifecycle / navigation 版本按 compileSdk 36 回调，kotlin-stdlib-common 不纳入锁定，见下文 |
| 1.3 Windows .NET 解决方案 | 已完成 | build + test 通过 |
| 1.4 Kotlin 属性测试辅助与生成器 | 已完成 | |
| 1.5 C# 属性测试辅助与生成器 | 已完成 | |
| 1.6 CI 工作流骨架 | 已完成 | 未在 GitHub 上实际运行（本地不是 git 仓库）；Actions 按提交 SHA 锁定 |
| 2.1 协议规范 | 已完成 | `protocol/spec/lan-transfer-protocol.md`、`CHANGELOG.md` |
| 2.2 测试向量格式与加载器 | 已完成 | `protocol/test-vectors/README.md`、`manifest.json`；两端 `TestVectors` 加载器 |
| 3.1–3.8 协议层基础（两端）与 Property 1–4、6、7 | 已完成 | Bytes / Tlv / StrictUtf8 / Messages / MessageCodec / Frame |
| 4.1–4.7 公告、二维码、前导（两端）与 Property 5、8、10–12 | 已完成 | Discovery / Qr / Base32 |
| 5.1–5.5 NoiseXX、记录层、验证码（两端）与 Property 13–15 | 已完成 | 两端都通过 Noise 官方向量 Noise_XX_25519_AESGCM_SHA256（cacophony）；`advertisedStaticOverride` 只供测试模拟冒充者 |
| 6.1–6.2 向量生成工具与 Kotlin 一致性测试 | 已完成 | `android/tools/vector-gen`（`gradlew :tools:vector-gen:run` 重写 protocol/test-vectors 与 manifest）；两端共用 `VectorJson` 映射 |
| 6.3 C# 一致性测试 | 已完成 | `TestVectorConformanceTests.cs` |
| 7 协议层检查点 | 已完成 | Kotlin（JVM 模块）与 C# 全部测试通过（C# 72 个） |
| 8.1–8.6 文件名处理（两端）、filename-sanitizer.json 与 Property 17–19 | 已完成 | Kotlin `FileNameSanitizer.trace` 额外输出触发的步骤，供生成向量的 `steps` 字段；保留名只做 ASCII 大小写折叠 |
| 9.1–9.6 显示名称与地址规则（两端）与 Property 20–23 | 已完成 | `Network.kt/.cs`（Ipv4、IfaceKind、LocalInterface、Route、PortParse、AddressRules、`connectInQrOrder`）、`DisplayNames.kt/.cs` |
| 10.1–10.9 文字规则、显示映射、大小格式化、进度、设备列表（两端）与 Property 24–29、9 | 已完成 | `Text`、`Progress`、`DeviceList`（Kotlin `.kt` / C# `.cs`）；C# 另有 `ValueSet<T>`（按集合比较的只读集合）。非法公告生成器 `Gens.invalidAnnouncement` / `Gens.InvalidAnnouncementGen`（10 种破坏方式，两端 kind 取值一致）；C# `Gens.Choose64` 对应 Kotlin `Arb.long`。C# 共 96 个测试通过 |
| 11.1–11.7 M1 实体、M3 枚举、C3.5 规则（两端）与 Property 30–34 | 已完成 | `States`、`Entities`、`Rules`（Kotlin `.kt` / C# `.cs`）。`Rng`（C# `IRng`）定义在领域层，引擎层复用。实体中的字节字段用 `Bytes`、C# 列表字段用 `ValueList`。设计未写明、在此确定的细节：OfferPrecheck 的语义校验另含 mime（≤ 255 字节可打印 ASCII，按协议规范）；ResumePlanner 中 stat 为 null 按未变化处理、RESUME_STATE 缺项按 NONE 处理、接收方报告 COMPLETED / TERMINAL 也给 SKIP；TempInfo 的 transferId 为 null 视为不属于进行中任务；令牌“使用中”不触发 needsRefresh。两端差异：Kotlin `DirRef.Path.path` ↔ C# `DirRef.Path.Value`，C# `DirRef.Default.Instance`。C# 领域测试 41 个通过 |
| 12.1–12.7 三个纯归约器（两端）、Property 35–38 与状态迁移表测试 | 已完成 | `PairingReducer`、`TextReducer`、`TransferReducer`（Kotlin `.kt` / C# `.cs`）；C# 另有 `ValueMap<K, V>`（按内容比较的不可变映射）。测试：`ReducerPropertiesTest` / `ReducerPropertyTests`（P35 双方模拟、P36 参考模型逐步比对 effects、P37、P38 不变量；C# 另有两个覆盖率检查）、`TransitionTableTest`（Kotlin 60 个）/ `TransitionTableTests`（C#）。设计约定见下文“归约器的设计约定” |
| 13 领域层检查点 | 已完成 | Gradle 全量 `build` 通过（JVM 模块 24 个测试类、159 个测试，0 失败）；C# 全解决方案 174 个测试通过 |
| 14.1 / 14.2 引擎层端口与内存实现（两端） | 已完成 | 端口在 Kotlin `core/engine/.../engine/ports/`（`Network`、`Files`、`Stores`、`System`）与 C# `LanTransfer.Engine/Ports/`（`Network`、`Files`、`Stores`、`SystemPorts`，接口加 `I` 前缀）；内存实现在 Kotlin `core/engine` testFixtures（`com.lantransfer.engine.testing`）与 C# `LanTransfer.Testing`。自检：Kotlin `EngineFixturesTest` 17 个、C# `EngineFixturesTests` 18 个 + `ObservablesTests` 4 个。C# 全解决方案 196 个测试通过。偏离设计草图之处见下文“任务 14 记录” |
| 15.1–15.8 LocalNetworkGuard、Connection、SessionManager（两端）、Property 40–44 与连接 / 会话单元测试 | 已完成 | Kotlin `core/engine/.../engine/`：`SessionTypes`、`Handshake`、`Connection`、`LocalNetworkGuard`、`SessionManager`（含 `Session`、`PendingConnection`）；C# `LanTransfer.Engine/` 同名。测试：Kotlin `ConnectionTest` 9、`SessionManagerTest` 21、`SessionPropertiesTest` 5（P40、P42–P44 各 100 次，P41 穷举 10 种组合）；C# `ConnectionTests` 9、`SessionManagerTests` 21、`SessionPropertyTests` 5。Gradle `:core:engine:test` 通过；C# 全解决方案 231 个测试通过（连续两次）。设计约定见下文“任务 15 记录” |
| 16.1–16.3 PairingService（两端）与配对单元测试 | 已完成 | 见下文“任务 16 记录”。Kotlin `:core:engine:test` 通过；C# Engine.Tests 73 个通过 |
| 17.1–17.4 TextService（两端）、Property 39 与文字单元测试 | 已完成 | 见下文“任务 17 记录”。Kotlin `TextServiceTest` 17 个 + `TextPropertiesTest`（P39，100 次）；C# `TextServiceTests` 17 个 + `TextPropertyTests`（P39）。Gradle 全量 `build`（含 `:app` lint、animal-sniffer）通过；C# 全解决方案 263 个测试通过（Engine.Tests 91 个） |
| 18.1–18.9 FileTransferEngine（两端）、Property 45–47、52 与文件传输单元测试 | 已完成 | 见下文“任务 18 记录”。Kotlin 13 个引擎测试类全部通过，Gradle 全量 `build` 通过；C# `FileTransferEngineTests` 17、`FileTransferRecoveryTests` 11、`TransferPropertyTests` 3（P45、P47、P52）、`ResumePropertyTests` 3（P46 + 2 个确定用例）；C# 全解决方案 297 个测试通过；四个传输属性在 `LT_PBT_SCALE=5`（各 500 次）下也通过 |
| 19.1–19.4 DiscoveryService、EngineHost 与 Property 16（两端） | 已完成 | C# `DiscoveryService`、`EngineHost` 及 `WireConfidentialityPropertyTests`（100 次迭代）补齐并通过 |
| 21–23 存储层、共享文案与统一设计令牌 | 已完成（不含 CI 校验脚本 21.3 / 23.4） | Windows SQLite / Android Room M2 全量建表与存储实现（Room schema 导出至 `android/app/schemas/`）；`shared/ui-text/zh-CN.tsv` + `generate.py` 生成双端文案；`shared/design-tokens.json` |
| 25–31 平台适配层与双端现代 UI（Windows WPF + Android Compose M3） | 已完成 | Windows Fluent Win11 现代界面与 Android Material 3 现代界面全量实现，支持浅色 / 深色 / 跟随系统切换；编译与基础测试全部通过 |
| 32–33 Windows 安装程序（Inno Setup 6） | 已完成 | `windows/installer/LanTransfer.iss`、`Languages/ChineseSimplified.isl` 与 `.github/workflows/installer.yml` 完成；Release 自包含发布并编译产出 `LanTransfer-Setup-1.0.0-win-x64.exe` |
| iOS Wave 0–5 全量源码与单元测试（Swift 6 / SwiftUI / 零三方依赖） | 已完成代码实装（待 macOS/Xcode 编译联调） | 已完成 `LanTransferProtocol`（含 9 份协议测试向量测试）、`LanTransferDomain`（含 `filename-sanitizer.json` 向量与 3 个纯归约器）、`LanTransferEngine`（全部并发 actor 与内存 E2E 测试）、`LanTransferApp/Platform`（Network/Keychain/SQLite M2/Files/Clipboard/Notifier/Lifecycle/ShareExtension）及 9 大 SwiftUI 页面 + `LosslessTextView` 无损选区复制；65 个 Swift 文件通过全量静态审计，待 macOS / Xcode 16+ 或 GitHub Actions `macos-15` 执行编译与真机三端互通验证（任务 5.6） |
| 22 / 34 / 35 互通测试、CI 门禁与性能基准 | 暂缓 | 按用户指示停止后续测试任务 |

两端命名差异（C# 成员不能与类型同名等）：Kotlin `OutgoingText.text` ↔ C# `OutgoingText.Body`；Kotlin `TextItem.text` ↔ C# `TextItem.Body`；C# 的 `PairingSession` 各子类型自带 `Deadline`（基类只有 `Peer`、`Role`），`PairingEffect.CancelConnect` 等无字段 effect 是无参数 record；Kotlin `Text.text` ↔ C# `Text.Body`；Kotlin 用 `UInt` / `ULong` 表示 u32 / u64，C# 用 `uint` / `ulong`；Kotlin `QrPayload.ipv4List: List<Int>` ↔ C# `ValueList<uint>`；C# 中带列表字段的 record 用 `ValueList<T>` 保证值相等。

## 任务 12 记录

### 代码位置（Kotlin `android/core/domain/src/main/kotlin/com/lantransfer/domain/`，C# `windows/src/LanTransfer.Domain/` 同名）

- `PairingReducer.kt`：`Step<S, F>`、`ConnId`、`PeerInfo`、`PairingRole`、`PairingSession`（Connecting / AwaitingPeer / ShowingSas / AwaitingUser / AwaitingQrResult / AwaitingComplete / Saving）、`PairingState(session, rejectedAt, last)`、`PairingEvent`、`PairingEffect`。
- `TextReducer.kt`：`TextKey(peer, messageId)`、`OutgoingText`（`showRetry`）、`TextState`、`TextEvent`（Send / Retry / AckReceived / CorruptReceived / Tick / Restored / Forget）、`TextEffect`（Transmit / SaveStatus / ArmTimer）。
- `TransferReducer.kt`：`reduce(TransferTask, TransferEvent)`，`VerifyOutcome`、`TransferEvent`（发送方、接收方、双方三组）、`TransferEffect`。
- `States.kt` 的 `PairingResult` 新增 `STORE_FAILED`（保存 / 提交信任失败，M3.4 未列出）。
- 测试辅助 `src/test/.../ReducerFixtures.kt`：`deviceIdOf`、`bytes32`、`peerInfo`、`fixtureTransferId`、`fixtureLocalNow`、`transferTask(direction, state, sizes)`、`Random.bytes`。

### 归约器的设计约定（设计文档未写明，此处确定，C# 镜像须一致）

- 计时：截止时刻存放在状态中（单调时钟毫秒），由 `Tick(now)` 事件处理到期；进入有截止时刻的阶段时发出 `ArmTimer(at)`。TransferReducer 例外：Offer 计时用 `StartOfferTimer` / `CancelOfferTimer` effect 加 `OfferTimedOut` 事件。
- 配对：点选连接 10 s、扫码连接 20 s（4 个候选 × 5 s）、请求 60 s、等待 PAIR_COMPLETE / QR_PAIR_RESULT 15 s、冷却 60 s。接收方自动拒绝的顺序：先查冷却 → 回复 REJECTED（结局 AUTO_REJECTED），再查是否已有任何进行中的配对（包括本机正在发起的）→ BUSY；同一设备 ID 的第二个请求也回复 BUSY。只有收到 PAIR_REVEAL 并校验承诺后（`sas` 非空）才接受用户的接受 / 拒绝。用户拒绝时记录 `rejectedAt`。
- 信任：发起方 / 扫描方收到 ACCEPTED / QR OK → `Saving` + `SaveTrust` → `TrustStored(ok)`：成功则发 PAIR_COMPLETE 并 SUCCESS，失败则 STORE_FAILED 并关闭连接；`Saving` 期间断开 → `DeleteTrust` + DISCONNECTED。接收方 / 显示方：接受或令牌通过时 `StageTrust`，收到 PAIR_COMPLETE → `Saving` + `CommitTrust` → `TrustStored`；此后断开不影响结局。显示方成功时 `MarkTokenUsed`，中断、失败时 `ReleaseToken`。提交失败时 `DiscardStagedTrust`。
- 文字：按（peer, messageId）记录；TEXT_ACK 让 SENDING / FAILED → DELIVERED；TEXT_CORRUPT 只影响 SENDING；Retry 只对 FAILED 有效，消息 ID、文本和 sent_at 都不变；`Restored` 把 SENDING 改为 FAILED；`Forget` 删除条目，之后的迟到应答被忽略。
- 传输：“当前文件”是第一个未进入终态的文件。对方消息针对更靠后的文件或不存在的文件 → INTERRUPTED(PROTOCOL_ERROR) + `ProtocolViolation`；针对已终态的文件 → 忽略。接收方收到的字节数超过 size 时立即 CHECKSUM_FAILED。`LocalCancel`：OFFERING → CANCELLED + TRANSFER_CANCEL；AWAITING_USER → REJECTED（回复 REJECT）；RUNNING → CANCELLED；INTERRUPTED → CANCELLED + `AddPendingCancel`；RESUMING 时按 `sessionActive` 决定直接发送还是写 pending_cancel。接收方取消时对全部非终态文件发出 `DeleteTemp`。`ResumeRequested` 清空 interrupt；`ResumeFailed(reason)` 带回中断原因。`ResumePlanned`：SKIP 的非终态文件记为 COMPLETED，CONTINUE → WAITING 且 persisted = start，RESTART → WAITING、persisted 归零、更新 size / mtime，接收方另外发出 `DeleteTemp`；发送方随后发送 RESUME_PLAN 并开始当前文件。`Restored`：RUNNING / RESUMING → INTERRUPTED(APP_KILLED)，OFFERING / AWAITING_USER → CONNECTION_LOST。
- 时间戳 `updatedAtUtcMs` 不由归约器维护，由引擎层保存任务时写入。

### 测试做法（P35–P38，两端相同；每个用例由一个种子决定，失败信息里带种子）

   - P35：用两份 `PairingState`（发起方 I、接收方 R）加一个内存链路模拟（两个消息队列；`Close` 之后对方在收完已发出的消息后收到 `Disconnected`）和信任存储模拟（`SaveTrust` / `CommitTrust` 进入待完成队列，由动作决定 ok；`DeleteTrust` 让 I 侧尚未完成的保存作废）。投递给 R 的 PAIR_REQUEST 要转换成 `PairRequestReceived`。动作包括：两个方向的投递、接受、拒绝、I 取消、推进时间（并给双方 Tick）、断线、PAIR_COMPLETE 丢失（丢弃后断线）、完成存储操作。之后进入“排空”阶段，直到双方都没有进行中的配对。断言：I 已保存 ⇔ I 的结局为 SUCCESS；R 已提交 ⇔ R 的结局为 SUCCESS；`CommitTrust` 只在处理 PAIR_COMPLETE 时出现；最终暂存为空；拒绝 / 取消 / 超时时双方都没有信任；距 PAIR_REQUEST 满 60 s 后 I 不在 AwaitingPeer / ShowingSas，R 收到请求满 60 s 后不在 AwaitingUser。
   - P36：只模拟接收方，参考模型状态为 无 / Showing(k, revealed) / Completing / Saving，加上 `rejectedAt`。动作：来自 3 个设备的请求（每个请求用新的 ConnId，并用该设备的 pk 与 nI 计算 commitment）、Reveal、接受、拒绝、PAIR_COMPLETE、存储完成、断线、PAIR_CANCEL、推进时间。逐步比较会话类型与设备，以及 REJECTED / BUSY / NONCE 回复和 `ShowRequest` 是否出现。
   - P37：3 个 TextKey 上的 Send / Retry / Ack / Corrupt / 推进时间 / 重启，参考模型按 M3.1 表编写；检查状态、`showRetry` 和重试时 Transmit 的 ID 与文本不变。
   - P38：每个用例用种子化 Random 在循环中按当前任务现场生成事件（索引 70% 取当前文件），覆盖两个方向。逐步检查：至多 1 个文件处于 TRANSFERRING / VERIFYING，且它之前的文件都已终态；终态任务不变且没有 effects；`Restored` 后不存在 RUNNING / RESUMING / OFFERING / AWAITING_USER；RUNNING、INTERRUPTED、RESUMING 的接收方任务被取消时，`DeleteTemp` 恰好覆盖全部非终态文件；文件变为 READ_FAILED / CHECKSUM_FAILED 后任务仍为 RUNNING 或 COMPLETED，发送方此时下一个文件为 TRANSFERRING 并发出 `StartFile`；interrupt 非空 ⇔ INTERRUPTED。
   - 覆盖率：Kotlin 在属性测试结束后检查见过的结局 / 状态集合；C# 的 FsCheck 属性不便跨用例收集，另用 `[Fact]` 跑种子 0–999 检查（`Property35_CoversEveryOutcome`、`Property38_CoversEveryState`）。
- 实现中补充的一处约定：对方取消（TRANSFER_CANCEL）发生在 Offer 阶段时不发出 `DeleteTemp`（此时还没有临时文件）。

## 任务 14 记录

### 端口与设计草图（C5）的差异（两端一致）

- `TcpConn.write` 返回 `Boolean`（连接已关闭时为 false）；`TcpListener.accept()`、`UdpEndpoint.receive()` 关闭后返回 null；`ChunkReader.read` 返回 `ReadResult<Int>`，-1 表示 EOF。
- `StoreResult` = `Ok` / `Duplicate` / `Failed(detail)`；新增 `TaskStore.get`、`SettingsStore.current()`；`recordAddress` 返回 `StoreResult`。
- `HistoryRecord` = `Text(TextRecord)` / `File(FileRecord)`；`FileRecord` 额外带 `taskState` 与 `interrupt`。
- `NotifyKey` 是密封类型：`Text(peer, messageId)`、`PairingRequest(peer)`、`Transfer(transferId)`、`File(transferId, index)`；`RequestKind` = `PAIRING` / `FILE_OFFER`。
- `NetworkMonitor.interfaces` 直接复用领域层 `LocalInterface`（不含设计中的 `hasGateway`、`bindHandle`，平台句柄由适配层按接口名维护）。Kotlin 为 `StateFlow<Set<LocalInterface>>`（保持插入顺序），C# 为 `IObservableState<ValueList<LocalInterface>>`。

### C# 侧的做法

- 异步方法：`Task` / `ValueTask` + `CancellationToken`，只有调用方取消时抛 `OperationCanceledException`。文件端口与 Kotlin 一样是同步方法。`TcpConn` 读写用 `Memory<byte>` / `ReadOnlyMemory<byte>`，`ChunkReader` / `TempWriter` 用 `Span`。
- 状态：`LanTransfer.Engine` 命名空间下自带最小实现，不依赖 System.Reactive：`IObservableState<T>` / `ObservableState<T>`（≈ StateFlow：订阅时先收到当前值，相等的值不推送，推送在锁内进行、可重入）、`Subject<T>`（≈ 无重放的 SharedFlow）、`Unit`、扩展方法 `Subscribe(Action)` 与 `FirstAsync(predicate, ct)`。
- 时钟：`IClock` 多一个 `TimeProvider Time`；引擎里的 `Task.Delay`、`CancellationTokenSource` 超时与 `PeriodicTimer` 都必须用它。`FakeClock : TimeProvider, IClock`（自写，不引入 Microsoft.Extensions.TimeProvider.Testing）：`AdvanceBy` 按到期顺序同步触发定时器（含回调中新建的、周期的、到期时间为 0 的立即触发）；与 Kotlin runTest 不同，await 之后的续体可能在线程池上运行，引擎测试应等待可观察结果（`FirstAsync`）。
- 命名差异：`ClipRead.Text.Body`（Kotlin `text`）；`InMemorySourceFiles.EntryOf`（Kotlin `entry`）；`RecordingNotifier.Active` 是按显示顺序的键值对列表；`InMemoryReceiveStorage.FilesIn` 返回键值对列表；`ConnectResult` / `StoreResult` 等无字段情形是无参数 record（按值相等）。
- 内存实现（两端同名）：`InMemoryNetwork(time)` → `Host(name, ifaces, reportUdpVia)` 得到 `InMemoryTransport`（`BlockedTcpPorts`、`BlockedUdpPorts`、`UdpSendFails`、`ConnectBehavior`（Refuse / Unreachable / Hang / BindFail）、`ConnectAttempts`、`ListeningPorts`）；`Connections[i]` 的 `BytesFromClient/Server`、`Blackhole()`（静默断网）、`Reset()`（RST）；系统分配端口从 50000 起。`FakeNetworkMonitor`、`InMemoryDatabase`（`FailWrites(op)`、`FailReads`、`PendingCancels()`）+ `InMemoryHistoryStore` / `InMemoryTaskStore`、`InMemoryTrustStore`、`InMemoryIdentityStore`、`InMemorySettingsStore`、`InMemoryReceiveStorage(profile, clock)`、`InMemorySourceFiles`、`SeededRng`、`RecordingClipboard` / `RecordingFileLocator` / `RecordingNotifier`、`FakeAppLifecycle`；生成器 `FileSize`、`FileContent`、`GeneratedFile(Gen)`、`SourceFileSet`、`InterruptionPoints`、`FailureSchedule`、`BackgroundToggles`。

## 任务 15 记录

### 结构

- `Handshake`（内部）：前导 + NoiseXX，发起方在 msg2 之后调用 `decide(peer)`（自连接 → 扫码指纹 → Device 目标的设备 ID 是否相符 → 可信公钥一致性 → 选择 intent → SESSION 时登记出站尝试）。`ConnReader` 保留握手阶段多读到的字节给读循环。截止时刻用单调时钟毫秒；前导超时报 NoVersionResponse，之后的步骤超时报 Timeout。
- `Connection` 在握手完成后才构造，初始为 Pending。读循环、写循环、计时循环各一个协程 / Task。`sendAndClose(last, reason)` 写出已入队的 P0 和 `last` 后关闭（超过 1 s 强制关闭）；`closeGracefully` 先发 SESSION_CLOSE。
- `SessionManager`：`open(Target, intent?)` → `OpenResult` = `Ok(Session)` / `Pairing(PendingConnection)` / `Failed(OpenFailure)`；`pendingInbound`（PAIR / QR_PAIR 入站连接）；`events`（Opened / Closed）；`inboundFailures`（接收方需要提示的 VersionIncompatible、HandshakeAuthFailed、KeyMismatch，去抖由 UI 做）；`sessions`。`PendingConnection.promote()` 在配对完成后把连接注册为 Session。

### 设计文档未写明、在此确定的约定（两端一致）

- 读循环原子地把连接切换为 Active：发起方收到 SESSION_RESULT(ACCEPTED) 时、接收方收到 PAIR_COMPLETE 时，使对方紧随其后的应用消息不会被当成 Pending 违规。之后信任提交失败时由 PairingService 关闭连接。
- 同时互连：对端 P 发来 SESSION 入站连接 N 时——已注册 S：S 由 P 发起或 P 的 ID 更小 → P 已丢失 S，以 Superseded 关闭 S 并接受 N；否则回复 DUPLICATE 并在 S 上发 1 次 HEARTBEAT 探测。未注册但有到 P 的出站尝试 O：O 已读完 msg2 时按 ID 判定（P 更小则接受 N 并放弃 O，否则 DUPLICATE）；O 尚未读完 msg2 时直接接受 N 并放弃 O（O 的调用方得到 N 的 Session）。因此“双方同时发起时保留发起方 ID 较小者”只在两条连接都已过 msg2 时成立，其余情况两端仍保留同一条连接；Property 42 检查的是一致性（两端是同一条 TCP 连接、所有 open 得到同一 Session、被放弃的连接都已关闭）。
- 发起方收到 DUPLICATE 不算失败：剩余预算内等待保留的连接（同一对端另有出站尝试时等它的结果，否则等注册），预算用尽报 Timeout。
- `intent` 参数只接受 PAIR / QR_PAIR（传 SESSION 等同于 null，自动选择）。Device 目标的地址：地址簿（DiscoveryService）→ TrustStore 的 lastAddress / lastPort → 都没有时 Unreachable；msg2 中的设备 ID 与目标不符也报 Unreachable。
- 扫码：只尝试 classify 为 Local 的候选地址，每个 5 s；遇到 FingerprintMismatch、HandshakeAuthFailed、KeyMismatch、VersionIncompatible、SelfConnection 时不再尝试后面的地址；没有任何 Local 候选时报 NotLocal（某个候选是本机地址时报 SelfConnection）。
- 认证后：两端都按握手中的名称更新可信设备名称（9.6）；只有本机发起时 `recordAddress`（入站连接的对端端口不是监听端口）。存在 Session 时 `setFastPolling(true)`，最后一个关闭后 `false`。
- 心跳：出队时如果最近 5 s 内已发送过其他帧就跳过；每次写出、以及心跳出队（写出或跳过）后都唤醒计时循环重新计算。
- `CloseReason → InterruptReason`：Local(NORMAL / APP_EXIT / BACKGROUND) → APP_EXIT；Local(DUPLICATE / UNPAIRED) 与 PeerClosed(DUPLICATE) → 不影响任务（null）；PeerClosed(其他 / EOF) 与 Superseded → PEER_CLOSED；PendingViolation、ProtocolError → PROTOCOL_ERROR。
- 入站：来源不在本地子网、前导 magic 不符、对方提前关闭 → 静默关闭；对方出示的设备 ID 等于本机 → 静默关闭。

### 测试装置

- Kotlin `src/test/.../engine/SessionTestKit.kt`：`TestDevice`（内存网络上的设备，ID 的 16 字节都是 idByte）、`cipherQuad()`（内存中完成 NoiseXX 得到两对 RecordCipher）、`RawPeer`（只持有密钥，可发送任意消息 / 明文 / 字节）、`RecordingConn`（记录写入时刻与长度，心跳 record 长度 25）、`GatedConn`（每次写入等测试放行）、`connectionUnderTest(...)`。
- C# `LanTransfer.Engine.Tests/SessionTestKit.cs` 同名。C# 的循环跑在线程池上，推进 FakeClock 前必须等连接稳定：`Connection.IsQuiescent(framesReceived)`（内部观测点：写循环空闲、计时循环已按最近一次写出重新挂起、已处理的帧数）与 `FakeClock.NextTimerDueMs`；`Kit.AdvanceToAsync` 每次只推进到下一个定时器。C# 的引擎属性测试用 `[Fact]` + `ForSeedsAsync`（`Pbt.Iterations(100)` 个用例，基准种子取 `LT_PBT_SEED`，未设置时随机，失败信息带种子）。

## 任务 16 记录

- `PairingService`（Kotlin `core/engine/.../engine/PairingService.kt`，C# `LanTransfer.Engine/PairingService.cs`）：一个 actor（Kotlin 协程 / C# Task + Channel）持有 `PairingState`，执行 `PairingReducer` 的 effects。对外：`state`（`PairingUiState`：Idle / Connecting / WaitingForPeer / ShowingCode / Request(id, peer, sas) / Completing，由会话推导）、`notices`（Finished(done, failure) / AutoRejected）、`pair(target, forcePair)`、`cancel()`、`respond(RequestId, accept)`、`pairByQr(text)`、`qrScreen()`（C# 为 `QrScreenAsync` 的 `IAsyncEnumerable`）、`unpair(peer)`。
- 约定：
  - `pair` 用 `SessionManager.open(target, null)`：对方已可信且公钥一致时返回 `AlreadyTrusted(session)`，不出提示；`forcePair = true`（“重新配对”）传 PAIR。连接阶段 Address 目标的 peer 用全零 ID 占位。
  - 每条配对连接一个读取协程：接收方读到 PAIR_COMPLETE 后停止读取；发起方 / 扫描方进入 Saving 时取消读取（在发送 PAIR_COMPLETE 之前），之后的消息留给 Session。断开在读取结束后才报告，保证对方关闭前的最后一条消息（PAIR_CANCEL、PAIR_DECISION）先被处理。
  - 入站配对连接的第一条消息必须是对应请求（PAIR_REQUEST / QR_PAIR_REQUEST），否则关闭；握手后 10 s 内不发请求也关闭。
  - 信任：发起方 / 扫描方记录对方地址与监听端口；接收方 / 显示方不记录地址（入站对端端口不是监听端口）。重新配对整条替换，“自动接收文件”回到关闭（9.9）。
  - 令牌注册表不是线程安全的，所有访问都 `synchronized(tokens)` / `lock (_tokens)`。`qrScreen` 每秒检查：地址集合变化、`needsRefresh`（过期、已用、失败 5 次）或从 NoNetwork 恢复时重新 issue；没有接口或尚未监听时 NoNetwork。
  - `unpair`：删除信任 → `cancelTransfers(peer)`（构造参数，FileTransferEngine 在任务 18 接入）→ `SessionManager.close(peer, UNPAIRED)`。
- 测试：Kotlin `PairingServiceTest` 14 个、C# `PairingServiceTests` 14 个（点选成功与验证码一致、可信设备直连、拒绝与 60 s 冷却、60 s 超时阈值、BUSY、取消、保存失败与暂存信任清理、确认期间断线、连接失败原因、扫码成功与 2 s 内刷新、错误令牌 / 失败 5 次 / 5 min 过期、指纹不符不发令牌、NoNetwork、unpair 2 s 内完成）。C# 推进长时间时用小步推进（每步留出真实时间给定时器续体）。

## 任务 17 记录

- 代码：Kotlin `core/engine/.../engine/TextService.kt`（`TextItem`、`SendCheck`、`TextNotice`、`TextService`、内部扩展 `Clock.recordTime()`）；C# `LanTransfer.Engine/TextService.cs`（同名类型，另有内部 `ClockExtensions.RecordNow()`）。新端口 `OutgoingTextCache` / `IOutgoingTextCache`（`Files.kt` / `Files.cs`，设计 C5.2 未列出）：`write(dirName, fileName, content)` → `WriteResult<SourceRef>`；内存实现 `InMemoryOutgoingTextCache(sources)` 把文件同时登记到 `InMemorySourceFiles`。
- 构造参数比设计草图多：`trust`（记录中的对方名称快照）、`lifecycle`（Background_State）、`textCache`、Kotlin 的 `scope`。对外：`conversation(peer)`、`notices`、`send` / `SendAsync`、`retry`、`onMessage(session, msg)`（分发器调用，TEXT / TEXT_ACK / TEXT_CORRUPT 返回 true）、`restoreOnStartup`、`forget(keys)`、`forgetAll()`、`originalText(peer, id)`（通知“复制”用，先查内存会话再查存储）、`writeTextFile(text)`、`textFileName(now)`。
- 设计未写明、在此确定的约定（两端一致）：
  - 一个 actor 持有 TextReducer 状态与“已显示集合”（`TextKey → 是否已保存`）。会话条目是每个对端一个 StateFlow / ObservableState，只在 actor 中修改。
  - 发送：每个对端一个发送队列（按点击顺序打开 Session、P1 入队）；出队时已过 10 s 截止的不再发出；打开失败只发 `TextNotice.ConnectFailed`（KeyMismatch / NotTrusted 等由界面提示），消息仍在 10 s 时由计时器标为失败；`open` 返回 Pairing（本机已不信任对方）时直接关闭。计时器循环等到单调时钟确实到达截止时刻才发 Tick。`appendText` 失败时仍然发送（E5）。
  - 接收（C1.12）：非法 UTF‑8 只回 TEXT_CORRUPT；已显示且已保存 → 只回 ACK（记录此后被删除也不重新保存）；`appendText` Ok → 先 ACK 再显示、后台时通知、开启时排队自动复制；Duplicate → 只回 ACK；Failed → 提示，若 `findText` 仍能找到记录则按重复处理，否则不回 ACK、未显示过的显示一次并记为“未保存”。已显示集合在 `forgetAll` 时保留。
  - 自动复制：单独一个串行协程 / 任务按收到顺序写剪贴板，失败时 `TextNotice.AutoCopyFailed`（13.9）。
  - 启动恢复把 History_Store 中的文字放进会话（在已有条目之前），再交给 `TextEvent.Restored`（SENDING → FAILED 并写回存储）。
  - .txt：子目录名为 16 字节随机数的 32 位小写十六进制；文件名 `文字_YYYYMMDD_HHMMSS.txt`（本地时间）；前置条件是不含 Unpaired_Surrogate（否则抛出）。任务 18.2 的 TempFileJanitor 清理缓存时需要给该端口补充列出 / 删除方法。
- 两端差异：C# `TextItem.Body`（Kotlin `text`）；C# `Conversation` 返回 `IObservableState<ValueList<TextItem>>`；C# 方法名 `SendAsync`、`RestoreOnStartupAsync`、`ForgetAsync`、`ForgetAllAsync`；`ForgetAsync` 参数为 `IReadOnlySet<TextKey>`。
- 测试装置：Kotlin `src/test/.../engine/TextTestKit.kt`、C# `LanTransfer.Engine.Tests/TextTestKit.cs`：`dispatchTo` / `TextKit.DispatchTo`（测试用分发器，正式的分发在任务 19 的 EngineHost）、`TextDevice`（带全部内存端口）、`TextWorld`（A、B 互信，B 监听）、`messageIdOf`、`rawText`。C# 另有 `TextKit.StepToAsync`（小步推进虚拟时间）、`NextAsync`、`TextWorld.SessionBAAsync`。C# 的 P39 在每次投递后用“分发计数 + 一次 `ForgetAsync(空集)` 的 actor 往返”作为屏障。

## 任务 18 记录（已完成）

- Kotlin 测试（`core/engine/src/test/.../engine/`）：`TransferTestKit.kt`（`XferDevice`、`XferWorld`、`moveTimeTo/By`；`XferDevice.trace` 可输出收到的非数据块消息）、`FileTransferEngineTest`（17 个：主流程、自动接收、拒绝、60 s / 65 s、NO_SPACE / CANNOT_SAVE、1000 / 1001、文件夹与连接失败、双方取消、读取失败、校验失败、断开续传（发送方 / 接收方继续、17.10）、写入失败、E3 未知任务、1 TiB 边界）、`FileTransferRecoveryTest`（11 个：pending_cancel 2 s、已有 Session 立即送达、提交失败 start = size、接收目录固定、启动恢复、Janitor 24 h 边界与每小时、进行中不清理、.txt 缓存清理、abandonAll、abandon、cancelPeer）、`TransferPropertiesTest`（P45、P47、P52 各 100 次）、`ResumePropertyTest`（P46 100 次 + 2 个固定种子回归）。
- 测试中发现并修正：接收方已完成但发送方因断开没收到最后的 FILE_RESULT 时，发送方续传收到的是 TRANSFER_CANCEL(USER) → 改为已完成的接收任务对 RESUME_REQUEST 回复全部 COMPLETED / TERMINAL 的 RESUME_STATE，发送方据此全部 SKIP 并完成。
- 测试写法：内存网络没有延迟，观察“传输进行中”要用足够大的文件（8 MB）并等第一个数据块；精确的中断位置用 `InMemoryReceiveStorage.writeFailure` 回调在指定字节处触发（返回失败 = 写入失败，调用 `cut()` = 断开）。P46 每轮用“中断已触发 + 双方都停下”判断结束，避免把续传前的旧“已中断”状态当成本轮结果。
- 本机工具：同一条命令 stop 后再 start 有时不会真正运行（日志不更新），给 Gradle 命令加一个无关参数（如 `-Pn=2`）使命令不同即可。

- Kotlin 实现：`core/engine/.../engine/TransferTypes.kt`（`TransferTaskView`、`SendOfferResult`、`ResumeResult`、`TransferNotice`、`TransferTimings`）与 `FileTransferEngine.kt`（18.1 + 18.2 的全部功能）。`OutgoingTextCache` 端口补充了 `list()`、`owns(ref)`、`delete(ref)`（内存实现已同步）；C# 侧尚未补充。
- 构造参数：`sessions, storage, sources, store: TaskStore, trust, notifier, settings, lifecycle, textCache, clock, rng, scope, profile: FsProfile, io: CoroutineContext = EmptyCoroutineContext（生产传 Dispatchers.IO）, timings`。设计草图中的 `history` 改为 `TaskStore`（任务与文件行由 TaskStore.save 写入）。
- 对外：`tasks`、`notices`、`unfinishedCount()`、`send`、`accept`、`reject`、`cancel`、`resume`、`restoreOnStartup`、`abandonAll`、`abandon`、`cancelPeer(peer)`（供 PairingService.unpair 的 cancelTransfers）、`onMessage(session, msg)`、`runJanitor()`。
- 关键约定：
  - FILE_END 与 FILE_CHUNK 走同一个容量 2 的 ChunkSource（保证排在数据之后）；读盘协程用代数 `gen` 防止被 stop 的协程再放入数据；窗口 `pos + chunk − acked ≤ 4 MiB`。
  - 接收方：actor 维护“线上当前文件”（`wirePos`、`expected` offset、`startOf`），在入队前检查文件顺序、offset 连续性与 4 MiB 窗口，违反时 `session.connection.close(ProtocolError)`；之前的文件（已结束）的在途数据忽略。FILE_CHUNK / FILE_END / FILE_READ_FAILED 都进写盘协程的 FIFO，写盘结果（ChunkWritten → EndReceived → VerifyDone …）按顺序回到 actor 再交给归约器。FILE_ACK 由写盘协程发出：每 256 KiB、距上次 200 ms 仍有未确认数据、文件结束时。
  - 续传：接收方在回复 RESUME_STATE 之前，先等上一次写盘协程结束，再在 io 上读取每个未结束文件的临时文件计算长度与哈希（读不出按 NONE）；收到 RESUME_PLAN 后新写盘协程接着这些哈希状态继续。发送方收到 RESUME_STATE 时用 `SourceFiles.stat` 与 ResumePlanner 生成计划；发送方读盘时从 0 读起，重算 [0, offset) 的哈希（`checkingPrefix`）。RESTART 的临时文件由新写盘协程在写入前删除（`pendingDeletes`）。续传 10 s 计时到期仍在 RESUMING → ResumeFailed（恢复原中断原因）。
  - E3：未知任务的 FILE_CHUNK / FILE_END / FILE_READ_FAILED / RESUME_* 回复 TRANSFER_CANCEL(UNKNOWN_TRANSFER)，其余忽略；已结束任务收到 RESUME_REQUEST / RESUME_STATE 回复 TRANSFER_CANCEL(USER)；只接受该任务对端的消息。
  - 断开后写盘协程仍完成了提交：直接把文件记为 COMPLETED（不经归约器），避免续传时重复接收。
  - pending_cancel：Session Opened 时 `takePendingCancels` 并发送 TRANSFER_CANCEL(USER)，发不出去时放回；写入 pending_cancel 时若恰好有 Session 立即送达。
  - 通知（只在 Background_State）：AWAITING_USER 时 request，离开时 cancel；接收完成 fileCompleted；文件失败 / 任务中断 problem（status 字符串为键：FileState 名称或 `INTERRUPTED:<原因>`，由 UI / 平台层映射文案）；RUNNING 时每 1 s progress，结束时 cancel。
  - 写库：状态或文件状态、tempRef 变化立即保存，只有进度变化时最多每 1 s 保存。发布：结构变化立即发布，只有进度变化时 250 ms 内合并发布。
- Kotlin 验证：Gradle 全量 `build` 通过（全部模块、`:app` lint、animal-sniffer），`:core:engine:test` 13 个测试类全部通过。
- C# 实现：`LanTransfer.Engine/TransferTypes.cs`、`FileTransferEngine.cs`（同名镜像）；`IOutgoingTextCache` 已补 `List()`、`Owns(ref)`、`Delete(ref)`，`InMemoryOutgoingTextCache` 同步。构造参数没有 `scope` / `io`（actor、读写盘都在线程池上）。对外方法名：`SendAsync`、`Accept`、`Reject`、`Cancel`、`ResumeAsync`、`RestoreOnStartupAsync`、`AbandonAllAsync`、`AbandonAsync`、`CancelPeerAsync`、`RunJanitorAsync`、`OnMessage`；`Tasks`、`Notices`、`UnfinishedCount` 是属性。哈希用 `IncrementalHash`（前缀哈希用 `Clone()`）；计时器的到期时刻在 `StartTimer` 调用时确定（`Task.Run` 晚启动不会让 FakeClock 下的计时偏移），TempFileJanitor 的每小时循环同样按到期时刻推进。
- C# 测试（`LanTransfer.Engine.Tests/`）：`TransferTestKit.cs`（`XferKit`：`Big`、`PauseAt`、`SentId`、`Tid`、`TerminalOrInterrupted`、`Describe`、`ForSeedsAsync`、`InterruptedTransferAsync`；`ReadGate`；`XferDevice`、`XferWorld`，二者都是 `IDisposable`，用例结束时断开全部连接并停止引擎，避免属性测试上百个用例的数据一直被测试取消令牌引用）、`FileTransferEngineTests`（17）、`FileTransferRecoveryTests`（11）、`TransferPropertyTests`（P45、P47、P52）、`ResumePropertyTests`（P46，外加“接收方已完成而发送方没收到最后的 FILE_RESULT”“写入失败后由接收方继续”两个确定用例，代替 Kotlin 的两个回归种子）。
- C# 测试与 Kotlin 的差异：C# 的传输在真实时间里进行，8 MB 的文件也可能在测试动作之前传完。“传输进行中”的用例改用 `ReadGate`：`InMemorySourceFiles.Entry.BeforeRead`（C# 测试夹具新增，每次读取前以当前偏移调用、可阻塞）让发送方读盘停在 `PauseAt`（1 000 000，按 64 KiB 块即 1 048 576 字节）处，测试动作完成后 `Release()`；续传之前必须放行。FakeClock 不推进时：结构变化立即发布，只有进度变化的快照要等 250 ms 虚拟时间——接收方第一个块会让文件进入 TRANSFERRING，所以 `AwaitDataAsync` 不需要推进时钟；FILE_ACK 每 256 KiB 与文件结束时发出，窗口不会卡住。P46 的触发器只在 B 处于 Running 时生效（排除上一轮写盘任务在中断后处理剩余数据时误触发），轮询用真实时间（上限 30 s）。
- C# 验证：全解决方案 297 个测试通过（Engine.Tests 125 个）；`TransferPropertyTests` + `ResumePropertyTests` 在 `LT_PBT_SCALE=5` 下通过（`.dev\dnscale.ps1 5 test --project tests\LanTransfer.Engine.Tests --filter-class ...`）。
- 遗留到任务 19：PairingService 的 `cancelTransfers` 接到 `FileTransferEngine.cancelPeer` / `CancelPeerAsync`；EngineHost 的消息分发（TextService / FileTransferEngine / PairingService）与两个服务的 `restoreOnStartup`；Kotlin 生产环境给 `io` 传 `Dispatchers.IO`。

### 下一步

1. 19.1 / 19.2：DiscoveryService 与 EngineHost（设计 C4.6、C4.7，tasks.md 第 371 行起），然后 19.3（Property 16）与 19.4。动手前先读：领域层 `DeviceList`（DeviceListReducer）、协议层 `Discovery`（公告编解码）、端口 `UdpSocket` / `NetworkMonitor` / `IdentityStore`、`LocalNetworkGuard.acceptsInbound`、`SessionManager.startListening`，以及上面“遗留到任务 19”的三项接线。
2. 每完成一组任务后的验证：Kotlin `.dev\gw.ps1 build`（全部模块，约 1–6 min），C# `.dev\dn.ps1 test --solution LanTransfer.sln`。

## 本机开发环境与辅助脚本

- 辅助脚本放在 `.dev/`（已加入 `.gitignore`，只在本机使用）。**不要用 fs_write 写工作区以外的路径**：此前三次会话中断都发生在写入 `%TEMP%` 之后。
- 终端工具会把命令回显约 10 次，且长命令可能在结束前就返回。做法：命令保持简短，调用 `.dev/*.ps1`；脚本把完整输出写入 `.dev/*.log` 并在末尾写 `exit=<code>`，之后用 read_file 读取日志确认结束。
- 本机执行策略禁止直接运行脚本，统一用 `powershell -NoProfile -ExecutionPolicy Bypass -File .dev\xxx.ps1 ...`（只对该进程生效）。
- `.dev/dn.ps1 <dotnet 参数>`：使用 `%LOCALAPPDATA%\Microsoft\dotnet` 下的 .NET SDK 10.0.401（PATH 上的 dotnet 没有 SDK），日志 `.dev/dn.log`。
- `.dev/gw.ps1 <gradle 参数>`：使用 `%USERPROFILE%\.jdks\jdk-17.0.20.1+1` 运行 `android/gradlew.bat`，日志 `.dev/gw.log`。
- `.dev/testcount.ps1`：汇总 Android JVM 测试结果 XML；`.dev/suites.ps1 [模块]`：列出某模块各测试用例与耗时（写入 `.dev/suites.log`）。
- 构建 / 测试命令不要和文件编辑放在同一批并行调用里，否则构建可能读到编辑前的文件；先编辑，再单独启动构建。
- `.dev/wait.ps1 <日志> [结束标记] [超时秒] [行数]`：等待日志出现结束标记。**必须给日志路径**：曾因漏传路径空转 15 分钟、阻塞终端（现已改为缺参数时立即退出）。终端被阻塞时，可用后台进程按命令行查找并结束卡住的 powershell。
- Android SDK（用户已同意 Google 许可）：`%LOCALAPPDATA%\Android\Sdk`，含 cmdline-tools 23.0（latest）、platform-tools 37.0.1、platforms/android-36、build-tools 35.0.0 与 36.0.0；`android/local.properties`（已在 .gitignore 中）写有 `sdk.dir`。新版 cmdline-tools 的 `sdkmanager` 已改为调用 Android CLI（`cmdline-tools\latest\bin\android.exe`），包名用 `/` 分隔：`android --no-metrics --sdk=<路径> sdk install platforms/android-36`。不要经 `sdkmanager.bat` 传 `platforms;android-36`：批处理会在 `;` 处拆开参数。辅助脚本 `.dev/sdk3.ps1 list|install <包...>`。
- `.dev/*.ps1` 只写 ASCII（含注释）：Windows PowerShell 5.1 把无 BOM 的 UTF‑8 脚本按 GBK 读取，中文注释可能吞掉下一行代码（曾导致 `$log`、`$lp` 为空）。`gw.ps1` 的注释已改为英文。
- 后台进程（control_pwsh_process）按“命令 + 目录”复用：启动时若返回 `isReused: true`，拿到的是上次同名命令的旧进程和旧输出。先 stop 该 terminalId，再重新 start。判断构建结果以 `.dev/*.log` 末尾的 `exit=` 为准。execute_pwsh 的 `cwd` 参数有时不生效（终端停在上次的目录，如 `windows\`），脚本路径用绝对路径 `d:\phone-windows-file\.dev\xxx.ps1` 更稳妥。后台进程的状态显示 running 也可能根本没有启动（日志一直不更新）：用 `.dev\ps.ps1`（列出 java / powershell 进程命令行与 gw.log 修改时间，写入 `.dev\ps.log`）确认，没有对应进程时 stop 后重新 start。`wait.ps1` 只认日志中的 `exit=`，日志还是上一次的内容时会立即返回，启动新构建后先确认日志已被重写。
- 仓库目录还不是 git 仓库（未执行 `git init`）。

## 1.2 Android 工程要点

- Gradle 8.14.5 wrapper（带 distributionSha256Sum）；AGP 8.13.2、Kotlin 2.2.21、KSP 2.2.21-2.0.5。
- compileSdk 36 限制了部分 AndroidX 版本：Compose BOM 2026.08.00 及以后、androidx.core 1.19 及以后要求 compileSdk 37 与 AGP 9.1，因此 Compose BOM 停在 2026.06.01、core-ktx 停在 1.18.0（已在 `libs.versions.toml` 注明）。
- 包名根为 `com.lantransfer`（protocol / domain / engine / app / peercli），applicationId `com.lantransfer.app`。
- `settings.gradle.kts`：找到 Android SDK（`local.properties` 的 `sdk.dir` 或 `ANDROID_HOME`）时才包含 `:app`；`CI=true` 时缺少 SDK 直接失败。
- 根 `build.gradle.kts` 统一配置 JVM 模块：JDK 17 toolchain、`allWarningsAsErrors`、JUnit 5 平台 + Kotest；core 模块启用 `java-test-fixtures` 与 animal-sniffer（Android API 26 签名，只检查 main 源集）。
- 依赖锁定：`gradlew resolveAndLockAll --write-locks`。全部模块（含 `:app`）与 buildscript / settings 的锁文件已生成。`resolveAndLockAll` 只解析依赖图（`incoming.resolutionResult.root`），不选择制品：AGP 的 androidTest 配置包含 `:app` 自身，选择制品时有多个候选变体会失败。
- `org.jetbrains.kotlin:kotlin-stdlib-common` 用 `dependencyLocking.ignoredDependencies` 排除在锁定之外：它在 `:app:debugRuntimeClasspath` 中是否出现取决于解析顺序（写锁时不施加锁约束、不出现；正常构建时出现），锁文件永远对不上，`checkDebugAarMetadata` 报 “not part of the dependency lock state”。2.x 的该制品只是兼容占位，版本由已锁定的 kotlin-stdlib 约束。排除后重写了全部锁文件，`gradlew build :app:assembleDebugAndroidTest`（全部模块，含 `:app` lint）通过。
- 安装 SDK 后首次构建 `:app`（`:app:assembleDebug` 通过）发现 AAR 元数据检查不通过：lifecycle 2.11.x、navigation 2.10.x 要求 compileSdk 37 与 AGP 9.1，已改为 lifecycle 2.10.0、navigation 2.9.8。
- Kotest 6 中 `PropTestConfig.iterations` 标为实验性，`Pbt.iterations` 内用 `@OptIn(ExperimentalKotest::class)`。

## 1.3 Windows 工程要点

- 包版本见 `windows/Directory.Packages.props`；`Directory.Build.props` 开启警告即错误（NU1901–NU1904 除外）。
- 每个项目都有 `packages.lock.json`，CI（`CI=true`）以锁定模式还原。

## 1.4 / 1.5 属性测试辅助

- Kotlin：`core/protocol` testFixtures 中的 `com.lantransfer.testing.Pbt`、`TextCharClass`、`Gens`（文本、孤立代理项、非法 UTF‑8）；`core/domain` testFixtures 中以 `Gens.xxx()` 扩展函数提供文件名、IPv4、接口集合与本地时间生成器。
- C#：`tests/LanTransfer.Testing` 中的 `Pbt`、`PbtPropertyAttribute`（`[PbtProperty(1000)]` = MaxTest × scale + Replay + 注册 Gens）、`TextCharClass`、`Gens`（partial，Gens.cs + DomainGens.cs）及包装类型 `UnicodeText`、`Utf16WithUnpairedSurrogate`、`InvalidUtf8`、`RawFileName`、`Ipv4Sample`、`PrefixLength`、`InterfaceSpec`、`InterfaceSet`。
- 两端字符类标签（22 个，顺序一致）：lf、cr、crlf、tab、consecutive-spaces、edge-whitespace、nbsp、ideographic-space、nul、c0-control、del、c1-control、zwj-sequence、skin-tone-modifier、variation-selector-16、zero-width、bom、combining-mark、supplementary-plane、private-use、unassigned、noncharacter。任务 6.1 生成 text-messages.json 时使用这些标签。
- 非法 UTF‑8 类别：truncated、overlong、surrogate、above-max、invalid-byte、unexpected-continuation、missing-continuation。
- `LT_PBT_SEED`：Kotlin 侧为 64 位整数种子；C# 侧原样作为 FsCheck Replay 字符串（如 `(123,456)`）。

## 后续任务的注意事项

- dotnet 模板和 Visual Studio 可能写入 BOM，新增文件需要检查（`.editorconfig` 要求 UTF‑8 无 BOM）。
- 32.1：Inno Setup 6.3 及以上才能直接读取无 BOM 的 UTF‑8 `.iss` / `.isl`。
- `protocol/test-vectors/**` 和 `*.tsv` 在 `.gitattributes` 中设为 `-text`，按字节存取，不要手工修改其中的换行。
- 2.1、2.2、23.1、1.6 写入实际文件后删除对应目录下的 `.gitkeep`。

## 2026-11-22 阶段汇总（双端现代 UI 重构、Bug 修复与 iOS 预适配）

### 核心 Bug 修复
- **FileTransferEngine（C# & Kotlin）**：`OnResumeOpened` / `StartSend` 调整为先执行归约与状态发布再完成 `cmd.Done`，消除 `Resuming` 过期状态竞态。
- **PairingService（C# & Kotlin）**：`Finished` 通知改为在 reader 停止并更新 `uiState` 为 `Idle` 后发出；监听端口或显示名称变化时触发 QR 刷新；C# 延迟任务捕获 `OperationCanceledException`。
- **平台与界面修复**：Windows `IncomingOfferDialog` 点标题栏关闭自动拒绝 Offer、全应用输入框附加简体中文右键菜单、修复 WPF `.NET 10` `ThemeMode` 冲突与 `WFO0003`；Android 补全 `POST_NOTIFICATIONS` 运行时权限检查与 API 29 `MediaStore.Downloads` `@RequiresApi` 标注。

### 关键产出
- 共享设计与文案：`shared/design-tokens.json`、`shared/ui-text/zh-CN.tsv`、`shared/ui-text/generate.py`。
- Windows 端：`LanTransfer.Platform.Windows`（SQLite / DPAPI / Win32 剪贴板 / Shell 定位 / 防火墙 / 单实例 / 通知）+ `LanTransfer.App`（Fluent Win11 主题、DWM 原生暗色标题栏、托盘与全套自定义中文对话框）+ Inno Setup 6 安装程序（`windows/installer/LanTransfer.iss`、`Languages/ChineseSimplified.isl`，产出 `LanTransfer-Setup-1.0.0-win-x64.exe`）；全解决方案 11 个项目 **0 警告 / 0 错误**，测试 **315/315 通过**，Windows 与 Android 端实机运行良好。
- Android 端：Room M2（导出 `android/app/schemas/.../1.json`）+ Keystore / DataStore + Wi-Fi/热点网络绑定 + MediaStore/SAF + 前台服务 + CameraX/ZXing 扫码 + Material 3 Compose UI；`:core:*:test`、`:app:assembleDebug`、`:app:testDebugUnitTest`、`:app:lintDebug` **全部通过（0 编译警告 / 0 Lint 错误）**。
- iOS 预适配：`DeviceType.IOS(3)`、`FsProfile.IOS`、默认名 `"iOS 设备"`、`announcements.json` 向量、双端 iOS 徽章及 `docs/ios-architecture-and-ui-guide.md`。
