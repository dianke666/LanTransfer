# Requirements Document

## Introduction

本系统（暂定名 LanTransfer）由一个 Android 应用和一个 Windows 桌面客户端组成，用于在同一本地网络内的设备之间互相传输文件和简短文字。支持的使用环境有两种：设备连接到同一 Wi‑Fi 局域网；或由一台 Android 手机开启移动热点，另一台设备（Windows 电脑或 Android 手机）连接该热点。全部传输数据只在本地网络内点对点传输，不经过互联网或云端中转，因此不消耗移动数据流量。

系统提供简易配对（自动发现后点选确认、扫描二维码、手动输入地址）、无损文字传输（换行符、制表符、表情符号、控制字符等特殊字符完整保留）、复制粘贴，以及一键打开文件或文件所在位置等功能。

本需求文档是下一阶段架构设计的输入。技术栈、协议细节（端口号、帧格式、加密算法、校验算法等）在设计阶段确定。

**范围说明：**

- 目标传输组合：Android ↔ Android、Android ↔ Windows。Windows ↔ Windows 不是本期目标场景，但协议设计不排斥该组合。
- 本期不包含：文件夹整体传输、跨网络（经互联网）传输、iOS/macOS 客户端、IPv6 网络。
- 说明：电脑连接手机热点时，电脑上其他程序（如系统更新）可能经由热点消耗手机流量，这不受本系统控制。本系统支持在热点手机关闭移动数据的情况下正常工作，用户可借此确保零流量。
- 分发方式：Android_App 以签名的 APK 安装包直接安装（不经过应用商店），主要验证机型为 Google Pixel 和小米（HyperOS / MIUI）手机；Windows_Client 以 Windows_Installer 安装。

## Glossary

- **System**：整个局域网传输系统，包括 Android_App 和 Windows_Client。
- **Android_App**：运行在 Android 手机上的本系统应用。
- **Windows_Client**：运行在 Windows 电脑上的本系统桌面客户端。
- **Windows_Installer**：Windows_Client 的安装程序。
- **Endpoint**：运行 Android_App 或 Windows_Client 的一个设备实例，即传输的一端。
- **Local_Network**：Endpoint 所在的 Wi‑Fi 局域网，或由 Android 手机开启移动热点所形成的网络。
- **Hotspot_Host**：开启移动热点、为其他设备提供 Local_Network 的 Android 手机。
- **Cellular_Network**：手机的移动数据网络（蜂窝网络）。
- **Private_IPv4_Address**：属于 10.0.0.0/8、172.16.0.0/12、192.168.0.0/16 或 169.254.0.0/16 范围的 IPv4 地址。
- **Default_Port**：Transfer_Service 默认监听的服务端口，具体数值在设计阶段确定。
- **Device_Identity**：Endpoint 首次启动时生成的唯一设备 ID 及对应的身份密钥对，用于识别和认证设备。
- **Discovery_Service**：负责在 Local_Network 内广播本机信息并发现其他 Endpoint 的组件。
- **Discovery_Announcement**：Discovery_Service 周期性发送的发现公告报文。
- **Announcement_Codec**：Discovery_Announcement 的序列化与解析组件。
- **Pairing_Service**：负责设备配对、信任管理的组件。
- **Pairing_Request**：发起方 Endpoint 向接收方 Endpoint 发出的配对请求。
- **Verification_Code**：配对过程中双方界面显示的 6 位数字验证码，用于用户核对双方是否连接到正确的设备。
- **QR_Payload**：二维码中编码的连接与配对信息。
- **QR_Codec**：QR_Payload 的序列化与解析组件。
- **Pairing_Token**：QR_Payload 中携带的一次性配对凭证。
- **Trusted_Device**：已与本机完成配对、被本机保存为信任对象的 Endpoint。
- **Session**：两个已配对 Endpoint 之间经过相互认证并加密的连接。
- **Background_State**：Android_App 的界面未显示在屏幕前台（用户已切换到其他应用、返回桌面或屏幕已关闭）的状态；或 Windows_Client 的主窗口已隐藏到系统托盘、已最小化或不是当前活动窗口的状态。
- **Transfer_Service**：负责建立 Session、收发 Text_Message 与文件的组件。
- **Protocol_Message**：Endpoint 之间通过 Session 交换的结构化消息（如 Text_Message、File_Offer、确认应答、心跳等）。
- **Frame**：Protocol_Message 在字节流中的二进制表示单元。
- **Frame_Encoder**：将 Protocol_Message 序列化为 Frame 字节序列的组件。
- **Frame_Decoder**：将字节流解析为 Protocol_Message 的组件。
- **Protocol_Version**：协议版本号，由主版本号和次版本号组成。
- **Test_Vector**：协议规范中给出的 Protocol_Message 及其期望字节序列的样例集合，用于验证两端实现一致。
- **Heartbeat**：Session 空闲时周期性发送的心跳消息，用于检测连接是否存活。
- **Text_Message**：一次发送的文字内容及其元数据（消息 ID、发送时间等）。
- **Unpaired_Surrogate**：UTF‑16 字符串中未与另一半配对的代理项码元（U+D800–U+DFFF），无法以合法 UTF‑8 编码。
- **File_Offer**：发送方发出的文件传输请求，包含每个文件的文件名、大小、类型等元数据。
- **Transfer_Task**：一次 File_Offer 对应的文件传输任务，可包含多个文件。
- **Receive_Directory**：接收文件的保存目录。
- **File_Name_Sanitizer**：将收到的文件名转换为接收方文件系统合法且安全的文件名的组件。
- **Clipboard_Manager**：负责读写系统剪贴板的组件。
- **File_Locator**：负责打开文件、打开文件所在目录的组件。
- **History_Store**：持久化保存传输记录的组件。

## Requirements

### Requirement 1: 支持的平台与设备组合

**User Story:** 作为用户，我希望在 Android 手机和 Windows 电脑上都能使用本系统，以便在手机与手机、手机与电脑之间互传内容。

#### Acceptance Criteria

1. THE Android_App SHALL 能在 Android 8.0（API 级别 26）及以上版本的 Android 设备上安装、启动，并提供第 4 条所列的全部功能。
2. THE Windows_Client SHALL 能在 Windows 10（版本 1809）及以上版本（包括 Windows 11）的 x64 系统上通过 Windows_Installer 安装、启动，并提供第 4 条所列的全部功能。
3. THE System SHALL 支持同一 Local_Network 内 Android_App 与 Android_App 之间、Android_App 与 Windows_Client 之间双向传输 Text_Message 和文件，组合中的任一 Endpoint 均可作为发送方或接收方。
4. THE System SHALL 在 Android_App 和 Windows_Client 上均提供以下功能，各功能在各平台上的具体形式以对应需求的规定为准：设备发现（需求 5）、配对与已信任设备管理（需求 6–9）、文字传输（需求 11、12）、复制粘贴（需求 13）、文件传输（需求 14–18）、打开文件及文件所在位置（需求 19）和传输记录（需求 20）。
5. THE System SHALL 以简体中文显示由 System 生成的全部用户界面文字（包括菜单、按钮、提示、对话框、系统通知内容以及 Windows_Installer 的安装界面），且显示语言不随操作系统语言设置改变；用户输入或传输的内容（Text_Message 文本、设备显示名称、文件名）以及操作系统自带的界面（如系统文件选择器、系统权限对话框）不在此要求范围内。
6. IF Windows_Installer 在低于 Windows 10 版本 1809 的系统或 32 位（x86）Windows 系统上运行, THEN THE Windows_Installer SHALL 终止安装、不安装 Windows_Client 且不创建防火墙规则，并显示说明最低系统要求（Windows 10 版本 1809 及以上的 x64 系统）的提示。
7. THE Android_App SHALL 将最低支持的系统版本声明为 API 级别 26，使低于 Android 8.0 的设备无法安装 Android_App。

### Requirement 2: 仅使用本地网络，不消耗移动数据流量

**User Story:** 作为用户，我希望传输只经过局域网或热点，以便传输文件时不消耗手机移动数据流量。

#### Acceptance Criteria

1. THE Transfer_Service SHALL 仅在 Local_Network 内的两个 Endpoint 之间直接建立 Session 并传输 Text_Message 和文件数据，Session 的对端地址为对方 Endpoint 的 Private_IPv4_Address，传输路径中不经过任何互联网服务器或云端中转服务。
2. WHILE Endpoint 所在的 Local_Network 无法访问互联网（如路由器未连接互联网，或 Android 设备的 Cellular_Network 已关闭）, THE System SHALL 完成设备发现、配对、文字传输和文件传输的全部功能，并满足需求 5、需求 7 和需求 11 中规定的各项时限。
3. THE Android_App SHALL 将用于发现、配对和传输的每个网络连接绑定到目标地址所在子网对应的 Wi‑Fi 网络接口或热点网络接口，使任意发现、配对和传输操作前后 Android 系统流量统计中 Android_App 的 Cellular_Network 数据用量增量为 0 字节。
4. WHILE Android 设备连接的 Wi‑Fi 网络无互联网访问能力且系统默认网络为 Cellular_Network, THE Android_App SHALL 通过该 Wi‑Fi 网络接口收发全部发现、配对和传输数据，且已建立的 Session 不因系统默认网络切换为 Cellular_Network 而断开。
5. IF 连接目标地址（包括手动输入的地址和 QR_Payload 中的地址）不是与本机某一 Local_Network 接口处于同一子网的 Private_IPv4_Address, THEN THE Transfer_Service SHALL 在 1 秒内拒绝建立连接、不向该地址发送任何数据包，并显示“目标地址不在本地网络内”提示；当一次连接操作包含多个候选地址时（需求 8 第 5 条），被拒绝的地址视为连接失败，仅在全部候选地址均被拒绝时显示该提示。
6. WHILE Android 设备既未连接 Wi‑Fi 也未开启移动热点, THE Android_App SHALL 显示“请连接 Wi‑Fi 或开启热点”提示，并禁用“发送”、“发送文件”和“发送剪贴板”按钮；提示和禁用在进入该状态后 3 秒内生效，查看传输记录、复制文字和打开已接收文件的功能保持可用。
7. THE System SHALL 仅向 Local_Network 内的 Private_IPv4_Address 以及 Local_Network 的广播或组播地址发送网络数据，不发起统计上报、崩溃上报、在线更新检查、域名解析或任何其他目的地址位于 Local_Network 以外的网络请求。
8. WHEN Android 设备在第 6 条所述状态下连接 Wi‑Fi 或开启移动热点, THE Android_App SHALL 在 3 秒内隐藏“请连接 Wi‑Fi 或开启热点”提示，并将“发送”、“发送文件”和“发送剪贴板”按钮恢复为由其他需求（如需求 11 第 2 条）决定的启用状态。
9. IF 收到的入站连接或 Discovery_Announcement 的源地址不是与本机某一 Local_Network 接口处于同一子网的 Private_IPv4_Address, THEN THE System SHALL 在交换任何 Protocol_Message 之前关闭该入站连接或丢弃该 Discovery_Announcement，且不将该来源显示在设备列表中。
10. IF Android_App 无法将某个网络连接绑定到 Wi‑Fi 网络接口或热点网络接口（如 Android 设备上的 VPN 禁止应用绕过）, THEN THE Android_App SHALL 放弃该连接、不经由系统默认网络（Cellular_Network 或 VPN）发送该连接的任何数据，并显示提示，说明无法通过本地网络建立连接并建议检查 VPN 设置。

### Requirement 3: 手机热点场景

**User Story:** 作为用户，我希望电脑或另一台手机连接我手机开启的热点后也能互传，以便在没有路由器的环境中使用。

