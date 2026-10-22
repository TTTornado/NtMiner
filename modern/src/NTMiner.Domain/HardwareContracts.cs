namespace NTMiner.Modern;

/// <summary>一次硬件探测的完整结果。探测失败的分区保留为 null，不影响其他分区。</summary>
public sealed record HardwareSnapshot(
    DateTimeOffset CapturedAt,
    HardwareProcessor? Processor,
    HardwareMemory? Memory,
    HardwareMotherboard? Motherboard,
    HardwareGraphics[] Graphics,
    HardwareDisk[] Disks,
    HardwareOperatingSystem? OperatingSystem,
    string[] Problems)
{
    public static HardwareSnapshot Empty(DateTimeOffset capturedAt, params string[] problems) =>
        new(capturedAt, null, null, null, [], [], null, problems);
}

public sealed record HardwareProcessor(
    string Name, int Cores, int LogicalProcessors, int BaseClockMhz, int L3CacheKb)
{
    public bool Reported => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>物理内存。模块信息可能读不到，此时仅保留总量。</summary>
public sealed record HardwareMemory(long TotalBytes, HardwareMemoryModule[] Modules)
{
    public int SlotCount => Modules.Length;
    public long InstalledBytes => Modules.Sum(m => m.CapacityBytes);
}

public sealed record HardwareMemoryModule(
    string Slot, long CapacityBytes, int SpeedMhz, string Manufacturer, string PartNumber);

public sealed record HardwareMotherboard(string Manufacturer, string Product, string BiosVersion, string BiosDate);

/// <summary>
/// 显卡。IsVirtual 表示驱动型/远程桌面型虚拟显示适配器，不能用于挖矿；
/// IsIntegrated 由名称启发式判断，只用于界面提示，不替代内核的真实检测结果。
/// </summary>
public sealed record HardwareGraphics(
    string Name, string DriverVersion, string DriverDate, long? AdapterRamBytes, string PnpDeviceId)
{
    public bool IsVirtual => HardwareDisplay.IsVirtualAdapter(Name, PnpDeviceId);
    public bool IsIntegrated => HardwareDisplay.IsIntegratedAdapter(Name);
}

public sealed record HardwareDisk(string Model, string InterfaceType, long SizeBytes, bool IsRemovable);

public sealed record HardwareOperatingSystem(
    string Caption, string Version, int BuildNumber, string Architecture, int AvailableMemoryMb);

/// <summary>纯展示与判断规则，不接触 Windows API，便于在验收中直接验证。</summary>
public static class HardwareDisplay
{
    private static readonly string[] VirtualAdapterKeywords =
    [
        "virtual display", "microsoft basic", "remote desktop", "rdp", "todesk", "sunlogin", "parsec",
        "spacedesk", "usb display", "idd driver", "indirect display", "virtualbox", "vmware", "hyper-v",
        "citrix", "anydesk", "teamviewer", "splashtop"
    ];

    private static readonly string[] IntegratedAdapterKeywords =
    [
        "intel(r) uhd", "intel(r) hd", "uhd graphics", "hd graphics", "iris", "radeon(tm) graphics",
        "vega 8", "vega 11", "vega 7", "radeon graphics", "apu"
    ];

    private static readonly string[] DedicatedAdapterKeywords =
    [
        "geforce", "quadro", "rtx", "gtx", "radeon rx", "radeon pro", "rx ", "arc(tm)", "arc a",
        "tesla", "rtx a"
    ];

    /// <summary>虚拟适配器按名称或 PnP 路径识别；ROOT\ 枚举路径通常是软件适配器。</summary>
    public static bool IsVirtualAdapter(string name, string pnpDeviceId) =>
        !string.IsNullOrWhiteSpace(pnpDeviceId) && pnpDeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
        || ContainsAny(name, VirtualAdapterKeywords);

    /// <summary>
    /// 核显判断：先排除独显关键词，避免 "Radeon RX 760M" 这类独显被误判。
    /// 名称缺失时返回 false，不猜测。
    /// </summary>
    public static bool IsIntegratedAdapter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (ContainsAny(name, DedicatedAdapterKeywords)) return false;
        return ContainsAny(name, IntegratedAdapterKeywords);
    }

    /// <summary>只能用于挖矿的物理显卡，即排除虚拟适配器后的结果。</summary>
    public static HardwareGraphics[] Mineable(IEnumerable<HardwareGraphics> graphics) =>
        graphics.Where(g => !g.IsVirtual).ToArray();

    public static string AdapterRole(HardwareGraphics adapter) => adapter.IsVirtual ? "虚拟显示适配器 · 不可用于挖矿"
        : adapter.IsIntegrated ? "集成显卡（核显）· 仅适用于「仅核显」离线自测"
        : "独立显卡 · 可用于 GPU 挖矿";

    /// <summary>「仅核显」模式是否在这台主机上可能成立；没有显卡信息时不作结论。</summary>
    public static bool? SupportsIntegratedOnly(IReadOnlyList<HardwareGraphics> graphics)
    {
        if (graphics.Count == 0) return null;
        return graphics.Any(g => !g.IsVirtual && g.IsIntegrated);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static bool ContainsAny(string value, string[] keywords) =>
        !string.IsNullOrWhiteSpace(value)
        && keywords.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));
}
