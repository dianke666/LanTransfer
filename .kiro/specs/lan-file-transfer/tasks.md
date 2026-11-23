# Implementation Plan: LAN 文件传输（lan-file-transfer）

## Overview

按设计自下而上实现：仓库脚手架 → 协议规范与测试向量 → 协议层 → 领域层 → 引擎层（基于端口与内存实现）→ 存储适配 → Peer CLI 与互通测试 → 界面文案目录 → Android 平台与界面 → Windows 平台、界面与安装程序 → CI 静态检查与性能测试。Android 端用 Kotlin（core 模块为纯 Kotlin/JVM），Windows 端用 C#（.NET 10 + WPF），两端同名镜像。同一组件在两端都需要时，用一个父任务下的 Kotlin / C# 两个子任务实现。每条 Correctness Property 在应有的平台上各有 1 个属性测试（Kotest / FsCheck），作为可选子任务紧跟在对应实现之后。

## Tasks

- [x] 1. 搭建仓库脚手架与两端构建、测试工具
  - [x] 1.1 创建仓库根目录结构与通用配置
    - 按设计“仓库与模块布局”创建 protocol/、shared/ui-text/、android/、windows/、interop-tests/、.github/workflows/ 骨架
    - 添加 .gitignore（含 *.jks、*.keystore、keystore.properties、构建输出）、.editorconfig（UTF‑8 无 BOM）、.gitattributes（测试向量和 .tsv 不做换行转换）
    - 设计：仓库与模块布局、D14
    - _Requirements: 1.1, 1.2, 24.1_
  - [x] 1.2 建立 Android Gradle 多模块工程
    - settings.gradle.kts、build.gradle.kts、gradle/libs.versions.toml：Kotlin 2.2.x、JDK 17、AGP 8.x；按设计技术栈表一次性精确锁定全部依赖（Compose BOM、coroutines、Room + KSP、DataStore、BouncyCastle、ZXing core、CameraX、Kotest、JUnit 5、Robolectric、Turbine、MockK），开启 dependency locking 并提交锁文件
    - 模块：core/protocol、core/domain、core/engine 为纯 Kotlin/JVM 模块（engine → domain → protocol）；app 为 Android 应用（compileSdk 36、targetSdk 36、minSdk 26，各模块依赖和 Room exportSchema 目录一次配好）；tools/peer-cli 为 JVM 应用
    - core 模块启用 java-test-fixtures、JUnit 5 平台与 Kotest
    - _Requirements: 1.1, 1.7_
  - [x] 1.3 建立 Windows .NET 解决方案
    - global.json 锁定 .NET 10 SDK；Directory.Build.props（C# 14、Nullable、警告即错误）；Directory.Packages.props 中央包管理，精确锁定技术栈表全部包；启用 packages.lock.json
    - 项目：LanTransfer.Protocol / Domain / Engine（net10.0）；LanTransfer.Platform.Windows、LanTransfer.App（net10.0-windows10.0.19041.0，SupportedOSPlatformVersion=10.0.17763.0；App 为 WPF + UseWindowsForms，win-x64 自包含 + ReadyToRun，WindowsPackageType=None、WindowsAppSDKSelfContained=true）；tools/LanTransfer.PeerCli；tests 下各测试项目与共享测试库 LanTransfer.Testing（xUnit v3 + FsCheck 3）
    - _Requirements: 1.2_
  - [x] 1.4 编写 Kotlin 属性测试辅助与共享生成器
    - 在 core 模块 testFixtures 中实现 Pbt.iterations(base)（读取 LT_PBT_SCALE）和 LT_PBT_SEED 种子重放
    - 生成器：偏向需求 12.2–12.4 各字符类的 Unicode 字符串（字符类清单作为常量，供 text-messages.json 的 class 标签比对）、含 Unpaired_Surrogate 的 UTF‑16 字符串、非法 UTF‑8 字节序列、文件名、IPv4 与接口集合；消息模型和事件序列生成器在后续任务中补充
    - 设计：测试策略“属性测试约定”
    - _Requirements: 12.2, 12.3, 12.4_
  - [x] 1.5 编写 C# 属性测试辅助与共享生成器
    - 在 tests/LanTransfer.Testing 中实现与 1.4 同名、分布相近的 Pbt 辅助和 FsCheck Arbitrary（Gens）
    - _Requirements: 12.2, 12.3, 12.4_
  - [x] 1.6 建立 CI 工作流骨架
    - .github/workflows：android-jvm、android-instrumented（模拟器 API 26/29/33/34/36 矩阵）、windows、interop（Windows runner，JDK 17 + .NET 10，占位）、installer（占位）、nightly（LT_PBT_SCALE=10）
    - _Requirements: 1.1, 1.2_

- [x] 2. 编写协议规范与测试向量格式
  - [x] 2.1 编写平台无关协议规范
    - protocol/spec/lan-transfer-protocol.md：按 C1.1–C1.13 写出常量、通用编码规则（大端序、String、TLV、List、规范编码）、Discovery_Announcement、QR_Payload、Preamble、Noise_XX 握手与 prologue、记录层、Frame、C1.9 全部消息类型与字段、验证码派生、Session / 文字 / 文件规则；每个消息类型和字段标注引入版本 1.0
    - protocol/spec/CHANGELOG.md 记录 1.0
    - _Requirements: 22.1, 22.11, 24.1_
  - [x] 2.2 定义测试向量格式并接入两端测试
    - protocol/test-vectors/README.md 说明各 JSON 文件结构（模型字段、期望字节 hex、期望错误类型、字符类标签 class、android / windows 两套期望输出）
    - Gradle 把 protocol/test-vectors 加为 core 模块测试资源目录；MSBuild 以链接方式复制到测试输出目录；两端各实现 JSON 向量加载器
    - _Requirements: 24.2, 24.3_

- [x] 3. 实现协议层基础：TLV、StrictUtf8 与 Frame 编解码
  - [x] 3.1 Kotlin 实现（core/protocol）
    - Bytes16、DeviceId / MessageId / TransferId、DeviceType、ProtocolVersion；TLV 读写（tag 严格递增、未知 tag 跳过、定长校验、List<Struct>、不越界）；StrictUtf8.encode / decode
    - C1.9 全部 ProtocolMessage 数据类与 FileEntry（保留可选字段是否出现）；FrameEncoder（只输出协商版本已定义的字段，超过 MAX_PAYLOAD = 2 MiB 时返回 PayloadTooLarge 且不输出字节）
    - FrameDecoder：7 字节帧头到齐即检查 BadMagic / UnknownType / PayloadTooLarge；载荷按类型解析，失败为 MalformedPayload；出错前先输出已完整的消息，出错后不再接受输入；bufferedBytes ≤ 7 + MAX_PAYLOAD；close() 丢弃残留字节
    - 在 testFixtures 中补充 C1.9 每种消息类型的生成器（FileEntry size 偏向 0、2³²、1 TiB）
    - _Requirements: 12.5, 14.5, 22.1, 22.2, 22.3, 22.4, 22.5, 22.7, 22.11, 22.12, 22.13_
  - [x] 3.2 C# 实现（LanTransfer.Protocol）
    - 与 3.1 同名镜像（PascalCase、ReadOnlySpan<byte>、readonly record struct 结果类型），并在 LanTransfer.Testing 中补充消息生成器
    - _Requirements: 12.5, 14.5, 22.1, 22.2, 22.3, 22.4, 22.5, 22.7, 22.11, 22.12, 22.13_
  - [x] 3.3 编写 Property 1 的属性测试（Kotlin / C#）
    - **Property 1: Frame 编解码往返与规范编码**
    - **Validates: Requirements 22.1, 22.2, 22.8, 22.9, 14.5**
  - [x] 3.4 编写 Property 2 的属性测试（Kotlin / C#）
    - **Property 2: 流切分不变与缓冲上界**
    - **Validates: Requirements 22.3, 22.4, 22.10, 22.13**
  - [x] 3.5 编写 Property 3 的属性测试（Kotlin / C#）
    - **Property 3: 解码错误定位**
    - **Validates: Requirements 22.5, 22.6**
  - [x] 3.6 编写 Property 4 的属性测试（Kotlin / C#）
    - **Property 4: 编码超限**
    - **Validates: Requirements 22.11, 22.12**
  - [x] 3.7 编写 Property 6 的属性测试（Kotlin / C#）
    - **Property 6: 文字无损往返**
    - **Validates: Requirements 12.1, 12.2, 12.3, 12.4, 12.5, 12.9**
  - [x] 3.8 编写 Property 7 的属性测试（Kotlin / C#）
    - **Property 7: 严格 UTF‑8 解码**
    - 参考校验器按 Unicode 标准表 3‑7 编写
    - **Validates: Requirements 12.7**

