# WinUI 3 工作台

Preview 0.4 提供原生前端、核显选择与 GPU 离线自测。双击 `Start-WinUI.cmd` 启动；首次默认演示模式，不会自动开挖。WinUI 是唯一桌面前端，原 WPF 前端已移除。

## 使用

- **运行概览**：查看状态、算力、接受/拒绝份额与恢复次数，启动或停止任务。
- **硬件仪表盘**：只读检测本机处理器、内存、主板、显卡、存储与操作系统，并提示当前所选挖矿模式是否可能受硬件限制。
- **挖矿配置**：选择 SRBMiner 或 BzMiner、CPU/全部 GPU/仅核显、CPU 线程与离线自测，选择本地内核并填写收款地址和矿池；可切换跟随系统、浅色、深色主题。
- **运行日志**：查看当前窗口收集的日志，导出到自行选择的文本文件。

硬件仪表盘数据来自后台 Agent 的 `hardware` 命令，因为 WinUI 进程（`Microsoft.NETCore.App` 宿主）无法使用 `System.Management`。因此该页需要后台已连接；未连接时会直接说明，不会显示推测值。虚拟显示适配器（例如远程控制软件的虚拟屏）会被标出且不计入可挖矿设备；显存接近 4 GB 时界面会标注 WMI 数值不可信。判断规则只用于界面提示，实际可用设备以内核报告为准。

选择 CPU 后自动切换 BzMiner，默认 1 线程并勾选离线自测。取消演示模式后，真实任务启动前会显示确认框；离线自测无需钱包和矿池。真实内核与硬件验证边界见 [README.md](README.md) 和 [VERIFICATION.md](VERIFICATION.md)。

选择“仅核显”同样自动切换 BzMiner 和离线自测，CPU 线程控件隐藏。“全部 GPU”下可手动勾选 BzMiner 离线自测；两种 GPU 模式均关闭 CPU 挖矿。仅核显包含受支持厂商的集成显卡，不限定 Intel；当前实际硬件证据来自 Intel UHD Graphics 730。

推荐体验顺序：挖矿配置 → 仅核显 → 保持演示模式体验启停 → 停止 → 关闭演示模式并选择官方内核 → 保存配置 → 运行概览 → 开始离线自测 → 核对并确认。真实自测完成后点击停止。

概览显示内核报告的设备名。零算力或尚无算力不视为成功，过期 30 秒的算力隐藏；内存不足时显示所需与可用系统内存，停止且不重试。演示设备始终标注模拟性质。配置格式升级为 v3，v1/v2 读取迁移不自动覆盖原文件。

配置存储在 `%LOCALAPPDATA%\NtMiner.Modern\profile.json`，需要点击保存；主题选择仅在当前窗口有效。同一用户一次只能运行一个窗口。关闭窗口会停止后台与内核。

## 独立构建

要求 Windows x64（最低系统版本 10.0.19041）和 .NET 10 SDK。使用 Windows App SDK **2.5.1**，原生 NavigationView、InfoBar、ContentDialog、文件选择器及系统支持时的 Mica 背景。未打包运行，Windows App Runtime 依赖随应用构建输出，仍需 .NET 10 运行环境；当前启动脚本会使用工作区便携 SDK。

```powershell
.\scripts\BuildWinUI.ps1 -Action Build
.\scripts\BuildWinUI.ps1 -Action Run
.\scripts\BuildWinUI.ps1 -Action Test
```

测试也可双击 `Test-WinUI.cmd`。测试会短暂打开实际窗口，只运行模拟内核，不执行真实 CPU/GPU 挖矿。

WinUI 项目为 `src/NTMiner.WinUI/NTMiner.WinUI.csproj`，不加入 `NTMiner.Modern.sln`，因此后台仍可通过 `Build.ps1` 独立构建。WinUI 引用现有 Infrastructure，并复制 Agent 和演示内核输出；界面通过相同命名管道调用 Agent，不直接启动真实内核。

首次 WinUI 构建需要联网还原微软 NuGet 包。包源限定为 `WinUI.NuGet.Config` 中的 nuget.org，依赖版本与哈希记录在 `packages.lock.json`，启动脚本启用锁定还原。当前工作区缓存位于 `D:\重构NTMiner\.tools\nuget-packages`。

## 已验证与边界

验收使用实际 WinUI 窗口与独立测试配置，不覆盖真实配置。覆盖默认演示、Agent 演示启停、CPU/仅核显联动、全部 GPU 离线演示、设备名、离线标识、配置校验和 v3 保存、日志导航，以及零/正/过期算力和内存错误的受控状态展示。

结果在 `artifacts/winui/result.json`，截图在同目录；完整构建及验收输出在 `artifacts/winui-validation.log`。`igpu-demo-light.png` 来自实际 Agent 模拟运行；文件名带 `sample` 的截图为受控状态样例，不是新一轮真实硬件运行证据。

文件选择、导出和真实启动确认采用原生窗口关联方式，尚未逐项进行人工交互验收。多显示器 DPI、屏幕阅读器、安装包及签名发布仍待后续验证。真实矿池与硬件联调沿用当前后台验证边界。
