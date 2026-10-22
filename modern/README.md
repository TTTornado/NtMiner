# NtMiner PRL Preview 0.4

Windows 桌面原型，基于 NtMiner 项目逐步重构。使用 .NET 10，前端为 WinUI 3，共用配置、后台与内核适配。与根目录的旧版 `NTMiner.sln` 隔离，不加载旧版程序集或云端配置。

## 启动

双击 `Start-WinUI.cmd` 打开 WinUI 3 工作台。该入口会先检查源码是否比上次构建新：**没有改动就直接启动**（约 1–2 秒），改动过才做一次增量构建。构建前会检查是否已有程序在运行——程序开着时输出文件被占用，此时会直接提示关闭窗口，而不是抛出构建错误。需要显式重建时使用 `Build-WinUI.cmd` / `Build.cmd`，或给脚本加 `-Force`。入口不会自动开始挖矿；同一 Windows 用户一次只能打开一个现代版窗口。WinUI 使用说明与独立构建方法见 [WINUI.md](WINUI.md)。

判断依据是 `artifacts\.build\` 下的构建标记，而不是可执行文件的时间戳：MSBuild 在输出与上次完全相同时会保留原文件，可执行文件时间戳因此不可靠。

脚本以 **UTF-8 with BOM** 保存。Windows PowerShell 5.1 会把无 BOM 的脚本按 ANSI 读取，中文字符串会破坏解析。

开发环境需要 Windows 和 .NET 10 SDK。当前工作区已在 `D:\重构NTMiner\.tools\dotnet` 安装便携 SDK 10.0.402；启动脚本会优先使用这份 SDK，不修改系统 PATH。在其他电脑上可从微软官方获取 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0 。后台构建不需要第三方 NuGet 包，`NuGet.Config` 指向工作区本地包缓存。WinUI 独立项目从 `WinUI.NuGet.Config` 获取微软官方依赖，锁定 Windows App SDK 2.5.1。

## 先体验演示模式

演示模式开关位于**设置**页。首次启动默认开启，回到运行概览点击“开始演示”，即可查看模拟算力、接受/拒绝份额和实时日志。演示进程不会连接网络、加载显卡内核或产生收益，横幅、任务状态和算力标题都会标注模拟性质。

点击“停止”结束任务。关闭窗口会停止后台与内核；后台连接丢失或超过心跳期限时也会结束任务。当前版本不提供关闭界面后继续挖矿。

## 用 CPU 验证 BzMiner

1. 打开工作台，设备选择 **CPU**，界面会自动切换为 **BzMiner**；默认 1 个线程。
2. 勾选 **离线自测**，取消 **演示模式**。
3. 选择官方 `bzminer.exe`，点击 **开始离线自测** 并确认。此时运行真实 PRL CPU 算法，不需要钱包或矿池，不产生收益；完成后点击停止。
4. 若要验证矿池份额，停止后取消 **离线自测**，填入你的 PRL 地址和支持 CPU 的矿池，再启动。

当前工作区已从官方发布页下载并校验 **BzMiner v100.46**，内核路径为：

```text
D:\重构NTMiner\.tools\bzminer-v100.46\bzminer_v100.46_windows\bzminer.exe
```

官方发布页：https://github.com/bzminer/bzminer/releases/tag/v100.46 。Windows ZIP 的官方 SHA-256 为 `c1dda90fb738e2f0cc7b795a152a69719d248768798bb6903c7269c5a88659ec`，已匹配。本地程序放在仓库外的工具目录，不打包或纳入源码。

**实际自测边界：** 已在本机 i5-13400 上确认 CPU-only 参数、PRL 离线任务启动及停止清理。后续自测遇到系统可用内存不足：该版本报告需要约 1097 MB + 384 MB 工作空间，当时只有约 1234 MB；设备被跳过。软件现会识别此错误并停止任务，不能把进程存活或零算力视为自测通过。请先释放足够内存再试。没有连接真实矿池或验证 PRL 结算。

BzMiner 适配基于当前官方参数：`-a pearl`、`--cpu 1`、`--cpu_threads N`；CPU 模式显式禁用 NVIDIA/AMD/Intel GPU 和 CUDA/OpenCL/Metal 后端。离线模式使用 **`--bench`**，不使用带本地 Stratum 服务的 `--benchmark`。指定独立空配置，避免加载内核旁的 `config.txt`；关闭 Web、UDP 发现、内核/插件自动更新、CPU 传感器和大页分配，不运行驱动安装脚本。使用官方完整 Windows 包，旧版内核不保证兼容；不支持的参数会作为错误停止。

演示模式仍然是模拟数据，与 CPU 离线自测不同。离线自测的份额显示“不适用”，并禁用自动恢复；真实 CPU 算力、初始化时间与内存需求以实际版本和硬件为准。

## 核显与全部 GPU 离线自测

在 WinUI 的“挖矿配置”中选择 **仅核显**，会自动切换 BzMiner 并勾选离线自测。取消演示模式、选择官方 `bzminer.exe`，保存配置后回到运行概览，点击“开始离线自测”并核对设备。当前可使用你提供的 `D:\miner\bzminer_v100.46_windows\bzminer.exe`；不自动选择或执行该文件。

“全部 GPU”包含内核识别到的独显与核显；选择 BzMiner 后也可勾选离线自测。CPU 挖矿在两种 GPU 模式中均禁用。“仅核显”使用不带值的 `--igpu`，保留三家厂商的核显选择，禁用 CPU 挖矿；不会使用 `--devices all` 扩大选择。该模式使用 OpenCL 后端，具体硬件与驱动仍需验证。

运行概览会显示内核报告的设备名，不把所选模式当作硬件检测结果。初始或零算力显示“尚无有效算力”，新鲜的正算力才显示“已产生算力”；超过 30 秒未更新时隐藏旧数值。内存不足时显示内核报告的所需、可用系统内存，停止任务且不自动重试。离线自测不统计份额，不验证矿池、不产生收益，完成后需点击停止。

已有硬件证据：2026-10-21 使用 BzMiner v100.46 直接进行 UHD Graphics 730 的限时离线测试，释放内存前因可用内存 1636 MB 低于所需 1737 MB 被跳过；释放后启动前约 2847 MB 可用，报告 14.70–27.58 GH/s，测试停止后无残留进程。这是短时离线计算证据，不代表长期稳定性或收益。日志保存在 `artifacts/bz-igpu-real/retry-2/`，不纳入源码。

## 使用真实 SRBMiner（GPU）

1. 从开发者官方发布页自行获取兼容 Pearl / pearlhash 的 Windows 版本：https://github.com/doktor83/SRBMiner-Multi/releases 。本项目不包含、下载或再分发它。
2. 停止演示后取消“使用演示模式”，选择本机 `SRBMiner-MULTI.exe`。
3. 填写 PRL 收款地址、矿池地址和矿机名。矿池格式为 `stratum+tcp://主机:端口`，TLS 使用 `stratum+ssl://主机:端口`；不预设矿池或钱包。
4. 点击“开始挖矿”，核对本次的内核路径、钱包及矿池，再确认启动。