- [x] 4. 实现发现公告、二维码载荷与前导编解码
  - [x] 4.1 Kotlin 实现 AnnouncementCodec、QrCodec、PreambleCodec
    - AnnouncementCodec：magic "LTDA" + tag 1–6，编码 ≤ 512 字节，按 23.2 全部条件解码失败返回 null
    - QrCodec："LT1:" + RFC 4648 Base32（大写、无填充）；严格校验字符集、长度模 8、末尾比特为 0、1–4 个互不相同的 IP、端口与名称长度；失败返回解析错误且不输出部分结果
    - PreambleCodec："LANT" + major + minor；negotiate 返回 Compatible(major, min(minor)) 或 Incompatible
    - _Requirements: 5.6, 8.3, 23.1, 23.2, 23.3, 23.4, 23.5, 23.6, 23.7, 23.9, 23.10, 24.4, 24.5, 24.6_
  - [x] 4.2 C# 实现 AnnouncementCodec、QrCodec、PreambleCodec
    - 与 4.1 同名镜像
    - _Requirements: 5.6, 8.3, 23.1, 23.2, 23.3, 23.4, 23.5, 23.6, 23.7, 23.9, 23.10, 24.4, 24.5, 24.6_
  - [x] 4.3 编写 Property 5 的属性测试（Kotlin / C#）
    - **Property 5: 未知字段被忽略**
    - **Validates: Requirements 22.7, 23.9, 23.10**
  - [x] 4.4 编写 Property 8 的属性测试（Kotlin / C#）
    - **Property 8: 发现公告往返与长度上限**
    - **Validates: Requirements 5.6, 23.1, 23.3**
  - [x] 4.5 编写 Property 10 的属性测试（Kotlin / C#）
    - **Property 10: 二维码载荷往返**
    - **Validates: Requirements 8.3, 23.4, 23.5, 23.7**
  - [x] 4.6 编写 Property 11 的属性测试（Kotlin / C#）
    - **Property 11: 非法二维码文本被整体拒绝**
    - **Validates: Requirements 8.8, 23.6**
  - [x] 4.7 编写 Property 12 的属性测试（Kotlin / C#）
    - **Property 12: 前导往返与版本协商**
    - **Validates: Requirements 24.4, 24.5, 24.6**

- [x] 5. 实现握手、记录层与配对验证码
  - [x] 5.1 Kotlin 实现 NoiseXX、RecordCipher、VerificationCode、Fingerprint
    - EphemeralKeySource、X25519KeyPair（BouncyCastle X25519）；平台 AES‑GCM、SHA‑256、HMAC‑SHA256
    - NoiseXX：Noise_XX_25519_AESGCM_SHA256，握手消息 len:u16 封装，prologue = "LanTransfer-Noise-v1" ‖ Preamble_I ‖ Preamble_R，msg2 / msg3 携带 HandshakeIdentity（msg3 含 intent），失败抛 HandshakeAuthError，split() 返回双向 RecordCipher
    - RecordCipher / RecordReader：len 17–65535、nonce = 0x00000000 ‖ n:u64、解密失败或计数将溢出时为 IntegrityError，且不输出之后的明文
    - VerificationCode.commitment / sas（C1.10），Fingerprint = SHA‑256(公钥)
    - _Requirements: 7.2, 10.1, 10.2, 10.3, 10.5, 10.6_
  - [x] 5.2 C# 实现 NoiseXX、RecordCipher、VerificationCode、Fingerprint
    - 底层用 BouncyCastle.Cryptography X25519、AesGcm、SHA256、HMACSHA256（CNG），与 5.1 同名镜像
    - _Requirements: 7.2, 10.1, 10.2, 10.3, 10.5, 10.6_
  - [x] 5.3 编写 Property 13 的属性测试（Kotlin / C#）
    - **Property 13: 记录层往返与完整性**
    - **Validates: Requirements 10.1, 10.5**
  - [x] 5.4 编写 Property 14 的属性测试（Kotlin / C#）
    - **Property 14: Noise_XX 相互认证**
    - **Validates: Requirements 10.2, 10.6**
  - [x] 5.5 编写 Property 15 的属性测试（Kotlin / C#）
    - **Property 15: 配对验证码派生**
    - **Validates: Requirements 7.2, 10.3**

- [x] 6. 生成测试向量并完成跨端一致性测试
  - [x] 6.1 编写 Kotlin 向量生成工具并提交首批向量
    - 生成 frames.json（每种消息类型 ≥ 1 个）、frames-invalid.json（每种 DecodeError ≥ 1 个）、text-messages.json（12.2–12.4 每个字符类 ≥ 1 个，带 class 标签）、utf8-invalid.json（期望 TEXT_CORRUPT）、announcements.json 与 qr-payloads.json（32 个字符、含中文和表情的名称；≥ 2 个 IP 的二维码）、preamble.json、noise-xx.json（官方 Noise_XX_25519_AESGCM_SHA256 用例子集 + 固定临时密钥的完整转录）、verification-code.json
    - 工具只在手动调用时运行，CI 不重新生成期望字节
    - _Requirements: 24.2, 24.7_
  - [x] 6.2 Kotlin 向量一致性测试与覆盖检查
    - 有效向量断言 encode(model) == bytes、decode(bytes) == model；无效样例断言期望错误类型
    - 覆盖检查：每种消息类型、每个字符类、每种 DecodeError 与非法 UTF‑8、24.7 所列公告和二维码样例都存在
    - _Requirements: 24.2, 24.3, 24.7, 24.8_
  - [x] 6.3 C# 向量一致性测试与覆盖检查
    - 读取同一份 JSON，断言与 6.2 相同（含 Noise 握手向量）
    - _Requirements: 24.2, 24.3, 24.7, 24.8_

- [x] 7. 检查点 - 协议层
  - 确保全部测试通过，如有疑问请询问用户。

- [x] 8. 实现领域层：文件名处理
  - [x] 8.1 Kotlin 实现 FileNameSanitizer 与 NameCollisionResolver（core/domain）
    - FsProfile（ANDROID 255 个 UTF‑8 字节，WINDOWS 255 个 UTF‑16 码元）；按 C3.1 执行 S0、S1、S2，再循环 L1–L4 至不动点；回退名 file_YYYYMMDD_HHMMSS + ext₀
    - NameCollisionResolver：不存在时返回原名，否则追加最小 " (n)"，超限先截断主干，支持 startAt；WINDOWS 不区分大小写由 exists 回调保证
    - _Requirements: 16.4, 16.5, 16.6, 16.7, 16.8, 16.9, 16.10, 16.11_
  - [x] 8.2 C# 实现 FileNameSanitizer 与 NameCollisionResolver（LanTransfer.Domain）
    - 与 8.1 同名镜像
    - _Requirements: 16.4, 16.5, 16.6, 16.7, 16.8, 16.9, 16.10, 16.11_
  - [x] 8.3 生成文件名测试向量并接入两端
    - 扩展 6.1 的工具输出 filename-sanitizer.json，覆盖 S0–S2、L1–L4 每一步，给出 android / windows 两套期望；两端测试读取断言
    - 包含边界示例：con.txt、Nul.tar.gz、COM1、CON .txt、..、a/../b、report.、255 字节中文名、file_20250101_093005.txt
    - _Requirements: 16.4, 16.5, 16.6, 16.7, 16.8, 16.9_
  - [x] 8.4 编写 Property 17 的属性测试（Kotlin / C#）
    - **Property 17: 文件名处理的输出约束与幂等**
    - **Validates: Requirements 16.4, 16.5, 16.6, 16.7, 16.8, 16.9, 16.10**
  - [x] 8.5 编写 Property 18 的属性测试（Kotlin / C#）
    - **Property 18: 合法名称保留、去除路径、截断保留扩展名**
    - **Validates: Requirements 16.4, 16.5, 16.8**
  - [x] 8.6 编写 Property 19 的属性测试（Kotlin / C#）
    - **Property 19: 重名取最小序号**
    - **Validates: Requirements 16.11**

- [x] 9. 实现领域层：显示名称与地址规则
  - [x] 9.1 Kotlin 实现 DisplayNameRules 与 AddressRules
    - Ipv4、IfaceKind、LocalInterface 类型
    - DisplayNameRules：removeUnpairedSurrogates、trimWhiteSpace（显式 White_Space 表，不用 Character.isWhitespace）、defaultName（移除 → 去空白 → 前 32 码点，空则“Android 设备”/“Windows 电脑”）、validateUserInput（Ok(cleaned, removedAny) / Empty / TooLong / HasControl）、isAcceptableRemote
    - AddressRules：parseIpv4（自写，不触发 DNS）、parsePort（空 → Default(42580)）、isPrivate、classify（Self / Local 取最长前缀、同前缀按 WIFI、ETHERNET、HOTSPOT、OTHER / NotLocal）、acceptsInbound、qrCandidates、qrAddressList（去重，最多 4 个）
    - 按 QR 顺序逐个尝试候选地址、在首个成功处停止的纯函数驱动逻辑（供 SessionManager 的 Target.Qr 使用）
    - _Requirements: 2.1, 2.5, 2.7, 2.9, 4.3, 4.4, 4.5, 4.8, 4.9, 5.5, 6.3, 6.4, 6.9, 8.5, 8.12, 23.2, 23.8_
  - [x] 9.2 C# 实现 DisplayNameRules 与 AddressRules
    - 与 9.1 同名镜像（不用 char.IsWhiteSpace、IPAddress.TryParse）
    - _Requirements: 2.1, 2.5, 2.7, 2.9, 4.3, 4.4, 4.5, 4.8, 4.9, 5.5, 6.3, 6.4, 6.9, 8.5, 8.12, 23.2, 23.8_
  - [x] 9.3 编写 Property 20 的属性测试（Kotlin / C#）
    - **Property 20: 显示名称规则**
    - **Validates: Requirements 4.3, 4.4, 4.5, 4.8, 4.9**
  - [x] 9.4 编写 Property 21 的属性测试（Kotlin / C#）
    - **Property 21: IPv4 与端口解析**
    - **Validates: Requirements 6.3, 6.4**
  - [x] 9.5 编写 Property 22 的属性测试（Kotlin / C#）
    - **Property 22: 本地地址判定**
    - **Validates: Requirements 2.1, 2.5, 2.9, 5.5, 6.9**
  - [x] 9.6 编写 Property 23 的属性测试（Kotlin / C#）
    - **Property 23: 二维码地址列表与候选地址顺序**
    - **Validates: Requirements 2.5, 8.5, 8.12, 23.8**

