using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            await CaptureAsync("overview-light.png");
            await StartTaskAsync();
            await Wait(() => _state == MiningState.Mining && !_busy);
            Check(SharesLabel.Text != "0 / 0" && SpeedLabel.Text.Contains("MH/s"), "SRBMiner demo metrics arrive through real agent");
            await CaptureAsync("demo-light.png");
            await StopTaskAsync();
            Check(_state == MiningState.Idle, "Stop completes and returns idle");
            Navigation.SelectedItem = ConfigurationItem; ShowPage("configuration");
            DeviceChoice.SelectedIndex = 1;
            Check(SelectedMiner == MinerKind.BzMiner && IsBenchmark && CpuThreads.Value == 1, "CPU selects BzMiner and one-thread offline self-test");
            await CaptureAsync("configuration-light.png");
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
            var rendered = HardwareSections.ItemsSource as IReadOnlyList<HardwareSection>;
            Check(LastRenderedSectionCount > 0 && rendered is { Count: > 0 }, $"Dashboard renders at least one hardware section (rendered={LastRenderedSectionCount}, source={rendered?.Count ?? -1}, summary={HardwareSummary.Text})");
            // 本机装有远程控制软件的虚拟显示适配器；它必须被排除在可挖矿设备之外。
            if (hardware.Graphics.Length > 0)
            {
                var mineable = HardwareDisplay.Mineable(hardware.Graphics);
                Check(mineable.All(g => !g.IsVirtual), "Virtual display adapters are excluded from mineable devices");
                Check(HardwareSummary.Text.Contains("显卡") || HardwareDisplay.SupportsIntegratedOnly(hardware.Graphics) == false, "Summary describes the detected graphics");
            }
            Check(HardwareAdvice.IsOpen && !string.IsNullOrWhiteSpace(HardwareAdvice.Message), "Dashboard explains how hardware matches the selected mining mode");
            await CaptureAsync("dashboard-dark.png");
            Navigation.SelectedItem = LogsItem; ShowPage("logs");
            Check(LogOutput.Text.Contains("[miner]"), "Logs remain visible across navigation");
            await CaptureAsync("logs-dark.png");
            Navigation.SelectedItem = ConfigurationItem; ShowPage("configuration");
            DemoMode.IsOn = false;
            Check(WalletFields.Visibility == Visibility.Collapsed && RealFields.IsEnabled, "Offline real mode hides payout fields");
            await StartTaskAsync();
            Check(FeedbackBar.Severity == InfoBarSeverity.Error && _state == MiningState.Idle, "Invalid real kernel rejected before confirmation or start");
            await CaptureAsync("configuration-dark.png");
            await _store.SaveAsync(Options());
            var saved = await _store.LoadAsync();
            Check(saved.SchemaVersion == 3 && saved.Device == MiningDevice.IntegratedGpu && saved.BenchmarkMode, "WinUI saves igpu and benchmark in the shared v3 profile");
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
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(_smokeDirectory!, "result.json"), JsonSerializer.Serialize(new { Passed = error is null, Steps = steps, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = error is null ? 0 : 1;
            Close();
        }
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
