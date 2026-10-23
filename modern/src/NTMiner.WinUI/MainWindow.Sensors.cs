using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NTMiner.Modern.WinUI;

/// <summary>概览页一行显卡传感器。字段用字符串承载已格式化结果，避免界面里再判断 null。</summary>
public sealed record GpuSensorRow(
    string Title,
    string Temperature,
    string Fan,
    string Power,
    string Clock,
    string Memory,
    string OverclockNote)
{
    /// <summary>温度达到告警阈值时换成主题强调色，未读到读数时不着色。</summary>
    public SolidColorBrush TemperatureBrush => Level switch
    {
        GpuHeatLevel.Critical => new SolidColorBrush(Colors.OrangeRed),
        GpuHeatLevel.Warning => new SolidColorBrush(Colors.Orange),
        _ => (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"]
    };

    internal GpuHeatLevel Level { get; init; }
}

/// <summary>
/// 显卡传感器页面逻辑。数据来自后台 Agent 的 sensors 命令——
/// NVML/ADL 的加载上下文由 Agent 进程提供，前端只负责展示。
/// </summary>
public sealed partial class MainWindow
{
    private GpuSensorSnapshot? _sensors;
    private DispatcherQueueTimer? _sensorPoll;
    private bool _sensorBusy;

    /// <summary>最近一次渲染的行数，供自动化验收判断渲染确实发生。</summary>
    internal int LastSensorRowCount { get; private set; }

    /// <summary>开始周期采样。间隔比状态轮询长，避免频繁触发厂商 SDK 调用。</summary>
    private void StartSensorPolling()
    {
        if (_sensorPoll is not null) return;
        _sensorPoll = DispatcherQueue.CreateTimer();
        _sensorPoll.Interval = TimeSpan.FromSeconds(3);
        _sensorPoll.Tick += async (_, _) => await RefreshSensorsAsync();
        _sensorPoll.Start();
        _ = RefreshSensorsAsync();
    }

    private void StopSensorPolling() => _sensorPoll?.Stop();

    private async Task RefreshSensorsAsync()
    {
        if (!_connected || _closing || _sensorBusy) return;
        _sensorBusy = true;
        try
        {
            var response = await _client.SendAsync(new("sensors"));
            if (response.Sensors is { } snapshot) RenderSensors(snapshot);
            else
            {
                SensorEmptyHint.Text = "无法读取显卡传感器：" + (response.Error ?? "后台未返回数据。");
                SensorEmptyHint.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            // 传感器失败不应打断挖矿监控，只在卡片内说明。
            SensorEmptyHint.Text = "显卡传感器读取失败：" + ex.Message;
            SensorEmptyHint.Visibility = Visibility.Visible;
        }
        finally { _sensorBusy = false; }
    }

    private void RenderSensors(GpuSensorSnapshot snapshot)
    {
        _sensors = snapshot;
        var rows = snapshot.Readings.Select(reading => new GpuSensorRow(
            $"{reading.Name} · {GpuSensorDisplay.VendorName(reading.Vendor)}",
            GpuSensorDisplay.Temperature(reading.TemperatureC),
            GpuSensorDisplay.Fan(reading.FanSpeedPercent),
            GpuSensorDisplay.Power(reading.PowerWatts, reading.PowerLimitWatts),
            GpuSensorDisplay.Clock(reading.CoreClockMhz),
            GpuSensorDisplay.Memory(reading.MemoryUsedMb, reading.MemoryTotalMb),
            OverclockDisplay.DenyWriteReason(OverclockDisplay.FromSensor(reading)) ?? "")
        { Level = GpuSensorDisplay.HeatLevel(reading.TemperatureC) }).ToArray();

        GpuSensorList.ItemsSource = rows;
        LastSensorRowCount = rows.Length;
        SensorSourceLabel.Text = snapshot.Provider == "none" ? "无可用传感器" : $"数据源 {snapshot.Provider} · {snapshot.CapturedAt:HH:mm:ss}";

        if (rows.Length == 0)
        {
            // 说明为什么没有数据，而不是显示一个空卡片。
            var reason = snapshot.Problems.Length > 0
                ? string.Join("；", snapshot.Problems)
                : "本机没有可读取传感器的显卡。核显通常不提供温度与风扇读数，集成显卡的显存与系统共享。";
            SensorEmptyHint.Text = reason;
            SensorEmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            SensorEmptyHint.Visibility = Visibility.Collapsed;
        }
    }
}