- [x] 10. 实现领域层：文字规则、显示映射、大小格式化、进度与设备列表
  - [x] 10.1 Kotlin 实现 TextRules、TextDisplayMapper、SizeFormatter、ProgressCalculator、DeviceListReducer
    - TextRules：checkOutgoing（Empty → HasUnpairedSurrogate → TooLong → Ok(utf8)）、replaceUnpaired、lineCount、collapse（20 行 / 1000 码点，不切开 CRLF）、notificationPreview（100 码点 + “…”）
    - DisplayText / TextDisplayMapper：LF、CRLF、单独 CR → "\n"，C0 控制字符和 DEL → Control Pictures，C1 → "▯"，其余原样；originalRange 扩展到完整映射单元
    - SizeFormatter（1024 进制、整数运算截断 1 位小数，Q6）；ProgressCalculator（3 s 滑动窗口、reset、ETA 向上取整、速率 0 时“未知”）
    - DeviceListReducer（Announced / Tick / TrustChanged，忽略本机 ID 和不通过 acceptsInbound 的来源，10 s 过期）
    - _Requirements: 5.2, 5.3, 5.4, 5.5, 5.8, 11.2, 11.3, 11.9, 11.10, 12.6, 12.8, 12.10, 13.3, 13.4, 13.7, 15.1, 18.1, 21.6, 23.2_
  - [x] 10.2 C# 实现 TextRules、TextDisplayMapper、SizeFormatter、ProgressCalculator、DeviceListReducer
    - 与 10.1 同名镜像
    - _Requirements: 5.2, 5.3, 5.4, 5.5, 5.8, 11.2, 11.3, 11.9, 11.10, 12.6, 12.8, 12.10, 13.3, 13.4, 13.7, 15.1, 18.1, 21.6, 23.2_
  - [x] 10.3 编写 Property 24 的属性测试（Kotlin / C#）
    - **Property 24: 发送前检查与代理项替换**
    - **Validates: Requirements 11.2, 11.3, 11.9, 12.6, 12.10, 13.7**
  - [x] 10.4 编写 Property 25 的属性测试（Kotlin / C#）
    - **Property 25: 行数、折叠与通知预览**
    - **Validates: Requirements 11.10, 21.6**
  - [x] 10.5 编写 Property 26 的属性测试（Kotlin / C#）
    - **Property 26: 显示映射与选区还原原文**
    - **Validates: Requirements 12.8, 13.3, 13.4**
  - [x] 10.6 编写 Property 27 的属性测试（Kotlin / C#）
    - **Property 27: 文件大小格式化**
    - 另加示例：0 → 0 B、1023 → 1023 B、1024 → 1.0 KB、1,048,575 → 1023.9 KB
    - **Validates: Requirements 15.1**
  - [x] 10.7 编写 Property 28 的属性测试（Kotlin / C#）
    - **Property 28: 进度计算**
    - **Validates: Requirements 18.1**
  - [x] 10.8 编写 Property 29 的属性测试（Kotlin / C#）
    - **Property 29: 设备列表归约**
    - **Validates: Requirements 5.2, 5.3, 5.4, 5.5, 5.8**
  - [x] 10.9 编写 Property 9 的属性测试（Kotlin / C#）
    - **Property 9: 非法公告被丢弃**
    - 经 acceptsInbound → AnnouncementCodec.decode → DeviceListReducer 管线验证状态与过期计时不变
    - **Validates: Requirements 23.2**

- [x] 11. 实现领域层：实体、状态枚举、配对令牌与传输规则
  - [x] 11.1 Kotlin 实现 M1 实体、M3 枚举与 C3.5 规则
    - M1：DeviceIdentity、TrustedDevice、TextRecord、TransferTask、TransferFile、InterruptInfo、PendingCancel、DirRef（序列化为 default / tree: / path:）、Settings、RecordTime
    - M3：TextStatus、TaskState、InterruptReason、FileState 及固定码值（终态 ≥ 10），配对结果枚举
    - PairingTokenRegistry（16 字节、5 min、失败 < 5、使用中租约、markUsed / release、常数时间比较、needsRefresh）、PendingListRules、OfferPrecheck、ResumePlanner、TempFileSelector
    - _Requirements: 8.6, 8.7, 8.9, 14.1, 14.2, 14.8, 14.9, 15.1, 15.2, 15.5, 15.7, 17.4, 17.5, 17.7, 20.1_
  - [x] 11.2 C# 实现 M1 实体、M3 枚举与 C3.5 规则
    - 与 11.1 同名镜像（record / readonly record struct）
    - _Requirements: 8.6, 8.7, 8.9, 14.1, 14.2, 14.8, 14.9, 15.1, 15.2, 15.5, 15.7, 17.4, 17.5, 17.7, 20.1_
  - [x] 11.3 编写 Property 30 的属性测试（Kotlin / C#）
    - **Property 30: 配对令牌注册表**
    - **Validates: Requirements 8.6, 8.7, 8.9**
  - [x] 11.4 编写 Property 31 的属性测试（Kotlin / C#）
    - **Property 31: 待发送列表规则**
    - **Validates: Requirements 14.1, 14.2, 14.8, 14.9**
  - [x] 11.5 编写 Property 32 的属性测试（Kotlin / C#）
    - **Property 32: File_Offer 预检判定表**
    - **Validates: Requirements 15.1, 15.2, 15.5, 15.7**
  - [x] 11.6 编写 Property 33 的属性测试（Kotlin / C#）
    - **Property 33: 续传计划**
    - **Validates: Requirements 17.4, 17.5**
  - [x] 11.7 编写 Property 34 的属性测试（Kotlin / C#）
    - **Property 34: 过期临时文件选择**
    - **Validates: Requirements 17.7**

- [x] 12. 实现领域层：纯状态归约器
  - [x] 12.1 Kotlin 实现 PairingReducer、TextReducer、TransferReducer
    - Step(state, effects)；effects 包括发送帧、启动 / 取消计时器、写存储、界面提示
    - PairingReducer 覆盖 M3.4（发起方、接收方、扫码；60 s 超时与冷却；BUSY；接收方暂存、收到 PAIR_COMPLETE 才提交信任）
    - TextReducer 覆盖 M3.1；TransferReducer 覆盖 M3.2 / M3.3（逐文件顺序、0 字节文件、WRITE_FAILED 非终态、取消、放弃、启动恢复）
    - _Requirements: 7.4, 7.6, 7.7, 7.8, 7.9, 7.10, 7.12, 8.4, 11.5, 11.6, 11.7, 12.12, 14.6, 14.7, 15.3, 15.4, 15.8, 17.2, 17.3, 17.6, 17.9, 18.2, 18.3, 18.7, 18.9, 20.5, 20.10_
  - [x] 12.2 C# 实现 PairingReducer、TextReducer、TransferReducer
    - 与 12.1 同名镜像
    - _Requirements: 7.4, 7.6, 7.7, 7.8, 7.9, 7.10, 7.12, 8.4, 11.5, 11.6, 11.7, 12.12, 14.6, 14.7, 15.3, 15.4, 15.8, 17.2, 17.3, 17.6, 17.9, 18.2, 18.3, 18.7, 18.9, 20.5, 20.10_
  - [x] 12.3 编写 Property 35 的属性测试（Kotlin / C#）
    - **Property 35: 配对结局与信任写入一致**
    - **Validates: Requirements 7.4, 7.6, 7.7, 7.10, 7.12**
  - [x] 12.4 编写 Property 36 的属性测试（Kotlin / C#）
    - **Property 36: 配对请求自动拒绝**
    - **Validates: Requirements 7.8, 7.9**
  - [x] 12.5 编写 Property 37 的属性测试（Kotlin / C#）
    - **Property 37: 文字发送状态迁移**
    - **Validates: Requirements 11.5, 11.6, 11.7, 12.12**
  - [x] 12.6 编写 Property 38 的属性测试（Kotlin / C#）
    - **Property 38: 传输任务归约不变量**
    - **Validates: Requirements 14.6, 14.7, 17.2, 17.9, 18.3**
  - [x] 12.7 编写状态迁移表驱动单元测试（Kotlin / C#）
    - M3.1–M3.4 中每条迁移至少 1 个示例
    - _Requirements: 7.7, 11.6, 15.4, 17.9, 18.9_

- [x] 13. 检查点 - 领域层
  - 确保全部测试通过，如有疑问请询问用户。

- [x] 14. 实现引擎层端口与内存实现
  - [x] 14.1 Kotlin 端口与内存实现（core/engine）
    - C5 全部端口与结果类型：NetworkMonitor、Transport、TcpConn、TcpListener、UdpEndpoint、ReceiveStorage、SourceFiles、IdentityStore、TrustStore、HistoryStore、TaskStore、SettingsStore、Clipboard、FileLocator、Notifier、AppLifecycle、Clock、Rng
    - testFixtures：InMemoryTransport（成对连接、可配置接口集合、捕获线上字节）、FakeClock（虚拟时间 + 协程调度）、内存版各 Store、记录调用的 Clipboard / Notifier / FileLocator 假实现、引擎事件序列生成器
    - _Requirements: 2.3, 2.10, 13.9, 19.5, 19.6, 20.8, 21.11_
  - [x] 14.2 C# 端口与内存实现（LanTransfer.Engine）
    - 与 14.1 同名镜像，基于 Task + System.Threading.Channels，状态以 IObservable<T> 暴露；内存实现放在 LanTransfer.Testing
    - _Requirements: 2.3, 2.10, 13.9, 19.5, 19.6, 20.8, 21.11_

