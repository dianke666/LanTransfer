# 跨端互通测试

规划在 `android/tools/peer-cli`（JVM）与 `windows/tools/LanTransfer.PeerCli`（.NET）之间运行场景脚本。两个对端使用各自真实的 Protocol、Domain、Engine 和文件系统存储，通过真实 TCP / UDP 通信，由场景脚本经 JSON Lines 控制接口（stdin / stdout）驱动，每个场景按两个方向各运行一次。

> 当前状态：两端核心工程与 UI 已实现并通过各自的单元/属性测试；跨端自动化互通测试脚本与 `PeerCli` 驱动（`tasks.md` 任务 22）已按计划暂缓，留待后续需要自动化互通回归时补齐。