#### Acceptance Criteria

1. WHILE Android 设备作为 Hotspot_Host 开启移动热点, THE Android_App SHALL 将热点网络接口作为一个活动的 Local_Network 接口，并在该接口上提供与 Wi‑Fi 网络接口相同的设备发现、配对（点选配对、扫码配对、手动输入地址连接）和传输服务。
2. WHILE Hotspot_Host 上的 Android_App 与接入热点的 Endpoint 均处于需求 5 规定的发送 Discovery_Announcement 的运行状态, WHEN 该 Endpoint（Android_App 或 Windows_Client）接入 Hotspot_Host 的热点网络并获得 IPv4 地址, THE Discovery_Service SHALL 自该 Endpoint 获得 IPv4 地址起 5 秒内使 Hotspot_Host 与该 Endpoint 在各自的设备列表中互相显示。
3. WHILE Hotspot_Host 的移动数据处于关闭状态, THE System SHALL 在 Hotspot_Host 与接入其热点的 Endpoint 之间完成设备发现、点选配对、扫码配对、手动输入地址连接、文字传输和文件传输，并满足本文档为这些功能规定的全部时限。
4. WHILE Windows 将 Windows_Client 所连接的 Local_Network 识别为“公用网络”配置文件, THE Windows_Client SHALL 接收来自该 Local_Network 内 Endpoint 的 Discovery_Announcement 和入站连接，无需用户将该网络更改为“专用网络”配置文件。
5. THE Windows_Installer SHALL 在安装时创建仅对 Windows_Client 程序生效的 Windows 防火墙入站规则，允许 Windows_Client 在“专用网络”和“公用网络”配置文件下接收 Discovery_Announcement 和入站连接，并在卸载 Windows_Client 时删除这些规则。
6. WHEN Android_App 运行期间 Android 设备的移动热点由关闭变为开启, THE Android_App SHALL 无需重启 Android_App，于热点开启后 5 秒内开始在热点网络接口上提供设备发现、配对和传输服务；若热点开启时 Android_App 不在前台，则于 Android_App 回到前台后 5 秒内开始提供。
7. WHEN Hotspot_Host 在 Session 存续期间开启或关闭移动数据, THE Transfer_Service SHALL 保持该 Session 连接，使进行中的 Transfer_Task 不被标记为“已中断”且不重新传输已传输的数据。
8. IF Windows_Client 启动时检测到第 5 条所述的 Windows 防火墙规则不存在或已被禁用, THEN THE Windows_Client SHALL 在主界面显示指明其他 Endpoint 可能无法连接本机的警告，并提供“修复防火墙规则”按钮；用户点击该按钮并授予管理员权限后，Windows_Client 重新创建该规则并移除警告，用户拒绝授权时警告保持显示。

### Requirement 4: 设备身份与显示名称

**User Story:** 作为用户，我希望每台设备有稳定的身份和易于辨认的名称，以便在设备列表中找到正确的设备。

#### Acceptance Criteria

1. WHEN Android_App 或 Windows_Client 启动且本机不存在已保存的 Device_Identity（包括安装后首次启动和应用数据被清除后启动）, THE System SHALL 为该 Endpoint 生成新的 Device_Identity 并持久化保存，使任意两个 Endpoint 的设备 ID 互不相同。
2. THE System SHALL 在应用重启、操作系统重启、应用版本升级以及修改设备显示名称之后，保持 Device_Identity 的设备 ID 和身份密钥对不变。
3. WHEN Endpoint 启动且本机不存在已保存的设备显示名称, THE System SHALL 将设备显示名称默认设置为对设备型号（Android_App）或计算机名称（Windows_Client）依次执行以下处理所得的结果：先移除其中全部 Unpaired_Surrogate，再去除首尾空白字符（具有 Unicode White_Space 属性的字符），最后取前 32 个 Unicode 码点；该结果为空时改用“Android 设备”（Android_App）或“Windows 电脑”（Windows_Client）。
4. WHEN 用户在设置中提交设备显示名称，且对提交内容先移除全部 Unpaired_Surrogate、再去除首尾空白字符所得的结果（以下称“清理后名称”）长度为 1 至 32 个 Unicode 码点且不含控制字符（U+0000–U+001F 及 U+007F–U+009F）, THE System SHALL 持久化保存清理后名称，使其在应用重启后保持不变，并在此后发送的每个 Discovery_Announcement 和此后生成的每个 QR_Payload 中使用该名称。
5. IF 用户提交设备显示名称时，其清理后名称（按第 4 条计算）长度为 0、超过 32 个 Unicode 码点或包含控制字符（U+0000–U+001F 及 U+007F–U+009F）, THEN THE System SHALL 拒绝保存、保持原设备显示名称不变、保留输入框中用户已输入的内容（不移除其中的 Unpaired_Surrogate）、不显示“已移除无法识别的字符”提示，并显示说明名称须为 1 至 32 个字符且不含控制字符的提示。
6. IF Endpoint 启动时已保存的 Device_Identity 无法读取或内容已损坏, THEN THE System SHALL 生成新的 Device_Identity 并持久化保存、保留本机已保存的 Trusted_Device 列表，并显示提示说明本机身份已重置、需要与此前配对的设备重新配对。
7. THE System SHALL 仅在本机保存 Device_Identity 的身份私钥，不在任何发送给其他 Endpoint 的数据（包括 Discovery_Announcement、QR_Payload 和 Protocol_Message）中包含该私钥。
8. IF 用户提交的设备显示名称中含有至少 1 个 Unpaired_Surrogate，且其清理后名称满足第 4 条的保存条件, THEN THE System SHALL 在按第 4 条保存清理后名称后，将输入框内容替换为已保存的名称，并显示“已移除无法识别的字符”提示，该提示持续 1 至 3 秒后自动消失。
9. THE System SHALL 使本机保存的设备显示名称（包括第 3 条的默认名称和第 4 条保存的名称），以及本机发送的每个 Discovery_Announcement 和生成的每个 QR_Payload 中的设备显示名称，均不包含 Unpaired_Surrogate。

### Requirement 5: 局域网设备自动发现

**User Story:** 作为用户，我希望打开应用后自动看到同一网络内的其他设备，以便无需手动输入地址即可连接。

#### Acceptance Criteria

1. WHILE Android_App 处于前台、Android_App 在已开启“后台接收”的情况下于后台运行，或 Windows_Client 正在运行（包括最小化到系统托盘）, THE Discovery_Service SHALL 在每个活动的 Local_Network 接口上以 2 秒（允许 ±0.5 秒偏差）的间隔发送 Discovery_Announcement 并接收其他 Endpoint 的 Discovery_Announcement，对于运行期间新变为活动的 Local_Network 接口，自该接口变为活动起 2 秒内开始发送。
2. WHEN 一个不在设备列表中的 Endpoint 开始在本机所在的 Local_Network 上发送 Discovery_Announcement（包括对方接入该 Local_Network 或启动应用），或本机 Discovery_Service 开始运行时该 Endpoint 已在发送 Discovery_Announcement, THE Discovery_Service SHALL 在该事件发生后 5 秒内将该 Endpoint 显示在设备列表中。
3. THE Discovery_Service SHALL 在设备列表中为每个设备 ID 仅显示 1 个列表项，列表项包含设备显示名称、设备类型（Android 或 Windows）、最近一次收到的有效 Discovery_Announcement 的来源 IP 地址，以及是否为 Trusted_Device 的标识。
4. IF 设备列表中的某个 Endpoint 自本机最后一次收到其有效 Discovery_Announcement 起已满 10 秒未再收到, THEN THE Discovery_Service SHALL 在之后 2 秒内将该 Endpoint 从设备列表中移除，且不删除该 Endpoint 的 Trusted_Device 信息、不因此终止与该 Endpoint 已建立的 Session。
5. IF 收到的 Discovery_Announcement 携带本机设备 ID，或其来源地址不是与接收接口处于同一子网的 Private_IPv4_Address, THEN THE Discovery_Service SHALL 忽略该 Discovery_Announcement，不据此新增或更新设备列表中的任何列表项。
6. THE Discovery_Announcement SHALL 仅包含设备 ID、设备显示名称、设备类型、Protocol_Version 和 Transfer_Service 实际监听的端口。
7. IF Default_Port 已被其他程序占用, THEN THE Transfer_Service SHALL 自动改用另一个未被占用的端口监听，无需用户操作。
8. WHEN Discovery_Service 收到设备列表中已有设备 ID 的有效 Discovery_Announcement，且其中的设备显示名称、设备类型、端口或该报文的来源 IP 地址与该列表项当前记录不同, THE Discovery_Service SHALL 在 1 秒内以新值更新该列表项，不新增列表项。
9. IF Transfer_Service 在 Default_Port 和任何其他端口上均无法开始监听, THEN THE System SHALL 不发送 Discovery_Announcement，并在主界面显示表明传输服务启动失败的提示。
10. IF Discovery_Service 无法在活动的 Local_Network 接口上发送或接收 Discovery_Announcement（如所需端口被占用或网络访问被系统拒绝）, THEN THE System SHALL 在主界面显示表明自动发现不可用、可改用手动输入地址或二维码配对的提示，并保持需求 6 的手动输入地址连接和需求 8 的扫码配对功能可用。

### Requirement 6: 手动输入地址连接

**User Story:** 作为用户，我希望在自动发现失败时（如路由器开启了客户端隔离或屏蔽了广播）可以手动输入地址连接，以便在受限网络中仍能使用。

#### Acceptance Criteria

1. THE System SHALL 在主界面显示本机在每个活动 Local_Network 接口上的 IP 地址和 Transfer_Service 实际监听的端口（包括按需求 5 第 7 条改用的端口），并在活动接口或其 IP 地址发生变化后 5 秒内更新所显示的内容。
2. WHEN 用户输入格式有效且满足需求 2 第 5 条本地网络地址要求的 IP 地址和端口并点击"连接", THE Pairing_Service SHALL 向该 IP 地址和端口发起连接。
3. WHEN 用户点击"连接"时端口输入项去除首尾空白后为空, THE Pairing_Service SHALL 使用 Default_Port 发起连接。
4. IF 用户输入的 IP 地址去除首尾空白后不是点分十进制 IPv4 地址（以"."分隔的 4 段十进制整数，每段取值 0–255，且除单个"0"外不以 0 开头），或输入的端口去除首尾空白后非空且不是 1–65535 范围内的十进制整数, THEN THE System SHALL 不发起连接，显示指明哪一输入项（IP 地址或端口）格式无效的错误提示，并保留用户已输入的内容。
5. IF 连接尝试失败（目标拒绝连接、目标不可达或目标未按协议应答），或自用户点击"连接"起 10 秒内仍未与目标地址上的 Transfer_Service 建立连接并获得目标 Endpoint 的设备 ID, THEN THE Pairing_Service SHALL 在点击"连接"后 10 秒内终止连接尝试、显示"无法连接，请确认双方在同一网络且防火墙已放行"提示，并保留用户已输入的 IP 地址和端口。
6. WHEN 手动连接已建立（已获得目标 Endpoint 的设备 ID）且目标 Endpoint 不是 Trusted_Device, THE Pairing_Service SHALL 按需求 7 的点选配对流程向该 Endpoint 发送 Pairing_Request。
7. WHEN 手动连接已建立（已获得目标 Endpoint 的设备 ID）且目标 Endpoint 是 Trusted_Device, THE Transfer_Service SHALL 按需求 9 第 2 条直接建立 Session 并打开与该 Endpoint 的会话界面，不发送 Pairing_Request。
8. WHILE 一次手动连接尝试正在进行, THE System SHALL 显示"正在连接"状态并禁用"连接"按钮。
9. IF 用户输入的 IP 地址属于本机某一活动 Local_Network 接口，或手动连接后目标 Endpoint 的设备 ID 与本机设备 ID 相同, THEN THE Pairing_Service SHALL 终止该连接尝试，显示表明不能连接本机的提示，并保留用户已输入的内容。