- [x] 15. 实现引擎层：连接驱动与 SessionManager
  - [x] 15.1 Kotlin 实现 LocalNetworkGuard、Connection、SessionManager
    - LocalNetworkGuard：classify 为 Local 才调用 Transport；NotLocal 在 1 s 内失败且不发包
    - Connection：前导交换（10 s 内未完成 → NoVersionResponse）→ NoiseXX → 身份检查（自连接、可信公钥一致性、扫码指纹，均在 msg3 前完成）→ Pending（白名单 SESSION_RESULT、PAIR_*、QR_PAIR_*、HEARTBEAT、SESSION_CLOSE，其余回复 NOT_TRUSTED 并关闭）→ Active
    - 读循环 RecordReader → FrameDecoder → 分发；单写循环 P0 → P1 → P2（P2 轮询各 ChunkSource，每次 1 块）；心跳 5 s / 15 s；关闭原因映射为 InterruptReason（EOF/RST、接口消失、IntegrityError、DecodeError）
    - SessionManager：Target（Device / Address / Qr）、10 s 共用预算与扫码每地址 5 s、intent 选择、同一对端 open 合并、同时互连按发起方设备 ID 去重（DUPLICATE）、SESSION_RESULT 处理（含 PeerKeyMismatch）、认证后更新可信设备名称与 recordAddress、监听端口 42580 → 42590–42599 → 0、OpenFailure 全集、存在 Session 时 setFastPolling(true)
    - _Requirements: 2.1, 2.5, 2.9, 2.10, 5.7, 5.9, 6.2, 6.5, 6.6, 6.7, 6.9, 7.11, 9.2, 9.4, 9.5, 9.6, 9.8, 10.2, 10.4, 10.5, 10.6, 10.7, 18.5, 18.6, 18.8, 22.6, 24.4, 24.5, 24.6, 24.9, 25.1, 25.2, 25.6_
  - [x] 15.2 C# 实现 LocalNetworkGuard、Connection、SessionManager
    - 与 15.1 同名镜像
    - _Requirements: 2.1, 2.5, 2.9, 2.10, 5.7, 5.9, 6.2, 6.5, 6.6, 6.7, 6.9, 7.11, 9.2, 9.4, 9.5, 9.6, 9.8, 10.2, 10.4, 10.5, 10.6, 10.7, 18.5, 18.6, 18.8, 22.6, 24.4, 24.5, 24.6, 24.9, 25.1, 25.2, 25.6_
  - [x] 15.3 编写 Property 40 的属性测试（Kotlin / C#）
    - **Property 40: Pending 阶段的应用消息被拒**
    - **Validates: Requirements 10.4**
  - [x] 15.4 编写 Property 41 的属性测试（Kotlin / C#）
    - **Property 41: 信任判定表**
    - **Validates: Requirements 6.6, 6.7, 6.9, 9.2, 9.4, 9.5**
  - [x] 15.5 编写 Property 42 的属性测试（Kotlin / C#）
    - **Property 42: 每个对端恰好 1 个 Session**
    - **Validates: Requirements 9.2, 25.2**
  - [x] 15.6 编写 Property 43 的属性测试（Kotlin / C#）
    - **Property 43: 心跳与无响应判定**
    - **Validates: Requirements 18.5, 18.6**
  - [x] 15.7 编写 Property 44 的属性测试（Kotlin / C#）
    - **Property 44: 写循环优先级与公平性**
    - **Validates: Requirements 25.1, 25.5, 25.6**
  - [x] 15.8 编写连接与会话单元测试（Kotlin / C#）
    - E1 每一行 OpenFailure；10 s / 5 s 预算在阈值前 1 ms 与恰好到达时的行为；端口回退；IntegrityError、DecodeError 后不再读取字节；18.8 的 2 s 断开检测
    - _Requirements: 2.5, 6.5, 8.5, 10.5, 18.8, 22.6, 24.9_

- [x] 16. 实现引擎层：PairingService
  - [x] 16.1 Kotlin 实现 PairingService
    - pair（点选与手动连接共用）、cancel、respond、pairByQr、qrScreen（每秒检查 needsRefresh 与本机地址集合，无活动接口输出 NoNetwork）、unpair（删除信任与自动接收设置、调用传输引擎的按对端取消入口、SESSION_CLOSE(UNPAIRED)，2 s 内完成）
    - 执行 PairingReducer 的 effects；自动拒绝在发出 PAIR_NONCE 前判定；扫码令牌校验、markUsed / release；E3 CODE_MISMATCH 与 E5 信任保存失败处理
    - _Requirements: 6.6, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6, 7.7, 7.8, 7.9, 7.10, 7.11, 7.12, 8.1, 8.4, 8.6, 8.7, 8.9, 8.10, 8.12, 8.13, 9.3, 9.9, 10.3, 10.7, 10.8_
  - [x] 16.2 C# 实现 PairingService
    - 与 16.1 同名镜像
    - _Requirements: 6.6, 7.1, 7.2, 7.3, 7.4, 7.5, 7.6, 7.7, 7.8, 7.9, 7.10, 7.11, 7.12, 8.1, 8.4, 8.6, 8.7, 8.9, 8.10, 8.12, 8.13, 9.3, 9.9, 10.3, 10.7, 10.8_
  - [x] 16.3 编写配对单元测试（Kotlin / C#）
    - 60 s 响应超时与冷却期、令牌 5 min、二维码 2 s 内刷新、令牌失效各情形、指纹不符时令牌不上线、unpair 2 s 内完成、连接中断后的信任清理
    - _Requirements: 7.7, 7.8, 7.12, 8.6, 8.7, 8.9, 8.10, 10.8_

- [x] 17. 实现引擎层：TextService
  - [x] 17.1 Kotlin 实现 TextService
    - send：checkOutgoing → 写 History_Store → 需要时打开 Session → P1 入队 TEXT；从点击起计时 10 s（含建立 Session）；retry 使用相同 ID 与文本；迟到 TEXT_ACK → 已送达；TEXT_CORRUPT → 发送失败
    - 接收按 C1.12：严格 UTF‑8 → (发送方, message_id) 去重 → 保存 → TEXT_ACK → 显示、后台通知、自动复制；写入失败时不 ACK 但显示，用进程内“已显示集合”防重复；自动复制串行写入
    - “以 .txt 文件发送”：把 UTF‑8 字节（无 BOM）写入 outgoing-text 缓存目录并返回 SourceRef，由调用方走文件发送流程
    - _Requirements: 9.2, 11.3, 11.4, 11.5, 11.6, 11.7, 11.8, 11.11, 11.12, 11.13, 12.6, 12.7, 12.10, 12.11, 12.12, 13.6, 13.8, 20.8, 21.6, 25.1_
  - [x] 17.2 C# 实现 TextService
    - 与 17.1 同名镜像
    - _Requirements: 9.2, 11.3, 11.4, 11.5, 11.6, 11.7, 11.8, 11.11, 11.12, 11.13, 12.6, 12.7, 12.10, 12.11, 12.12, 13.6, 13.8, 20.8, 21.6, 25.1_
  - [x] 17.3 编写 Property 39 的属性测试（Kotlin / C#）
    - **Property 39: 文字接收恰好一次**
    - **Validates: Requirements 11.4, 11.8, 11.12, 12.7, 13.8, 20.8, 21.6**
  - [x] 17.4 编写文字单元测试（Kotlin / C#）
    - 10 s 超时阈值、1,048,576 / 1,048,577 字节边界、.txt 文件内容逐字节等于 UTF‑8 且无 BOM
    - _Requirements: 11.6, 11.9, 11.13_

