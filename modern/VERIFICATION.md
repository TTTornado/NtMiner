# Preview 0.4 验证记录

日期：2026-10-21。环境：Windows x64，便携 .NET SDK 10.0.402。

## 0.4 侧边栏收起时的布局修复（2026-10-22）

用户反馈"挖矿配置页在侧边栏收回时显示不正常"，随后指出修复后"内容宽度变成固定的，不像其他页面一样适配窗口"。加截图验收项后确认问题并修复：

1. **设备下拉框溢出**：配置页外层是 `ScrollViewer > StackPanel MaxWidth=860 HorizontalAlignment=Stretch`。`ScrollViewer` 按内容固有宽度测量子元素，`Stretch` 在滚动方向上不生效，右列下拉框被裁掉。
2. **底部说明文字被压成竖排单字**：`NavigationView.PaneFooter` 里的文字按展开态 `Margin="16,16,12,20"` 排版，收起后侧边栏只剩约 48 px。改为随侧边栏状态隐藏。
3. **内容宽度被固定**：`MaxWidth="860"` 本身把配置页限死在 860 px，而运行概览、硬件仪表盘、运行日志三个页面都没有该限制。第一次修复时只把 `Stretch` 改成 `Center`，反而让它恒为 860 px 且不再响应窗口宽度。移除 `MaxWidth` 后恢复与其他页面一致的伸缩行为。

宽度由验收量化确认：窗口 1262 px 时配置卡片宽 **746 px**，收窄到 820 px 后为 **378 px**，随窗口线性变化。

WinUI 验收由 22 项增至 24 项：侧边栏收起渲染检查，以及对比宽/窄两种窗口宽度下的卡片实际宽度。后台 35 项通过、WPF 界面验收通过、WinUI 24 项通过，0 警告 0 错误；宽、窄、收起三张截图均已目视确认。

## 0.4 启动流程改为按需构建（2026-10-22）

原 `Start-WinUI.cmd` → `BuildWinUI.ps1 -Action Run` 每次启动都无条件先还原再构建，实测 **21 秒**；即使没有任何源码改动也要付出该成本，且在程序已打开时必然因 DLL 被占用而失败。改为按需构建：`Start-*` 检查源码是否比构建标记新，未变化时直接启动。

实测四种场景（Windows 11 / i5-13400 / 便携 SDK 10.0.402）：

| 场景 | 改前 | 改后 |
|---|---|---|
| 无源码变更启动（WinUI） | 21 s，且必构建 | **2 s，不构建** |
| 修改一个 .cs 后启动 | 21 s | 10.7 s，自动增量构建 |
| 构建完成后再启动 | 21 s | **2 s，不构建** |
| 程序已打开时再次启动 | 30 行 MSB3021/MSB3027 堆栈 | 一行提示，退出码 1 |

同时去掉每次启动的冗余还原：仅当 `packages.lock.json` 比 `project.assets.json` 新或资产文件缺失时才还原，符合条件时输出"依赖未变化，跳过还原"。

**两处经验证的设计更正**：
1. 初版用可执行文件时间戳判断过期，实测不可靠——MSBuild 在输出字节相同时保留旧文件，构建成功后 exe 时间戳仍是旧的，导致每次启动都重建。改为写 `artifacts\.build\<Configuration>-<目标>.stamp` 构建标记（已被 `.gitignore` 的 `artifacts/` 覆盖）。
2. 初版用 `throw` 报告占用冲突，PowerShell 会附带堆栈。改为 `Write-Host` + `exit 1`，`.cmd` 的 `pause` 仍能留住提示。

**编码问题**：脚本含中文消息，必须以 UTF-8 **带 BOM** 保存。Windows PowerShell 5.1 对无 BOM 脚本按 ANSI 解码，中文字符串会破坏语法解析。三个脚本已固定为 BOM 并在文件头注明；编辑后需复核编码。

回归：后台 35 项通过 0 失败，WPF 界面验收通过，WinUI 22 项通过，0 警告 0 错误。

## 0.4 会话状态机与重试策略抽取（2026-10-22）

把 `MiningSession` 中散落在 8 处的状态赋值收敛到 `SessionStateMachine`，把重试次数与退避规则抽成 `RetryPolicy`。属纯重构，不改行为：重构前后后台验收均为 33 项、WinUI 22 项通过；重构后新增 2 项纯逻辑验收，合计 **后台 35 项通过、0 失败**。

新增验收覆盖：Failed 为终态且不会被迟到的份额事件改回 Mining；Illegal 转换被拒绝且状态不变；用户主动停止导致的收尾不显示为失败；重试预算 3 次、关闭自动恢复与离线自测不重试、致命错误不重试、退避为 2/4/8 秒、异常入参不溢出。这 2 项不启动进程、不等待退避，全程毫秒级。

**重构过程中暴露并修复的一处缺陷**：初版在异常路径先 `Reset()` 再转 Failed，而 `Idle → Failed` 不在允许表内，导致失败后状态停留在 Idle，7 项与失败收尾相关的验收因此超时。改为由状态机自己决定 `Fault()` 的去向（Stopping 收尾转 Idle，其余转 Failed）。这个错误被现有回归测试当场拦住，说明重构前记录的基线是有效的。