### Requirement 7: 点选设备配对

**User Story:** 作为用户，我希望点选设备并由对方确认即可完成配对，以便用最少的操作建立信任。

#### Acceptance Criteria

1. WHEN 用户在设备列表中点选一个非 Trusted_Device 的 Endpoint, THE Pairing_Service SHALL 向该 Endpoint 发送 Pairing_Request，并在发起方界面显示“等待对方确认”状态和“取消”按钮。
2. WHEN 接收方 Endpoint 收到一个不属于第 8、9 条自动拒绝情形的 Pairing_Request, THE Pairing_Service SHALL 在 1 秒内于发起方和接收方界面各显示 1 个 6 位数字（000000–999999，含前导零）的 Verification_Code，且在通信未被篡改时双方显示的 Verification_Code 相同。
3. WHEN 接收方 Endpoint 收到一个不属于第 8、9 条自动拒绝情形的 Pairing_Request, THE Pairing_Service SHALL 在接收方界面显示发起方的设备显示名称、设备类型（Android 或 Windows）、IP 地址以及“接受”和“拒绝”按钮。
4. WHEN 接收方用户在 Pairing_Request 等待响应期间点击“接受”, THE Pairing_Service SHALL 在双方将对方保存为 Trusted_Device、建立 Session，并在双方显示“配对成功”提示。
5. THE Pairing_Service SHALL 使用户仅通过发起方 1 次点选设备和接收方 1 次点击“接受”完成配对，配对过程中不要求任何一方输入 Verification_Code、地址或其他内容。
6. WHEN 接收方用户点击“拒绝”, THE Pairing_Service SHALL 在 1 秒内于发起方显示“对方拒绝了配对请求”提示，且双方均不将对方保存为 Trusted_Device。
7. IF 自发起方发送 Pairing_Request 起 60 秒内接收方用户未点击“接受”或“拒绝”, THEN THE Pairing_Service SHALL 取消该 Pairing_Request、关闭接收方界面上该 Pairing_Request 的确认界面，并在发起方显示“配对请求已超时”提示。
8. IF 接收方在用户点击“拒绝”后 60 秒内再次收到来自同一设备 ID 的 Pairing_Request, THEN THE Pairing_Service SHALL 自动拒绝该 Pairing_Request、不在接收方界面显示确认界面，并在发起方显示“对方拒绝了配对请求”提示。
9. WHILE 接收方界面正在显示一个等待用户响应的 Pairing_Request, IF 收到来自其他设备 ID 的新 Pairing_Request, THEN THE Pairing_Service SHALL 自动拒绝该新 Pairing_Request、不在接收方界面显示该请求，并在新发起方显示“对方正忙”提示。
10. WHEN 发起方用户在 Pairing_Request 等待响应期间点击“取消”, THE Pairing_Service SHALL 取消该 Pairing_Request，并在 1 秒内关闭接收方界面上该 Pairing_Request 的确认界面。
11. IF 发起方在点选设备后 10 秒内未能与目标 Endpoint 建立连接, THEN THE Pairing_Service SHALL 终止该次配对尝试，并在发起方显示“无法连接，请确认双方在同一网络且防火墙已放行”提示。
12. IF 在 Pairing_Request 等待响应期间或接收方点击“接受”后至 Session 建立完成前连接中断, THEN THE Pairing_Service SHALL 在检测到中断的一方结束该 Pairing_Request、不保留对方的 Trusted_Device 信息（已保存的予以删除），并在连接中断后 15 秒内显示“配对失败，请重试”提示。

### Requirement 8: 扫码配对

**User Story:** 作为用户，我希望用 Android 手机扫描对方屏幕上的二维码即可完成配对，以便无需等待确认或输入地址。

#### Acceptance Criteria

1. WHEN 用户打开 Windows_Client 的配对界面或 Android_App 的“显示本机二维码”界面（以下统称“二维码界面”，打开该界面的 Endpoint 称为“显示方”）, THE Pairing_Service SHALL 生成新的 Pairing_Token，并在二维码界面显示编码了包含该 Pairing_Token 的 QR_Payload 的二维码。
2. THE Android_App SHALL 在配对界面提供“显示本机二维码”和“扫描二维码”两个入口。
3. THE QR_Payload SHALL 包含设备 ID、设备显示名称、每个活动 Local_Network 接口的 IPv4 地址、Transfer_Service 实际监听的端口、身份公钥指纹和 Pairing_Token。
4. WHEN Android_App（以下称“扫描方”）扫描到有效的 QR_Payload, THE Pairing_Service SHALL 按第 5 条连接显示方，并在身份公钥指纹与 Pairing_Token 均校验通过后，无需显示方用户确认，即在双方将对方保存为 Trusted_Device（每方的 Trusted_Device 列表中对方仅保留 1 条记录）、建立 Session，并在双方显示包含对方设备显示名称的配对成功提示。
5. WHEN Pairing_Service 根据 QR_Payload 发起连接, THE Pairing_Service SHALL 跳过与本机任一 Local_Network 接口均不处于同一子网的 IP 地址，按 QR_Payload 中的排列顺序依次尝试其余地址（每个地址最长等待 5 秒），并使用第一个连接成功的地址，不再尝试后续地址。
6. THE Pairing_Service SHALL 仅在同时满足以下全部条件时视 Pairing_Token 为有效：自生成起未超过 5 分钟；尚未用于完成配对；是显示方最近一次生成的 Pairing_Token；显示方自该 Pairing_Token 生成后收到的 Pairing_Token 校验失败次数少于 5 次。
7. WHILE 显示方的二维码界面处于打开状态, WHEN 当前 Pairing_Token 按第 6 条失效或本机活动 Local_Network 接口的 IPv4 地址集合发生变化, THE Pairing_Service SHALL 在 2 秒内生成新的 Pairing_Token 并刷新二维码界面上的二维码。
8. IF 扫描到的二维码不是有效的 QR_Payload（QR_Codec 返回解析错误）, THEN THE Android_App SHALL 显示“无法识别的二维码”提示，并保持扫描界面继续扫描。
9. IF 扫描方提交的 Pairing_Token 未通过显示方校验（已过期、已被使用、已被更新的 Pairing_Token 取代或不存在）, THEN THE Pairing_Service SHALL 拒绝配对、双方均不保存对方为 Trusted_Device，并在扫描方显示“二维码已失效，请刷新后重试”提示。
10. IF 连接后显示方出示的身份公钥与 QR_Payload 中的身份公钥指纹不一致, THEN THE Pairing_Service SHALL 终止配对、不向该 Endpoint 发送 Pairing_Token、扫描方不将该 Endpoint 保存为 Trusted_Device，并在扫描方显示指明对方身份与二维码不符的安全警告。
11. IF 用户点击“扫描二维码”时 Android_App 未获得相机权限且用户拒绝了授权请求, THEN THE Android_App SHALL 显示需要相机权限的说明，并提供进入需求 6 手动输入地址连接界面的入口。
12. IF 按第 5 条尝试后没有任何地址连接成功（包括全部地址均被跳过的情况）, THEN THE Pairing_Service SHALL 终止配对、扫描方不保存任何 Trusted_Device，并在扫描方显示“无法连接，请确认双方在同一网络且防火墙已放行”提示。
13. WHILE 显示方的二维码界面处于打开状态且本机没有任何活动的 Local_Network 接口, THE Pairing_Service SHALL 不显示二维码，并显示“请先连接 Wi‑Fi 或热点”提示。

### Requirement 9: 已信任设备管理

**User Story:** 作为用户，我希望配对过的设备下次可以直接传输，并能随时解除配对，以便兼顾便捷与安全。

#### Acceptance Criteria

1. THE Pairing_Service SHALL 持久化保存 Trusted_Device 列表，每项包括设备 ID、设备显示名称、设备类型和身份公钥，支持保存至少 100 个 Trusted_Device，并在应用重启和系统重启后保持列表内容不变。
2. WHEN 用户向 Trusted_Device 发送 Text_Message 或文件且双方之间尚无 Session, THE Transfer_Service SHALL 直接建立 Session，且发起方和接收方界面均不显示 Verification_Code 或配对确认界面。
3. WHEN 用户在已信任设备列表中点击某 Trusted_Device 的"解除配对"并在确认对话框中确认, THE Pairing_Service SHALL 删除该设备的全部信任信息（包括身份公钥和"自动接收文件"设置）、关闭与该设备之间已建立的 Session，并将该设备从已信任设备列表中移除。
4. IF 本机作为发起方或接收方建立 Session 时，对方出示的设备 ID 属于 Trusted_Device 但身份公钥与保存的身份公钥不一致, THEN THE Pairing_Service SHALL 拒绝建立 Session、保持已保存的身份公钥不变，并显示安全警告，警告内容包含该 Trusted_Device 的设备显示名称，并说明对方身份与配对时不一致、可能被冒充。
5. IF 本机向 Trusted_Device 发起 Session 时，对方以本机不在对方的 Trusted_Device 列表中为由拒绝, THEN THE Pairing_Service SHALL 显示"对方已解除配对"提示和"重新配对"按钮，该按钮按需求 7 的点选配对流程向对方发送 Pairing_Request。
6. WHEN 本机与 Trusted_Device 完成相互认证并建立 Session，且对方在 Session 中出示的设备显示名称与保存的设备显示名称不同, THE Pairing_Service SHALL 将保存的设备显示名称更新为对方出示的名称，且不依据未经认证的 Discovery_Announcement 中的设备显示名称修改保存的名称。
7. THE System SHALL 在设置中提供已信任设备列表，为每个 Trusted_Device 显示设备显示名称、设备类型和"解除配对"按钮。
8. IF 本机发起与 Trusted_Device 的 Session 后 10 秒内 Session 未建立, THEN THE Transfer_Service SHALL 终止连接尝试、显示"无法连接，请确认双方在同一网络且防火墙已放行"提示、保留用户已选择但尚未发送的文件，并保持该设备的信任信息不变。
9. WHEN 与某 Endpoint 完成配对且该 Endpoint 的设备 ID 已存在于 Trusted_Device 列表中, THE Pairing_Service SHALL 以本次配对获得的设备显示名称、设备类型和身份公钥替换原有条目，使 Trusted_Device 列表中每个设备 ID 只对应 1 个条目。

### Requirement 10: 通信安全

**User Story:** 作为用户，我希望传输内容只能被我配对过的设备接收和读取，以便在公共或共享网络中保护隐私。

#### Acceptance Criteria