- [x] 18. 实现引擎层：FileTransferEngine
  - [x] 18.1 Kotlin 实现收发与续传主流程
    - send（10 s 内无法建立 Session 时不发 Offer、保留待发送列表）、accept / reject、OfferPrecheck、Offer 计时（接收方 60 s，发送方保底 65 s）、等待响应期间断开 → CONNECTION_LOST
    - FileSender：64 KiB 块、容量 2 的 ChunkSource、未确认 ≤ 4 MiB、增量 SHA‑256、FILE_END 后等待 FILE_RESULT、FILE_READ_FAILED、0 字节文件
    - FileReceiver：接受时固定接收目录并在写入前 ensureDirectory；写盘通道容量即窗口；offset 连续性检查；FILE_ACK 每 256 KiB 或 200 ms；flushSync → 校验长度与哈希 → FileNameSanitizer（startedAt）→ commit；CHECKSUM_FAILED、WRITE_FAILED
    - ResumeCoordinator：RESUME_REQUEST / STATE / PLAN、重算 [0, start) 哈希、10 s 预算；进度快照限流 250 ms
    - _Requirements: 14.4, 14.5, 14.6, 14.7, 14.10, 15.1, 15.2, 15.3, 15.4, 15.5, 15.6, 15.7, 15.8, 16.3, 16.9, 16.11, 16.12, 16.14, 16.15, 17.1, 17.2, 17.3, 17.4, 17.5, 17.6, 17.8, 17.10, 18.1, 18.4, 25.3, 25.5, 25.6, 25.7_
  - [x] 18.2 Kotlin 实现取消、放弃、清理与启动恢复
    - cancel（2 s 内停止，接收方 5 s 内删除未完成文件的临时文件）；对已中断任务写入 pending_cancel，PendingCancelDelivery 在 Session Active 后 2 s 内发出；UNKNOWN_TRANSFER 处理
    - abandonAll / abandon 按 C4.5 的状态路由；unfinishedCount 在变化后 1 s 内更新
    - restoreOnStartup（M3.2 启动恢复规则）；TempFileJanitor（启动时与每小时，TempFileSelector，清理 .txt 缓存）
    - 为 PairingService.unpair 提供按对端取消全部任务的入口（TRANSFER_CANCEL(UNPAIRED)，Q7）
    - _Requirements: 10.8, 15.8, 17.7, 17.9, 18.2, 18.3, 18.7, 18.9, 20.5, 20.10, 20.11_
  - [x] 18.3 C# 实现收发与续传主流程
    - 与 18.1 同名镜像
    - _Requirements: 14.4, 14.5, 14.6, 14.7, 14.10, 15.1, 15.2, 15.3, 15.4, 15.5, 15.6, 15.7, 15.8, 16.3, 16.9, 16.11, 16.12, 16.14, 16.15, 17.1, 17.2, 17.3, 17.4, 17.5, 17.6, 17.8, 17.10, 18.1, 18.4, 25.3, 25.5, 25.6, 25.7_
  - [x] 18.4 C# 实现取消、放弃、清理与启动恢复
    - 与 18.2 同名镜像
    - _Requirements: 10.8, 15.8, 17.7, 17.9, 18.2, 18.3, 18.7, 18.9, 20.5, 20.10, 20.11_
  - [x] 18.5 编写 Property 45 的属性测试（Kotlin / C#）
    - **Property 45: 当且仅当校验通过才提交**
    - **Validates: Requirements 16.12, 17.1, 17.2**
  - [x] 18.6 编写 Property 46 的属性测试（Kotlin / C#）
    - **Property 46: 续传等价**
    - **Validates: Requirements 17.3, 17.4, 17.5, 17.6, 17.8, 25.3**
  - [x] 18.7 编写 Property 47 的属性测试（Kotlin / C#）
    - **Property 47: 故障隔离**
    - **Validates: Requirements 25.2, 25.6, 25.7**
  - [x] 18.8 编写 Property 52 的属性测试（Kotlin / C#）
    - **Property 52: 清空记录放弃未完成任务**
    - **Validates: Requirements 18.9, 20.5, 20.10, 20.11**
  - [x] 18.9 编写文件传输单元测试（Kotlin / C#）
    - Offer 60 s / 65 s、取消 1–2 s 与 5 s、pending_cancel 2 s、临时文件 24 h / 1 h 的阈值边界；1000 / 1001 个文件；1 TiB + 1 → INVALID_OFFER；E3、E4 每一行
    - _Requirements: 14.5, 14.8, 15.4, 17.7, 18.2, 18.3, 18.9_

- [x] 19. 实现引擎层：DiscoveryService 与 EngineHost
  - [x] 19.1 Kotlin 实现 DiscoveryService 与 EngineHost
    - DiscoveryService：每个活动接口每 2 s（±0.2 s 抖动）向定向广播地址和组播组 239.255.42.58 各发 1 份公告；新接口出现时立即发送；接收 → acceptsInbound → decode → DeviceListReducer，1 s Tick；UDP 绑定或全部发送失败时为 Unavailable
    - EngineHost：组装全部服务；startNetworking（loadOrCreate 身份，新身份无法保存时不启动网络并报告；先监听后发现，监听全部失败时不发公告）、shutdownNetworking；接线 PairingService.unpair 与传输引擎
    - _Requirements: 3.2, 3.6, 4.1, 4.6, 5.1, 5.2, 5.4, 5.9, 5.10, 21.4, 21.9_
  - [x] 19.2 C# 实现 DiscoveryService 与 EngineHost
    - 与 19.1 同名镜像
    - _Requirements: 3.2, 3.6, 4.1, 4.6, 5.1, 5.2, 5.4, 5.9, 5.10, 21.4, 21.9_
  - [x] 19.3 编写 Property 16 的属性测试（Kotlin / C#）
    - **Property 16: 线路机密性**
    - 在 InMemoryTransport 上运行点选配对、扫码配对、随机文字和随机文件脚本，检查捕获字节
    - **Validates: Requirements 4.7, 10.1, 10.7**
  - [x] 19.4 编写发现与宿主单元测试（Kotlin / C#）
    - 公告周期与 10 s 过期阈值、Unavailable 状态、监听失败不发公告、身份损坏重置与无法保存
    - _Requirements: 4.6, 5.1, 5.4, 5.9, 5.10_

- [x] 20. 检查点 - 引擎层
  - 确保全部测试通过，如有疑问请询问用户。

- [ ] 21. 实现存储适配层
  - [x] 21.1 Android Room 存储（android/app）
    - 按 M2 定义 Room 实体与 DAO：lantransfer.db（WAL）、exportSchema、user_version = 1；schema_meta.next_history_seq 与插入在同一写事务中分配
    - RoomTrustStore、RoomHistoryStore（UNIQUE 冲突即重复消息；UNION ALL 按 recorded_at, seq 排序取 10,000；deleteFileRecord 无剩余文件行时删除任务行；clearAll 单事务）、RoomTaskStore（unfinished、pending_cancel）；persisted / last_write_at 每文件最多每 1 s 写一次
    - 数据库无法打开或版本更高时返回失败、不删库；禁止 fallbackToDestructiveMigration
    - _Requirements: 9.1, 9.3, 9.9, 11.8, 18.9, 20.1, 20.2, 20.3, 20.4, 20.5, 20.7, 20.8_
  - [x] 21.2 Windows SQLite 存储（LanTransfer.Platform.Windows/Storage）
    - Schema.sql、Migrations 目录、SqliteTrustStore / SqliteHistoryStore / SqliteTaskStore（1 个写连接 + 若干读连接），行为与 21.1 一致
    - _Requirements: 9.1, 9.3, 9.9, 11.8, 18.9, 20.1, 20.2, 20.3, 20.4, 20.5, 20.7, 20.8_
  - [ ] 21.3 实现两端表结构比对检查
    - 脚本比对 Room 导出的 schema JSON 与 Schema.sql（表名、列名、类型、可空性、主键、索引，排除 room_master_table），接入 CI
    - _Requirements: 20.1, 20.3_
  - [x] 21.4 编写 Property 48 的属性测试（Kotlin / C#）
    - **Property 48: 传输记录存储**
    - **Validates: Requirements 11.8, 20.1, 20.2, 20.3, 20.4, 20.5, 20.7**
  - [x] 21.5 编写 Property 49 的属性测试（Kotlin / C#）
    - **Property 49: 信任列表存储**
    - **Validates: Requirements 9.1, 9.3, 9.9**
  - [x] 21.6 编写存储版本与异常单元测试（Kotlin / C#）
    - user_version 校验、降级与损坏数据库不被修改、迁移测试脚手架
    - _Requirements: 4.2, 9.1, 20.8_

- [ ] 22. 实现 Peer CLI 与互通测试
  - [ ] 22.1 Kotlin peer-cli（android/tools/peer-cli）
    - JSON Lines 控制接口；使用真实 Protocol / Domain / Engine、JVM 文件系统 ReceiveStorage 与 SourceFiles、内存 Store、记录调用的 UI / 通知 / 剪贴板假实现
    - 地址映射 Transport：虚拟接口 192.168.77.x/24 ↔ 回环 127.0.77.x，入站来源映射回虚拟地址，广播 / 组播映射为单播；支持 --proto 版本参数与注入非法 UTF‑8 的测试模式
    - _Requirements: 1.3, 24.4, 24.5, 24.6_
  - [ ] 22.2 C# PeerCli（windows/tools/LanTransfer.PeerCli）
    - 与 22.1 相同的控制协议与地址映射 Transport
    - _Requirements: 1.3, 24.4, 24.5, 24.6_
  - [ ] 22.3 编写互通测试框架与场景 1–6
    - interop-tests/ 场景运行器（每个场景两个方向运行）；record 级故障注入代理（延迟、限速、断开、比特翻转、重放、重排、注入其他 Session 的 record）；接入 interop 工作流
    - 场景 1–6：点选配对、扫码配对、可信直连 / 解除配对 / 公钥不一致、文字、多文件、> 4 GiB 稀疏文件（仅夜间）
    - _Requirements: 1.3, 7.4, 8.4, 9.4, 9.5, 12.1, 14.5, 14.6_
  - [ ] 22.4 编写互通场景 7–11
    - 断点续传与源文件修改、取消与 pending_cancel、主 / 次版本差异、传输中收发文字与 3 个对端并行、记录层篡改
    - _Requirements: 10.5, 17.4, 17.5, 18.9, 24.5, 24.6, 25.1, 25.2_
  - [ ] 22.5 编写 Property 51 的差分测试
    - **Property 51: Kotlin 与 C# 差分一致**
    - **Validates: Requirements 16.5, 24.3, 24.8**

- [ ] 23. 实现界面文案目录与生成器
  - [x] 23.1 编写 shared/ui-text/zh-CN.tsv
    - 列：key、文案、来源、状态、平台、备注；收录需求中全部引号原文（状态“原文”）、C8.4 初稿表全部条目（“暂定”）、M3.2 状态与中断原因文案、E1–E7 的提示；命名占位符 {peer}、{file}、{count}、{path}
    - _Requirements: 1.5, 2.5, 2.6, 6.5, 7.6, 8.8, 11.9, 12.6, 13.7, 15.3, 19.5, 24.5_
  - [x] 23.2 实现 Android 文案生成器
    - 通过 `shared/ui-text/generate.py` 生成 res/values/strings.xml（转义 '、"、@、? 与 XML 实体，占位符转为 %1$s）和按名称传参的 Kotlin UiText 访问函数；文件头注明自动生成
    - _Requirements: 1.5_
  - [x] 23.3 实现 Windows 文案生成器
    - 通过 `shared/ui-text/generate.py` 生成 Strings.resx（NeutralLanguage=zh-Hans，占位符转为 {0}）和 C# UiText 访问函数
    - _Requirements: 1.5_
  - [ ] 23.4 实现文案一致性 CI 检查
    - 重新生成后与提交内容一致；无未使用 key、key 不重复、占位符与访问函数参数一致
    - 禁止 UI 代码硬编码中文：Lint HardcodedText、detekt 自定义规则（含 CJK 的 Kotlin 字面量）、Roslyn 分析器（C# 字面量）、.xaml 扫描脚本；测试代码除外
    - _Requirements: 1.5_
  - [x] 23.5 实现两端 UI 层结果到文案键的映射表
    - OpenFailure（含 PeerKeyMismatch）、LocateResult、DecodeError、InterruptReason、TaskState（发送方 / 接收方）、FileState、TextStatus、ClipRead / ClipWrite、NameCheck → 文案键；Kotlin 放在 android/app，C# 放在 LanTransfer.App
    - _Requirements: 1.5, 6.5, 9.4, 9.5, 10.6, 18.6, 18.8, 19.3, 19.5, 19.6, 24.5_
  - [x] 23.6 编写文案测试（Kotlin / C#）
    - 维护“key → 需求原文”期望表，断言“原文”条目逐字一致；每个枚举值都映射到存在的 key
    - _Requirements: 1.5_

