using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NTMiner.Modern.WinUI;

/// <summary>仪表盘的显示模型。行与分区都是不可变的只读投影。</summary>
public sealed record HardwareRow(string Label, string Value);
public sealed record HardwareSection(string Title, HardwareRow[] Rows);

/// <summary>
/// 硬件仪表盘：只读检测，不提升权限、不改变系统状态。
/// 检测放在后台线程执行，避免 WMI 首次查询阻塞界面。
/// </summary>
public sealed partial class MainWindow
{
    private HardwareSnapshot? _hardware;
    private bool _detecting;
    private Task? _detectingTask;
    /// <summary>最近一次渲染的分区数，仅供自动化验收判断渲染是否真的执行过。</summary>
    internal int LastRenderedSectionCount { get; private set; }

    private async Task LoadHardwareAsync()
    {
        if (_hardware is not null) return;
        // 同一时刻只允许一次探测；并发调用等待同一个进行中的任务。
        if (_detecting && _detectingTask is not null) { await _detectingTask; return; }
        if (!_connected) { HardwareSummary.Text = "后台未连接，无法读取硬件信息。"; return; }
        _detecting = true;
        RescanButton.IsEnabled = false;
        HardwareSummary.Text = "正在检测本机硬件…";
        _detectingTask = DetectAndRenderAsync();
        try { await _detectingTask; }
        finally { _detecting = false; _detectingTask = null; RescanButton.IsEnabled = true; }
    }

