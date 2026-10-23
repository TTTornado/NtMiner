namespace NTMiner.Modern;

/// <summary>
/// 显卡超频能力的**只读**视图：当前频率、功耗上限与厂商允许的调节范围。
///
/// 本版本刻意不实现写入。写入需要调用 NVAPI / ADL 的设置接口，一旦参数错误
/// 可能造成驱动崩溃或硬件损伤；而开发环境没有 NVIDIA/AMD 显卡，无法验证任何
/// 写入路径。这里先把"能读到什么"做出来，界面据此展示可调范围，写入留待
/// 有真实硬件的环境中单独实现并验证。
/// </summary>
public sealed record OverclockCapability(
    string DeviceName,
    GpuVendor Vendor,
    bool IsWritable,
    double? CoreClockMhz,
    double? MemoryClockMhz,
    double? PowerLimitWatts,
    OverclockRange? CoreOffsetRange,
    OverclockRange? MemoryOffsetRange,
    OverclockRange? PowerLimitRange)
{
    /// <summary>当前实现只读，因此任何设备都应返回 false。</summary>
    public bool CanApply => IsWritable;

    /// <summary>无法读取能力时的占位结果，界面据此显示"该设备不支持超频读取"。</summary>
    public static OverclockCapability Unavailable(string deviceName, GpuVendor vendor, string reason) =>
        new(deviceName, vendor, false, null, null, null, null, null, null) { Reason = reason };

    /// <summary>说明为何不可写，供界面展示而不是让用户以为是 bug。</summary>
    public string? Reason { get; init; }
}

/// <summary>一个可调参数的上下限与步进，用于界面绘制滑块。</summary>
public sealed record OverclockRange(double Minimum, double Maximum, double Step)
{
    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    /// <summary>把用户输入夹到合法区间，并按步进对齐。</summary>
    public double Clamp(double value)
    {
        var clamped = Math.Clamp(value, Minimum, Maximum);
        if (Step <= 0) return clamped;
        var steps = Math.Round((clamped - Minimum) / Step, MidpointRounding.AwayFromZero);
        return Math.Min(Maximum, Minimum + steps * Step);
    }

    public override string ToString() => $"{Minimum:0} ~ {Maximum:0}（步进 {Step:0}）";
}

/// <summary>超频能力的展示与安全规则，纯函数，便于在无显卡环境中验收。</summary>
public static class OverclockDisplay
{
    /// <summary>
    /// 由传感器读数推导能力视图。写入能力恒为 false——
    /// 这是当前版本的明确边界，而不是"暂时读不到"。
    /// </summary>
    public static OverclockCapability FromSensor(GpuSensorReading reading) =>
        new(reading.Name, reading.Vendor, IsWritable: false,
            reading.CoreClockMhz, reading.MemoryClockMhz, reading.PowerLimitWatts,
            CoreOffsetRange: null, MemoryOffsetRange: null, PowerLimitRange: null)
        {
            Reason = "当前版本只读取超频状态，不写入硬件。写入功能需在有 NVIDIA/AMD 显卡的环境中单独验证后开放。"
        };

    public static string Describe(OverclockCapability capability) => capability switch
    {
        { CoreClockMhz: null, MemoryClockMhz: null, PowerLimitWatts: null } => "该设备未提供频率或功耗上限读数",
        _ => $"核心 {GpuSensorDisplay.Clock(capability.CoreClockMhz)} · 显存 {GpuSensorDisplay.Clock(capability.MemoryClockMhz)} · 上限 {GpuSensorDisplay.Power(capability.PowerLimitWatts, null)}"
    };

    /// <summary>
    /// 写入前置检查。当前版本恒返回不可执行的原因，防止界面绕过只读约束。
    /// 将来开放写入时，这里应补充权限、驱动版本与温度联锁检查。
    /// </summary>
    public static string? DenyWriteReason(OverclockCapability capability) =>
        capability.CanApply ? null : capability.Reason ?? "该设备不支持超频写入。";
}
