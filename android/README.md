# Android 端

Kotlin + Jetpack Compose (Material 3) + Room，Gradle（Kotlin DSL）多模块工程。`compileSdk 36`、`targetSdk 36`、`minSdk 26`。

| 模块 | 类型 | 内容 |
|---|---|---|
| `core/protocol/` | Kotlin/JVM | TLV、帧、发现公告（含 `DeviceType.IOS`）、二维码、Noise XX、记录层、验证码 |
| `core/domain/` | Kotlin/JVM | 文件名清洗（含 `FsProfile.IOS`）、显示名与地址规则、无损文本显示映射、纯状态归约器 |
| `core/engine/` | Kotlin/JVM + coroutines | 会话、发现、配对、文字、文件传输引擎与宿主 `EngineHost`（只依赖端口） |
| `app/` | Android 应用 | Room M2 存储（导出 schema 至 `app/schemas/`）、Keystore/DataStore、Wi-Fi/热点传输、MediaStore/SAF、前台服务、系统分享页、CameraX+ZXing 扫码与 Material 3 Compose 界面 |
| `tools/vector-gen/` | JVM 工具 | 生成 `protocol/test-vectors/` 协议向量与 `manifest.json` |
| `tools/peer-cli/` | JVM 应用 | 命令行对端骨架（供后续跨端自动化互通测试使用） |

依赖方向：`engine → domain → protocol`；`app` 与 `tools/peer-cli` 依赖三个 core 模块。core 模块不依赖任何 Android API，只使用 API 26 上可用的 Java API。

## 界面与平台特性

- **视觉与主题**：基于 `shared/design-tokens.json` 的现代 Material 3 卡片流界面，支持 **跟随系统 / 浅色 / 深色** 切换、自定义统一矢量图标（含 Android / Windows / iOS 徽章），且强制简体中文区域（`LocaleHelper` + `localeFilters = ["zh-rCN"]`）。
- **无损文字与大文件**：文字气泡内嵌可选 `TextView` 拦截复制并经 `TextDisplayMapper.originalRange` 精确还原原文；默认接收目录在 API 29+ 使用 `MediaStore.Downloads`（`IS_PENDING`），自定义目录支持 SAF 持久授权。
- **后台与系统集成**：`TransferForegroundService`（`connectedDevice`）+ Wi-Fi 锁 / 唤醒锁、通知栏直接复制中转 `CopyActivity`、系统分享目标页 `ShareActivity`、`FLAG_SECURE` 二维码页与小米/通用后台运行引导。

## 构建与检查

需要 JDK 17 与 Android SDK（在 `android/local.properties` 配置 `sdk.dir`）：

```powershell
./gradlew :core:protocol:test :core:domain:test :core:engine:test :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --offline
```

- Debug APK 输出：`app/build/outputs/apk/debug/app-debug.apk`
- `allWarningsAsErrors = true`；依赖版本统一锁定在 `gradle/libs.versions.toml` 与各模块 `gradle.lockfile`。
- release 签名配置从环境变量或本地 `keystore.properties` 读取，该文件和 keystore 不得提交到仓库。