    private async Task DetectAndRenderAsync()
    {
        try
        {
            // 探测由后台 Agent 执行：前端运行在 Microsoft.NETCore.App 上，WMI 不可用。
            var response = await _client.SendAsync(new("hardware"));
            if (response.Hardware is { } snapshot) { RenderHardware(snapshot); return; }
            var reason = response.Error ?? "后台未返回硬件信息。";
            HardwareSummary.Text = "硬件检测失败：" + reason;
            Feedback("硬件检测失败：" + reason, InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            HardwareSummary.Text = "硬件检测失败：" + ex.Message;
            Feedback("硬件检测失败：" + ex.Message, InfoBarSeverity.Error);
            ConnectionFailed(ex);
        }
    }

    private async void RescanHardware(object sender, RoutedEventArgs e)
    {
        _hardware = null;
        await LoadHardwareAsync();
    }

    private void RenderHardware(HardwareSnapshot snapshot)
    {
        _hardware = snapshot;
        var sections = new List<HardwareSection>();
        var summary = new List<string>();

        if (snapshot.Processor is { Reported: true } cpu)
        {
            summary.Add(cpu.Name);
            summary.Add($"{cpu.Cores} 核 / {cpu.LogicalProcessors} 线程");
            sections.Add(new HardwareSection("处理器", HardwareRows(
                ("型号", cpu.Name),
                ("物理核心 / 逻辑线程", $"{cpu.Cores} / {cpu.LogicalProcessors}"),
                ("基准频率", cpu.BaseClockMhz > 0 ? $"{cpu.BaseClockMhz} MHz" : "未报告"),
                ("L3 缓存", cpu.L3CacheKb > 0 ? $"{cpu.L3CacheKb / 1024.0:0.#} MB" : "未报告"))));
        }

        if (snapshot.Memory is { } memory)
        {
            summary.Add($"内存 {HardwareDisplay.FormatBytes(memory.TotalBytes)}");
            var rows = new List<HardwareRow>
            {
                new("物理内存总量", HardwareDisplay.FormatBytes(memory.TotalBytes)),
                new("已识别内存条", memory.Modules.Length == 0 ? "系统未报告模块信息" : $"{memory.Modules.Length} 条，合计 {HardwareDisplay.FormatBytes(memory.InstalledBytes)}")
            };
            for (var i = 0; i < memory.Modules.Length; i++)
            {
                var module = memory.Modules[i];
                var detail = $"{HardwareDisplay.FormatBytes(module.CapacityBytes)}"
                    + (module.SpeedMhz > 0 ? $" · {module.SpeedMhz} MT/s" : "")
                    + (string.IsNullOrWhiteSpace(module.Manufacturer) ? "" : $" · {module.Manufacturer}")
                    + (string.IsNullOrWhiteSpace(module.PartNumber) ? "" : $" · {module.PartNumber}");
                rows.Add(new HardwareRow($"插槽 {i + 1}（{module.Slot}）", detail));
            }
            rows.Add(new HardwareRow("当前可用", snapshot.OperatingSystem is { } os ? $"{os.AvailableMemoryMb} MB" : "见操作系统一栏"));
            sections.Add(new HardwareSection("内存", rows.ToArray()));
        }

        if (snapshot.Motherboard is { } board)
        {
            if (!string.IsNullOrWhiteSpace(board.Product)) summary.Add(board.Product);
            sections.Add(new HardwareSection("主板与固件", HardwareRows(
                ("主板", Join(board.Manufacturer, board.Product)),
                ("BIOS 版本", Blank(board.BiosVersion)),
                ("BIOS 日期", Blank(board.BiosDate)))));
        }

        if (snapshot.Graphics.Length > 0)
        {
            var mineable = HardwareDisplay.Mineable(snapshot.Graphics);
            if (mineable.Length > 0) summary.Add($"{mineable.Length} 个物理显卡");
            var rows = new List<HardwareRow>();
            for (var i = 0; i < snapshot.Graphics.Length; i++)
            {
                var adapter = snapshot.Graphics[i];
                var index = i + 1;
                rows.Add(new HardwareRow($"显卡 {index} · 型号", adapter.Name));
                rows.Add(new HardwareRow($"显卡 {index} · 角色", HardwareDisplay.AdapterRole(adapter)));
                rows.Add(new HardwareRow($"显卡 {index} · 驱动", Join(adapter.DriverVersion, adapter.DriverDate)));
                // AdapterRAM 是 32 位字段，达到 4 GB 附近时数值不可信，如实标注而不是当作真实显存。
                var ramStated = adapter.AdapterRamBytes is { } bytes
                    ? HardwareDisplay.FormatBytes(bytes) + (bytes >= 4_000_000_000 ? "（WMI 字段上限，实际显存可能更大）" : "")
                    : "未报告";
                rows.Add(new HardwareRow($"显卡 {index} · 显存", ramStated));
                if (adapter.IsVirtual) rows.Add(new HardwareRow($"显卡 {index} · 说明", "由软件驱动提供的显示适配器，挖矿内核不会把它当作可用 GPU。"));
            }
            sections.Add(new HardwareSection("显卡", rows.ToArray()));
        }

        if (snapshot.Disks.Length > 0)
            sections.Add(new HardwareSection("存储", snapshot.Disks
                .Select((disk, i) => (Label: $"磁盘 {i + 1}", Value: $"{disk.Model} · {HardwareDisplay.FormatBytes(disk.SizeBytes)}"
                    + (string.IsNullOrWhiteSpace(disk.InterfaceType) ? "" : $" · {disk.InterfaceType}")
                    + (disk.IsRemovable ? " · 可移动" : "")))
                .Select(x => new HardwareRow(x.Label, x.Value)).ToArray()));

        if (snapshot.OperatingSystem is { } system)
        {
            summary.Add($"{system.Caption}（{system.BuildNumber}）");
            sections.Add(new HardwareSection("操作系统", HardwareRows(
                ("版本", Join(system.Caption, system.Version)),
                ("内部版本号", system.BuildNumber == 0 ? "未报告" : system.BuildNumber.ToString()),
                ("体系结构", Blank(system.Architecture)),
                ("当前可用物理内存", $"{system.AvailableMemoryMb} MB"))));
        }

        HardwareSections.ItemsSource = sections;
        LastRenderedSectionCount = sections.Count;
        // 用不换行空格连接词组内部，避免“1 个物理显卡”在窄窗口被拆开。
        HardwareSummary.Text = $"{string.Join("　·　", summary)}　·　检测于 {snapshot.CapturedAt:HH:mm:ss}";

        HardwareProblems.Message = string.Join("\n", snapshot.Problems);
        HardwareProblems.IsOpen = snapshot.Problems.Length > 0;
        RenderAdvice(snapshot);
    }

    /// <summary>把硬件结果与当前配置的挖矿模式对照，只提示可能不可行，不替代内核的真实检测。</summary>
    private void RenderAdvice(HardwareSnapshot snapshot)
    {
        var graphics = snapshot.Graphics;
        var integratedSupported = HardwareDisplay.SupportsIntegratedOnly(graphics);
        var mineable = HardwareDisplay.Mineable(graphics);
        string message;
        InfoBarSeverity severity = InfoBarSeverity.Informational;

        if (integratedSupported is null && mineable.Length == 0)
            message = "未检测到任何物理显卡。CPU 模式仍可用；GPU 相关模式需要内核自行识别设备。";
        else if (integratedSupported == false)
        {
            message = "这台主机没有检测到物理集成显卡，选择「仅核显」很可能无法启动；可改用 CPU 或全部 GPU 模式。";
            severity = InfoBarSeverity.Warning;
        }
        else if (SelectedDevice == MiningDevice.IntegratedGpu)
            message = "当前配置为「仅核显」，本机检测到集成显卡，模式在硬件上可能可行；实际可用性以内核运行结果为准。";
        else
            message = $"检测到 {mineable.Length} 个可用物理显卡"
                + (graphics.Any(g => g.IsIntegrated) ? "（含集成显卡）" : "")
                + "。全部 GPU 模式会使用内核识别到的这些设备；最终以内核报告为准。";

        if (snapshot.OperatingSystem is { AvailableMemoryMb: < 1600 } os)
        {
            message += $"\n当前可用内存仅 {os.AvailableMemoryMb} MB，BzMiner 的 PRL 任务通常需要约 1.5 GB，"
                + "CPU/核显离线自测可能因内存不足被跳过。";
            severity = InfoBarSeverity.Warning;
        }
        HardwareAdvice.Message = message;
        HardwareAdvice.Severity = severity;
        HardwareAdvice.IsOpen = true;
    }

    private static HardwareRow[] HardwareRows(params (string Label, string Value)[] rows) =>
        rows.Select(r => new HardwareRow(r.Label, r.Value)).ToArray();

    private static string Join(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? Blank(second)
        : string.IsNullOrWhiteSpace(second) ? first : $"{first} · {second}";

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "未报告" : value;
}
