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
    string[] Problems,
    HardwareMonitor[] Monitors,
    HardwareSoundDevice[] SoundDevices,
    HardwareNetworkAdapter[] NetworkAdapters)
{
    public static HardwareSnapshot Empty(DateTimeOffset capturedAt, params string[] problems) =>
        new(capturedAt, null, null, null, [], [], null, problems, [], [], []);
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

/// <summary>SMBIOS 内存类型编号（34 = DDR5）换算成可读名称，未知返回空串。</summary>
public sealed record HardwareMemoryModule(
    string Slot, long CapacityBytes, int SpeedMhz, string Manufacturer, string PartNumber, int SmbiosType)
{
    public string MemoryType => HardwareDisplay.MemoryTypeName(SmbiosType);
}

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

public sealed record HardwareMonitor(string Name, string Manufacturer, int Width, int Height);

public sealed record HardwareSoundDevice(string Name, string Manufacturer);

/// <summary>网卡。已禁用的适配器通过 IsEnabled 标出，避免与当前可用的网卡混在一起。</summary>
public sealed record HardwareNetworkAdapter(string Name, string ConnectionName, long SpeedBitsPerSecond, bool IsEnabled)
{
    public string LinkSpeed => HardwareDisplay.FormatLinkSpeed(SpeedBitsPerSecond);
}

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
    // ---- 友好名称：把 SMBIOS/WMI 的英文标识转换成界面上更易读的中文描述 ----

    /// <summary>SMBIOS 内存类型。未列出的编号返回空串，由界面按"未知"处理，不猜测。</summary>
    public static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3",
        26 => "DDR4", 34 => "DDR5", 35 => "LPDDR4", 36 => "LPDDR5", _ => ""
    };

    /// <summary>
    /// CPU 摘要："13th Gen Intel(R) Core(TM) i5-13400 10C/16T"。
    /// 保留 WMI 原始型号，不做翻译；核数用物理/逻辑线程的紧凑写法。
    /// </summary>
    public static string ProcessorSummary(HardwareProcessor? cpu)
    {
        if (cpu is not { Reported: true }) return "未检测到";
        var name = cpu.Name.Trim();
        var cores = cpu.Cores > 0 && cpu.LogicalProcessors > 0 ? $"{cpu.Cores}C/{cpu.LogicalProcessors}T"
            : cpu.LogicalProcessors > 0 ? $"{cpu.LogicalProcessors}T"
            : "";
        return string.IsNullOrEmpty(cores) ? name : $"{name} {cores}";
    }
    /// <summary>
    /// SMBIOS 的占位厂商值（未填写的默认字符串）。这类值不是真实厂商，展示出来只有噪声。
    /// 这是数据清洗，不是名称翻译：真实厂商名保持原样。
    /// </summary>
    private static bool IsPlaceholderVendor(string vendor) =>
        vendor.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
        || vendor.Equals("Default string", StringComparison.OrdinalIgnoreCase)
        || vendor.Equals("O.E.M.", StringComparison.OrdinalIgnoreCase)
        || vendor.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)
        || vendor.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    /// <summary>主板摘要："Micro-Star International Co., Ltd. B760M GAMING PLUS WIFI (MS-7D99)"。</summary>
    public static string MotherboardSummary(HardwareMotherboard? board)
    {
        if (board is null) return "未检测到";
        var vendor = string.IsNullOrWhiteSpace(board.Manufacturer) ? "" : board.Manufacturer.Trim();
        var product = string.IsNullOrWhiteSpace(board.Product) ? "" : board.Product.Trim();
        return string.IsNullOrWhiteSpace(vendor) ? (string.IsNullOrWhiteSpace(product) ? "未检测到" : product)
            : string.IsNullOrWhiteSpace(product) ? vendor : $"{vendor} {product}";
    }

    /// <summary>
    /// 内存摘要："Kingston 16GB DDR5 6000MHz"。多条模块合并容量并按去重后的频率与类型展示；
    /// 厂商不同或类型不一致时省略该段，避免把不准确的信息拼成一条。
    /// </summary>
    public static string MemorySummary(HardwareMemory? memory)
    {
        if (memory is null || memory.TotalBytes <= 0) return "未检测到";
        var modules = memory.Modules;
        var capacity = FormatGiB(modules.Length > 0 ? memory.InstalledBytes : memory.TotalBytes);
        if (modules.Length == 0) return capacity;
        var vendors = modules.Select(m => m.Manufacturer.Trim()).Where(v => v.Length > 0 && !IsPlaceholderVendor(v)).Distinct().ToArray();
        var types = modules.Select(m => m.MemoryType).Where(t => t.Length > 0).Distinct().ToArray();
        var speeds = modules.Select(m => m.SpeedMhz).Where(s => s > 0).Distinct().ToArray();
        var parts = new List<string>();
        if (vendors.Length == 1) parts.Add(vendors[0]);
        parts.Add(capacity);
        if (types.Length == 1) parts.Add(types[0]);
        if (speeds.Length == 1) parts.Add(speeds[0] + "MHz");
        return string.Join(" ", parts);
    }

    /// <summary>以 GiB 为单位展示容量：整 GB 不带小数，其余保留一位。</summary>
    public static string FormatGiB(long bytes)
    {
        if (bytes <= 0) return "—";
        var gib = bytes / 1024d / 1024d / 1024d;
        return Math.Abs(gib - Math.Round(gib)) < 0.05 ? $"{Math.Round(gib):0}GB" : $"{gib:0.#}GB";
    }

    /// <summary>显卡摘要："Intel(R) UHD Graphics 730（核显）"；多条用顿号并列。</summary>
    public static string GraphicsSummary(IReadOnlyList<HardwareGraphics> graphics)
    {
        var mineable = Mineable(graphics);
        // 虚拟适配器已在探测层排除；此处仍保留判断，防止其他数据源再次引入。
        if (mineable.Length == 0) return "未检测到";
        return string.Join("、", mineable.Select(g => $"{g.Name}（{(g.IsIntegrated ? "核显" : "独显")}）"));
    }

    public static string DiskSummary(IReadOnlyList<HardwareDisk> disks) =>
        disks.Count == 0 ? "未检测到" : string.Join("、", disks.Select(d => d.Model));

    public static string MonitorSummary(IReadOnlyList<HardwareMonitor> monitors)
    {
        var named = monitors.Where(m => !string.IsNullOrWhiteSpace(m.Name)
            && !m.Name.Contains("默认监视器", StringComparison.OrdinalIgnoreCase)
            && !m.Name.Contains("Default Monitor", StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length == 0 ? "未检测到" : string.Join("、", named.Select(m => m.Name));
    }

    public static string SoundSummary(IReadOnlyList<HardwareSoundDevice> devices) =>
        devices.Count == 0 ? "未检测到" : devices[0].Name;

    /// <summary>网卡摘要：优先列出已连接的有线/无线网卡，蓝牙虚拟网卡排在后面。</summary>
    public static string NetworkSummary(IReadOnlyList<HardwareNetworkAdapter> adapters)
    {
        var real = adapters.Where(a => !a.Name.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)).ToArray();
        var chosen = real.Length > 0 ? real : adapters.ToArray();
        return chosen.Length == 0 ? "未检测到" : string.Join("、", chosen.Select(a => a.Name));
    }

    /// <summary>网卡速率；未知或异常值返回 "—"，不把 9223372036854775807 这类哨兵值当作真实速率。</summary>
    public static string FormatLinkSpeed(long bitsPerSecond) =>
        bitsPerSecond is <= 0 or > 1_000_000_000_000 ? "—" : $"{bitsPerSecond / 1_000_000d:0.#} Gbps";
}