1. THE Transfer_Service SHALL 对 Session 中传输的全部 Protocol_Message（包括 Text_Message、File_Offer、文件数据、确认应答和 Heartbeat）进行加密和完整性保护，使在 Local_Network 上捕获的 Session 数据中不出现任何 Text_Message 文本、文件名或文件内容的明文。
2. WHEN 两个 Endpoint 建立 Session, THE Transfer_Service SHALL 在交换任何 Text_Message、File_Offer 或文件数据之前，使用双方的 Device_Identity 完成相互认证，由每一方验证对方持有与其出示的身份公钥对应的私钥，且该身份公钥与本机为该 Trusted_Device 保存的身份公钥一致。
3. THE Pairing_Service SHALL 由双方身份公钥以及双方在本次配对中各自新生成的随机数派生 Verification_Code，使同一对 Endpoint 的每次配对均重新派生 Verification_Code，且当中间人以其他身份公钥替换任一方身份公钥时，双方显示的 Verification_Code 相同的概率不超过 1/1,000,000。
4. IF 非 Trusted_Device 的 Endpoint 请求建立 Session，或发送 Text_Message、File_Offer 或文件数据（Pairing_Request 不受本条限制，按需求 7 处理）, THEN THE Transfer_Service SHALL 拒绝该请求、向对方返回表明本机未将其保存为 Trusted_Device 的拒绝应答并关闭连接，且不显示其中任何内容、不写入 History_Store、不在 Receive_Directory 中创建任何文件。
5. IF 收到的 Protocol_Message 未通过完整性校验，或为被重放的 Protocol_Message（同一 Session 中再次出现已接收过的同一份受保护数据，或出现属于其他 Session 的受保护数据；需求 11 中以相同消息 ID 重新发送的 Text_Message 属于新的 Protocol_Message，不视为重放）, THEN THE Transfer_Service SHALL 丢弃该 Protocol_Message、不显示或保存其中任何内容、终止 Session，并将与该 Endpoint 进行中的 Transfer_Task 标记为“已中断”，中断原因显示为数据完整性校验失败。
6. IF 对方 Endpoint 在相互认证中未能证明持有与其出示的身份公钥对应的私钥, THEN THE Transfer_Service SHALL 关闭该连接且不建立 Session，不处理该连接上收到的任何 Protocol_Message，并显示安全警告。
7. WHEN Android_App 扫描到有效的 QR_Payload 并连接到对方 Endpoint, THE Pairing_Service SHALL 先确认对方出示的身份公钥与 QR_Payload 中的身份公钥指纹一致，再通过加密连接发送 Pairing_Token，使 Pairing_Token 不以明文出现在 Local_Network 上。
8. WHEN 用户删除某 Trusted_Device, THE Transfer_Service SHALL 在 2 秒内终止与该 Endpoint 的全部 Session，并停止与该 Endpoint 进行中的全部 Transfer_Task。

### Requirement 11: 文字发送与接收

**User Story:** 作为用户，我希望在设备之间发送简短文字，以便快速分享链接、验证码、笔记等内容。

#### Acceptance Criteria

1. THE System SHALL 在与目标 Endpoint 的会话界面提供多行文字输入框和“发送”按钮。
2. WHILE 输入框内容长度为 0, THE System SHALL 禁用“发送”按钮。
3. WHEN 用户点击“发送”且输入框文字的 UTF‑8 编码长度不超过 1,048,576 字节, THE Transfer_Service SHALL 为输入框中的完整文字（包括仅由空白字符组成的文字）生成一个新的消息 ID，并将其作为 Text_Message 发送给目标 Endpoint。
4. WHEN Endpoint 收到一条 Text_Message，且 History_Store 中不存在来自同一发送方设备 ID、具有相同消息 ID 的 Text_Message, THE System SHALL 在完整收到该 Text_Message 后 1 秒内将其文本和发送时间追加显示在与发送方的会话界面末尾，并保存到 History_Store。
5. WHEN 发送方收到接收方针对某消息 ID 的送达确认（包括在该 Text_Message 已被标记为“发送失败”之后收到的送达确认）, THE Transfer_Service SHALL 将该 Text_Message 标记为“已送达”并隐藏其“重试”按钮。
6. IF 自用户点击“发送”或“重试”起 10 秒内未收到该 Text_Message 的送达确认（包括因无法建立 Session 而未能发出的情况），或收到接收方针对该 Text_Message 的“消息损坏”应答, THEN THE Transfer_Service SHALL 将该 Text_Message 标记为“发送失败”，在会话界面保留其完整文本并显示“重试”按钮。
7. WHEN 用户点击“重试”, THE Transfer_Service SHALL 以相同的消息 ID 和相同的文本重新发送该 Text_Message，并将其状态重新标记为“发送中”。
8. WHEN Endpoint 收到一条 Text_Message，且 History_Store 中已存在来自同一发送方设备 ID、具有相同消息 ID 的 Text_Message, THE Transfer_Service SHALL 返回送达确认，且不再次显示、不再次保存该 Text_Message，也不再次触发系统通知或自动复制，使会话界面中该消息只显示 1 次。
9. IF 用户点击“发送”或“发送剪贴板”时待发送文字的 UTF‑8 编码长度超过 1 MiB（1,048,576 字节）, THEN THE System SHALL 不发送 Text_Message、保持输入框内容不变，并显示文字超出长度上限的提示及“以 .txt 文件发送”选项。
10. WHEN 会话界面显示一条行数超过 20 行或 Unicode 码点数超过 1000 的已接收或已发送 Text_Message, THE System SHALL 折叠显示该 Text_Message（最多显示前 20 行且不超过前 1000 个 Unicode 码点）并提供“展开”按钮，在用户点击“展开”后显示完整文本；其中行数为文本中换行序列的个数加 1，LF、CR 和 CRLF 各计为 1 个换行序列。
11. WHEN Transfer_Service 开始发送一条新的 Text_Message, THE System SHALL 将该 Text_Message 以“发送中”状态追加显示在发送方会话界面末尾；若该文字来自输入框，则同时清空输入框。
12. WHEN 接收方成功将 Text_Message 保存到 History_Store, THE Transfer_Service SHALL 向发送方返回针对该消息 ID 的送达确认；保存失败时不返回送达确认。
13. WHEN 用户选择“以 .txt 文件发送”, THE Transfer_Service SHALL 以一个扩展名为 .txt、内容为待发送文字 UTF‑8 编码字节（不添加字节顺序标记或其他任何字节）的文件，按需求 14 的文件发送流程发送给目标 Endpoint。

### Requirement 12: 文字无损传输

**User Story:** 作为用户，我希望文字中的换行、制表符、表情符号和其他特殊字符在对方设备上完全一致，以便内容没有缺漏、错位或乱码。

#### Acceptance Criteria

1. THE System SHALL 使接收方得到的 Text_Message 文本与发送方发送的文本逐个 Unicode 码点完全一致（码点数量、顺序和数值均相同），不进行 Unicode 规范化（NFC、NFD、NFKC、NFKD）、全角/半角转换、大小写转换或其他任何字符替换。
2. THE System SHALL 在从发送方获取待发送文本（包括经“粘贴”按钮插入输入框的文本）到接收方得到 Text_Message 文本的全过程中原样保留 LF（U+000A）、CR（U+000D）和 CRLF 序列：不将 LF 转换为 CRLF，不将 CRLF 转换为 LF，不将单独的 CR 转换为 LF，也不增加或删除任何换行符。
3. THE System SHALL 原样保留制表符（U+0009）、连续空格、首尾空白（包括空格、制表符、换行符、U+00A0 和 U+3000）、NUL（U+0000）、其他 C0 控制字符（U+0001–U+001F）、DEL（U+007F）以及 C1 控制字符（U+0080–U+009F），不删除、不合并，也不在任何控制字符处截断文本。
4. THE System SHALL 原样保留表情符号（含 ZWJ 组合序列、肤色修饰符和变体选择符 U+FE0F）、零宽字符（如 U+200B、U+200C、U+200D）、U+FEFF（包括位于文本开头的 U+FEFF）、组合附加符号，以及 U+0000–U+10FFFF 范围内除 U+D800–U+DFFF 以外的全部码点（包括增补平面码点、私用区码点、未分配码点和非字符码点，如 U+FFFE、U+FFFF）。
5. THE Frame_Encoder SHALL 以标准 UTF‑8 编码 Text_Message 文本（每个码点使用最短编码形式，NUL 编码为单个 0x00 字节，U+10000 及以上码点编码为 1 个 4 字节序列而非 2 个代理项的 3 字节序列），不在文本前添加字节顺序标记，并以显式字节长度界定文本边界，不依赖分隔符或转义字符。
6. IF 待发送文本（来自输入框、系统剪贴板或 Android 系统“分享”菜单）包含 Unpaired_Surrogate, THEN THE System SHALL 在用户作出选择前不发送任何内容，并显示“文本包含无法编码的字符”提示，提示中提供“替换并发送”和“取消”两个选项。
7. IF 收到的 Text_Message 文本字节不是合法的 UTF‑8 序列（包括截断的多字节序列、非最短形式编码、编码 U+D800–U+DFFF 的序列或解码值大于 U+10FFFF 的序列）, THEN THE Transfer_Service SHALL 丢弃该 Text_Message（不在会话界面显示其中任何部分、不保存到 History_Store、不写入系统剪贴板、不显示系统通知、不将该消息 ID 记为已接收），保持 Session 不中断，并向发送方返回携带该消息 ID 的“消息损坏”应答。
8. WHEN 会话界面显示 Text_Message, THE System SHALL 将每个 LF、每个 CRLF 序列和每个其后不紧跟 LF 的 CR 各显示为 1 次换行（CRLF 不显示为 2 次换行），将每个制表符显示为宽度不小于 1 个空格的水平空白，不合并连续空格，不省略首尾的空白和空行，并显示 NUL（U+0000）之后的全部文字。
9. FOR ALL 不含 Unpaired_Surrogate 且 UTF‑8 编码长度为 0 至 1,048,576 字节的字符串 s，将 s 编码为 Text_Message 的 Frame 后再解码 SHALL 得到与 s 完全相同的字符串（往返属性）。
10. WHEN 用户在“文本包含无法编码的字符”提示中选择“替换并发送”, THE System SHALL 将每个 Unpaired_Surrogate 替换为 1 个 U+FFFD、其余码点保持不变后作为 Text_Message 发送，并在发送方会话界面和 History_Store 中使用替换后的文本。
11. IF 用户在“文本包含无法编码的字符”提示中选择“取消”或关闭该提示, THEN THE System SHALL 不发送该文本，并保持输入框中的内容不变。
12. IF 发送方收到针对某 Text_Message 消息 ID 的“消息损坏”应答, THEN THE Transfer_Service SHALL 将该 Text_Message 标记为“发送失败”并显示“重试”按钮。

### Requirement 13: 复制与粘贴

**User Story:** 作为用户，我希望一键复制收到的文字、一键粘贴或发送剪贴板内容，以便在设备之间和其他应用之间流转文字。

#### Acceptance Criteria