地址目前只做格式校验，**不做 Bech32 校验和验证**，请与钱包里的完整地址核对。份额数为当前任务由本机日志识别的事件数，内核日志格式变化可能导致指标为空或遗漏；最终有效算力、收益与结算以矿池为准。30 秒未更新的算力显示为“—”。

SRBMiner 适配将界面地址转换成官方 `--pool host:port` 与 `--tls true/false` 参数，固定使用 `pearlhash`，禁用 CPU 挖矿、内核超频和 MSR 调整。参数通过 `ProcessStartInfo.ArgumentList` 传递，不经 shell。参考参数文档：https://github.com/doktor83/SRBMiner-Multi/blob/master/Parameters 。内核开发费以实际官方版本为准。

**尚未完成真实显卡、具体 SRBMiner 版本及矿池的联调。** 不保证日志解析覆盖所有版本。常规自动化验收使用自建模拟进程；实际 BzMiner CPU 自测情况见上文，不能作为收益或矿池兼容性证明。

## 已实现

- 收款地址、矿池、矿机名、内核路径校验和本机配置保存。
- 独立 Agent、随机名称且限同一 Windows 用户的命名管道。
- 串行启动/停止；重复启动拦截；显式任务状态。
- 受 Windows Job 管理的后台与内核进程；停止时回收进程树。
- 内核退出后最多 3 次恢复，等待 2、4、8 秒；支持关闭自动恢复。
- 常见配置、认证和设备错误停止重试；有界日志和消息长度。
- SHA-256 文件标识、重启前校验；该标识不代表来源认证。
- 本机日志、接受/拒绝份额和保守识别的总算力。
- BzMiner CPU / 全部 GPU / 仅核显选择、CPU 线程预算和三种模式的 PRL 离线自测。
- 内核报告设备名、新鲜正算力提示、零算力及过期算力区分、内存不足时显示所需与可用内存。
- 实测 v100.46 表格摘要解析；累计份额不按每次刷新重复累加；内存不足停止重试。
- 首次默认演示、不自动开挖；不连接旧版云端、不替换钱包、不关闭系统安全功能。
- WinUI 硬件仪表盘：只读检测本机处理器、内存、主板、显卡、存储与系统，并给出与挖矿模式的对照提示。
- 独立设置页收纳主题与演示模式；主题选择随配置保存，重启后沿用。
- 挖矿配置按内核、收款账户、内核参数分组；配置修改后立即写入本机。
- 内核参数以复选框呈现，常用项直接展示、其余收进高级设置，悬停可查看对应的真实命令行；未知参数名不会进入启动命令。
- 命令行预览卡片展示按当前选择推导出的参数。
- 任务运行期间修改配置时提示需要重启任务才生效。

## 硬件仪表盘（WinUI）

