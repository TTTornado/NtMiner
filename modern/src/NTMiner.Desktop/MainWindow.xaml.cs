using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NTMiner.Modern;

public partial class MainWindow : Window
{
    private readonly AgentClient _client = new();
    private readonly ProfileStore _store;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<string> _logs = new();
    private bool _busy, _closing, _closeReady, _connected;
    private long _sequence;
    private MiningState _state;
    private Task? _initialization;
    private bool _uiReady;

    public MainWindow() : this(new ProfileStore(ProfileStore.DefaultPath)) { }
    public MainWindow(ProfileStore store)
    {
        _store = store; InitializeComponent(); _uiReady = true;
        CpuHint.Text = $"本机 {Environment.ProcessorCount} 逻辑线程";
        UpdateMode(); _poll.Tick += PollStatus;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        _initialization = InitializeAsync();
        await _initialization;
    }

    private async Task InitializeAsync()
    {
        try
        {
            try
            {
                var config = await _store.LoadAsync();
                KernelPath.Text = config.KernelPath; Wallet.Text = config.Wallet; Pool.Text = config.Pool; Worker.Text = config.Worker;
                DemoMode.IsChecked = config.DemoMode; AutoRecover.IsChecked = config.AutoRecover;
                MinerChoice.SelectedIndex = config.Miner == MinerKind.BzMiner ? 1 : 0;
                DeviceChoice.SelectedIndex = (int)config.Device;
                CpuThreads.Text = config.CpuThreads.ToString();
                BenchmarkMode.IsChecked = config.BenchmarkMode;
                UpdateMode();
            }
            catch (Exception ex) { Feedback.Text = "配置未加载：" + ex.Message; }
            await _client.ConnectAsync();
            _connected = true;
            Apply((await _client.SendAsync(new("status"))).Snapshot);
            _poll.Start();
        }
        catch (Exception ex) { ConnectionFailed(ex); }
    }

    private MiningOptions Options() => new()
    {
        KernelPath = KernelPath.Text.Trim(), Wallet = Wallet.Text.Trim(), Pool = Pool.Text.Trim(),
        Worker = Worker.Text.Trim(), DemoMode = DemoMode.IsChecked == true, AutoRecover = !IsBenchmark && AutoRecover.IsChecked == true,
        Miner = SelectedMiner, Device = SelectedDevice,
        CpuThreads = int.TryParse(CpuThreads.Text.Trim(), out var threads) ? threads : 0,
        BenchmarkMode = IsBenchmark
    };

    private MinerKind SelectedMiner => MinerChoice.SelectedIndex == 1 ? MinerKind.BzMiner : MinerKind.SrbMiner;
    private MiningDevice SelectedDevice => (MiningDevice)DeviceChoice.SelectedIndex;
    private bool IsBenchmark => SelectedMiner == MinerKind.BzMiner && BenchmarkMode.IsChecked == true;

