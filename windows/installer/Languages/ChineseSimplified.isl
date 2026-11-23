; *** Inno Setup 6.5+ 简体中文语言文件（LanTransfer 专用，UTF-8 无 BOM） ***

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=14

[Messages]

; *** 应用标题
SetupAppTitle=安装程序
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1

; *** 通用弹窗标题
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=错误

; *** 启动引导消息
SetupLdrStartupMessage=即将安装 %1。是否继续？
LdrCannotCreateTemp=无法创建临时文件，安装已中止
LdrCannotExecTemp=无法在临时目录中执行文件，安装已中止
HelpTextNote=

; *** 启动错误消息
LastErrorMessage=%1。%n%n错误 %2：%3
SetupFileMissing=安装目录中缺少文件 %1。请修正问题或重新获取安装包。
SetupFileCorrupt=安装文件已损坏，请重新获取安装包。
SetupFileCorruptOrWrongVer=安装文件已损坏，或与当前安装程序版本不兼容，请重新获取安装包。
InvalidParameter=命令行包含无效参数：%n%n%1
SetupAlreadyRunning=安装程序已在运行。
WindowsVersionNotSupported=此程序不支持您计算机上运行的 Windows 版本。
WindowsServicePackRequired=此程序需要 %1 Service Pack %2 或更高版本。
NotOnThisPlatform=此程序无法在 %1 上运行。
OnlyOnThisPlatform=此程序必须在 %1 上运行。
OnlyOnTheseArchitectures=此程序只能安装在适用于以下处理器架构的 Windows 版本上：%n%n%1
WinVersionTooLowError=此程序需要 %1 版本 %2 或更高版本。
WinVersionTooHighError=此程序无法安装在 %1 版本 %2 或更高版本上。
AdminPrivilegesRequired=安装此程序需要管理员权限。
PowerUserPrivilegesRequired=安装此程序需要管理员或高级用户组成员权限。
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请先关闭该程序的所有窗口，然后点击“确定”继续，或点击“取消”退出。
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请先关闭该程序的所有窗口，然后点击“确定”继续，或点击“取消”退出。

; *** 启动安装模式选择
PrivilegesRequiredOverrideTitle=选择安装模式
PrivilegesRequiredOverrideInstruction=选择安装模式
PrivilegesRequiredOverrideText1=可以为所有用户安装 %1（需要管理员权限），或仅为您个人安装。
PrivilegesRequiredOverrideText2=可以仅为您个人安装 %1，或为所有用户安装（需要管理员权限）。
PrivilegesRequiredOverrideAllUsers=为所有用户安装(&A)
PrivilegesRequiredOverrideAllUsersRecommended=为所有用户安装（推荐）(&A)
PrivilegesRequiredOverrideCurrentUser=仅为我个人安装(&M)
PrivilegesRequiredOverrideCurrentUserRecommended=仅为我个人安装（推荐）(&M)

; *** 目录错误
ErrorCreatingDir=安装程序无法创建目录“%1”
ErrorTooManyFilesInDir=由于目录“%1”中的文件过多，无法在其中创建文件

; *** 安装通用提示
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，将不会安装该程序。%n%n您可以稍后再次运行安装程序以完成安装。%n%n确定要退出安装程序吗？
AboutSetupMenuItem=关于安装程序(&A)...
AboutSetupTitle=关于安装程序
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 主页：%n%4
AboutSetupNote=
TranslatorNote=

; *** 按钮
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部为是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部为否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)

; *** 选择语言对话框
SelectLanguageTitle=选择安装语言
SelectLanguageLabel=请选择安装过程中使用的语言。

; *** 向导通用文案
ClickNext=点击“下一步”继续，或点击“取消”退出安装。
BeveledLabel=
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=请在下方列表中选择一个文件夹，然后点击“确定”。
NewFolderName=新建文件夹

; *** 欢迎页
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=本向导将在您的计算机上安装 [name/ver]。%n%n建议在继续前先关闭正在运行的相关程序。

; *** 密码页
WizardPassword=密码
PasswordLabel1=此安装程序受密码保护。
PasswordLabel3=请输入密码，然后点击“下一步”继续。密码区分大小写。
PasswordEditLabel=密码(&P)：
IncorrectPassword=您输入的密码不正确，请重试。

; *** 许可协议页
WizardLicense=许可协议
LicenseLabel=继续前请阅读以下重要信息。
LicenseLabel3=请阅读以下许可协议。您必须接受此协议的条款才能继续安装。
LicenseAccepted=我同意此协议(&A)
LicenseNotAccepted=我不同意此协议(&D)