WinUI 的 **硬件仪表盘**页只读检测本机硬件：处理器核心与频率、内存插槽与频率、主板与 BIOS、显卡、存储、操作系统，并显示当前可用内存。页面同时对照当前挖矿模式给出提示：本机没有物理集成显卡却选择「仅核显」时提前说明可能无法启动；可用内存低于 1.5 GB 时说明 BzMiner 离线自测可能因内存不足被跳过。

显卡按三类区分：`ROOT\` 枚举路径或远程控制/虚拟显示类名称识别的**虚拟显示适配器**不计入可挖矿设备；含 `GeForce`、`Radeon RX`、`Arc` 等关键词的算**独显**；含 `UHD`、`Iris`、`Radeon(TM) Graphics` 等的算**核显**。这只是界面提示，不替代内核自己的设备检测。WMI 的 `AdapterRAM` 是 32 位字段，显存接近 4 GB 时界面会标注该数值不可信。

**探测由后台 Agent 执行。** WinUI 3 运行在 `Microsoft.NETCore.App` 上，`System.Management` 在该宿主会抛 `PlatformNotSupportedException`（该包只认可 `Microsoft.WindowsDesktop.App` 桌面框架），因此前端通过命名管道新增的 `hardware` 命令向 Agent 取值。仪表盘需要 Agent 已连接；后端未连接时页面会说明而不是显示错误数据。

配置存储在 `%LOCALAPPDATA%\NtMiner.Modern\profile.json`。只存收款地址等挖矿配置，不应输入私钥或助记词；不自动导入旧 NtMiner 配置。当前格式为 v4，新增内核参数开关与界面主题两项。v1 迁移后保留 SRBMiner/GPU 原有行为，v2 迁移保留 CPU/GPU 与自测选择，v3 直接补上新增字段的默认值；迁移只发生在内存中，不改写原文件，用户下一次修改配置时才以 v4 写回。未知版本拒绝读取且不会被自动覆盖。

## 工程与验证

```powershell
.\scripts\Build.ps1 -Action Build
.\scripts\Build.ps1 -Action Test
.\scripts\Build.ps1 -Action Run          # 等同 Start-WinUI.cmd，仅转发到 WinUI

# 首次或新增依赖后联网补齐工作区包缓存；之后构建仍可离线。
.\scripts\Build.ps1 -Action Build -RestorePackages
```

包来源限定为工作区内的 `.tools\nuget-packages`（`NuGet.Config` 指向该目录），因此常规构建不访问网络。WinUI 独立项目仍从 `WinUI.NuGet.Config` 还原并锁定版本。

也可双击 `Test.cmd`（后台验收）或 `Test-WinUI.cmd`（WinUI 界面验收）。测试使用控制台验收程序，不依赖测试框架包；进程/IPC 验证需要正常 Windows 用户环境，受限沙箱可能禁止命名管道和文件替换。WinUI 验收会短暂打开测试窗口，截图写入 `artifacts/winui/`。

| 工程 | 职责 |
|---|---|
| NTMiner.Domain | 配置、状态、协议模型，目标 net10.0 |
| NTMiner.Application | 适配器契约、单任务生命周期与重试；状态机与会话协调分离，目标 net10.0 |
| NTMiner.Infrastructure | Windows Job、SRBMiner 参数/日志、配置文件、本机通信与只读硬件探测 |
| NTMiner.Agent | 后台命令处理和连接租约，并持有硬件探测（WMI 在这里可用） |
| NTMiner.WinUI | 唯一前端（WinUI 3）：导航、主题、本机控制与硬件仪表盘，单独构建 |
| NTMiner.FakeMiner | 自建演示内核，不连接网络 |
| NTMiner.Tests | 配置、解析、进程与 IPC 验收 |
| NTMiner.CpuProbe | 显式启用的 30 秒、单线程 BzMiner 离线硬件验证，不在常规测试中执行 |

CPU 硬件验证工具只在手动调用时执行：

```powershell
# 先执行 Build.ps1 -Action Build；使用 .NET 10 的 dotnet。
dotnet tests/NTMiner.CpuProbe/bin/Release/net10.0-windows/NTMiner.CpuProbe.dll <bzminer.exe绝对路径> <结果目录>
```

它先读取 BzMiner 的解析后配置确认 CPU-only 和本地 benchmark，再执行最多 30 秒并停止。日志和快照写入指定结果目录；不能运行或没有正算力时返回失败。普通用户体验请直接使用界面。

## 后续工作

真实硬件与内核版本验收、正式指标接口适配、设备与温度监控、签名更新和回滚、安装包、无障碍与 DPI 验证、群控。普通权限和同用户命名管道不等于恶意本机程序隔离沙箱；不要执行来源不明的第三方内核。启动后的 Job 绑定用于意外退出清理，不把其当作对不可信可执行文件的安全隔离。

保留上游版权和根目录 LICENSE、996LICENSE。SRBMiner 等外部内核的使用及分发遵循各自授权。