- [ ] 24. 检查点 - 审阅暂定文案
  - 确保全部测试通过；请用户审阅 zh-CN.tsv 中全部“暂定”条目（C8.4），按反馈只修改文案表，审定后状态改为“已定”；如有疑问请询问用户。

- [ ] 25. 实现 Android 清单基线与网络适配
  - [x] 25.1 配置 AndroidManifest、构建与发布签名
    - 清单：C6.5 权限表全部权限（WRITE_EXTERNAL_STORAGE 设 maxSdkVersion=28）、camera uses-feature required=false、allowBackup=false、dataExtractionRules 排除 cloud-backup 与 device-transfer 全部数据域
    - app/build.gradle.kts：localeFilters 只保留 zh-rCN；release 开启 R8（签名密钥留待正式发布前按本地 keystore.properties 配置）
    - _Requirements: 1.1, 1.5, 1.7, 2.7, 4.1, 4.2_
  - [x] 25.2 实现 AndroidNetworkMonitor
    - Wi‑Fi：registerNetworkCallback（TRANSPORT_WIFI、移除 NET_CAPABILITY_INTERNET，不调用 requestNetwork），从 LinkProperties 取接口与前缀，Network 作为绑定句柄
    - 热点：监听全部网络识别蜂窝 / VPN 接口名，每 2 s 枚举 NetworkInterface，已启用、非回环、带 Private_IPv4_Address 且不属于任何 ConnectivityManager 网络者判为 HOTSPOT（不依赖接口名）；setFastPolling 时 1 s
    - 无 Wi‑Fi 也无热点状态在 3 s 内对外发布
    - _Requirements: 2.6, 2.8, 3.1, 3.6, 18.8_
  - [x] 25.3 实现 AndroidTransport
    - 出站：Wi‑Fi 子网先 wifiNetwork.bindSocket 再绑定本地地址；热点子网绑定热点接口本地地址；任一步失败返回 BindFailed（含 VPN 疑似标记），不经默认网络发送
    - 监听 0.0.0.0:port；UDP 用 MulticastSocket 绑定 0.0.0.0:42581 并在每个接口 joinGroup；SO_SNDBUF 512 KiB；发现运行期间持有 MulticastLock
    - _Requirements: 2.3, 2.4, 2.10, 3.7, 5.1_
  - [ ] 25.4 编写 Android 网络适配测试
    - 用 Robolectric 和抽取出的纯分类函数测试接口分类（排除蜂窝 / VPN、Wi‑Fi 与热点并存）和 BindFailed 路径
    - _Requirements: 2.10, 3.1_

- [ ] 26. 实现 Android 存储、身份、设置与系统服务适配
  - [x] 26.1 实现 MediaStoreReceiveStorage 与 SafReceiveStorage
    - 默认目录：API 29+ 用 MediaStore.Downloads（RELATIVE_PATH = Download/LanTransfer/，IS_PENDING 临时条目，提交时更新 DISPLAY_NAME 并清除 pending）；API 26–28 直接文件 I/O（.lantransfer-tmp/ + 原子提交）
    - SAF：只接受 com.android.externalstorage.documents 目录树，持久授权，.lt-probe 可写性探测，.lt-<tid>-<n>.part 临时文档；可用空间（StatFs，取不到为 null）；按文档 ID 换算完整路径
    - _Requirements: 15.5, 15.7, 16.2, 16.3, 16.11, 16.12, 16.13, 16.14, 16.15, 19.8_
  - [x] 26.2 实现 ContentResolverSourceFiles
    - OpenableColumns / COLUMN_LAST_MODIFIED / getType；file:// 缓存文件直接读取；大小未知的 URI 拒绝加入（Q19）；识别文件夹
    - _Requirements: 14.1, 14.4, 14.7, 14.9, 17.5_
  - [x] 26.3 实现 KeystoreIdentityStore 与 DataStoreSettingsStore
    - identity.bin 按 M4 格式存于 noBackupFilesDir，私钥用 Keystore AES‑256‑GCM 密钥加密，先写临时文件再原子重命名，损坏时返回 ResetCorrupted
    - Preferences DataStore 存 M5 设置项与主题模式；默认显示名称取系统设备名 / Build.MODEL 经 defaultName 处理
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.6, 4.7, 13.8, 16.3, 21.2_
  - [x] 26.4 实现 AndroidClipboard、AndroidFileLocator 与其余基础适配
    - AndroidClipboard（ClipData.newPlainText + 读回校验）；AndroidFileLocator（ACTION_VIEW + FLAG_GRANT_READ_URI_PERMISSION，API 26–28 经 FileProvider，目录 URI 打开，返回 NOT_FOUND / NO_HANDLER / NO_FILE_MANAGER / NO_ACCESS）；在清单中声明不导出的 FileProvider
    - ProcessLifecycleAppLifecycle、SystemClock、SecureRandomRng
    - _Requirements: 13.1, 13.3, 13.5, 13.9, 19.2, 19.3, 19.4, 19.5, 19.6, 19.7, 19.11_
  - [x] 26.5 实现 AndroidNotifier 与 CopyActivity
    - 三个通知渠道（传输进度 LOW、收到内容 HIGH、后台接收 LOW）；API 33+ 申请 POST_NOTIFICATIONS；Notifier.enabled 每次回前台重新计算；NotifyKey → (tag, id)；PendingIntent 使用 FLAG_IMMUTABLE 且只带 ID
    - 透明 CopyActivity（不导出）：按 (peer, message_id) 读原文 → 写剪贴板并读回 → “已复制”（含 NUL 时附加说明）
    - _Requirements: 13.2, 13.10, 21.1, 21.6, 21.7, 21.8, 21.10, 21.11_
  - [ ] 26.6 编写 Android 存储仪器测试
    - 模拟器 API 26 / 29 / 33 / 34 / 36：MediaStore 提交与重名、API 26–28 直接写入、SAF 持久授权与 .part 提交、FileProvider 打开
    - _Requirements: 16.2, 16.11, 16.12, 19.4_
  - [ ] 26.7 编写 Android Robolectric 测试
    - 身份往返与损坏重置、DataStore 设置、通知构建与移除、CopyActivity 流程（当前已含 Room / UiText / QrHelper 的 Robolectric 测试，其余已按用户指示暂缓）
    - _Requirements: 4.6, 13.2, 21.8_

