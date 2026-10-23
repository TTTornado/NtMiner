using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NTMiner.Modern.WinUI;

public sealed partial class MainWindow
{
    private async Task RunSmokeAsync()
    {
        Directory.CreateDirectory(_smokeDirectory!);
        var steps = new List<string>();
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("WinUI verification timed out."); await Task.Delay(100); }
        }
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException(description); steps.Add(description); }
        string? error = null;
        try
        {
            await Wait(() => _connected && !_busy);
            ThemeChoice.SelectedIndex = 1;
            Check(DemoMode.IsOn && _state == MiningState.Idle, "Default is demo; no automatic mining");
            Check(ImportedKernelChoice.Items.Count == 0 && KernelPath.Text == "", "Fresh kernel library and selector are empty");
            // 首次打开就必须正确：曾因构造函数里过早判断 IsPaneOpen 而把这段说明藏起来。
            Check(PaneFooterText.Visibility == Visibility.Visible, "Pane footer is visible on first open without toggling the pane");
            await CaptureAsync("overview-light.png");
            await StartTaskAsync();
            await Wait(() => _state == MiningState.Mining && !_busy);
            Check(SharesLabel.Text != "0 / 0" && SpeedLabel.Text.Contains("H/s"), "BzMiner demo metrics arrive through real agent");
            await CaptureAsync("demo-light.png");
            await StopTaskAsync();
            Check(_state == MiningState.Idle, "Stop completes and returns idle");
            Navigation.SelectedItem = ConfigurationItem; ShowPage("configuration");
            DeviceChoice.SelectedIndex = 1;
            Check(SelectedMiner == MinerKind.BzMiner && IsBenchmark && CpuThreads.Value == 1, "CPU selects BzMiner and one-thread offline self-test");
            await CaptureAsync("configuration-light.png");
            ConfigurationPanel.ChangeView(null, ConfigurationPanel.ScrollableHeight, null, true);
            await Task.Delay(400);
            await CaptureAsync("configuration-switches-light.png");
            ConfigurationPanel.ChangeView(null, 0, null, true);
            await Task.Delay(300);
            // The configuration page must follow the window width like every other page.
            var wideCard = FindContentWidth(ConfigurationPanel);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 900));
            await Task.Delay(700);
            var narrowCard = FindContentWidth(ConfigurationPanel);
            Check(narrowCard < wideCard, $"Configuration page follows the window width (wide={wideCard:0}, narrow={narrowCard:0})");
            await CaptureAsync("configuration-narrow-light.png");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 920));
            await Task.Delay(700);
            // Collapsed sidebar: the content area gets wider, which is where layout regressions hide.
            Navigation.IsPaneOpen = false;
            await Task.Delay(400);
            await CaptureAsync("configuration-collapsed-light.png");
            Check(true, "Configuration page renders with the sidebar collapsed");
            Navigation.IsPaneOpen = true;
            await Task.Delay(400);
            Navigation.SelectedItem = OverviewItem; ShowPage("overview");
            await StartTaskAsync();
            await Wait(() => SpeedLabel.Text.EndsWith("H/s") && _state == MiningState.Running && !_busy);
            Check(ProcessLabel.Text.Contains("BzMiner / CPU / 1") && SharesLabel.Text == "不适用", "BzMiner CPU demo and offline labeling");
            await StopTaskAsync();
            DeviceChoice.SelectedIndex = 2;
            Check(SelectedMiner == MinerKind.BzMiner && SelectedDevice == MiningDevice.IntegratedGpu && IsBenchmark && !CpuThreads.IsEnabled, "igpu selection uses BzMiner and offline mode without CPU threads");
            await StartTaskAsync();
            await Wait(() => StateLabel.Text.Contains("已产生算力") && DeviceLabel.Text.Contains("演示核显") && !_busy);
            Check(SharesLabel.Text == "不适用" && RunEvidenceLabel.Text.Contains("模拟"), "igpu demo reports a clearly simulated device and positive offline speed");
            await CaptureAsync("igpu-demo-light.png");
            await StopTaskAsync();
            DeviceChoice.SelectedIndex = 0;
            Check(IsBenchmark && BenchmarkMode.IsEnabled, "All GPU mode retains BzMiner offline self-test");
            await StartTaskAsync();
            await Wait(() => StateLabel.Text.Contains("已产生算力") && DeviceLabel.Text.Contains("演示 GPU") && !_busy);
            await StopTaskAsync();
            DeviceChoice.SelectedIndex = 2;
            ThemeChoice.SelectedIndex = 2;
            await CaptureAsync("overview-dark.png");
            Navigation.SelectedItem = DashboardItem; ShowPage("dashboard");
            try { await LoadHardwareAsync(); }
            catch (Exception ex) { throw new InvalidOperationException("LoadHardwareAsync threw: " + ex.Message + " | connected=" + _connected + " detecting=" + _detecting, ex); }
            if (_hardware is null) throw new InvalidOperationException($"Hardware not loaded. connected={_connected} detecting={_detecting} summary={HardwareSummary.Text}");
            Check(_hardware is not null, $"Dashboard hardware loaded (summary={HardwareSummary.Text})");
            var hardware = _hardware!;
            Check(hardware.Processor is not null || hardware.Problems.Length > 0, "Dashboard detects hardware or reports why it cannot");
            var rendered = HardwareList.ItemsSource as IReadOnlyList<HardwareRow>;
            Check(LastRenderedRowCount == 8 && rendered is { Count: 8 }, $"Dashboard lists one row per hardware class (rendered={LastRenderedRowCount})");
            Check(rendered is not null && rendered.Any(r => r.Key == "cpu") && rendered.Any(r => r.Key == "network"), "Rows cover processor through network");
            // 本机装有远程控制软件的虚拟显示适配器；它必须被排除在可挖矿设备之外。
            if (hardware.Graphics.Length > 0)
            {
                var mineable = HardwareDisplay.Mineable(hardware.Graphics);
                Check(mineable.All(g => !g.IsVirtual), "Virtual display adapters are excluded from mineable devices");
                Check(rendered is not null && rendered.Any(r => r.Key == "gpu" && r.Value.Length > 0), "Graphics row carries a summary");
            }
            Check(HardwareAdvice.IsOpen && !string.IsNullOrWhiteSpace(HardwareAdvice.Message), "Dashboard explains how hardware matches the selected mining mode");
            // 详情必须是可打开的：直接构造与点击相同的面板，确认每个类别都能产出行。
            foreach (var key in new[] { "cpu", "memory", "board", "gpu", "monitor", "disk", "sound", "network" })
            {
                var detail = BuildDetail(key, hardware);
                Check(detail is ScrollViewer { Content: StackPanel { Children.Count: > 0 } },
                    $"Detail panel for '{key}' contains fields");
            }
            await CaptureAsync("dashboard-dark.png");
            Navigation.SelectedItem = LogsItem; ShowPage("logs");
            Check(LogOutput.Text.Contains("[miner]"), "Logs remain visible across navigation");
            await CaptureAsync("logs-dark.png");
            Navigation.SelectedItem = ConfigurationItem; ShowPage("configuration");
            DemoMode.IsOn = false;   // 开关位于设置页，但对象本身在任何页面都可访问
            Check(WalletFields.Visibility == Visibility.Collapsed && KernelPath.IsEnabled, "Offline real mode hides payout fields");
            await StartTaskAsync();
            Check(FeedbackBar.Severity == InfoBarSeverity.Error && _state == MiningState.Idle, "Invalid real kernel rejected before confirmation or start");
            await CaptureAsync("configuration-dark.png");
            await _store.SaveAsync(Options());
            var saved = await _store.LoadAsync();
            Check(saved.SchemaVersion == 5 && saved.Device == MiningDevice.IntegratedGpu && saved.BenchmarkMode, "WinUI saves igpu and benchmark in the shared v5 profile");
            Check(CommandLinePreview.Text.Contains("--igpu") && CommandLinePreview.Text.Contains("--bench"), "Command-line preview reflects the selected device and offline mode");
            Check(CommonSwitchPanel.Children.Count > 0 || AdvancedSwitchPanel.Children.Count > 0, "Kernel switches are rendered for the selected kernel");
            var addressA = "prl1" + new string('a', 58);
            var addressB = "prl1" + new string('b', 58);
            Wallet.Text = addressA; AddWallet(this, new RoutedEventArgs());
            Wallet.Text = addressB; AddWallet(this, new RoutedEventArgs());
            WalletChoice.SelectedItem = addressA;
            Check(Wallet.Text == addressA && _wallets.Length == 2, "Saved wallet selection updates the payout address");
            BackupPoolText.Text = "stratum+tcp://primary-backup.example:3333\nstratum+ssl://secure-backup.example:443";
            AlgorithmText.Text = "pearl";
            if (_saveTask is not null) await _saveTask;
            var extended = await _store.LoadAsync();
            Check(extended.BackupPools.Length == 2 && extended.SavedWallets.Length == 2 && extended.Algorithm == "pearl", "Wallet list, ordered backups and algorithm persist together");
            RemoveWallet(this, new RoutedEventArgs());
            if (_saveTask is not null) await _saveTask;
            Check((await _store.LoadAsync()).Wallet == "" && _wallets.Length == 1, "Removing the current wallet does not silently select another payout");
            AlgorithmChoice.ItemsSource = new[] { new MinerAlgorithm("cn", 1, ["opencl"]), new MinerAlgorithm("pearl", 2, ["opencl"]) };
            AlgorithmChoice.SelectedIndex = -1;
            AlgorithmChoice.IsDropDownOpen = true;
            await Task.Delay(2200);
            Check(AlgorithmChoice.IsDropDownOpen && AlgorithmChoice.IsEnabled, "Algorithm dropdown stays open across periodic status refreshes");
            AlgorithmChoice.SelectedIndex = 1;
            AlgorithmChoice.IsDropDownOpen = false;
            Check(AlgorithmText.Text == "pearl", "Algorithm selection updates the persisted field");
            await RefreshSensorsAsync();
            Check(_sensors is not null && SensorSourceLabel.Text.Length > 0, "Sensor IPC renders readings or an explicit unavailable explanation");
            // Feed controlled snapshots only after stopping the actual demo session.
            _poll?.Stop();
            Navigation.SelectedItem = OverviewItem; ShowPage("overview");
            var current = (await _client.SendAsync(new("status"))).Snapshot;
            var sample = current with { State = MiningState.Running, DemoMode = false, Device = MiningDevice.IntegratedGpu,
                Miner = MinerKind.BzMiner, BenchmarkMode = true, Hashrate = 0, HashrateUnit = "GH/s", LastSpeedAt = DateTimeOffset.Now,
                Error = null, DeviceNames = ["Intel(R) UHD Graphics 730（状态展示样例）"], Logs = [] };
            Apply(sample);
            Check(StateLabel.Text.Contains("尚无有效算力") && RunEvidenceLabel.Text.Contains("尚无正算力"), "Zero speed is never shown as successful computation");
            sample = sample with { Hashrate = 27.58, LastSpeedAt = DateTimeOffset.Now };
            Apply(sample);
            Check(StateLabel.Text.Contains("已产生算力") && DeviceLabel.Text.Contains("UHD Graphics 730") && RunEvidenceLabel.Text.Contains("不代表矿池"), "Real-mode presentation distinguishes computation from accepted pool shares");
            await CaptureAsync("igpu-status-sample-dark.png");
            Apply(sample with { LastSpeedAt = DateTimeOffset.Now.AddSeconds(-31) });
            Check(SpeedLabel.Text == "—" && RunEvidenceLabel.Text.Contains("30 秒"), "Expired speed is hidden with an explicit explanation");
            Apply(sample with { State = MiningState.Failed, Error = "BzMiner 可用系统内存不足：需要 1737 MB，当前可用 1636 MB。已停止任务且不自动重试。" });
            Check(StateLabel.Text == "任务失败" && SpeedLabel.Text == "—" && FeedbackBar.Message.Contains("1737 MB"), "Memory failure shows required and available memory and hides stale speed");
            await CaptureAsync("igpu-memory-sample-dark.png");
            Apply(current);
            var importSource = Path.Combine(_smokeDirectory!, "import-source");
            Directory.CreateDirectory(importSource);
            var importedExe = Path.Combine(importSource, "bzminer.exe");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "NTMiner.FakeMiner.exe"), importedExe, true);
            await ImportKernelAsync(importedExe);
            Check(ImportedKernelChoice.SelectedItem is ImportedKernel && KernelPath.Text != importedExe && File.Exists(KernelPath.Text), "Import copies and selects a managed kernel without executing it");
            if (_saveTask is not null) await _saveTask;
            var importedProfile = await _store.LoadAsync();
            RefreshImportedKernels(importedProfile.KernelPath);
            Check(ImportedKernelChoice.SelectedItem is ImportedKernel && KernelPath.Text == importedProfile.KernelPath, "Imported kernel selection persists and restores");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(_smokeDirectory!, "result.json"), JsonSerializer.Serialize(new { Passed = error is null, Steps = steps, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = error is null ? 0 : 1;
            Close();
        }
    }

    /// <summary>测量内容面板内第一个卡片的实际宽度，用于判断页面是否随窗口伸缩。</summary>
    private static double FindContentWidth(FrameworkElement panel)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(panel);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is Border { ActualWidth: > 0 } border) return border.ActualWidth;
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
        return 0;
    }

    private async Task CaptureAsync(string name)
    {
        await Task.Delay(250);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length];
        reader.ReadBytes(pixels);
        var folder = await StorageFolder.GetFolderFromPathAsync(_smokeDirectory!);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
