using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NTMiner.Modern.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AgentClient _client = new();
    private readonly ProfileStore _store;
    private readonly Queue<string> _logs = new();
    private readonly string? _smokeDirectory;
    private DispatcherQueueTimer? _poll;
    private Task? _initialization;
    private bool _ready, _busy, _closing, _closeReady, _connected;
    private bool _confirming;
    private long _sequence;
    private MiningState _state;
    private string? _activeRunOptions;

    public MainWindow(string? smokeDirectory = null)
    {
        _smokeDirectory = smokeDirectory;
        _store = new(smokeDirectory is null ? ProfileStore.DefaultPath : Path.Combine(smokeDirectory, "profile.json"));
        InitializeComponent();
        Title = "NtMiner · WinUI 3 · PRL";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1280, 920));
        AppWindow.Closing += WindowClosing;
        // The pane footer is plain text: hide it when the pane collapses to icons only,
        // otherwise it is squeezed into a single-character column.
        Navigation.PaneOpening += (_, _) => UpdatePaneFooter();
        Navigation.PaneOpened += (_, _) => UpdatePaneFooter();
        Navigation.PaneClosing += (_, _) => UpdatePaneFooter();
        Navigation.PaneClosed += (_, _) => UpdatePaneFooter();
        // DisplayMode 与 IsPaneOpen 要等首次布局才确定；构造函数里读到的是默认值，
        // 会把底部说明误判为收起态而隐藏。改为布局完成后与每次尺寸变化时重新判断。
        Navigation.Loaded += (_, _) => UpdatePaneFooter();
        Navigation.SizeChanged += (_, _) => UpdatePaneFooter();
        CpuThreads.Maximum = Environment.ProcessorCount;
        CpuHint.Text = $"本机 {Environment.ProcessorCount} 个逻辑线程；建议先用 1 个线程验证。";
        _ready = true;
        UpdateControls();
    }

    private async void RootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialization is not null) return;
        _initialization = InitializeAsync();
        await _initialization;
        if (_smokeDirectory is not null) await RunSmokeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            try
            {
                var options = _smokeDirectory is null ? await _store.LoadAsync() : new MiningOptions();
                KernelPath.Text = options.KernelPath; Wallet.Text = options.Wallet; Pool.Text = options.Pool; Worker.Text = options.Worker;
                DemoMode.IsOn = options.DemoMode; AutoRecover.IsChecked = options.AutoRecover;
                MinerChoice.SelectedIndex = options.Miner == MinerKind.BzMiner ? 1 : 0;
                DeviceChoice.SelectedIndex = (int)options.Device;
                CpuThreads.Value = options.CpuThreads; BenchmarkMode.IsChecked = options.BenchmarkMode;
                ThemeChoice.SelectedIndex = (int)options.Theme;
                ApplyTheme();   // 索引为 0（跟随系统）时 SelectionChanged 不触发，必须主动应用
                // 加载阶段不能触发自动保存，否则会把“刚读到的默认值”立刻写回文件。
                _suppressAutoSave = true;
                RestoreSwitches(options);   // 内部会调用 RebuildSwitches，保证复选框存在
                _suppressAutoSave = false;
                _lastSavedOptions = System.Text.Json.JsonSerializer.Serialize(Options());
                AutoSaveState.Text = "配置已载入，修改后自动保存。";
                UpdateControls();
                UpdateCommandLinePreview();
            }
            catch (Exception ex) { Feedback("配置未加载：" + ex.Message, InfoBarSeverity.Warning); }
            await _client.ConnectAsync();
            _connected = true;
            Apply((await _client.SendAsync(new("status"))).Snapshot);
            _poll = DispatcherQueue.CreateTimer();
            _poll.Interval = TimeSpan.FromSeconds(1);
            _poll.Tick += PollStatus;
            _poll.Start();
        }
        catch (Exception ex) { ConnectionFailed(ex); }
    }

    private MinerKind SelectedMiner => MinerChoice.SelectedIndex == 1 ? MinerKind.BzMiner : MinerKind.SrbMiner;
    private MiningDevice SelectedDevice => (MiningDevice)DeviceChoice.SelectedIndex;
    private bool IsBenchmark => SelectedMiner == MinerKind.BzMiner && BenchmarkMode.IsChecked == true;
    private bool HasActiveTask => _state is MiningState.Starting or MiningState.Running or MiningState.Mining or MiningState.Recovering or MiningState.Stopping;

    private MiningOptions Options() => new()
    {
        Miner = SelectedMiner, Device = SelectedDevice,
        CpuThreads = double.IsFinite(CpuThreads.Value) && CpuThreads.Value == Math.Truncate(CpuThreads.Value)
            && CpuThreads.Value >= 1 && CpuThreads.Value <= Environment.ProcessorCount ? (int)CpuThreads.Value : 0,
        DemoMode = DemoMode.IsOn, BenchmarkMode = IsBenchmark,
        AutoRecover = !IsBenchmark && AutoRecover.IsChecked == true,
        KernelPath = KernelPath.Text.Trim(), Wallet = Wallet.Text.Trim(), Pool = Pool.Text.Trim(), Worker = Worker.Text.Trim(),
        KernelSwitches = EnabledSwitches(),
        Theme = (AppTheme)ThemeChoice.SelectedIndex
    };

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateControls();
        UpdateCommandLinePreview();
        AutoSave();
    }
    private void MinerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ReferenceEquals(sender, MinerChoice) && SelectedMiner == MinerKind.SrbMiner) { DeviceChoice.SelectedIndex = 0; BenchmarkMode.IsChecked = false; }
        if (ReferenceEquals(sender, DeviceChoice) && SelectedDevice is MiningDevice.Cpu or MiningDevice.IntegratedGpu)
        { MinerChoice.SelectedIndex = 1; BenchmarkMode.IsChecked = true; }
        // 换内核后可用参数随之变化，必须重建复选框，否则会把旧内核的参数带进启动命令。
        if (ReferenceEquals(sender, MinerChoice)) RebuildSwitches();
        UpdateControls();
        UpdateCommandLinePreview();
        AutoSave();
        // 设备模式改变可能让硬件适配提示失效，重新评估一次。
        if (_hardware is not null) RenderAdvice(_hardware);
    }

    private void UpdateControls()
    {
        if (!_ready) return;
        bool editable = !_busy && !_confirming && !HasActiveTask && !_closing;
        StartButton.IsEnabled = _connected && editable;
        StopButton.IsEnabled = _connected && !_busy && HasActiveTask && !_closing;
        MinerChoice.IsEnabled = DeviceChoice.IsEnabled = editable;
        CpuOptions.Visibility = SelectedDevice == MiningDevice.Cpu ? Visibility.Visible : Visibility.Collapsed;
        CpuThreads.IsEnabled = editable && SelectedDevice == MiningDevice.Cpu;
        BenchmarkMode.IsEnabled = editable && SelectedMiner == MinerKind.BzMiner;
        KernelPath.IsEnabled = editable && !DemoMode.IsOn;
        Wallet.IsEnabled = Pool.IsEnabled = Worker.IsEnabled = editable;
        AutoRecover.IsEnabled = editable && !IsBenchmark;
        WalletFields.Visibility = IsBenchmark ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = DemoMode.IsOn ? "开始演示" : IsBenchmark ? "开始离线自测" : "开始挖矿";
        ModeBanner.Title = DemoMode.IsOn ? "演示模式" : IsBenchmark ? MiningDisplay.DeviceName(SelectedDevice) + " · 离线自测" : "真实矿池模式";
        ModeBanner.Severity = DemoMode.IsOn ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        ModeBanner.Message = DemoMode.IsOn ? "显示模拟算力和份额，不连接矿池，不运行真实挖矿。"
            : IsBenchmark ? "使用所选设备运行真实 PRL 算法，不需要钱包或矿池，不产生收益；完成后请点击停止。"
            : "使用你选择的本机内核与收款配置；开始前请核对内核来源和完整地址。";
        DeviceHint.Text = SelectedDevice == MiningDevice.IntegratedGpu ? "仅使用核显，禁用 CPU 挖矿；核显与系统共享内存，请保留足够可用内存。"
            : SelectedDevice == MiningDevice.Cpu ? "只使用设定的 CPU 线程，禁用 GPU 挖矿。"
            : "使用内核识别到的全部可用 GPU（包含核显），禁用 CPU 挖矿。";
        ConfigurationSummary.Text = $"{MiningDisplay.MinerName(SelectedMiner)} · {MiningDisplay.DeviceName(SelectedDevice)}{(SelectedDevice == MiningDevice.Cpu ? $" · {CpuThreads.Value:0} 线程" : "")} · {(DemoMode.IsOn ? "演示模式" : IsBenchmark ? "离线自测" : "矿池挖矿")}";
        if (!HasActiveTask) SpeedCaption.Text = DemoMode.IsOn ? "模拟算力 · 非真实收益" : IsBenchmark ? "离线自测算力" : "内核报告算力";
    }

    private async void ChooseKernel(object sender, RoutedEventArgs e)
    {
        if (_busy || HasActiveTask || _closing) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) KernelPath.Text = file.Path;
        }
        catch (Exception ex) { Feedback("文件选择失败：" + ex.Message, InfoBarSeverity.Error); }
    }



    private async void StartClicked(object sender, RoutedEventArgs e) => await StartTaskAsync();
    private async Task StartTaskAsync()
    {
        if (_busy || _confirming || !_connected || _closing) return;
        var options = Options();
        var errors = options.Validate();
        if (errors.Count > 0) { Feedback(string.Join("\n", errors), InfoBarSeverity.Error); return; }
        _busy = true; UpdateControls();
        try
        {
            if (!options.DemoMode)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = "核对本次运行配置", PrimaryButtonText = "确认启动", CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = options.BenchmarkMode ? $"BzMiner / {MiningDisplay.DeviceName(options.Device)}{(options.Device == MiningDevice.Cpu ? $" / {options.CpuThreads} 线程" : "")}\n\n内核：{options.KernelPath}\n\n将使用所选设备运行真实离线自测，不连接矿池、不产生收益。"
                            : $"{options.Miner} / {options.Device}\n\n内核：{options.KernelPath}\n收款地址：{options.Wallet}\n矿池：{options.Pool}\n矿机名：{options.Worker}\n\n请核对来源与完整地址。开发费按实际内核版本执行。"
                    }
                };
                _confirming = true;
                _busy = false; UpdateControls();
                try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; }
                finally { _confirming = false; }
                _busy = true; UpdateControls();
                if (_closing || !_connected) return;
            }
            var response = await _client.SendAsync(new("start", options, _sequence));
            if (response.Success) RememberRunOptions(options);
            Apply(response.Snapshot);
            Feedback(response.Success ? options.BenchmarkMode ? "已请求启动离线自测，请查看日志和算力，不等待矿池份额。" : "已请求启动，等待内核状态和接受份额。" : response.Error ?? "启动失败。",
                response.Success ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
        }
        catch (Exception ex) { ConnectionFailed(ex); }
        finally { _confirming = false; _busy = false; UpdateControls(); }
    }

    private async void StopClicked(object sender, RoutedEventArgs e) => await StopTaskAsync();
    private async Task StopTaskAsync()
    {
        if (_busy || !_connected || _closing) return;
        _busy = true; UpdateControls();
        try
        {
            var response = await _client.SendAsync(new("stop", AfterSequence: _sequence));
            Apply(response.Snapshot);
            Feedback(response.Success ? "任务已停止，内核进程已回收。" : response.Error ?? "停止失败。", response.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex) { ConnectionFailed(ex); }
        finally { _busy = false; UpdateControls(); }
    }

    private async void PollStatus(DispatcherQueueTimer sender, object args)
    {
        if (_busy || _closing || !_connected) return;
        _busy = true;
        try { Apply((await _client.SendAsync(new("status", AfterSequence: _sequence))).Snapshot); }
        catch (Exception ex) { ConnectionFailed(ex); }
        finally { _busy = false; UpdateControls(); }
    }

    private void Apply(MiningSnapshot snapshot)
    {
        _state = snapshot.State;
        UpdateRestartHint();
        StateLabel.Text = MiningDisplay.StateText(snapshot, DateTimeOffset.Now);
        ProcessLabel.Text = snapshot.ProcessId is { } pid ? $"{MiningDisplay.MinerName(snapshot.Miner)} / {MiningDisplay.DeviceName(snapshot.Device)}{(snapshot.Device == MiningDevice.Cpu ? $" / {snapshot.CpuThreads} 线程" : "")} · PID {pid}" : "内核未运行";
        bool fresh = MiningDisplay.Fresh(snapshot, DateTimeOffset.Now);
        DeviceLabel.Text = snapshot.DeviceNames is { Length: > 0 } names ? (snapshot.DemoMode ? "模拟设备：" : "内核报告设备：") + string.Join("、", names) : "内核尚未报告设备名称（所选模式不代表已识别硬件）";
        RunEvidenceLabel.Text = MiningDisplay.Evidence(snapshot, DateTimeOffset.Now);
        SpeedCaption.Text = snapshot.DemoMode ? "模拟算力 · 非真实收益" : snapshot.BenchmarkMode ? "离线自测算力" : "内核报告算力";
        SpeedLabel.Text = fresh && snapshot.Hashrate is { } rate ? $"{rate:0.##} {snapshot.HashrateUnit}" : "—";
        SharesLabel.Text = snapshot.BenchmarkMode ? "不适用" : $"{snapshot.Accepted} / {snapshot.Rejected}";
        RestartsLabel.Text = snapshot.Restarts.ToString();
        if (snapshot.Error is not null) Feedback(snapshot.Error, InfoBarSeverity.Error);
        foreach (var log in snapshot.Logs)
        { _sequence = Math.Max(_sequence, log.Sequence); _logs.Enqueue($"{log.Time:HH:mm:ss} [{log.Level}] {log.Message}"); }
        while (_logs.Count > 250) _logs.Dequeue();
        if (snapshot.Logs.Length > 0) { LogOutput.Text = string.Join(Environment.NewLine, _logs); LogOutput.SelectionStart = LogOutput.Text.Length; }
        UpdateControls();
    }

    private void Feedback(string text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    { FeedbackBar.Message = text; FeedbackBar.Severity = severity; FeedbackBar.IsOpen = true; }

    private void ConnectionFailed(Exception ex)
    {
        _connected = false; _poll?.Stop();
        StateLabel.Text = "后台连接中断"; ProcessLabel.Text = "进程保护已关闭，请重新打开工作台。"; SpeedLabel.Text = "—";
        Feedback(ex is OperationCanceledException ? "后台响应超时，已结束本次连接。" : ex.Message, InfoBarSeverity.Error);
        UpdateControls();
    }

    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true; _poll?.Stop(); UpdateControls();
        if (_initialization is not null) await _initialization;
        _poll?.Stop();
        try { await _client.DisposeAsync(); }
        catch (Exception ex) { Feedback("关闭时发生异常：" + ex.Message, InfoBarSeverity.Error); }
        _closeReady = true; Close();
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || args.SelectedItemContainer?.Tag is not string tag) return;
        ShowPage(tag);
    }
    private void UpdatePaneFooter()
    {
        if (PaneFooterText is null) return;
        // 收起后侧边栏只剩图标，说明文字没有空间，直接隐藏。
        var wide = Navigation.ActualWidth > 0 && Navigation.IsPaneOpen
            && Navigation.DisplayMode != NavigationViewDisplayMode.Minimal;
        PaneFooterText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPage(string tag)
    {
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        DashboardPanel.Visibility = tag == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ConfigurationPanel.Visibility = tag == "configuration" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        ModeBanner.Visibility = tag is "overview" or "configuration" or "logs" ? Visibility.Visible : Visibility.Collapsed;
        PageHeading.Text = tag == "configuration" ? "挖矿配置" : tag == "logs" ? "运行日志"
            : tag == "dashboard" ? "硬件仪表盘" : tag == "settings" ? "设置" : "运行概览";
        PageDescription.Text = tag == "configuration" ? "所选内核、收款账户与内核参数；修改后立即保存。"
            : tag == "logs" ? "核对内核输出、错误与接受份额。"
            : tag == "dashboard" ? "只读检测本机硬件，用于判断哪种挖矿模式可能可行。"
            : tag == "settings" ? "外观与运行方式，保存在本机配置。"
            : "查看任务、内核指标与运行状态。";
        if (tag == "dashboard") _ = LoadHardwareAsync();
        if (tag == "configuration") UpdateCommandLinePreview();
    }
    private void ShowConfiguration(object sender, RoutedEventArgs args) { Navigation.SelectedItem = ConfigurationItem; ShowPage("configuration"); }
    private void ShowSettings(object sender, RoutedEventArgs args) { Navigation.SelectedItem = SettingsItem; ShowPage("settings"); }
    private void ShowLogs(object sender, RoutedEventArgs args) { Navigation.SelectedItem = LogsItem; ShowPage("logs"); }
    private void ShowOverview(object sender, RoutedEventArgs args) { Navigation.SelectedItem = OverviewItem; ShowPage("overview"); }
    private void ShowDashboard(object sender, RoutedEventArgs args) { Navigation.SelectedItem = DashboardItem; ShowPage("dashboard"); }
    /// <summary>把当前选择应用到窗口。载入配置和用户切换都走这里，保证启动时主题一致。</summary>
    private void ApplyTheme()
    {
        Root.RequestedTheme = ThemeChoice.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready) return;
        ApplyTheme();
        AutoSave();
    }

    private async void ExportLogs(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "ntminer-session" };
            picker.FileTypeChoices.Add("日志文件", new List<string> { ".log" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is not null) { await FileIO.WriteTextAsync(file, string.Join(Environment.NewLine, _logs)); Feedback("日志已导出。", InfoBarSeverity.Success); }
        }
        catch (Exception ex) { Feedback("导出失败：" + ex.Message, InfoBarSeverity.Error); }
    }
}