1. WHEN 用户点击会话界面或传输记录中某条已发送或已接收 Text_Message 的“复制”按钮, THE Clipboard_Manager SHALL 将该 Text_Message 的完整原文写入系统剪贴板，并显示“已复制”提示，该提示持续 1 至 3 秒后自动消失。
2. WHEN 用户对处于折叠显示状态（需求 11 第 10 条）的 Text_Message 点击“复制”按钮，或点击 Text_Message 系统通知中的“复制”操作（需求 21 第 6 条）, THE Clipboard_Manager SHALL 写入该 Text_Message 的完整原文，而不是只写入界面中可见的部分或通知中的文字预览。
3. THE Clipboard_Manager SHALL 使写入系统剪贴板的文本与对应 Text_Message 原文（部分复制时为被选中的子串）逐个 Unicode 码点完全一致，不转换换行符（如不把 LF 转为 CRLF），不去除首尾空白，不做 Unicode 规范化。是否一致以 Clipboard_Manager 从系统剪贴板读回的文本为准。
4. WHEN 用户在会话界面的 Text_Message 显示区域选中部分文字并执行系统复制操作（Windows_Client 上为 Ctrl+C 或右键菜单“复制”，Android_App 上为文字选择菜单中的“复制”）, THE Clipboard_Manager SHALL 只将被选中的文字写入系统剪贴板。
5. WHEN 用户点击输入框旁的“粘贴”按钮且系统剪贴板包含纯文本, THE Clipboard_Manager SHALL 将该纯文本按 Unicode 码点原样插入输入框光标位置，并将光标放在插入文本之后。输入框中有选中文字时，插入的文本替换选中文字；输入框还没有光标位置时，插入的文本追加到末尾。
6. WHEN 用户在与目标 Endpoint 的会话界面点击“发送剪贴板”按钮且系统剪贴板包含纯文本, THE Transfer_Service SHALL 将该纯文本按 Unicode 码点原样作为 Text_Message 发送给该 Endpoint，不读取也不修改输入框中的内容。超长文本按需求 11 第 9 条处理，包含 Unpaired_Surrogate 的文本按需求 12 第 6 条处理。
7. IF 用户点击“粘贴”或“发送剪贴板”按钮时系统剪贴板为空、不包含纯文本内容（如仅包含图片或文件）或纯文本长度为 0, THEN THE Clipboard_Manager SHALL 显示“剪贴板中没有文本”提示，保持输入框内容不变，且不发送任何 Text_Message。
8. WHERE 用户在设置中开启“自动复制收到的文字”（该设置默认关闭）, WHEN Endpoint 收到消息 ID 此前未接收过的 Text_Message, THE Clipboard_Manager SHALL 在收到后 1 秒内将该 Text_Message 原文写入系统剪贴板并覆盖原有内容。连续收到多条 Text_Message 时，剪贴板最终保存最后收到的一条；消息 ID 重复的 Text_Message（需求 11 第 8 条）不再次写入。
9. IF Clipboard_Manager 读取或写入系统剪贴板失败（如剪贴板被其他程序占用超过 1 秒，或系统拒绝访问）, THEN THE Clipboard_Manager SHALL 显示剪贴板访问失败的提示，不显示“已复制”提示，保持输入框内容不变，且不发送任何 Text_Message。
10. IF 用户通过“复制”按钮或系统通知中的“复制”操作复制的 Text_Message 包含 NUL（U+0000）, THEN THE Clipboard_Manager SHALL 仍写入包含 NUL 的完整原文，并在“已复制”提示中附加说明：粘贴到其他应用时，文本可能在 NUL 字符处被截断。

### Requirement 14: 文件发送

**User Story:** 作为用户，我希望选择一个或多个文件发送给对方设备，以便在设备之间传递照片、文档、安装包等文件。

#### Acceptance Criteria

1. WHEN 用户在与目标 Endpoint 的会话界面点击“发送文件”, THE System SHALL 打开系统文件选择器，允许用户在一次选择中选取 1 个或多个文件，并将选中的文件按选择顺序追加到待发送列表末尾。
2. WHEN 用户将一个或多个文件拖放到 Windows_Client 与某 Endpoint 的会话窗口, THE Windows_Client SHALL 将被拖放的文件按拖放顺序追加到与该 Endpoint 的待发送列表末尾。
3. WHEN 用户在其他 Android 应用中通过系统“分享”菜单选择 Android_App 并在设备列表中选定目标 Endpoint, THE Android_App SHALL 将分享的文件加入与该 Endpoint 的待发送列表，或将分享的文字原样填入与该 Endpoint 会话界面的输入框。
4. WHEN 用户在非空的待发送列表中点击“发送”, THE Transfer_Service SHALL 向目标 Endpoint 发送 1 个 File_Offer，其中按待发送列表顺序列出每个文件的文件名（不含路径）、文件大小（字节）、MIME 类型和最后修改时间；对无法识别类型的文件，MIME 类型标记为通用二进制类型。
5. THE Transfer_Service SHALL 支持传输大小为 0 字节至 1 TiB（1,099,511,627,776 字节）的单个文件，包括大于 4 GiB（4,294,967,296 字节）的文件。
6. WHEN 一个 Transfer_Task 包含多个文件, THE Transfer_Service SHALL 按 File_Offer 中的文件顺序逐个传输、同一时刻只传输 1 个文件，并在双方为每个文件单独显示其当前状态（“等待中”、“传输中”、“已完成”、“读取失败”、“校验失败”或“写入失败”）。
7. IF 某个待发送文件在开始读取时或读取过程中无法读取（已被删除、移动或无访问权限）, THEN THE Transfer_Service SHALL 在发送方将该文件标记为“读取失败”、通知接收方删除该文件已接收部分的临时文件并将其标记为“读取失败”，然后继续传输同一 Transfer_Task 中的下一个文件。
8. IF 本次加入后待发送列表中的文件数量将超过 1000 个, THEN THE System SHALL 拒绝本次加入的全部文件、保持待发送列表不变，并显示单次最多发送 1000 个文件的提示。
9. IF 用户通过拖放或系统“分享”加入的项目中包含文件夹, THEN THE System SHALL 不将该文件夹加入待发送列表、将其余文件照常加入，并显示“暂不支持发送文件夹”提示。
10. IF 用户点击“发送”后 10 秒内未能与目标 Endpoint 建立 Session, THEN THE Transfer_Service SHALL 不发送 File_Offer、显示“无法连接到对方设备”提示，并保留待发送列表中的全部文件。

### Requirement 15: 文件接收确认

**User Story:** 作为用户，我希望在接收文件前看到文件信息并决定是否接收，以便避免接收不需要的文件。

#### Acceptance Criteria

1. WHEN Endpoint 收到通过第 5 条和第 7 条检查且不满足第 2 条自动接收条件的 File_Offer, THE System SHALL 显示确认界面，界面中包含发送方设备显示名称、文件数量、全部文件大小之和、以可滚动列表列出的每个文件的文件名（File_Offer 中的原始文件名）和大小，以及"接收"和"拒绝"按钮；文件大小按 1024 进制换算，选用使数值不小于 1 的最大单位（B、KB、MB、GB、TB），B 以外的单位保留 1 位小数。
2. WHERE 用户已为某 Trusted_Device 开启"自动接收文件"（该选项对每个 Trusted_Device 默认关闭）, WHEN 收到来自该 Trusted_Device 且通过第 5 条和第 7 条检查的 File_Offer, THE Transfer_Service SHALL 不显示确认界面，直接开始接收该 File_Offer 中的全部文件。
3. WHEN 接收方用户在确认界面点击"拒绝", THE Transfer_Service SHALL 结束该 Transfer_Task、关闭确认界面、不在 Receive_Directory 中创建任何文件，并在 2 秒内在发送方显示"对方拒绝接收"提示。
4. IF 自接收方收到 File_Offer 起 60 秒内接收方用户未点击"接收"或"拒绝"（每个 File_Offer 独立计时）, THEN THE Transfer_Service SHALL 结束该 Transfer_Task、关闭接收方的确认界面、不在 Receive_Directory 中创建任何文件，并在发送方显示"对方未响应"提示。
5. IF Endpoint 收到 File_Offer 时 Receive_Directory 所在存储的可用空间小于该 File_Offer 中全部文件大小之和, THEN THE Transfer_Service SHALL 不显示确认界面、拒绝该 File_Offer，并在双方显示"接收方存储空间不足"提示。
6. WHEN 接收方用户在确认界面点击"接收", THE Transfer_Service SHALL 关闭确认界面、通知发送方，并在 2 秒内开始将该 File_Offer 中的文件接收到当前 Receive_Directory。
7. IF Endpoint 收到 File_Offer 时 Receive_Directory 不存在且无法创建，或本机对 Receive_Directory 没有写入权限, THEN THE Transfer_Service SHALL 不显示确认界面、拒绝该 File_Offer，在发送方显示"接收方无法保存文件"提示，并在接收方显示"接收目录不可用"提示及进入接收目录设置的入口。
8. IF 接收方用户响应 File_Offer 之前发送方取消该 Transfer_Task 或 Transfer_Service 判定 Session 已断开, THEN THE System SHALL 在收到取消通知或判定断开后 2 秒内关闭接收方的确认界面、结束该 Transfer_Task，并在接收方显示"对方已取消发送"提示（发送方取消时）或"连接已断开"提示（Session 断开时）。

### Requirement 16: 文件存储与文件名处理

**User Story:** 作为用户，我希望收到的文件保存在容易找到的位置，且文件名合法、不覆盖已有文件，以便安全地管理收到的文件。

#### Acceptance Criteria

1. THE Windows_Client SHALL 将 Receive_Directory 默认设置为当前用户“下载”已知文件夹（包括用户在 Windows 中更改过该文件夹位置的情况）下的 LanTransfer 子目录。
2. THE Android_App SHALL 将 Receive_Directory 默认设置为设备内部共享存储中的 Download/LanTransfer 目录，使系统文件管理器可以浏览该目录并打开其中的文件。
3. WHEN 用户在设置中选择一个可写入的目录作为新的 Receive_Directory, THE System SHALL 保存该设置，并将此后接受的 File_Offer 中的文件保存到新目录；已接收的文件不移动，变更前已接受的 Transfer_Task（包括之后按需求 17 第 4 条继续的“已中断”Transfer_Task）仍保存到原目录。
4. THE File_Name_Sanitizer SHALL 仅保留收到的文件名中最后一个“/”或“\”之后的部分作为文件名，去除其余路径成分；若保留的部分为“.”或“..”，则视为空文件名。
5. THE File_Name_Sanitizer SHALL 在 Android_App 和 Windows_Client 上均将文件名中的每个 \ / : * ? " < > |、U+0000–U+001F 及 U+007F 字符替换为 1 个下划线“_”。
6. IF 接收方为 Windows_Client 且文件名中第一个“.”之前的部分在去除末尾空格后不区分大小写地等于 Windows 保留名称（CON、PRN、AUX、NUL、COM1–COM9、LPT1–LPT9，如“con.txt”、“Nul.tar.gz”、“COM1”）, THEN THE File_Name_Sanitizer SHALL 在文件名前添加 1 个下划线“_”。
7. IF 接收方为 Windows_Client 且文件名以空格或“.”结尾, THEN THE File_Name_Sanitizer SHALL 去除文件名末尾全部连续的空格和“.”。
8. IF 文件名长度超过上限（Windows_Client 为 255 个 UTF‑16 码元，Android_App 为 255 个 UTF‑8 字节）, THEN THE File_Name_Sanitizer SHALL 从文件名主干末尾逐个删除完整的 Unicode 码点，直至整个文件名不超过上限，并保持扩展名不变；其中扩展名指文件名中最后一个“.”及其后的字符（“.”为首字符时视为无扩展名），文件名主干指扩展名之前的部分；若主干仅剩 1 个码点时仍超过上限，则继续从扩展名末尾删除码点；截断结果仍满足第 5 至 7 条。
9. IF 经第 4 至 7 条处理后的文件名为空, THEN THE File_Name_Sanitizer SHALL 使用“file_YYYYMMDD_HHMMSS”（接收方开始接收该文件时的本地时间，如 file_20250101_093005）作为文件名主干，并在原文件名带扩展名时保留原扩展名。
10. FOR ALL 文件名 n（包括空字符串、仅由路径成分或控制字符组成的字符串，以及含任意 Unicode 字符的字符串），File_Name_Sanitizer 对 n 处理 1 次与处理 2 次的结果 SHALL 相同（幂等属性），且处理结果 SHALL 非空、不含第 5 条所列字符、长度不超过第 8 条的上限，在 Windows_Client 上还 SHALL 不以空格或“.”结尾且不满足第 6 条的保留名称条件，并作为文件名位于 Receive_Directory 内。
11. IF 将临时文件重命名为最终文件名时 Receive_Directory 中已存在同名文件（Windows_Client 上比较时不区分大小写）, THEN THE Transfer_Service SHALL 在扩展名前（无扩展名时在文件名末尾）追加“ (n)”，n 取能使文件名在 Receive_Directory 中不重复的最小正整数；若追加后超过第 8 条的长度上限，则先截断文件名主干再追加；已有文件的名称和内容保持不变。
12. WHILE 文件正在接收, THE Transfer_Service SHALL 将数据写入临时文件，仅在该文件通过需求 17 第 1 条的完整性校验后才将临时文件重命名为最终文件名，使该文件在通过校验前不会以最终文件名出现在 Receive_Directory 中。
13. IF 用户在设置中选择的新 Receive_Directory 不可写入（如没有写入权限或位于只读存储）, THEN THE System SHALL 保持原 Receive_Directory 设置不变，并显示提示说明所选目录不可写入。
14. WHEN 接收方接受 File_Offer（包括按需求 15 第 2 条自动接收）且 Receive_Directory 不存在, THE Transfer_Service SHALL 在写入第一个文件的数据前创建 Receive_Directory 及其缺失的上级目录。
15. IF 接收方接受 File_Offer 时 Receive_Directory 无法创建或不可写入（如所在可移动存储已移除，或目录访问权限已被撤销）, THEN THE Transfer_Service SHALL 结束该 Transfer_Task 且不写入任何文件，在接收方显示接收目录不可用的提示并提供进入 Receive_Directory 设置的入口，同时在发送方显示对方无法保存文件的提示。