- [ ] 27. 实现 Android 应用宿主、前台服务、分享与二维码
  - [x] 27.1 实现 Application 容器、MainActivity 外壳与前台服务
    - Application 级容器持有 EngineHost 并注入全部适配器；Application、Activity、Service 的 attachBaseContext 固定 zh-CN，通知用同一 Context
    - TransferForegroundService（connectedDevice、PARTIAL_WAKE_LOCK、Wi‑Fi 低延迟锁、START_NOT_STICKY，前台通知取后台接收常驻通知或最早任务的进度通知，每 1 s 更新）；任务开始或开启后台接收时启动、条件消失时停止
    - 后台超过 30 s 且服务未运行时 shutdownNetworking，回前台 startNetworking；启动时 restoreOnStartup；在清单中声明 Application、MainActivity、Service
    - _Requirements: 1.5, 3.6, 5.1, 17.9, 21.1, 21.2, 21.9_
  - [x] 27.2 实现 ShareActivity
    - ACTION_SEND / ACTION_SEND_MULTIPLE（*/*）；EXTRA_STREAM 经 PendingListRules 加入（剔除文件夹），否则 EXTRA_TEXT 原样发送；选择目标设备后以 ClipData + FLAG_GRANT_READ_URI_PERMISSION 转交 MainActivity；在清单中声明导出
    - _Requirements: 12.6, 14.3, 14.9_
  - [x] 27.3 实现二维码扫描与生成组件
    - CameraX Preview + ImageAnalysis（KEEP_ONLY_LATEST）→ Y 平面 → PlanarYUVLuminanceSource → HybridBinarizer → QRCodeReader → QrCodec.decode；解析出错 3 s 防抖；成功后调用 pairByQr
    - ZXing QRCodeWriter 生成位图（纠错 M）；二维码界面设置 FLAG_SECURE；相机权限申请与拒绝处理
    - _Requirements: 8.1, 8.2, 8.4, 8.8, 8.11_
  - [x] 27.4 实现后台运行引导（C6.5）
    - 按 Build.MANUFACTURER / BRAND 识别小米（Xiaomi、Redmi、POCO）；开启后台接收时弹出 1 次（background_guide_shown）
    - 小米三步（电池设置、自启动设置、锁定最近任务说明），其他设备只显示第 1 步；设置中常驻入口
    - _Requirements: 21.1, 21.2_
  - [ ] 27.5 编写 Android 宿主测试
    - 前台服务启停条件、21.9 后台 30 s 关停、分享入口文件 / 文字路由、引导 Intent 回退
    - _Requirements: 14.3, 21.1, 21.9_

- [ ] 28. 实现 Android Compose 界面
  - [x] 28.1 实现导航与主界面
    - Compose Navigation 路由表与 MainActivity.setContent；MainViewModel 与现代 Material 3 发现主页：本机地址卡片、设备列表（名称、类型、IP、信任标识、Android/Windows/iOS 徽章）、状态横幅、浅/深/跟随系统主题切换
    - _Requirements: 2.6, 2.8, 4.6, 5.3, 5.9, 5.10, 6.1, 21.11_
  - [x] 28.2 实现会话界面
    - ConversationScreen；消息正文用 AndroidView(LosslessSelectableTextView) 拦截复制并经 originalRange 还原原文；折叠 / 展开、状态与重试、复制按钮；多行输入框与发送 / 粘贴 / 发送剪贴板 / 发送文件（OpenMultipleDocuments）；超长与无法编码字符对话框；待发送列表（应用级内存）；任务卡片（每文件状态、进度、速率、剩余时间、取消 / 继续）
    - _Requirements: 11.1, 11.2, 11.9, 11.10, 11.11, 12.6, 12.11, 13.1, 13.4, 13.5, 13.6, 13.7, 14.1, 14.6, 17.3, 18.1, 25.4_
  - [x] 28.3 实现配对、二维码与手动连接界面
    - 配对对话框（等待确认 + 取消、SAS、接收方名称 / 类型 / IP 与接受 / 拒绝、结果提示）；qr_show 与 qr_scan 路由（无网络提示、权限说明与手动连接入口）；手动连接界面（按字段报错、保留输入、“正在连接”时禁用按钮）
    - _Requirements: 6.2, 6.4, 6.5, 6.8, 7.1, 7.2, 7.3, 8.2, 8.11, 8.13_
  - [x] 28.4 实现文件接收确认、传输记录与设置界面
    - 接收确认对话框（发送方、文件数、总大小、可滚动列表）；传输记录（时间 yyyy-MM-dd HH:mm、完整路径与复制路径、打开文件 / 所在位置、20.4 / 20.10 删除对话框、清空确认显示未完成任务数）
    - 设置：外观主题切换、改名（4.4、4.5、4.8 行为）、接收目录（ACTION_OPEN_DOCUMENT_TREE）、打开接收目录、自动复制、后台接收、后台运行设置入口、已信任设备（自动接收开关、解除配对确认）
    - _Requirements: 4.4, 4.5, 4.8, 9.3, 9.7, 13.8, 15.1, 15.2, 16.3, 16.13, 19.8, 19.9, 19.10, 20.2, 20.4, 20.5, 20.6, 20.9, 20.10, 20.11, 21.2_
  - [x] 28.5 实现通知导航、横幅与提示呈现
    - 通知点击经 Intent extra 打开对应会话或历史页；Snackbar 轻提示、安全警告模态与 60 s 去抖；“对方已解除配对”+“重新配对”、版本不兼容对话框
    - _Requirements: 9.4, 9.5, 10.6, 21.8, 21.10, 24.5_
  - [ ] 28.6 编写 Compose UI 测试
    - 主要流程的 Compose UI Test 与 Accessibility Test Framework 自动检查（按用户指示暂缓）
    - _Requirements: 1.5, 11.2, 20.9_

- [x] 29. 检查点 - Android 应用
  - `:core:*:test`、`:app:assembleDebug`、`:app:testDebugUnitTest`、`:app:lintDebug` 全部通过（0 编译警告、0 Lint 错误）。

- [x] 30. 实现 Windows 平台适配（LanTransfer.Platform.Windows）
  - [x] 30.1 实现 WindowsNetworkMonitor 与 WindowsTransport
    - NetworkAddressChanged + 2 s（Session 期间 1 s）轮询；选取 Up、非 Loopback / Tunnel、带 Private_IPv4_Address 的接口并按网关、类型、虚拟网卡排序
    - TCP 先 Bind 本地接口地址再 ConnectAsync；监听 ExclusiveAddressUse；UDP 接收用 ReceiveMessageFromAsync 获取接收接口并关闭 SIO_UDP_CONNRESET；每接口 1 个发送套接字（EnableBroadcast、MulticastInterface）；SO_SNDBUF 512 KiB
    - _Requirements: 2.1, 2.9, 3.1, 5.1, 5.5, 6.1, 18.8, 23.8_
  - [x] 30.2 实现 FileSystemReceiveStorage 与 PathSourceFiles
    - 默认目录 SHGetKnownFolderPath(FOLDERID_Downloads)\LanTransfer，接受 Offer 时解析为 path: 固定；隐藏 .lantransfer-tmp\；FileStream SetLength + 追加、Flush(true)；MoveFileEx 不覆盖；不区分大小写 exists；GetDiskFreeSpaceEx；提交前写入 Zone.Identifier（ZoneId=3，非 NTFS 跳过）；OpenFolderDialog 选目录后探测可写
    - PathSourceFiles：FileInfo、FileShare.ReadWrite | Delete、MIME 取 HKCR Content Type → 内置映射 → application/octet-stream
    - _Requirements: 14.4, 14.7, 15.5, 16.1, 16.3, 16.11, 16.12, 16.13, 16.14, 17.5_
  - [x] 30.3 实现 DpapiIdentityStore 与 JsonSettingsStore
    - %LOCALAPPDATA%\LanTransfer\identity.bin（M4 格式，DPAPI CurrentUser，原子写入，损坏 → ResetCorrupted）；settings.json（File.Replace 原子替换，未知键忽略，含主题模式）；默认显示名称取 GetComputerNameEx 回退 MachineName
    - _Requirements: 4.1, 4.2, 4.3, 4.6, 4.7, 13.8, 16.3_

  - [x] 30.4 实现 Win32Clipboard、ShellFileLocator 与电源管理
    - Win32Clipboard：STA 线程 OpenClipboard，被占用时重试最多 1 s；同时写 CF_UNICODETEXT 与带显式长度的私有格式 LanTransfer.UnicodeText；读回选择函数按 C5.4 规则；逐码点比对
    - ShellFileLocator：SHOpenFolderAndSelectItems、AssocQueryString 检查关联后 ShellExecuteEx、打开目录
    - 有进行中任务时 SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)，全部结束后清除
    - _Requirements: 13.3, 13.9, 13.10, 19.1, 19.4, 19.5, 19.6, 19.7, 19.11_
  - [x] 30.5 编写 Property 50 的属性测试（C#）
    - **Property 50: Windows 剪贴板读回**
    - **Validates: Requirements 13.3, 13.10**
  - [x] 30.6 实现 AppNotificationNotifier
    - 先订阅 NotificationInvoked 再 Register()，退出时只 Unregister()；文字通知“复制”、文件完成“打开文件 / 打开所在位置”；Tag / Group 由 NotifyKey 决定，RemoveByTagAndGroupAsync；enabled 取 Setting == Enabled；中文显示名称
    - _Requirements: 1.5, 21.6, 21.7, 21.8, 21.10, 21.11_
  - [x] 30.7 实现 FirewallManager 与 SingleInstance
    - INetFwPolicy2：TCP / UDP 入站允许规则（ApplicationName 为自身 exe、专用 + 公用、RemoteAddresses=LocalSubnet、Grouping=LanTransfer、中文名称）；启动检查（缺失、禁用、阻止规则）；--repair-firewall（删除本组与本程序阻止规则后重建，不接受路径参数）、--remove-firewall；runas 提权等待退出并处理 ERROR_CANCELLED
    - SingleInstance：命名互斥体 Local\LanTransfer.SingleInstance + 命名管道唤起主窗口，修复实例跳过检查
    - _Requirements: 3.4, 3.5, 3.8_
  - [x] 30.8 编写 Windows 平台单元测试
    - 不覆盖重命名与不区分大小写的重名、DPAPI 往返与损坏、settings 原子替换、数据库降级保护与 Property 48/49/50（9/9 通过）
    - _Requirements: 3.8, 4.6, 16.11, 14.4_

- [ ] 31. 实现 Windows 应用（LanTransfer.App）
  - [x] 31.1 实现应用宿主、托盘与生命周期
    - AppController 组装 EngineHost 与全部适配器；启动时设 DefaultThreadCurrentUICulture 与 FrameworkElement.Language 为 zh-CN；app.manifest 声明 longPathAware、csproj 声明 ApplicationHighDpiMode=PerMonitorV2；处理 --repair-firewall / --remove-firewall 命令行；单实例与 Toast 激活路由；启动时 restoreOnStartup
    - NotifyIcon + ContextMenuStrip（打开主窗口、退出），单击恢复窗口；关闭窗口改为隐藏；“退出”有进行中任务时显示确认对话框（标记 APP_EXIT 后 3 s 内退出）；SessionEnding 直接中断并退出；WpfAppLifecycle 判定 Background_State
    - _Requirements: 1.5, 3.8, 17.9, 21.3, 21.4, 21.5_
  - [x] 31.2 实现主窗口与会话视图
    - MainWindow（左侧设备列表、本机地址卡片、浅/深/跟随系统主题与原生暗色标题栏、右侧页面切换、横幅含防火墙警告与“修复防火墙规则”）与 MainViewModel；ConversationView 与 ViewModel：消息只读文本控件绑定 ApplicationCommands.Copy 还原原文与简体中文右键菜单、折叠 / 展开、输入与四个发送按钮、待发送列表、任务卡片；拖放（FileDrop、剔除文件夹）；所有对话框自绘，不用 MessageBox
    - _Requirements: 3.8, 5.3, 6.1, 11.1, 11.10, 13.1, 13.4, 13.5, 13.6, 14.2, 14.9, 18.1, 25.4_
  - [x] 31.3 实现对话框与页面
    - AppDialog、PairingDialog、QrPairingDialog（QRCoder 生成，NearestNeighbor 缩放，无网络提示）、ManualConnectDialog、IncomingOfferDialog（非模态，每个 Offer 一个，关闭即拒绝）、HistoryView（筛选/搜索与删除/清空流程）、SettingsView（主题切换、改名、接收目录、自动复制、已信任设备）；安全警告 60 s 去抖；通知点击路由；AutomationProperties LiveSetting
    - _Requirements: 4.4, 4.5, 4.8, 6.4, 6.8, 7.1, 7.2, 7.3, 8.1, 8.13, 9.7, 15.1, 19.8, 20.4, 20.5, 20.6, 20.9, 20.10, 20.11, 21.8_
  - [ ] 31.4 编写 Windows ViewModel 单元测试
    - 改名、删除与清空对话框路由、退出确认、拖放过滤（按用户指示暂缓）
    - _Requirements: 4.8, 14.9, 20.10, 21.5_

- [x] 32. 实现 Windows 安装程序
  - [x] 32.1 编写 Inno Setup 脚本并接入 installer 工作流
    - installer/LanTransfer.iss：MinVersion=10.0.17763、ArchitecturesAllowed / ArchitecturesInstallIn64BitMode=x64compatible、PrivilegesRequired=admin、固定 AppId、AppMutex、CloseApplications=yes、只建开始菜单快捷方式；[Run] 执行 --repair-firewall，[UninstallRun] 执行 --remove-firewall（RunOnceId）；卸载保留 %LOCALAPPDATA%\LanTransfer
    - installer/Languages/ChineseSimplified.isl、ShowLanguageDialog=no、[Messages] 改写 WinVersionTooLowError 与 OnlyOnTheseArchitectures；工作流执行自包含发布并用 ISCC 编译
    - _Requirements: 1.2, 1.5, 1.6, 3.5, 4.2_

- [x] 33. 检查点 - Windows 应用与安装程序
  - 全解决方案 0 警告 / 0 错误，315 项测试全部通过；Release 自包含发布与 Inno Setup 6 安装包编译成功，且真机启动验证通过。

- [ ] 34. 实现 CI 静态检查
  - [ ] 34.1 实现依赖白名单检查
    - 把 Gradle 解析出的依赖图与 NuGet packages.lock.json 和仓库中的白名单比对；禁止 HTTP 客户端、统计 / 崩溃上报、广告 SDK、Google Play 服务、ML Kit；检查锁文件存在且无版本范围
    - _Requirements: 2.7_
  - [ ] 34.2 实现禁用 API 检查
    - detekt ForbiddenMethodCall / ForbiddenImport 与 BannedApiAnalyzers（BannedSymbols.txt）：域名解析与互联网 API（InetAddress.getByName、HttpURLConnection、DnsResolver、System.Net.Dns、HttpClient、WebClient 等）、领域层禁用的 IPAddress.Parse / TryParse、Character.isWhitespace、char.IsWhiteSpace、Windows 端 MessageBox；Transport 实现以外禁止创建套接字
    - _Requirements: 2.7, 4.3, 6.4_
  - [ ] 34.3 实现清单、API 级别与本地化资源检查
    - 合并后的 AndroidManifest：权限集合与 C6.5 一致、只导出 MainActivity 和 ShareActivity、前台服务类型 connectedDevice、allowBackup=false 且有 dataExtractionRules
    - core 模块 animal-sniffer + Android API 26 签名；不存在 values-xx 目录；localeFilters 与 SatelliteResourceLanguages 符合 C8.3
    - _Requirements: 1.5, 1.7, 2.7, 4.1_
  - [ ] 34.4 实现属性测试标注检查
    - 扫描两端测试源码，检查 Property 1–52 在应有平台上恰好各有 1 个带两行标注的测试，标题与 design.md 一致（Property 50 仅 C#，Property 51 仅 interop）
    - _Requirements: 24.3_

- [ ] 35. 实现性能与资源测试
  - [ ] 35.1 编写引擎与互通性能基准
    - 进度推送间隔 ≤ 500 ms；传输 2 GiB 期间每 5 s 发 64 KiB 文字，送达 ≤ 2 s（代理加 50 ms 延迟）；3 个对端并行任务任意 5 s 有进展；100 MiB 与 5 GiB 文件每任务内存增量 ≤ 16 MiB；接入 nightly 工作流
    - _Requirements: 18.1, 25.1, 25.2, 25.3_
  - [ ] 35.2 编写传输记录加载基准
    - 预置 10,000 条记录（含若干 1 MiB 文字），Room 与 SQLite 的 list() 及界面加载 ≤ 2 s
    - _Requirements: 20.2_
  - [ ] 35.3 编写 UI 响应基准
    - Android Macrobenchmark + JankStats 模块；Windows UI Automation 计时脚本；3 个任务进行中时点击、切换、滚动、输入的可见反馈 ≤ 200 ms
    - _Requirements: 25.4_

- [ ] 36. 最终检查点
  - 确保全部测试通过，如有疑问请询问用户。

## Notes

- 标 `*` 的子任务为可选的测试任务，可在快速 MVP 中跳过；顶层任务和实现子任务都不可跳过
- 属性测试遵循设计“属性测试约定”：每条属性在应有平台上恰好 1 个测试，用 Pbt.iterations(base) 计算迭代次数（纯函数属性默认 1,000 次，其余 100 次），失败时可用 LT_PBT_SEED 重放，并在测试上方写两行标注（Feature / Validates）
- 测试向量只有 protocol/test-vectors 一份，两端直接读取；修改向量或协议时两端测试须同时通过，并更新 protocol/spec/CHANGELOG.md
- 真机矩阵（Pixel、小米）、VM 上的防火墙 / 安装程序 / UAC 测试、无障碍人工评审、安全评审和安装程序文案审校属于手动测试，不在本任务列表中
- 每个任务都引用具体需求条款；设计章节号（C1–C8、M1–M5、E1–E8）用于定位实现细节

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2", "1.3"] },
    { "id": 2, "tasks": ["1.4", "1.5", "2.1"] },
    { "id": 3, "tasks": ["1.6", "2.2"] },
    { "id": 4, "tasks": ["3.1", "3.2"] },
    { "id": 5, "tasks": ["3.3", "3.4", "3.5", "3.6", "3.7", "3.8", "4.1", "4.2"] },
    { "id": 6, "tasks": ["4.3", "4.4", "4.5", "4.6", "4.7", "5.1", "5.2"] },
    { "id": 7, "tasks": ["5.3", "5.4", "5.5", "6.1"] },
    { "id": 8, "tasks": ["6.2", "6.3"] },
    { "id": 9, "tasks": ["8.1", "8.2", "9.1", "9.2"] },
    { "id": 10, "tasks": ["8.3", "8.4", "8.5", "8.6", "9.3", "9.4", "9.5", "9.6", "10.1", "10.2"] },
    { "id": 11, "tasks": ["10.3", "10.4", "10.5", "10.6", "10.7", "10.8", "10.9", "11.1", "11.2"] },
    { "id": 12, "tasks": ["11.3", "11.4", "11.5", "11.6", "11.7", "12.1", "12.2"] },
    { "id": 13, "tasks": ["12.3", "12.4", "12.5", "12.6", "12.7", "14.1", "14.2"] },
    { "id": 14, "tasks": ["15.1", "15.2"] },
    { "id": 15, "tasks": ["15.3", "15.4", "15.5", "15.6", "15.7", "15.8", "16.1", "16.2"] },
    { "id": 16, "tasks": ["16.3", "17.1", "17.2"] },
    { "id": 17, "tasks": ["17.3", "17.4", "18.1", "18.3"] },
    { "id": 18, "tasks": ["18.2", "18.4"] },
    { "id": 19, "tasks": ["18.5", "18.6", "18.7", "18.8", "18.9", "19.1", "19.2"] },
    { "id": 20, "tasks": ["19.3", "19.4", "21.1", "21.2", "23.1"] },
    { "id": 21, "tasks": ["21.3", "21.4", "21.5", "21.6", "22.1", "22.2", "23.2", "23.3"] },
    { "id": 22, "tasks": ["22.3", "23.4", "23.5"] },
    { "id": 23, "tasks": ["22.4", "22.5", "23.6"] },
    { "id": 24, "tasks": ["25.1", "30.1", "30.2", "30.3"] },
    { "id": 25, "tasks": ["25.2", "25.3", "26.1", "26.2", "26.3", "30.4", "30.6", "30.7"] },
    { "id": 26, "tasks": ["25.4", "26.4", "30.5", "30.8", "31.1"] },
    { "id": 27, "tasks": ["26.5", "26.6", "31.2"] },
    { "id": 28, "tasks": ["26.7", "27.1", "31.3"] },
    { "id": 29, "tasks": ["27.2", "27.3", "27.4", "31.4", "32.1"] },
    { "id": 30, "tasks": ["27.5", "28.1"] },
    { "id": 31, "tasks": ["28.2", "28.3", "28.4"] },
    { "id": 32, "tasks": ["28.5"] },
    { "id": 33, "tasks": ["28.6"] },
    { "id": 34, "tasks": ["34.1", "34.3", "35.1", "35.2"] },
    { "id": 35, "tasks": ["34.2", "34.4", "35.3"] }
  ]
}
```
