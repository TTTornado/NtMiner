namespace NTMiner.Modern;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

/// <summary>
/// 单个显卡的一次传感器读数。所有数值都是"读不到时为 null"，
/// 而不是用 0 冒充——0 度和"没有读数"在界面上含义完全不同。
/// </summary>
public sealed record GpuSensorReading(
    string Name,
    GpuVendor Vendor,
    double? TemperatureC,
    double? MemoryTemperatureC,
    int? FanSpeedPercent,
    double? PowerWatts,
    double? PowerLimitWatts,
    double? CoreClockMhz,
    double? MemoryClockMhz,
    double? MemoryUsedMb,
    double? MemoryTotalMb,
    double? UtilizationPercent)
{
    /// <summary>读到了至少一项传感器才算有效；用于区分"设备存在但读不到数据"。</summary>
    public bool HasAnyReading => TemperatureC is not null || FanSpeedPercent is not null
        || PowerWatts is not null || CoreClockMhz is not null || MemoryUsedMb is not null;

    /// <summary>温度达到该值时界面应提示风险。取厂商常见的降频阈值下限。</summary>
    public bool IsHot => TemperatureC is { } t && t >= 80;
}

/// <summary>一次传感器采样。Provider 名称用于界面说明数据来源。</summary>
public sealed record GpuSensorSnapshot(DateTimeOffset CapturedAt, string Provider, GpuSensorReading[] Readings, string[] Problems)
{
    public static GpuSensorSnapshot Empty(DateTimeOffset capturedAt, string reason) =>
        new(capturedAt, "none", [], [reason]);
}

/// <summary>
/// 显卡传感器提供者。实现方必须保证：DLL 缺失、驱动不支持、无此类设备时
/// 返回空数组而不是抛异常，让上层可以依次尝试其他提供者。
/// </summary>
public interface IGpuSensorProvider
{
    /// <summary>提供者名称，用于界面与问题列表，例如 "NVML"。</summary>
    string Name { get; }

    /// <summary>该提供者在本机是否可用（通常取决于厂商 DLL 能否加载）。</summary>
    bool IsAvailable();

    /// <summary>读取所有设备的传感器。不可用时返回空数组。</summary>
    IReadOnlyList<GpuSensorReading> Read();
}

/// <summary>传感器的单位换算与展示规则，纯函数，便于验收。</summary>
public static class GpuSensorDisplay
{
    public static string Temperature(double? value) => value is { } t ? $"{t:0} °C" : "—";
    public static string Fan(int? percent) => percent is { } p ? $"{p} %" : "—";
    public static string Power(double? watts, double? limit) => watts is { } w
        ? (limit is { } l && l > 0 ? $"{w:0} / {l:0} W" : $"{w:0} W")
        : "—";
    public static string Clock(double? mhz) => mhz is { } m ? $"{m:0} MHz" : "—";

    public static string Memory(double? usedMb, double? totalMb)
    {
        if (usedMb is not { } used) return "—";
        if (totalMb is { } total && total > 0) return $"{used:0} / {total:0} MB（{used / total * 100:0}%）";
        return $"{used:0} MB";
    }

    public static string Utilization(double? percent) => percent is { } p ? $"{p:0} %" : "—";

    /// <summary>温度的风险等级，仅用于界面配色，不改变判定逻辑。</summary>
    public static GpuHeatLevel HeatLevel(double? temperature) => temperature switch
    {
        null => GpuHeatLevel.Unknown,
        >= 85 => GpuHeatLevel.Critical,
        >= 78 => GpuHeatLevel.Warning,
        _ => GpuHeatLevel.Normal
    };

    public static GpuVendor VendorFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return GpuVendor.Unknown;
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ATI", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    public static string VendorName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "未知厂商"
    };
}

public enum GpuHeatLevel { Unknown, Normal, Warning, Critical }