### Requirement 17: 文件完整性校验与断点续传

**User Story:** 作为用户，我希望收到的文件与原文件完全一致，并且大文件传输中断后可以接着传，以便节省时间并避免损坏的文件。

#### Acceptance Criteria

1. WHEN 一个文件的已接收字节数达到 File_Offer 中该文件的大小, THE Transfer_Service SHALL 对完整的接收文件（包括续传前已接收的部分）计算完整性校验值，并与发送方为该文件提供的完整性校验值比对。
2. IF 完整性校验值不一致或已接收字节数超过 File_Offer 中该文件的大小, THEN THE Transfer_Service SHALL 删除该文件的临时文件且不生成最终文件，在发送方和接收方将该文件状态显示为"校验失败"，并继续传输同一 Transfer_Task 中的其余文件。
3. IF 文件传输过程中 Session 断开, THEN THE Transfer_Service SHALL 保留当前文件已接收部分的临时文件及该 Transfer_Task 中已完成的文件，将该 Transfer_Task 标记为"已中断"，并在发送方和接收方为该 Transfer_Task 提供"继续"按钮。
4. WHEN 发送方或接收方用户对"已中断"的 Transfer_Task 点击"继续"且双方在 10 秒内重新建立 Session, THE Transfer_Service SHALL 在接收方无需再次确认 File_Offer 的情况下，跳过该 Transfer_Task 中状态为"已完成"的文件，并从接收方临时文件已写入的字节数处继续传输未完成的文件，不重新传输已写入临时文件的数据。
5. IF 用户点击"继续"时源文件的文件大小或最后修改时间与发送方发出 File_Offer 时记录的值不同，或接收方该文件的临时文件已不存在, THEN THE Transfer_Service SHALL 丢弃该文件已有的临时文件并从第 0 字节重新传输该文件。
6. IF 接收过程中写入临时文件失败（如存储空间耗尽或失去写入权限）, THEN THE Transfer_Service SHALL 停止该 Transfer_Task 的数据传输，保留已写入的临时文件，将该文件标记为"写入失败"、将该 Transfer_Task 标记为"已中断"，并在发送方和接收方显示写入失败的原因。
7. WHEN Android_App 或 Windows_Client 启动，以及此后运行期间每满 1 小时, THE Transfer_Service SHALL 删除不属于正在进行的 Transfer_Task 且最后一次写入时间距当前时间超过 24 小时的临时文件。
8. FOR ALL 文件内容、任意中断次数与任意中断字节位置，在源文件未被修改的前提下，经一次或多次续传完成并通过完整性校验的文件 SHALL 与源文件逐字节一致（续传等价属性）。
9. WHEN Android_App 或 Windows_Client 启动且存在上次运行结束时尚未完成的 Transfer_Task（包括应用被强制结束时正在进行的 Transfer_Task）, THE Transfer_Service SHALL 将该 Transfer_Task 显示为"已中断"并提供"继续"按钮。
10. IF 用户点击"继续"后 10 秒内未能与对方重新建立 Session, THEN THE Transfer_Service SHALL 保持该 Transfer_Task 为"已中断"状态、保留其临时文件，并显示表明无法连接对方的提示。

### Requirement 18: 传输进度、取消与断线检测

**User Story:** 作为用户，我希望看到传输进度并能随时取消，以便掌握传输状态。

#### Acceptance Criteria

1. WHILE Transfer_Task 正在进行, THE System SHALL 在发送方和接收方至少每 500 毫秒更新一次当前文件及整个 Transfer_Task 的以下信息：进度百分比（已传输字节数占总字节数的比例，向下取整为 0 至 100 的整数）、已传输字节数与总字节数、当前速率（最近 3 秒内平均每秒传输的字节数）和预计剩余时间（剩余字节数除以当前速率；当前速率为 0 时显示为“未知”）。
2. WHEN 发送方用户对已发出 File_Offer、正在等待接收方响应的 Transfer_Task 点击“取消”，或发送方、接收方任一方用户对正在进行的 Transfer_Task 点击“取消”, THE Transfer_Service SHALL 在 2 秒内停止该 Transfer_Task 的全部数据收发，在本机将该 Transfer_Task 显示为“已取消”，并向对方发送该 Transfer_Task 的取消通知。
3. WHEN 接收方的 Transfer_Task 被取消（由接收方用户取消或接收方收到发送方的取消通知）, THE Transfer_Service SHALL 在 5 秒内删除接收方该 Transfer_Task 中全部未完成文件的临时文件，并保留该 Transfer_Task 中已显示为“已完成”的文件。
4. WHEN 接收方对一个文件的完整性校验通过, THE System SHALL 在 2 秒内在发送方和接收方将该文件状态显示为“已完成”，并将该文件的进度百分比显示为 100。
5. WHILE Session 已建立, WHEN Transfer_Service 连续 5 秒未向对方发送任何 Protocol_Message, THE Transfer_Service SHALL 向对方发送 1 次 Heartbeat。
6. IF Session 连续 15 秒未收到对方的任何 Protocol_Message（包括 Heartbeat）, THEN THE Transfer_Service SHALL 判定 Session 已断开并关闭该连接，并在每个进行中的 Transfer_Task 上显示“已中断”状态及指明对方无响应的中断原因。
7. WHEN Endpoint 收到对方针对某 Transfer_Task 的取消通知, THE System SHALL 在 1 秒内停止该 Transfer_Task 的数据收发，将该 Transfer_Task 显示为“对方已取消”，并关闭该 Transfer_Task 仍在显示的 File_Offer 确认界面。
8. IF 对方关闭了 Session 的连接，或本机用于该 Session 的 Local_Network 接口变为不可用（如 Wi‑Fi 断开或热点关闭）, THEN THE Transfer_Service SHALL 在 2 秒内判定 Session 已断开，并在每个进行中的 Transfer_Task 上显示“已中断”状态及指明“连接被对方关闭”或“本机网络已断开”的中断原因。
9. WHEN 用户对“已中断”的 Transfer_Task 点击“取消”, THE Transfer_Service SHALL 在 1 秒内在本机将该 Transfer_Task 显示为“已取消”，并在与对方下一次建立 Session 后 2 秒内向对方发送该 Transfer_Task 的取消通知。

### Requirement 19: 打开文件及文件所在位置

**User Story:** 作为用户，我希望从传输记录或通知中直接打开收到的文件或其所在文件夹，以便快速找到文件。

#### Acceptance Criteria

1. WHEN 用户在 Windows_Client 的传输记录或系统通知中，对状态为“已完成”的已接收文件点击“打开所在位置”, THE File_Locator SHALL 打开 Windows 资源管理器窗口，显示该文件所在的目录并选中该文件。
2. WHEN 用户在 Android_App 的传输记录或系统通知中，对状态为“已完成”的已接收文件点击“打开所在位置”, THE File_Locator SHALL 通过系统文件管理器打开该文件所在的目录。
3. IF 用户在 Android_App 中点击“打开所在位置”或“打开接收目录”时，设备上没有能打开目录的文件管理器应用, THEN THE File_Locator SHALL 显示对应文件或 Receive_Directory 的完整存储路径，并提供“复制路径”按钮。
4. WHEN 用户在传输记录或系统通知中，对状态为“已完成”的已接收文件点击“打开文件”, THE File_Locator SHALL 使用系统中与该文件类型关联的默认应用打开该文件，且被打开的应用能读取该文件的完整内容（Windows_Client 按文件扩展名确定关联应用；Android_App 按 MIME 类型确定，如果有多个可用应用且未设置默认应用，则显示系统应用选择器）。
5. IF 用户点击“打开文件”时，系统中没有可打开该文件类型的应用（Windows 中没有与该扩展名关联的应用，或 Android 中没有能处理该 MIME 类型的应用）, THEN THE File_Locator SHALL 显示“没有可打开此文件的应用”提示，并提供“打开所在位置”按钮。
6. IF 用户点击“打开文件”或“打开所在位置”时，传输记录中保存的路径处已不存在该文件（文件已被移动、重命名或删除）, THEN THE File_Locator SHALL 显示“文件已被移动或删除”提示，提供“打开接收目录”按钮，并保留该传输记录不变。
7. WHEN 用户点击“打开接收目录”, THE File_Locator SHALL 在系统文件管理器中打开当前设置的 Receive_Directory（如果用户已按需求 16 修改过 Receive_Directory，则打开修改后的目录）。
8. THE System SHALL 在传输记录中为每个状态为“已完成”的已接收文件显示完整存储路径。该路径从盘符（Windows）或存储卷根目录（Android）开始，到实际保存的文件名为止（即经 File_Name_Sanitizer 处理、追加重名序号后的名称，含扩展名），显示时不省略路径中的任何部分。
9. WHEN 用户点击“复制路径”按钮, THE Clipboard_Manager SHALL 将界面上显示的完整存储路径写入系统剪贴板，并显示“已复制”提示。
10. IF 已接收文件的状态不是“已完成”（如“已中断”、“校验失败”或“写入失败”）, THEN THE System SHALL 在传输记录中不为该文件显示“打开文件”和“打开所在位置”按钮。
11. IF 用户点击“打开接收目录”时，Receive_Directory 不存在或本应用无权访问, THEN THE File_Locator SHALL 显示“接收目录不存在或无法访问”提示，并显示 Receive_Directory 的完整路径和“复制路径”按钮。

### Requirement 20: 传输记录

**User Story:** 作为用户，我希望查看以往发送和接收的文字与文件，以便回顾、再次复制或打开。

#### Acceptance Criteria