## 0.4 硬件仪表盘（2026-10-22）

执行 `scripts/Build.ps1 -Action Test -Configuration Release` 与 `scripts/BuildWinUI.ps1 -Action Test -Configuration Release`。后台与 WPF Release 构建 0 警告、0 错误；后台验收 **33 项通过、0 失败**（新增 2 项）；WinUI 实际窗口验收 **22 项通过**（原 16 项 + 新增 6 项仪表盘检查）。

新增后台验收：显卡三分类（虚拟适配器 / 核显 / 独显，含"独显名称含 Radeon 不得判为核显"的回归断言）、`SupportsIntegratedOnly` 的三种结果（含未知硬件返回 null）、容量格式化，以及在本机实际执行一次完整探测且不抛异常。

本机实测探测结果（i5-13400 / B760M GAMING PLUS WIFI / 16 GB DDR5-6000 / WD Green SN350 1TB / Windows 11 26300）：处理器 10 核 16 线程、L3 20 MB；内存 1 条 16 GB 6000 MT/s；显卡识别为 `Todesk Virtual Display Adapter`（判为虚拟，已排除）与 `Intel(R) UHD Graphics 730`（判为核显）；BIOS H.10 / 2023-10-27。

**已验证的限制**：`System.Management` 在 WinUI 进程内抛 `PlatformNotSupportedException`（"only supported for Windows desktop applications"），UI 线程与后台线程表现一致；同一程序集在控制台宿主（Agent）中正常工作。因此探测改为经命名管道由 Agent 执行，`AgentResponse` 增加可选的 `Hardware` 字段。期间同时修复伙伴进程部署缺陷：原先的复制规则不递归 `runtimes\<rid>\lib\<tfm>\` 子目录，导致 Agent 加载 `System.Management` 失败；WinUI 与 Tests 两处复制目标均已改为递归。

界面证据：`artifacts/winui/dashboard-dark.png`（已目视检查摘要、模式对照、处理器分区与横幅隐藏）。仪表盘页不再显示与挖矿无关的模式横幅。本轮未启动真实内核，硬件数据来自 WMI 只读查询。

**未验证**：仪表盘在无 WMI 权限、虚拟化或最小化系统上的表现仅有代码路径上的分区降级处理，未在真实受限环境中执行；温度、功耗、显存实际容量未纳入本页。

## 0.4 核显接入与反馈验收（2026-10-22）

执行 `scripts/Build.ps1 -Action Test -Configuration Release` 和 Windows PowerShell 下的 `scripts/BuildWinUI.ps1 -Action Test -Configuration Release`。后台/WPF 与独立 WinUI Release 构建均为 0 警告、0 错误；后台 31 项通过、0 失败，WPF 界面回归通过，WinUI 16 项通过。

新增验收覆盖：CPU/全部 GPU/仅核显的 BzMiner 离线参数、裸 `--igpu` 和无钱包/矿池参数、GPU 模式禁用 CPU 挖矿、v2 配置迁移保留原设备选择及 v3 核显保存、真实日志形状的设备名与内存数字解析、设备名通过 IPC 传递、内存不足时停止清理且不重试、离线模式由后台强制不自动重试。状态展示分别验证零算力、新鲜正算力、超过 30 秒未更新、失败后隐藏算力。

WinUI 窗口覆盖仅核显联动、CPU 控件隐藏、GPU 离线演示启停、模拟设备标识、v3 保存和错误反馈。已目视检查 `artifacts/winui/igpu-demo-light.png`、`configuration-dark.png`、`igpu-memory-sample-dark.png`。带 `sample` 的截图来自受控状态注入，不能作为新一轮硬件测试证据。其他界面验收均只使用模拟进程，没有执行真实挖矿或连接矿池。

本轮没有覆盖原来的真实 profile、修改内核安装目录或提交/推送代码。完整输出见 `artifacts/validation.log`、`artifacts/winui-validation.log`，界面结果见 `artifacts/winui/result.json`。

已有核显硬件证据（2026-10-21）：官方 BzMiner v100.46，Intel UHD Graphics 730，驱动 32.0.101.7088，OpenCL 3.0 NEO。首次仅核显 PRL 离线任务报告需 1353 + 384 MB，只有 1636 MB 可用，跳过设备。用户释放内存后再次运行，启动前约 2847 MB 可用，45 秒限时内记录 22.04、14.70、22.05、27.58 GH/s，无错误行，结束后无残留内核进程。该次直接运行内核、未经过本次新增的 WinUI 交互；结果与日志在 `artifacts/bz-igpu-real/retry-2/`。短时正算力不能证明长期稳定性或矿池有效份额。

## 0.3 WinUI 新增验证

独立执行 `scripts/BuildWinUI.ps1 -Action Test -Configuration Release`，Windows App SDK 2.5.1，x64、未打包、自带 Windows App Runtime 依赖。Release 构建 0 警告、0 错误；实际 WinUI 窗口 9 项自动验收通过。

覆盖默认演示及无自动启动、SRBMiner 模拟指标、停止回到空闲、CPU 联动 BzMiner/单线程/离线自测、离线模拟不显示份额、导航保留日志、离线隐藏收款字段、真实配置无效时拒绝启动、共享配置格式保存。浅色和深色各生成三张实际界面截图，已目视检查概览、配置、日志布局。测试使用独立配置和模拟进程，不运行真实挖矿。

结果见 `artifacts/winui/result.json`（Passed=true，9 项）；构建日志见 `artifacts/winui-validation.log`，截图见 `artifacts/winui/`。原生文件选择器、导出对话框及真实启动确认尚未逐项人工点击验收，不能把构建成功当作这些流程已完成交互验证。

新增前端后再次运行后台与 WPF Release 验收：0 警告、0 错误，24 项后台验收全部通过，WPF 界面验收通过。WinUI 启动/测试脚本另用 Windows PowerShell 执行通过，与双击入口一致。

以下记录为共用后台与 WPF 的验收及既有硬件验证边界。

执行：`scripts/Build.ps1 -Action Test -Configuration Release`。

## 结果

- Release 构建：0 警告、0 错误。
- Preview 0.2 控制台验收：24 通过、0 失败；完整结果见 `artifacts/validation.log`。
- WPF 界面验收：通过。
- 界面预览：`artifacts/ui/preview-idle.png`、`artifacts/ui/preview-demo.png`，已查看演示图并修正日志的垂直对齐。
- 完整控制台输出：`artifacts/validation.log`（本机构建产物，不纳入版本控制）。

## 验收覆盖

1. 配置缺失、格式错误、未知版本拒绝。
2. 钱包及矿池结构校验、参数独立传递、TLS 映射。
3. 总算力/份额事件解析，排除单卡速度和累计摘要的重复计数。
4. 配置写入读取、未知版本保留。
5. 消息长度限制。
6. 启动、份额、重复启动拒绝、停止和再次启动。
7. 停止时清理模拟内核子进程。
8. 有进程但没有份额时保持“等待份额”。
9. 超长日志安全终止，不无限重试。
10. 用户关闭恢复时不重启。
11. 三次重试上限。
12. 停止操作取消重试等待。
13. 配置错误不重试。
14. 命名管道连接、状态、启动、停止和关闭。
15. 控制连接正常释放后的进程回收。
16. 控制进程未清理即退出后的进程回收。

WPF 验收额外检查首次默认演示、真实配置区域禁用、模拟指标和标识、演示启停、无效真实配置提示，并渲染了实际 WPF 控件树供布局检查。

## 未验证边界

没有使用真实 SRBMiner、显卡负载或矿池。矿池有效算力、拒绝率、温度、功耗与收益均未验证。使用外部内核前仍需用户核对文件来源、地址和矿池。

进程和 IPC 测试需要正常 Windows 用户权限，受限沙箱可能阻止命名管道或文件替换；没有通过弱化命名管道访问控制来解决此限制。

## 0.2 新增验证

- BzMiner/CPU 合法组合、线程上下限、文件类型匹配。
- 仅 CPU 和仅 GPU 参数、TLS 钱包保留、独立配置、不启用默认管理接口或自动下载。
- 新旧日志格式、真实 v100.46 摘要行与紧凑算力单位、重复累计摘要去重。
- v1 配置迁移，CPU/离线设置持久化。
- Agent 内核路由、CPU 池模式演示、离线演示不显示矿池份额。
- 内存不足的设备跳过错误会终止任务而不重试。
- WPF 设备联动、CPU 默认一线程、离线字段隐藏/说明、模拟启停和新增截图。

## 真实 BzMiner CPU 检查

从官方 GitHub v100.46 发布页下载 Windows ZIP，SHA-256 核对通过。内核本体 SHA-256：`B702AB94114EE516537063A8688BEEF69D7D5F88B92A37964556CB7A242D1C72`。没有把该内核复制到源码或发行输出。

硬件：Intel Core i5-13400，10 核 / 16 逻辑线程。通过本项目相同的适配器和进程管理流程，显式启用 `--bench`，1 个 CPU 线程，关闭 GPU 和网络管理/发现接口。

首次限时运行确认了 `pearl-bench` 任务、CPU-only 设备和一条 `smry` 算力行 `24.23gh`；当时客户端尚未适配表格，所以该次严格验收没有通过。随后根据真实输出补齐表头及摘要解析。后续运行报告：PRL 任务需要 1097 MB 加 384 MB 工作空间，系统只有 1234 MB 可用，设备被跳过，算力为零；该次也未通过，不声明硬件自测完成。已增加对这个错误的识别和模拟回归验收。

最后一次实际运行及停止快照保存在 `artifacts/bz-cpu-real/result.json`，对应日志在同目录 `cpu-probe.log`，BzMiner 解析后的配置在 `resolved-config.json`。其中结果来自增加内存错误识别前的实际运行，保留失败证据；新错误处理由自动化测试覆盖，不为获取成功结果而修改主机内存、安全策略或关闭用户应用。