; *** 信息页
WizardInfoBefore=信息
InfoBeforeLabel=继续前请阅读以下重要信息。
InfoBeforeClickLabel=准备继续安装时，请点击“下一步”。
WizardInfoAfter=信息
InfoAfterLabel=继续前请阅读以下重要信息。
InfoAfterClickLabel=准备继续安装时，请点击“下一步”。

; *** 用户信息页
WizardUserInfo=用户信息
UserInfoDesc=请输入您的信息。
UserInfoName=用户名(&U)：
UserInfoOrg=组织(&O)：
UserInfoSerial=序列号(&S)：
UserInfoNameRequired=您必须输入名称。

; *** 选择安装位置页
WizardSelectDir=选择目标位置
SelectDirDesc=您希望将 [name] 安装在何处？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹中。
SelectDirBrowseLabel=点击“下一步”继续。如需选择其他文件夹，请点击“浏览”。
DiskSpaceGBLabel=至少需要 [gb] GB 可用磁盘空间。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
CannotInstallToNetworkDrive=安装程序无法安装到网络驱动器。
CannotInstallToUNCPath=安装程序无法安装到 UNC 路径。
InvalidPath=您必须输入带盘符的完整路径，例如：%n%nC:\APP%n%n或以下格式的 UNC 路径：%n%n\\server\share
InvalidDrive=所选驱动器或网络共享不存在或无法访问，请重新选择。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB 可用空间，但所选驱动器仅剩 %2 KB 可用。%n%n是否仍要继续？
DirNameTooLong=文件夹名称或路径过长。
InvalidDirName=文件夹名称无效。
BadDirName32=文件夹名称不能包含以下任何字符：%n%n%1
DirExistsTitle=文件夹已存在
DirExists=文件夹：%n%n%1%n%n已存在。是否仍要安装到此文件夹？
DirDoesntExistTitle=文件夹不存在
DirDoesntExist=文件夹：%n%n%1%n%n不存在。是否创建该文件夹？

; *** 选择组件页
WizardSelectComponents=选择组件
SelectComponentsDesc=需要安装哪些组件？
SelectComponentsLabel2=选择要安装的组件，取消勾选不需要的组件，准备好后点击“下一步”。
FullInstallation=完整安装
CompactInstallation=精简安装
CustomInstallation=自定义安装
NoUninstallWarningTitle=组件已存在
NoUninstallWarning=安装程序检测到以下组件已安装在您的计算机上：%n%n%1%n%n取消选择这些组件不会将其卸载。%n%n是否仍要继续？
ComponentSize1=%1 KB
ComponentSize2=%1 MB
ComponentsDiskSpaceGBLabel=当前选择至少需要 [gb] GB 可用磁盘空间。
ComponentsDiskSpaceMBLabel=当前选择至少需要 [mb] MB 可用磁盘空间。

; *** 选择附加任务页
WizardSelectTasks=选择附加任务
SelectTasksDesc=需要执行哪些附加任务？
SelectTasksLabel2=选择在安装 [name] 时希望执行的附加任务，然后点击“下一步”。

; *** 选择开始菜单文件夹页
WizardSelectProgramGroup=选择开始菜单文件夹
SelectStartMenuFolderDesc=应在何处放置程序快捷方式？
SelectStartMenuFolderLabel3=安装程序将在以下开始菜单文件夹中创建程序快捷方式。
SelectStartMenuFolderBrowseLabel=点击“下一步”继续。如需选择其他文件夹，请点击“浏览”。
MustEnterGroupName=您必须输入文件夹名称。
GroupNameTooLong=文件夹名称或路径过长。
InvalidGroupName=文件夹名称无效。
BadGroupName=文件夹名称不能包含以下任何字符：%n%n%1
NoProgramGroupCheck2=不创建开始菜单文件夹(&D)

; *** 准备安装页
WizardReady=准备安装
ReadyLabel1=安装程序现已准备好在您的计算机上安装 [name]。
ReadyLabel2a=点击“安装”开始安装，如需检查或修改设置请点击“上一步”。
ReadyLabel2b=点击“安装”开始安装。
ReadyMemoUserInfo=用户信息：
ReadyMemoDir=目标位置：
ReadyMemoType=安装类型：
ReadyMemoComponents=选定组件：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：

; *** 下载向导页
DownloadingLabel2=正在下载文件...
ButtonStopDownload=停止下载(&S)
StopDownload=确定要停止下载吗？
ErrorDownloadAborted=下载已中止
ErrorDownloadFailed=下载失败：%1 %2
ErrorDownloadSizeFailed=获取文件大小失败：%1 %2
ErrorProgress=无效的进度值：%1 / %2
ErrorFileSize=文件大小无效：预期 %1，实际 %2

; *** 解压向导页
ExtractingLabel=正在解压文件...
ButtonStopExtraction=停止解压(&S)
StopExtraction=确定要停止解压吗？
ErrorExtractionAborted=解压已中止
ErrorExtractionFailed=解压失败：%1

