# Windows 端

.NET 10 + WPF 解决方案，win-x64 自包含发布，最低支持 Windows 10 版本 1809（含 Windows 11）。

| 路径 | 目标框架 | 内容 |
|---|---|---|
| `src/LanTransfer.Protocol/` | `net10.0` | 与 Android `core/protocol` 对应（含 `DeviceType.Ios`） |
| `src/LanTransfer.Domain/` | `net10.0` | 与 Android `core/domain` 对应（含 `FsProfile.Ios`） |
| `src/LanTransfer.Engine/` | `net10.0` | 与 Android `core/engine` 对应（含 `DiscoveryService`、`EngineHost`） |
| `src/LanTransfer.Platform.Windows/` | `net10.0-windows10.0.19041.0` | SQLite M2 存储、DPAPI 身份、JSON 设置、局域网传输、Win32 无损剪贴板、Shell 定位、防火墙管理、单实例/防休眠与 Windows App SDK 通知 |
| `src/LanTransfer.App/` | `net10.0-windows10.0.19041.0` | Fluent Win11 现代 WPF 应用（浅/深/跟随系统主题、沉浸式暗色标题栏、托盘、自定义中文对话框与无损选区复制） |
| `tools/LanTransfer.PeerCli/` | `net10.0` | 命令行对端骨架（供后续跨端自动化互通测试使用） |
| `tests/` | — | Protocol (59) / Domain (111) / Engine (136) / Platform.Windows (9) 共 315 项测试与共享测试库 `LanTransfer.Testing` |
| `installer/` | — | Inno Setup 6 安装脚本（`LanTransfer.iss`）与完整简体中文语言文件（`Languages/ChineseSimplified.isl`） |

各组件与 Kotlin 侧同名镜像（C# 使用 PascalCase）。

## 界面与平台特性

- **现代 Fluent 主题**：依据 `shared/design-tokens.json` 构建 `Themes/LightTheme.xaml`、`DarkTheme.xaml`、`Controls.xaml` 与 `ThemeManager`，支持浅色 / 深色 / 跟随系统实时切换并经 DWM 同步 Windows 原生暗色标题栏。
- **全中文与无损交互**：强制 `zh-CN` 区域文化，全部弹窗使用自定义 `AppDialog` / `PairingDialog` / `QrPairingDialog` / `ManualConnectDialog` / `IncomingOfferDialog`（不使用系统 `MessageBox`），全部输入框内置简体中文右键菜单，消息气泡选区复制经 `TextDisplayMapper` 精确还原原文（含 NUL 私有剪贴板格式）。
- **桌面常驻与系统服务**：关闭主窗口最小化至系统托盘继续监听、退出前检测进行中传输确认、支持 `--repair-firewall` / `--remove-firewall` 提权维护防火墙入站规则。

## 构建与测试

需要 `global.json` 指定的 .NET 10 SDK（同一功能段内允许更高补丁版本）。在本目录执行：

```powershell
dotnet restore LanTransfer.sln
dotnet build LanTransfer.sln
dotnet test --solution LanTransfer.sln
dotnet publish src/LanTransfer.App -c Release -o publish/LanTransfer   # win-x64 自包含 + ReadyToRun
iscc installer/LanTransfer.iss                                       # 生成安装包 installer/Output/LanTransfer-Setup-1.0.0-win-x64.exe
```

- Debug 可执行文件输出：`src/LanTransfer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/LanTransfer.exe`
- Windows 安装包输出：`installer/Output/LanTransfer-Setup-1.0.0-win-x64.exe`（仅支持 Windows 10 1809+ 64 位系统；安装时自动配置局域网防火墙入站规则，卸载时移除防火墙规则并保留 `%LOCALAPPDATA%\LanTransfer` 数据与接收文件）
- 测试使用 xUnit v3 + FsCheck 3，经 Microsoft Testing Platform 运行（`global.json` 的 `test.runner`）。
- 包版本只在 `Directory.Packages.props` 中声明；各项目的 `packages.lock.json` 需要提交。CI（`CI=true`）以锁定模式还原，锁文件与项目不一致时还原失败。修改依赖后在本地运行 `dotnet restore LanTransfer.sln --force-evaluate` 更新锁文件。
- `Directory.Build.props` 开启 C# 14、可空引用类型和“警告即错误”；只有 NuGet 漏洞审计警告（NU1901–NU1904）不升级为错误。