1. THE History_Store SHALL 持久化保存每条已发送和已接收的 Text_Message 记录（消息 ID、完整原文、记录时间、对方设备 ID、对方设备显示名称、方向和状态），以及每个文件的传输记录（记录时间、对方设备 ID、对方设备显示名称、方向、原始文件名、实际保存路径（仅已接收文件）、文件大小（字节）和最终状态），并保留每条记录直至用户删除该记录或清空记录。其中：记录时间为本机发出或收到该 Text_Message 或 File_Offer 时的本机时间；方向为“发送”或“接收”；状态与会话界面或传输界面中该条目最后显示的状态一致。
2. WHEN 用户打开传输记录界面（包括应用重启或系统重启后打开）, THE System SHALL 在 History_Store 中记录数不超过 10,000 条时于 2 秒内显示其中保存的全部记录，按记录时间从早到晚排列；记录时间相同时，按保存先后排列。
3. FOR ALL UTF‑8 编码长度为 1 至 1,048,576 字节且不含 Unpaired_Surrogate 的 Text_Message 文本 t（包括需求 12 所列的全部特殊字符），保存到 History_Store 后再读取（包括应用重启后读取） SHALL 得到与 t 逐个 Unicode 码点完全一致的文本（往返属性）。
4. WHEN 用户删除一条文件传输记录，且该记录所属的 Transfer_Task 不是未完成的 Transfer_Task（未完成的 Transfer_Task 指处于以下任一状态的 Transfer_Task：等待接收方响应，即 File_Offer 已发出或已收到而接收方尚未点击“接收”或“拒绝”；传输中；正在续传，即用户点击“继续”后尚未恢复数据传输、也未按需求 17 第 10 条判定续传失败；“已中断”）, THE System SHALL 按以下方式处理：若该记录为已接收文件且文件仍位于实际保存路径，则显示包含“仅删除记录”“同时删除本地文件”“取消”三个选项的对话框并按所选项执行；若删除本地文件失败，则保留该文件和该记录，并显示表明本地文件删除失败的提示；其他情况下仅删除该记录，不删除任何本地文件（包括发送方的源文件）。
5. WHEN 用户在第 11 条所述的“清空记录”确认对话框中点击“清空”, THE System SHALL 先取消此时全部未完成的 Transfer_Task（等待接收方响应、传输中以及已重新建立 Session 的正在续传 Transfer_Task 按需求 18 第 2、3 条取消，其中本机为接收方且尚未响应 File_Offer 的按需求 15 第 3 条拒绝；“已中断”以及尚未重新建立 Session 的正在续传 Transfer_Task 按需求 18 第 9 条取消，未完成文件的临时文件按需求 18 第 3 条删除），再从 History_Store 删除此时已存在的所有对方设备的全部 Text_Message 记录和文件传输记录，使传输记录界面在点击“清空”后 3 秒内不再显示任何被删除的记录、应用重启后同样不再显示，且被取消的 Transfer_Task 不按需求 17 第 9 条显示为“已中断”；此后收到的与被删除记录相关的应答（如送达确认）不重新创建这些记录；Receive_Directory 中已接收完成的文件不被删除或移动。
6. THE System SHALL 在传输记录界面为每条记录显示记录时间（精确到分钟）、对方设备显示名称、方向和状态；对 Text_Message 记录显示文字内容并提供“复制”按钮；对文件记录显示原始文件名和文件大小，并为状态为“已完成”的已接收文件记录提供“打开文件”和“打开所在位置”按钮。
7. THE History_Store SHALL 为同一消息 ID 的 Text_Message、同一 Transfer_Task 中的同一文件各只保存 1 条记录，并在其状态变化时（包括按需求 11 重试发送、按需求 17 续传完成）更新该记录的状态，不新增记录。
8. IF History_Store 读取或写入失败, THEN THE System SHALL 显示表明传输记录读取或保存失败的提示，并继续正常发送和接收 Text_Message 与文件。
9. WHILE 文件传输记录所属的 Transfer_Task 处于等待接收方响应、传输中或正在续传状态, THE System SHALL 不为该记录显示删除操作（如需删除，用户须先按需求 18 第 2 条取消该 Transfer_Task，或作为接收方按需求 15 第 3 条拒绝该 File_Offer），并在该 Transfer_Task 离开上述三种状态后 1 秒内为该记录显示删除操作。
10. WHEN 用户删除一条所属 Transfer_Task 为“已中断”的文件传输记录, THE System SHALL 显示对话框，说明删除该记录将取消整个 Transfer_Task（包括其中全部未完成的文件）：该记录为已接收文件且文件仍位于实际保存路径时提供“仅删除记录”“同时删除本地文件”“取消”三个选项，否则提供“删除”和“取消”两个选项。用户选择“取消”以外的选项后，System 先按需求 18 第 9 条取消该 Transfer_Task（未完成文件的临时文件按需求 18 第 3 条删除），再删除该记录并按所选项处理本地文件；删除本地文件失败时按第 4 条处理，已执行的取消不撤销；同一 Transfer_Task 的其他文件记录保留，并按第 7 条更新状态。用户选择“取消”或关闭对话框时，System 不删除任何记录或文件，该 Transfer_Task 保持“已中断”并保留其临时文件。
11. WHEN 用户点击“清空记录”, THE System SHALL 显示带有“清空”和“取消”按钮的确认对话框，说明将删除本机全部传输记录；存在至少 1 个未完成的 Transfer_Task 时，对话框还说明这些 Transfer_Task 将被一并取消，并显示其数量（该数量变化后 1 秒内更新）。用户点击“取消”或关闭对话框时，System 不删除任何记录、不取消任何 Transfer_Task，也不改变任何 Transfer_Task 的状态和已传输字节数。

### Requirement 21: 后台运行与系统通知

**User Story:** 作为用户，我希望应用切到后台或锁屏时传输不中断，并能通过通知快速处理收到的内容。

#### Acceptance Criteria

1. WHILE Transfer_Task 正在进行, WHEN Android_App 进入 Background_State, THE Android_App SHALL 继续传输该 Transfer_Task，直至其完成、被取消或按需求 18 第 6 条被判定为“已中断”，不因进入 Background_State 而暂停传输或关闭 Session；同时为每个进行中的 Transfer_Task 在通知栏显示进度通知（包含对方设备显示名称、当前文件名和进度百分比，至少每 1 秒更新 1 次），并在该 Transfer_Task 结束后 2 秒内移除该进度通知。
2. WHERE 用户在 Android_App 设置中开启“后台接收”（默认关闭）, WHILE Android_App 处于 Background_State, THE Android_App SHALL 持续接受入站 Session 和 Pairing_Request、通过 Session 接收 Text_Message 和 File_Offer、按需求 5 第 1 条的周期发送 Discovery_Announcement，并显示一条表明后台接收已开启的常驻通知，直至用户关闭该设置；用户点击该常驻通知时打开 Android_App 主界面。
3. WHEN 用户关闭 Windows_Client 主窗口, THE Windows_Client SHALL 隐藏主窗口、在系统托盘保留 Windows_Client 图标，并继续运行 Discovery_Service、Transfer_Service 和全部进行中的 Transfer_Task，直至用户通过托盘菜单选择“退出”；用户单击托盘图标时恢复显示主窗口。
4. WHEN 用户在系统托盘菜单中选择“退出”且不存在进行中的 Transfer_Task, THE Windows_Client SHALL 在 3 秒内停止 Discovery_Service 和 Transfer_Service、关闭全部 Session、移除托盘图标并结束运行。
5. IF 用户在系统托盘菜单中选择“退出”时存在进行中的 Transfer_Task, THEN THE Windows_Client SHALL 显示带有“退出”和“取消”按钮的“仍有传输进行中，确认退出？”对话框；用户点击“退出”时，将全部进行中的 Transfer_Task 标记为“已中断”并保留临时文件（之后可按需求 17 第 4 条继续），再按第 4 条退出；用户点击“取消”或关闭对话框时，Windows_Client 继续运行且全部 Transfer_Task 不受影响。
6. WHILE Android_App 或 Windows_Client 处于 Background_State, WHEN 收到 Text_Message, THE System SHALL 在 2 秒内显示系统通知，通知包含发送方设备显示名称、文字预览（Text_Message 的前 100 个 Unicode 码点，超出时以“…”结尾）和“复制”操作，且同一消息 ID 的 Text_Message 只通知 1 次；用户点击“复制”时，Clipboard_Manager 按需求 13 第 3 条将该 Text_Message 的完整原文写入系统剪贴板。
7. WHILE Android_App 或 Windows_Client 处于 Background_State, WHEN 接收的一个文件状态变为“已完成”, THE System SHALL 在 2 秒内显示系统通知，通知包含发送方设备显示名称、文件名以及“打开文件”和“打开所在位置”操作，两项操作分别按需求 19 第 4 条和第 1、2 条执行。
8. WHILE Android_App 或 Windows_Client 处于 Background_State, WHEN 收到需要用户确认的 File_Offer（接收方未对该发送方开启需求 15 第 2 条所述“自动接收文件”）或 Pairing_Request（不包括按需求 7 第 8、9 条被自动拒绝的请求）, THE System SHALL 在 2 秒内显示包含发起方设备显示名称和请求类型（文件接收请求另含文件数量）的系统通知，在用户点击通知后打开对应的确认界面，并在该请求因超时、被取消或已被处理而结束后 2 秒内移除该通知。
9. WHILE Android_App 处于 Background_State 超过 30 秒、“后台接收”处于关闭状态且不存在进行中的 Transfer_Task, THE Android_App SHALL 关闭全部 Session、停止发送 Discovery_Announcement 并拒绝新的入站连接，直至 Android_App 回到前台。
10. WHILE Android_App 或 Windows_Client 处于 Background_State, IF 某 Transfer_Task 变为“已中断”或其中某个文件被标记为“读取失败”、“校验失败”或“写入失败”, THEN THE System SHALL 在 2 秒内显示系统通知，通知包含对方设备显示名称、受影响的文件名和对应状态，并在用户点击通知后打开与该对方的会话界面。
11. IF Android_App 或 Windows_Client 的系统通知权限未被授予或已被用户在系统设置中关闭, THEN THE System SHALL 除不显示通知外照常执行第 1、2、3 条所述的后台传输与接收，并在应用主界面显示说明后台收到的内容将不会弹出通知的提示，以及打开系统通知设置的按钮。

### Requirement 22: 协议帧编解码

**User Story:** 作为开发者，我希望 Android_App 和 Windows_Client 使用统一、可验证的二进制帧协议，以便两端互通，且数据不因分包、粘包而错乱。

#### Acceptance Criteria