; *** 解压错误详情
ArchiveIncorrectPassword=密码不正确
ArchiveIsCorrupted=压缩包已损坏
ArchiveUnsupportedFormat=不支持的压缩格式

; *** 正在准备安装页
WizardPreparing=正在准备安装
PreparingDesc=安装程序正在准备将 [name] 安装到您的计算机上。
PreviousInstallNotCompleted=此前某项程序的安装或卸载尚未完成，需要重启计算机以完成该操作。%n%n重启后请再次运行安装程序以安装 [name]。
CannotContinue=安装无法继续，请点击“取消”退出。
ApplicationsFound=以下应用程序正在占用本次安装需要更新的文件。建议允许安装程序自动关闭这些应用程序。
ApplicationsFound2=以下应用程序正在占用本次安装需要更新的文件。建议允许安装程序自动关闭这些应用程序，安装完成后将尝试重新启动它们。
CloseApplications=自动关闭这些应用程序(&A)
DontCloseApplications=不关闭这些应用程序(&D)
ErrorCloseApplications=安装程序无法自动关闭所有相关应用程序。建议在继续前手动关闭占用目标文件的程序。
PrepareToInstallNeedsRestart=安装程序必须重启计算机。重启后请再次运行安装程序以完成 [name] 的安装。%n%n是否立即重启？

; *** 正在安装页
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在您的计算机上安装 [name]。

; *** 安装完成页
FinishedHeadingLabel=[name] 安装向导已完成
FinishedLabelNoIcons=安装程序已在您的计算机上完成 [name] 的安装。
FinishedLabel=安装程序已在您的计算机上完成 [name] 的安装。您可以通过开始菜单快捷方式启动应用。
ClickFinish=点击“完成”退出安装程序。
FinishedRestartLabel=必须重启计算机才能完成 [name] 的安装。是否立即重启？
FinishedRestartMessage=必须重启计算机才能完成 [name] 的安装。%n%n是否立即重启？
ShowReadmeCheck=查看自述文件
YesRadio=是，立即重启计算机(&Y)
NoRadio=否，稍后手动重启(&N)
RunEntryExec=运行 %1
RunEntryShellExec=查看 %1

; *** 更换磁盘提示
ChangeDiskTitle=安装程序需要下一张磁盘
SelectDiskLabel2=请插入磁盘 %1 并点击“确定”。%n%n如果磁盘文件位于其他文件夹，请输入正确路径或点击“浏览”。
PathLabel=路径(&P)：
FileNotInDir2=无法在“%2”中找到文件“%1”。请插入正确的磁盘或选择其他文件夹。
SelectDirectoryLabel=请指定下一张磁盘的位置。

; *** 安装阶段消息
SetupAborted=安装未完成。%n%n请修正问题后重新运行安装程序。
AbortRetryIgnoreSelectAction=选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略此错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
RetryCancelSelectAction=选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消

; *** 安装状态提示
StatusClosingApplications=正在关闭应用程序...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在解压文件...
StatusDownloadFiles=正在下载文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateIniEntries=正在创建 INI 配置...
StatusCreateRegistryEntries=正在创建注册表项...
StatusRegisterFiles=正在注册文件...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装配置...
StatusRestartingApplications=正在重新启动应用程序...
StatusRollback=正在回滚更改...

; *** 杂项错误
ErrorInternal2=内部错误：%1
ErrorFunctionFailedNoCode=%1 失败
ErrorFunctionFailed=%1 失败；错误码 %2
ErrorFunctionFailedWithMessage=%1 失败；错误码 %2。%n%3
ErrorExecutingProgram=无法执行文件：%n%1

; *** 注册表错误
ErrorRegOpenKey=打开注册表项出错：%n%1\%2
ErrorRegCreateKey=创建注册表项出错：%n%1\%2
ErrorRegWriteKey=写入注册表项出错：%n%1\%2

; *** INI 错误
ErrorIniEntry=在文件“%1”中创建 INI 项出错。

