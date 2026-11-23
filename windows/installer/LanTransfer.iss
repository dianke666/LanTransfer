; LanTransfer Windows 安装脚本（Inno Setup 6.3+，UTF-8 无 BOM）
; 设计依据：C7.3、C7.4、任务 32.1（需求 1.2、1.5、1.6、3.5、4.2）

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\LanTransfer"
#endif

#define MyAppName "局域网传输"
#define MyAppExeName "LanTransfer.exe"
#define MyAppPublisher "LanTransfer"

[Setup]
; 固定 AppId：覆盖升级时保留 %LOCALAPPDATA%\LanTransfer 用户数据（需求 4.2）
AppId={{7B6E3E2A-4D2C-4E91-9A62-8C2F1D5E9A34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LanTransfer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 最低支持 Windows 10 版本 1809（Build 17763）且仅限 64 位 x64（需求 1.2、1.6）
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 安装到 Program Files 与配置 Windows 防火墙规则均需要管理员权限（C7.4）
PrivilegesRequired=admin
; 关联单实例互斥体并在升级/卸载时自动关闭正在运行的实例
AppMutex=Local\LanTransfer.SingleInstance
CloseApplications=yes
RestartApplications=no
; 仅简体中文，不弹出语言选择框（需求 1.5、C7.4）
ShowLanguageDialog=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=Output
OutputBaseFilename=LanTransfer-Setup-{#MyAppVersion}-win-x64

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Messages]
; 系统要求提示明确告知最低系统版本与架构（需求 1.6、C7.4）
WinVersionTooLowError=局域网传输需要 Windows 10 版本 1809 及以上的 64 位（x64）系统。
OnlyOnTheseArchitectures=局域网传输需要 Windows 10 版本 1809 及以上的 64 位（x64）系统。

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 按设计 C7.4：只创建开始菜单快捷方式，不添加开机自启
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; 安装完成后自动配置局域网 TCP / UDP 入站允许防火墙规则（需求 3.5、C7.3）
Filename: "{app}\{#MyAppExeName}"; Parameters: "--repair-firewall"; StatusMsg: "正在配置 Windows 防火墙规则..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时删除本程序防火墙规则，保留 %LOCALAPPDATA%\LanTransfer 中的身份、信任列表、记录与接收文件（需求 3.5、C7.4）
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-firewall"; RunOnceId: "LanTransferRemoveFirewall"; Flags: runhidden waituntilterminated