1. THE Frame_Encoder SHALL 将每个 Protocol_Message 序列化为包含协议标识、消息类型、载荷长度和载荷的 Frame，其中载荷长度字段的值等于该 Frame 载荷的实际字节数。
2. WHEN Frame_Decoder 收到一个完整的 Frame 字节序列，且该 Frame 的协议标识匹配、消息类型为本机 Protocol_Version 已定义的类型、载荷长度在 0 至最大载荷长度之间（含两端）、载荷符合该消息类型在协议规范中定义的格式, THE Frame_Decoder SHALL 将该字节序列解析为对应的 Protocol_Message。
3. WHEN 一个 Frame 的字节分多次到达, THE Frame_Decoder SHALL 缓存已到达部分，直到该 Frame 完整到达后再解析，且每个连接上缓存的未解析字节数不超过 Frame 头部（协议标识、消息类型和载荷长度字段）长度与最大载荷长度之和。
4. WHEN 一次读取的数据中包含多个 Frame, THE Frame_Decoder SHALL 按到达顺序解析出其中全部完整 Frame 对应的 Protocol_Message，并按第 3 条缓存末尾未完整到达的 Frame 字节。
5. IF Frame 的协议标识不匹配、消息类型不是本机 Protocol_Version 已定义的类型、载荷长度超过最大载荷长度，或载荷不符合该消息类型在协议规范中定义的格式（如缺少必需字段、字段长度超出载荷边界）, THEN THE Frame_Decoder SHALL 先按顺序输出该 Frame 之前已完整到达的全部 Protocol_Message，再返回标明具体错误类型的解码错误，且不输出该 Frame 对应的任何 Protocol_Message；对于协议标识、消息类型和载荷长度错误，Frame_Decoder SHALL 在 Frame 头部到达后立即返回解码错误，不等待载荷字节到达（Text_Message 文本字节不是合法 UTF‑8 序列的情况不属于解码错误，按需求 12 第 7 条处理）。
6. IF Frame_Decoder 返回解码错误, THEN THE Transfer_Service SHALL 关闭对应连接、不再解析该连接上此后到达的任何字节、记录错误类型，并在界面上为该连接上进行中的 Transfer_Task 显示表明协议数据错误的中断原因。
7. WHEN Frame_Decoder 收到的 Frame 中包含本机 Protocol_Version 未定义的可选字段, THE Frame_Decoder SHALL 忽略该字段、正常解析其余字段且不返回解码错误，解析结果与不含该字段的同一 Frame 的解析结果相同。
8. FOR ALL 有效 Protocol_Message m，Frame_Decoder 解码 Frame_Encoder 编码 m 所得字节序列的结果 SHALL 与 m 逐字段相等（往返属性）。
9. FOR ALL 由当前协议版本的 Frame_Encoder 生成的有效 Frame 字节序列 b，先解码再编码 SHALL 得到与 b 完全相同的字节序列（规范编码属性）。
10. FOR ALL 有效 Protocol_Message 序列，将其编码后的字节流按任意位置切分（包括每段 1 字节的切分和整段一次输入）并逐段输入 Frame_Decoder，SHALL 得到与原序列相同的 Protocol_Message 序列（切分不变属性）。
11. THE System SHALL 在协议规范中定义一个固定的最大载荷长度，其值不小于容纳文本 UTF‑8 编码长度为 1,048,576 字节的 Text_Message 所需的载荷长度，且不超过 4,194,304 字节（4 MiB）。
12. IF 待编码的 Protocol_Message 的载荷长度超过最大载荷长度, THEN THE Frame_Encoder SHALL 返回编码错误，且不输出该 Protocol_Message 的任何字节。
13. IF 连接关闭时 Frame_Decoder 中仍缓存有未完整到达的 Frame 字节, THEN THE Frame_Decoder SHALL 丢弃这些字节，且不输出由这些字节构成的任何 Protocol_Message。

### Requirement 23: 发现公告与二维码载荷编解码

**User Story:** 作为开发者，我希望发现公告和二维码内容有明确的格式和可靠的解析，以便两端正确识别对方并完成配对。

#### Acceptance Criteria

1. THE Announcement_Codec SHALL 将 Discovery_Announcement 序列化为长度不超过 512 字节的字节序列，并将收到的字节序列解析为 Discovery_Announcement；设备显示名称由 32 个辅助平面 Unicode 字符（如表情符号）组成时，512 字节的上限同样成立。
2. IF 收到的数据报长度超过 512 字节、缺少设备 ID、设备显示名称、设备类型、Protocol_Version、端口中的任一字段、设备显示名称无法解码为合法的 Unicode 字符串或长度不在 1 至 32 个字符范围内、设备类型不是 Android 或 Windows，或者端口不在 1 至 65535 范围内, THEN THE Discovery_Service SHALL 丢弃该数据报且不向用户显示提示，设备列表中各 Endpoint 的显示内容保持不变，需求 5 准则 4 所述的 10 秒移除计时也不因该数据报重置。
3. FOR ALL 满足准则 2 所列全部约束的有效 Discovery_Announcement a（设备显示名称可包含任意 Unicode 平面中的字符、控制字符和表情符号 ZWJ 组合序列，但不含 Unpaired_Surrogate），Announcement_Codec 解析序列化 a 所得的结果 SHALL 在设备 ID、设备显示名称（逐个 Unicode 码点比较）、设备类型、Protocol_Version 和端口上与 a 完全相等（往返属性）。
4. THE QR_Codec SHALL 将 QR_Payload 序列化为可编码进二维码的文本，并将扫描得到的文本解析为 QR_Payload。
5. THE QR_Codec SHALL 使序列化后的 QR_Payload 文本 UTF‑8 编码长度不超过 512 字节；QR_Payload 包含 4 个 IP 地址且设备显示名称由 32 个辅助平面 Unicode 字符组成时，该上限同样成立。
6. IF 扫描得到的文本 UTF‑8 编码长度超过 512 字节、缺少协议规范定义的 QR_Payload 格式标识、缺少设备 ID、设备显示名称、IP 地址、Transfer_Service 监听端口、身份公钥指纹、Pairing_Token 中的任一字段、IP 地址数量为 0 或超过 4 个、任一 IP 地址不是合法的 IPv4 地址、端口不在 1 至 65535 范围内，或者设备显示名称长度不在 1 至 32 个字符范围内, THEN THE QR_Codec SHALL 返回解析错误，且不输出任何部分解析的 QR_Payload。
7. FOR ALL 满足准则 6 所列全部约束的有效 QR_Payload p（设备显示名称可包含任意 Unicode 平面中的字符，但不含 Unpaired_Surrogate），QR_Codec 解析序列化 p 所得的结果 SHALL 在全部字段上与 p 相等，设备显示名称逐个 Unicode 码点一致，且 IP 地址的数量和顺序保持不变（往返属性）。
8. IF 生成 QR_Payload 时本机活动 Local_Network 接口的 IP 地址超过 4 个, THEN THE Pairing_Service SHALL 仅将其中 4 个互不相同的 IP 地址写入 QR_Payload。
9. WHEN Announcement_Codec 解析的 Discovery_Announcement 包含当前协议版本未定义的字段, THE Announcement_Codec SHALL 忽略该字段并正常解析其余字段。
10. WHEN QR_Codec 解析的文本包含当前协议版本未定义的字段, THE QR_Codec SHALL 忽略该字段并正常解析其余字段。

### Requirement 24: 跨平台协议一致性与版本兼容

**User Story:** 作为开发者，我希望两端遵循同一份协议规范并能处理版本差异，以便独立开发、独立升级两端时仍能互通。

#### Acceptance Criteria

1. THE System SHALL 以一份不依赖特定编程语言、操作系统或平台 API 的协议规范定义 Protocol_Message、Frame、Discovery_Announcement 和 QR_Payload 的格式，在规范中为每个字段明确规定字节顺序、长度或长度界定方式以及字符编码，并为每种 Protocol_Message 类型和每个字段标明引入它的 Protocol_Version。
2. THE System SHALL 在协议规范中为每种 Protocol_Message 类型至少提供 1 个 Test_Vector，为需求 12 第 2 至 4 条所列的每一类字符至少提供 1 个包含该类字符的 Text_Message Test_Vector，并为需求 22 第 5 条所列的每种解码错误类型以及需求 12 第 7 条所述的非法 UTF‑8 文本各至少提供 1 个无效字节序列样例及其期望的错误结果。
3. THE Frame_Encoder 和 Frame_Decoder SHALL 在 Android_App 与 Windows_Client 中均通过全部 Test_Vector，即：编码每个 Test_Vector 中的 Protocol_Message 所得字节序列与其期望字节序列逐字节一致；解码每个期望字节序列所得 Protocol_Message 与对应 Test_Vector 中的 Protocol_Message 相等；对每个无效字节序列样例返回其期望的错误结果。
4. WHEN 两个 Endpoint 之间建立用于配对或 Session 的连接, THE System SHALL 在该连接上发送任何 Pairing_Request、Text_Message 或 File_Offer 之前交换双方的 Protocol_Version。
5. IF 双方交换的 Protocol_Version 主版本号不同, THEN THE System SHALL 关闭该连接且不处理该连接上的任何 Pairing_Request、Text_Message 或 File_Offer，保持双方的 Trusted_Device 列表不变，并在双方显示“对方版本不兼容，请将两端升级到最新版本”提示及双方的 Protocol_Version（格式为“主版本号.次版本号”）。
6. WHILE 双方交换的 Protocol_Version 主版本号相同而次版本号不同, THE System SHALL 在该连接上仅发送两者中较低次版本号已定义的 Protocol_Message 类型和字段。
7. THE System SHALL 在协议规范中为 Discovery_Announcement 和 QR_Payload 提供样例及其期望序列化结果，样例中至少包括：Discovery_Announcement 和 QR_Payload 各 1 个设备显示名称为 32 个字符且含中文字符和表情符号的样例，以及 1 个包含 2 个或以上 IP 地址的 QR_Payload 样例。
8. THE Announcement_Codec 和 QR_Codec SHALL 在 Android_App 与 Windows_Client 中均满足：序列化第 7 条所述的每个样例所得结果与其期望序列化结果逐字节一致，且解析每个期望序列化结果所得 Discovery_Announcement 或 QR_Payload 与对应样例相等。
9. IF 连接建立后 10 秒内未完成 Protocol_Version 交换, THEN THE System SHALL 关闭该连接，并在发起连接的一方显示表示对方未响应版本协商的提示。

### Requirement 25: 并发与资源占用

**User Story:** 作为用户，我希望传输大文件时仍能发文字、界面不卡顿，以便同时处理多项传输。

#### Acceptance Criteria

1. WHILE 与某 Endpoint 之间存在进行中的 Transfer_Task（本机为发送方或接收方均适用）, WHEN 用户向该 Endpoint 发送 UTF‑8 编码长度不超过 64 KiB（65,536 字节）的 Text_Message, THE Transfer_Service SHALL 在用户点击“发送”或“发送剪贴板”后 2 秒内收到该 Text_Message 的送达确认并将其标记为“已送达”，无需等待该 Transfer_Task 结束（在双方空闲时网络往返时延不超过 100 毫秒的 Local_Network 中测量）。
2. THE Transfer_Service SHALL 支持同时与至少 3 个不同 Endpoint 各保持 1 个 Session，并在这些 Session 上同时各进行 1 个 Transfer_Task，期间每个进行中 Transfer_Task 的已传输字节数在任意连续 5 秒内均有增加。
3. THE Transfer_Service SHALL 以流式方式读写文件，使每个进行中的 Transfer_Task 引起的 Endpoint 进程内存占用增量（相对于该 Transfer_Task 开始前）不超过 16 MiB，且该增量不随文件大小增长（包括单个文件超过 4 GiB 的情况）。
4. WHILE 至少 1 个 Transfer_Task 正在进行（包括 3 个 Transfer_Task 同时进行的情况）, WHEN 用户执行点击按钮、切换界面、滚动列表或在输入框中输入文字的操作, THE System SHALL 在操作后 200 毫秒内在界面上显示该操作的可见反馈（按钮状态变化、目标界面显示、列表滚动或输入字符回显），需等待网络应答的结果除外。
5. WHILE 与某 Endpoint 之间存在进行中的 Transfer_Task, WHEN 本机向该 Endpoint 发送或从该 Endpoint 接收 Text_Message, THE Transfer_Service SHALL 使该 Transfer_Task 继续传输，不暂停、不中断，且已传输字节数不回退。
6. WHEN 用户在与某 Endpoint 之间已有进行中的 Transfer_Task 时向该 Endpoint 确认发送文件, THE Transfer_Service SHALL 立即发送新的 File_Offer，并在接收方接受后与已有 Transfer_Task 同时传输，不等待已有 Transfer_Task 结束。
7. IF 某个 Transfer_Task 被取消、校验失败或写入失败，或与某个 Endpoint 的 Session 断开, THEN THE Transfer_Service SHALL 使不属于该 Transfer_Task 或该 Session 的其他进行中 Transfer_Task 继续传输，且不改变其状态和已传输字节数。
