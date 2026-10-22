using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace NTMiner.Modern.WinUI;

/// <summary>仪表盘里的一行：图标、类别、摘要以及打开详情所需的键。</summary>
public sealed record HardwareRow(string Key, string Glyph, string Title, string Value)
{
    /// <summary>未检测到的行标为次要色：由 DataTemplate 通过 IsMissing 决定，避免写死颜色值。</summary>
    public bool IsMissing => Value.Contains("未检测到", StringComparison.Ordinal)
        || Value.Contains("仅虚拟", StringComparison.Ordinal);
}

/// <summary>
/// 硬件仪表盘：只读检测，不提升权限、不改变系统状态。
/// 首页只显示每类硬件一行摘要，详细信息在点击后的弹窗中展开。
/// </summary>
public sealed partial class MainWindow
{
    private HardwareSnapshot? _hardware;
    private bool _detecting;
    private Task? _detectingTask;

    /// <summary>最近一次渲染的行数，仅供自动化验收判断渲染是否真的执行过。</summary>
    internal int LastRenderedRowCount { get; private set; }

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
        var rows = new List<HardwareRow>
        {
            new("cpu", "\uE950", "处理器", HardwareDisplay.ProcessorSummary(snapshot.Processor)),
            new("board", "\uE7F1", "主板", HardwareDisplay.MotherboardSummary(snapshot.Motherboard)),
            new("memory", "\uE9D9", "内存", HardwareDisplay.MemorySummary(snapshot.Memory)),
            new("gpu", "\uE7F4", "显卡", HardwareDisplay.GraphicsSummary(snapshot.Graphics)),
            new("monitor", "\uE7F8", "显示器", HardwareDisplay.MonitorSummary(snapshot.Monitors)),
            new("disk", "\uEDA2", "磁盘", HardwareDisplay.DiskSummary(snapshot.Disks)),
            new("sound", "\uE767", "声卡", HardwareDisplay.SoundSummary(snapshot.SoundDevices)),
            new("network", "\uE968", "网卡", HardwareDisplay.NetworkSummary(snapshot.NetworkAdapters))
        };
        HardwareList.ItemsSource = rows;
        LastRenderedRowCount = rows.Count;

        var detected = rows.Count(r => !r.Value.Contains("未检测到", StringComparison.Ordinal));
        HardwareSummary.Text = $"已识别 {detected} 类设备　·　检测于 {snapshot.CapturedAt:HH:mm:ss}"
            + (snapshot.OperatingSystem is { } os ? $"　·　{os.Caption}（{os.BuildNumber}）· 可用内存 {os.AvailableMemoryMb} MB" : "");

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
        var severity = InfoBarSeverity.Informational;

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