; *** 文件复制错误
FileAbortRetryIgnoreSkipNotRecommended=跳过此文件（不推荐）(&S)
FileAbortRetryIgnoreIgnoreNotRecommended=忽略错误并继续（不推荐）(&I)
SourceIsCorrupted=源文件已损坏
SourceDoesntExist=源文件“%1”不存在
SourceVerificationFailed=源文件校验失败：%1
VerificationSignatureDoesntExist=签名文件“%1”不存在
VerificationSignatureInvalid=签名文件“%1”无效
VerificationKeyNotFound=签名文件“%1”使用了未知密钥
VerificationFileNameIncorrect=文件名称不正确
VerificationFileTagIncorrect=文件标记不正确
VerificationFileSizeIncorrect=文件大小不正确
VerificationFileHashIncorrect=文件哈希不正确
ExistingFileReadOnly2=现有文件已标记为只读，无法替换。
ExistingFileReadOnlyRetry=移除只读属性并重试(&R)
ExistingFileReadOnlyKeepExisting=保留现有文件(&K)
ErrorReadingExistingDest=读取现有文件时出错：
FileExistsSelectAction=选择操作
FileExists2=文件已存在。
FileExistsOverwriteExisting=覆盖现有文件(&O)
FileExistsKeepExisting=保留现有文件(&K)
FileExistsOverwriteOrKeepAll=对后续冲突执行相同操作(&D)
ExistingFileNewerSelectAction=选择操作
ExistingFileNewer2=现有文件比安装程序尝试安装的文件更新。
ExistingFileNewerOverwriteExisting=覆盖现有文件(&O)
ExistingFileNewerKeepExisting=保留现有文件（推荐）(&K)
ExistingFileNewerOverwriteOrKeepAll=对后续冲突执行相同操作(&D)
ErrorChangingAttr=更改现有文件属性时出错：
ErrorCreatingTemp=在目标目录中创建文件时出错：
ErrorReadingSource=读取源文件时出错：
ErrorCopying=复制文件时出错：
ErrorDownloading=下载文件时出错：
ErrorExtracting=解压文件时出错：
ErrorReplacingExistingFile=替换现有文件时出错：
ErrorRestartReplace=重启替换（RestartReplace）失败：
ErrorRenamingTemp=在目标目录重命名文件时出错：
ErrorRegisterServer=无法注册 DLL/OCX：%1
ErrorRegSvr32Failed=RegSvr32 失败，退出码 %1
ErrorRegisterTypeLib=无法注册类型库：%1

; *** 卸载显示名称标注
UninstallDisplayNameMark=%1 (%2)
UninstallDisplayNameMarks=%1 (%2, %3)
UninstallDisplayNameMark32Bit=32 位
UninstallDisplayNameMark64Bit=64 位
UninstallDisplayNameMarkAllUsers=所有用户
UninstallDisplayNameMarkCurrentUser=当前用户

; *** 安装后错误
ErrorOpeningReadme=打开自述文件时出错。
ErrorRestartingComputer=安装程序无法自动重启计算机，请手动重启。

; *** 卸载程序消息
UninstallNotFound=文件“%1”不存在，无法卸载。
UninstallOpenError=无法打开文件“%1”，无法卸载
UninstallUnsupportedVer=当前卸载程序无法识别卸载日志文件“%1”的格式，无法卸载
UninstallUnknownEntry=卸载日志中存在未知条目（%1）
ConfirmUninstall=确定要从计算机中移除 %1 及其程序文件吗？%n%n（您的本机身份、已配对设备、传输记录及已接收文件将被保留。）
UninstallOnlyOnWin64=此安装只能在 64 位 Windows 上卸载。
OnlyAdminCanUninstall=只有具备管理员权限的用户才能卸载此程序。
UninstallStatusLabel=请稍候，正在从您的计算机中移除 %1。
UninstalledAll=已成功从您的计算机中移除 %1。
UninstalledMost=%1 卸载完成。%n%n部分文件未能移除，如有需要可手动清理。
UninstalledAndNeedsRestart=必须重启计算机才能完成 %1 的卸载。%n%n是否立即重启？
UninstallDataCorrupted=文件“%1”已损坏，无法卸载

; *** 卸载阶段消息
ConfirmDeleteSharedFileTitle=移除共享文件？
ConfirmDeleteSharedFile2=系统提示以下共享文件已不再被任何程序使用，是否将其移除？%n%n如果仍有其他程序依赖此文件，移除后可能导致异常。如不确定请选择“否”，保留文件不会造成影响。
SharedFileNameLabel=文件名：
SharedFileLocationLabel=位置：
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...

; *** 关机阻止原因
ShutdownBlockReasonInstallingApp=正在安装 %1。
ShutdownBlockReasonUninstallingApp=正在卸载 %1。

[CustomMessages]
NameAndVersion=%1 版本 %2
AdditionalIcons=附加快捷方式：
CreateDesktopIcon=创建桌面快捷方式(&D)
CreateQuickLaunchIcon=创建快速启动栏快捷方式(&Q)
ProgramOnTheWeb=%1 网站
UninstallProgram=卸载 %1
LaunchProgram=立即启动 %1
AssocFileExtension=将 %1 与 %2 文件扩展名关联(&A)
AssocingFileExtension=正在将 %1 与 %2 文件扩展名关联...
AutoStartProgramGroupDescription=启动：
AutoStartProgram=开机自动启动 %1
AddonHostProgramNotFound=在所选文件夹中未找到 %1。%n%n是否仍要继续？
