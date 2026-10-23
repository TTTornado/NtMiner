using System.Management;

namespace NTMiner.Modern;

/// <summary>
/// 依次尝试各个显卡传感器提供者，合并结果。
/// 任一提供者不可用（厂商 DLL 缺失、无此类设备、驱动不支持）都不影响其他提供者，
/// 也不会向调用方抛异常——界面上宁可显示"读不到"，也不能因为显卡差异导致程序崩溃。
/// </summary>
public sealed class GpuSensorService(Func<IReadOnlyList<IGpuSensorProvider>> providers)
{
    /// <summary>默认组合：NVIDIA(NVML) → AMD(ADL) → Intel(WMI)。顺序只影响同名设备的归属。</summary>
    public static GpuSensorService CreateDefault() => new(() =>
    [
        new IntelGpuSensorProvider(),
        new NvidiaGpuSensorProvider(),
        new AmdGpuSensorProvider()
    ]);

    public GpuSensorSnapshot Capture()
    {
        var problems = new List<string>();
        var readings = new List<GpuSensorReading>();
        var used = new List<string>();
        foreach (var provider in providers())
        {
            try
            {
                if (!provider.IsAvailable()) continue;
                var items = provider.Read();
                if (items.Count == 0) continue;
                readings.AddRange(items);
                used.Add(provider.Name);
            }
            catch (Exception ex)
            {
                // 单个提供者失败只记录类型，不影响其余提供者。
                problems.Add($"{provider.Name} 读取失败：{ex.GetType().Name}。");
            }
        }

        // 同名设备可能被多个提供者重复报告，保留字段更丰富的那一条。
        var merged = readings
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(Richness).First())
            .ToArray();

        return new GpuSensorSnapshot(DateTimeOffset.Now, used.Count == 0 ? "none" : string.Join("+", used),
            merged, problems.ToArray());
    }

    /// <summary>按"读到了几项"排序，用于在重复报告中挑选信息更完整的一条。</summary>
    private static int Richness(GpuSensorReading reading)
    {
        var score = 0;
        if (reading.TemperatureC is not null) score++;
        if (reading.MemoryTemperatureC is not null) score++;
        if (reading.FanSpeedPercent is not null) score++;
        if (reading.PowerWatts is not null) score++;
        if (reading.PowerLimitWatts is not null) score++;
        if (reading.CoreClockMhz is not null) score++;
        if (reading.MemoryClockMhz is not null) score++;
        if (reading.MemoryUsedMb is not null) score++;
        if (reading.UtilizationPercent is not null) score++;
        return score;
    }
}

/// <summary>
/// Intel 核显传感器。Intel 没有随驱动提供的独立传感器 SDK，因此走 WMI 的
/// Win32_VideoController 读取型号与驱动侧可得的显存信息；温度与功耗在多数平台上
/// 不可通过通用接口获取，此时如实返回 null，界面显示"—"。
/// </summary>
public sealed class IntelGpuSensorProvider : IGpuSensorProvider
{
    private const string CimNamespace = @"root\CIMV2";
    public string Name => "Intel-WMI";

    public bool IsAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(CimNamespace, "SELECT Name FROM Win32_VideoController");
            return searcher.Get().Cast<ManagementObject>()
                .Select(o => o["Name"]?.ToString() ?? "")
                .Any(n => n.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public IReadOnlyList<GpuSensorReading> Read()
    {
        var result = new List<GpuSensorReading>();
        using var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT Name,AdapterRAM,PnPDeviceId FROM Win32_VideoController");
        foreach (ManagementObject item in searcher.Get())
        {
            var name = item["Name"]?.ToString()?.Trim() ?? "";
            // 只处理 Intel 的物理显卡；ROOT\ 枚举的虚拟适配器与 Intel 无关。
            if (!name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) continue;
            var deviceId = item["PnPDeviceId"]?.ToString() ?? "";
            if (deviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;

            long? ram = null;
            try { var raw = Convert.ToInt64(item["AdapterRAM"] ?? 0L); if (raw > 0) ram = raw; }
            catch { /* 字段类型异常时留空 */ }

            result.Add(new GpuSensorReading(
                name, GpuVendor.Intel,
                TemperatureC: null, MemoryTemperatureC: null, FanSpeedPercent: null,
                PowerWatts: null, PowerLimitWatts: null,
                CoreClockMhz: null, MemoryClockMhz: null,
                // 核显与系统共享内存，AdapterRAM 只代表可分配的显示内存上限，不是独立显存。
                MemoryUsedMb: null,
                MemoryTotalMb: ram is { } bytes ? Math.Round(bytes / 1024d / 1024d) : null,
                UtilizationPercent: null));
        }
        return result;
    }
}