    private void ChooseKernel(object sender, RoutedEventArgs e)
    {
        var name = SelectedMiner == MinerKind.BzMiner ? "bzminer.exe" : "SRBMiner-MULTI.exe";
        var picker = new OpenFileDialog { Filter = $"{name}|{name}", CheckFileExists = true, Title = "选择官方 " + name };
        if (picker.ShowDialog(this) == true) KernelPath.Text = picker.FileName;
    }
    private void ModeChanged(object sender, RoutedEventArgs e) => UpdateMode();
    private void MinerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (sender == MinerChoice && SelectedMiner == MinerKind.SrbMiner) { DeviceChoice.SelectedIndex = 0; BenchmarkMode.IsChecked = false; }
        if (sender == DeviceChoice && SelectedDevice is MiningDevice.Cpu or MiningDevice.IntegratedGpu)
        {
            MinerChoice.SelectedIndex = 1;
            BenchmarkMode.IsChecked = true;
        }
        UpdateMode();
    }
    private void UpdateMode()
    {
        if (!_uiReady) return;
        bool demo = DemoMode.IsChecked == true;
        bool editable = !_busy && !HasActiveTask && !_closing;
        MinerChoice.IsEnabled = DeviceChoice.IsEnabled = editable;
        CpuThreads.IsEnabled = editable && SelectedDevice == MiningDevice.Cpu;
        BenchmarkMode.IsEnabled = editable && SelectedMiner == MinerKind.BzMiner;
        AutoRecover.IsEnabled = editable && !IsBenchmark;
        RealFields.IsEnabled = !demo && editable;
        WalletFields.IsEnabled = !IsBenchmark;
        WalletFields.Visibility = IsBenchmark ? Visibility.Collapsed : Visibility.Visible;
        if (!HasActiveTask)
            SpeedCaption.Text = demo ? "模拟算力 · 非真实收益" : IsBenchmark ? "离线自测算力" : "内核报告算力";
        StartButton.Content = demo ? "开始演示" : IsBenchmark ? SelectedDevice == MiningDevice.Cpu ? "开始 CPU 自测" : "开始离线自测" : "开始挖矿";
        ModeBanner.Text = demo ? "演示模式已开启。算力和份额均为模拟数据，不连接矿池，不运行真实挖矿。"
            : IsBenchmark ? $"{MiningDisplay.DeviceName(SelectedDevice)} 离线自测：真实运行 PRL 算法，不连接矿池、不产生收益。完成验证后请点击停止。"
            : SelectedDevice == MiningDevice.Cpu ? "CPU 矿池模式：只使用选定的 CPU 线程，禁用 GPU 挖矿。份额与结算以矿池为准。"
            : "真实 GPU 挖矿模式。仅在你点击开始并核对收款地址后启动；请使用你信任的官方内核。";
    }

    private async void SaveConfiguration(object sender, RoutedEventArgs e)
    {
        try { await _store.SaveAsync(Options()); Feedback.Text = "配置已保存在本机；不会自动启动挖矿。"; }
        catch (Exception ex) { Feedback.Text = "保存失败：" + ex.Message; }
    }

    private async void StartMining(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected || _closing) return;
        var options = Options();
        var errors = options.Validate();
        if (errors.Count > 0) { Feedback.Text = string.Join("\n", errors); return; }
        if (!options.DemoMode && MessageBox.Show(this,
            options.BenchmarkMode ? $"将使用 BzMiner 进行 PRL 离线自测。\n\n设备：{MiningDisplay.DeviceName(options.Device)}{(options.Device == MiningDevice.Cpu ? $" / {options.CpuThreads} 线程" : "")}\n内核：{options.KernelPath}\n\n不连接矿池，不产生收益。自测会使用真实硬件，请确认内核来源。"
            : $"将运行 {options.Miner} / {options.Device}{(options.Device == MiningDevice.Cpu ? $" / {options.CpuThreads} 线程" : "")}。\n\n收款地址：{options.Wallet}\n矿池：{options.Pool}\n矿机名：{options.Worker}\n内核：{options.KernelPath}\n\n请确认地址和内核来源。开发费按内核版本执行。",
            "核对本次运行配置", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        _busy = true; UpdateButtons();
        try
        {
            var response = await _client.SendAsync(new("start", options, _sequence));
            Apply(response.Snapshot);
            Feedback.Text = response.Success ? options.BenchmarkMode ? "离线自测请求已发送，不等待矿池份额；查看内核日志和算力。" : "启动请求已发送，等待内核和份额。" : response.Error;
        }
        catch (Exception ex) { ConnectionFailed(ex); }
        finally { _busy = false; UpdateButtons(); }
    }

    private async void StopMining(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected || _closing) return;
        _busy = true; UpdateButtons();
        try
        {
            var response = await _client.SendAsync(new("stop", AfterSequence: _sequence));
            Apply(response.Snapshot); Feedback.Text = response.Success ? "任务已停止。" : response.Error;
        }
        catch (Exception ex) { ConnectionFailed(ex); }
        finally { _busy = false; UpdateButtons(); }
    }

    private async void PollStatus(object? sender, EventArgs e)
    {
        if (_busy || _closing || !_connected) return;
        _busy = true;
        try { Apply((await _client.SendAsync(new("status", AfterSequence: _sequence))).Snapshot); }
        catch (Exception ex) { ConnectionFailed(ex); }
        finally { _busy = false; UpdateButtons(); }
    }

    private bool HasActiveTask => _state is MiningState.Starting or MiningState.Running or MiningState.Mining or MiningState.Recovering or MiningState.Stopping;
    private void UpdateButtons()
    {
        StartButton.IsEnabled = _connected && !_busy && !HasActiveTask && !_closing;
        StopButton.IsEnabled = _connected && !_busy && HasActiveTask && !_closing;
        DemoMode.IsEnabled = !HasActiveTask && !_busy && !_closing;
        AutoRecover.IsEnabled = DemoMode.IsEnabled;
        SaveButton.IsEnabled = !_closing;
        UpdateMode();
    }

    private void Apply(MiningSnapshot snapshot)
    {
        _state = snapshot.State;
        StateLabel.Text = MiningDisplay.StateText(snapshot, DateTimeOffset.Now);
        var device = snapshot.Device == MiningDevice.Cpu ? $"CPU / {snapshot.CpuThreads} 线程" : MiningDisplay.DeviceName(snapshot.Device);
        ProcessLabel.Text = snapshot.ProcessId is { } pid ? $"{snapshot.Miner} / {device} · PID {pid}" : "内核未运行";
        bool fresh = MiningDisplay.Fresh(snapshot, DateTimeOffset.Now);
        SpeedCaption.Text = snapshot.DemoMode ? "模拟算力 · 非真实收益" : snapshot.BenchmarkMode ? "离线自测算力" : "内核报告算力";
        SpeedLabel.Text = fresh && snapshot.Hashrate is { } speed ? $"{speed:0.##} {snapshot.HashrateUnit}" : "—";
        SharesLabel.Text = snapshot.BenchmarkMode ? "不适用" : $"{snapshot.Accepted} / {snapshot.Rejected}";
        RestartsLabel.Text = snapshot.Restarts.ToString();
        if (snapshot.Error is not null) Feedback.Text = snapshot.Error;
        foreach (var log in snapshot.Logs)
        {
            _sequence = Math.Max(_sequence, log.Sequence);
            _logs.Enqueue($"{log.Time:HH:mm:ss} [{log.Level}] {log.Message}");
        }
        while (_logs.Count > 250) _logs.Dequeue();
        if (snapshot.Logs.Length > 0) { LogOutput.Text = string.Join(Environment.NewLine, _logs); LogOutput.ScrollToEnd(); }
        UpdateButtons();
    }

    private void ConnectionFailed(Exception ex)
    {
        _connected = false; _poll.Stop();
        StateLabel.Text = "后台连接中断";
        ProcessLabel.Text = "进程保护已关闭；请重新打开客户端。";
        SpeedLabel.Text = "—";
        Feedback.Text = ex is OperationCanceledException ? "后台响应超时，已结束本次控制连接。" : ex.Message;
        UpdateButtons();
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _poll.Stop(); UpdateButtons();
        if (_initialization is not null) await _initialization;
        _poll.Stop();
        try { await _client.DisposeAsync(); }
        catch (Exception ex) { Feedback.Text = "关闭时发生异常：" + ex.Message; }
        _closeReady = true;
        Close();
    }
}