    private async void HardwareRowClicked(object sender, ItemClickEventArgs e)
    {
        if (_hardware is not { } snapshot || e.ClickedItem is not HardwareRow row) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = row.Title,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Content = BuildDetail(row.Key, snapshot)
        };
        await dialog.ShowAsync();
    }

    /// <summary>详情弹窗：按类别列出该硬件的全部字段；未知字段标为"未报告"而不是省略。</summary>
    private static UIElement BuildDetail(string key, HardwareSnapshot snapshot)
    {
        var lines = new List<(string Label, string Value)>();
        switch (key)
        {
            case "cpu" when snapshot.Processor is { } cpu:
                lines.Add(("型号", cpu.Name));
                lines.Add(("物理核心", cpu.Cores > 0 ? cpu.Cores.ToString() : "未报告"));
                lines.Add(("逻辑线程", cpu.LogicalProcessors > 0 ? cpu.LogicalProcessors.ToString() : "未报告"));
                lines.Add(("基准频率", cpu.BaseClockMhz > 0 ? cpu.BaseClockMhz + " MHz" : "未报告"));
                lines.Add(("L3 缓存", cpu.L3CacheKb > 0 ? $"{cpu.L3CacheKb / 1024.0:0.#} MB" : "未报告"));
                break;
            case "memory" when snapshot.Memory is { } memory:
                lines.Add(("安装总量", HardwareDisplay.FormatGiB(memory.InstalledBytes)));
                lines.Add(("可寻址总量", HardwareDisplay.FormatBytes(memory.TotalBytes)));
                lines.Add(("内存条数量", memory.Modules.Length.ToString()));
                for (var i = 0; i < memory.Modules.Length; i++)
                {
                    var m = memory.Modules[i];
                    var detail = string.Join(" · ", new[]
                    {
                        HardwareDisplay.FormatGiB(m.CapacityBytes),
                        m.MemoryType,
                        m.SpeedMhz > 0 ? m.SpeedMhz + " MHz" : "",
                        m.Manufacturer.Trim(),
                        m.PartNumber.Trim()
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    lines.Add(($"插槽 {i + 1}（{m.Slot}）", detail));
                }
                if (snapshot.OperatingSystem is { } memOs) lines.Add(("当前可用", memOs.AvailableMemoryMb + " MB"));
                break;
            case "board" when snapshot.Motherboard is { } board:
                lines.Add(("厂商", Blank(board.Manufacturer)));
                lines.Add(("型号", Blank(board.Product)));
                lines.Add(("BIOS 版本", Blank(board.BiosVersion)));
                lines.Add(("BIOS 日期", Blank(board.BiosDate)));
                break;
            case "gpu":
                if (snapshot.Graphics.Length == 0) lines.Add(("设备", "未检测到"));
                for (var i = 0; i < snapshot.Graphics.Length; i++)
                {
                    var g = snapshot.Graphics[i];
                    var index = i + 1;
                    lines.Add(($"显卡 {index}", g.Name));
                    lines.Add(($"显卡 {index} 类型", HardwareDisplay.AdapterRole(g)));
                    lines.Add(($"显卡 {index} 驱动", Blank(string.Join(" · ", new[] { g.DriverVersion, g.DriverDate }.Where(s => !string.IsNullOrWhiteSpace(s))))));
                    // AdapterRAM 是 32 位字段，接近 4 GB 时数值不可信。
                    lines.Add(($"显卡 {index} 显存", g.AdapterRamBytes is { } ram
                        ? HardwareDisplay.FormatBytes(ram) + (ram >= 4_000_000_000 ? "（WMI 字段上限，实际可能更大）" : "")
                        : "未报告"));
                    lines.Add(($"显卡 {index} 设备 ID", Blank(g.PnpDeviceId)));
                }
                break;
            case "monitor":
                if (snapshot.Monitors.Length == 0) lines.Add(("设备", "未检测到"));
                for (var i = 0; i < snapshot.Monitors.Length; i++)
                {
                    var m = snapshot.Monitors[i];
                    lines.Add(($"显示器 {i + 1}", m.Name));
                    lines.Add(($"显示器 {i + 1} 厂商", Blank(m.Manufacturer)));
                    lines.Add(($"显示器 {i + 1} 分辨率", m.Width > 0 && m.Height > 0 ? $"{m.Width} × {m.Height}" : "WMI 未报告"));
                }
                break;
            case "disk":
                if (snapshot.Disks.Length == 0) lines.Add(("设备", "未检测到"));
                for (var i = 0; i < snapshot.Disks.Length; i++)
                {
                    var d = snapshot.Disks[i];
                    lines.Add(($"磁盘 {i + 1}", d.Model));
                    lines.Add(($"磁盘 {i + 1} 容量", HardwareDisplay.FormatBytes(d.SizeBytes)));
                    lines.Add(($"磁盘 {i + 1} 接口", Blank(d.InterfaceType)));
                    if (d.IsRemovable) lines.Add(($"磁盘 {i + 1} 类型", "可移动设备"));
                }
                break;
            case "sound":
                if (snapshot.SoundDevices.Length == 0) lines.Add(("设备", "未检测到"));
                for (var i = 0; i < snapshot.SoundDevices.Length; i++)
                {
                    lines.Add(($"声卡 {i + 1}", snapshot.SoundDevices[i].Name));
                    lines.Add(($"声卡 {i + 1} 厂商", Blank(snapshot.SoundDevices[i].Manufacturer)));
                }
                break;
            case "network":
                if (snapshot.NetworkAdapters.Length == 0) lines.Add(("设备", "未检测到"));
                for (var i = 0; i < snapshot.NetworkAdapters.Length; i++)
                {
                    var n = snapshot.NetworkAdapters[i];
                    lines.Add(($"网卡 {i + 1}", n.Name));
                    lines.Add(($"网卡 {i + 1} 连接名", Blank(n.ConnectionName)));
                    lines.Add(($"网卡 {i + 1} 速率", n.LinkSpeed));
                    lines.Add(($"网卡 {i + 1} 状态", n.IsEnabled ? "已启用" : "已禁用"));
                }
                break;
            default:
                lines.Add(("说明", "该硬件的详细信息暂不可用。"));
                break;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        foreach (var (label, value) in lines)
        {
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            var box = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            panel.Children.Add(grid);
        }
        return new ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "未报告" : value;
}
