using System.Management;

namespace NTMiner.Modern;

/// <summary>
/// 只读硬件探测。每个分区独立捕获异常，单个分区失败不影响其他分区，
/// 失败原因写入 Problems 供界面如实展示，而不是静默返回"未知"。
/// 探测不提升权限、不写入注册表、不改变任何系统状态。
/// </summary>
public static class HardwareProbe
{
    private const string CimNamespace = @"root\CIMV2";

    /// <summary>同步探测；调用方应放到后台线程，避免阻塞界面。</summary>
    public static HardwareSnapshot Capture()
    {
        var problems = new List<string>();
        return new HardwareSnapshot(
            DateTimeOffset.Now,
            Probe(QueryProcessor, "处理器", problems),
            Probe(QueryMemory, "内存", problems),
            Probe(QueryMotherboard, "主板", problems),
            Probe(QueryGraphics, "显卡", problems) ?? [],
            Probe(QueryDisks, "存储", problems) ?? [],
            Probe(QueryOperatingSystem, "操作系统", problems),
            problems.ToArray(),
            Probe(QueryMonitors, "显示器", problems) ?? [],
            Probe(QuerySoundDevices, "声卡", problems) ?? [],
            Probe(QueryNetworkAdapters, "网卡", problems) ?? []);
    }

    private static T? Probe<T>(Func<T?> query, string section, List<string> problems) where T : class
    {
        try { return query(); }
        catch (Exception ex)
        {
            // 探测失败只记录分区与异常类型，不把底层堆栈暴露到界面。
            problems.Add($"{section}信息读取失败：{ex.GetType().Name}。" +
                (ex is ManagementException ? "系统可能限制了 WMI 访问。" : ""));
            return null;
        }
    }

    private static HardwareProcessor? QueryProcessor()
    {
        using var searcher = new ManagementObjectSearcher(CimNamespace, "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,L3CacheSize FROM Win32_Processor");
        foreach (ManagementObject item in searcher.Get())
        {
            var name = Text(item["Name"]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            return new HardwareProcessor(name,
                Int(item["NumberOfCores"]), Int(item["NumberOfLogicalProcessors"]),
                Int(item["MaxClockSpeed"]), Int(item["L3CacheSize"]));
        }
        return null;
    }

    private static HardwareMemory? QueryMemory()
    {
        long total = 0;
        using (var system = new ManagementObjectSearcher(CimNamespace, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            foreach (ManagementObject item in system.Get())
                total = Bytes(item["TotalPhysicalMemory"]);

        var modules = new List<HardwareMemoryModule>();
        using (var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT DeviceLocator,BankLabel,Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber,SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            foreach (ManagementObject item in searcher.Get())
            {
                var capacity = Bytes(item["Capacity"]);
                if (capacity <= 0) continue;
                // Speed 是标称频率，ConfiguredClockSpeed 是实际运行频率；优先展示实际值。
                var speed = Int(item["ConfiguredClockSpeed"]);
                if (speed <= 0) speed = Int(item["Speed"]);
                modules.Add(new HardwareMemoryModule(
                    First(Text(item["DeviceLocator"]), Text(item["BankLabel"]), "未报告插槽"),
                    capacity, speed, Text(item["Manufacturer"]), Text(item["PartNumber"]),
                    Int(item["SMBIOSMemoryType"])));
            }
        }
        if (total <= 0) total = modules.Sum(m => m.CapacityBytes);
        return total <= 0 && modules.Count == 0 ? null : new HardwareMemory(total, modules.ToArray());
    }

    private static HardwareMotherboard? QueryMotherboard()
    {
        string manufacturer = "", product = "", biosVersion = "", biosDate = "";
        using (var board = new ManagementObjectSearcher(CimNamespace, "SELECT Manufacturer,Product FROM Win32_BaseBoard"))
            foreach (ManagementObject item in board.Get())
            {
                manufacturer = Text(item["Manufacturer"]);
                product = Text(item["Product"]);
                break;
            }
        using (var bios = new ManagementObjectSearcher(CimNamespace, "SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS"))
            foreach (ManagementObject item in bios.Get())
            {
                biosVersion = Text(item["SMBIOSBIOSVersion"]);
                biosDate = CimDate(Text(item["ReleaseDate"]));
                break;
            }
        return string.IsNullOrWhiteSpace(manufacturer) && string.IsNullOrWhiteSpace(product)
            && string.IsNullOrWhiteSpace(biosVersion)
            ? null
            : new HardwareMotherboard(manufacturer, product, biosVersion, biosDate);
    }

    private static HardwareGraphics[]? QueryGraphics()
    {
        var adapters = new List<HardwareGraphics>();
        using var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT Name,DriverVersion,DriverDate,AdapterRAM,PnPDeviceId FROM Win32_VideoController");
        foreach (ManagementObject item in searcher.Get())
        {
            var name = Text(item["Name"]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var deviceId = Text(item["PnPDeviceId"]);
            // ROOT\ 枚举的适配器由软件驱动提供（远程控制、投屏、虚拟显示器），
            // 物理显卡必然挂在 PCI 总线上。这类条目在界面上没有意义，直接不返回。
            if (deviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;
            var ram = Bytes(item["AdapterRAM"]);
            adapters.Add(new HardwareGraphics(name, Text(item["DriverVersion"]), CimDate(Text(item["DriverDate"])),
                // AdapterRAM 是 32 位字段，显存大于 4 GB 时会报错值，交由界面标注，不在此处猜测。
                ram > 0 ? ram : null, deviceId));
        }
        return adapters.ToArray();
    }

    private static HardwareDisk[]? QueryDisks()
    {
        var disks = new List<HardwareDisk>();
        using var searcher = new ManagementObjectSearcher(CimNamespace, "SELECT Model,InterfaceType,Size,MediaType FROM Win32_DiskDrive");
        foreach (ManagementObject item in searcher.Get())
        {
            var model = Text(item["Model"]).Trim();
            if (string.IsNullOrWhiteSpace(model)) continue;
            var media = Text(item["MediaType"]);
            disks.Add(new HardwareDisk(model, Text(item["InterfaceType"]), Bytes(item["Size"]),
                media.Contains("Removable", StringComparison.OrdinalIgnoreCase)));
        }
        return disks.ToArray();
    }

    /// <summary>
    /// 显示器。WmiMonitorID 在部分机器（含本机实测）返回"无效类"，
    /// 因此使用 Win32_DesktopMonitor；它不提供分辨率，尺寸留 0 由界面按"未报告"处理。
    /// </summary>
    private static HardwareMonitor[]? QueryMonitors()
    {
        var monitors = new List<HardwareMonitor>();
        using var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT Name,MonitorManufacturer,ScreenWidth,ScreenHeight FROM Win32_DesktopMonitor");
        foreach (ManagementObject item in searcher.Get())
        {
            var name = Text(item["Name"]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            monitors.Add(new HardwareMonitor(name, Text(item["MonitorManufacturer"]),
                Int(item["ScreenWidth"]), Int(item["ScreenHeight"])));
        }
        return monitors.ToArray();
    }

    private static HardwareSoundDevice[]? QuerySoundDevices()
    {
        var devices = new List<HardwareSoundDevice>();
        using var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT Name,Manufacturer,Status FROM Win32_SoundDevice");
        foreach (ManagementObject item in searcher.Get())
        {
            var name = Text(item["Name"]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            // 只保留在用的设备：Status 非 OK 的多半是未连接的 HDMI 音频端点。
            if (Text(item["Status"]) is { Length: > 0 } status && !status.Equals("OK", StringComparison.OrdinalIgnoreCase)) continue;
            devices.Add(new HardwareSoundDevice(name, Text(item["Manufacturer"])));
        }
        return devices.ToArray();
    }

    private static HardwareNetworkAdapter[]? QueryNetworkAdapters()
    {
        var adapters = new List<HardwareNetworkAdapter>();
        // PhysicalAdapter 排除 VPN、回环等纯软件适配器。
        using var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT Name,NetConnectionID,Speed,NetEnabled,NetConnectionStatus FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE");
        foreach (ManagementObject item in searcher.Get())
        {
            var name = Text(item["Name"]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var enabled = item["NetEnabled"] is bool flag && flag;
            adapters.Add(new HardwareNetworkAdapter(name, Text(item["NetConnectionID"]), Bytes(item["Speed"]), enabled));
        }
        return adapters.ToArray();
    }
    private static HardwareOperatingSystem? QueryOperatingSystem()
    {
        using var searcher = new ManagementObjectSearcher(CimNamespace,
            "SELECT Caption,Version,BuildNumber,OSArchitecture,FreePhysicalMemory FROM Win32_OperatingSystem");
        foreach (ManagementObject item in searcher.Get())
        {
            var caption = Text(item["Caption"]);
            if (string.IsNullOrWhiteSpace(caption)) continue;
            return new HardwareOperatingSystem(caption, Text(item["Version"]),
                int.TryParse(Text(item["BuildNumber"]), out var build) ? build : 0,
                Text(item["OSArchitecture"]), Int(item["FreePhysicalMemory"]) / 1024);
        }
        return null;
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? "";

    private static long Number(object? value)
    {
        if (value is null) return 0;
        try { return Convert.ToInt64(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    /// <summary>WMI 的 UInt64 字段可能以字符串或不同整型返回，统一转成 byte 数。</summary>
    private static long Bytes(object? value) => Number(value);

    /// <summary>整型字段（核数、频率、MB 数）统一转成 int，超出范围按 0 处理。</summary>
    private static int Int(object? value)
    {
        var number = Number(value);
        return number is > int.MaxValue or < int.MinValue ? 0 : (int)number;
    }

    /// <summary>WMI 时间形如 20240617000000.000000+000，转成可读日期；解析失败返回空串。</summary>
    private static string CimDate(string raw)
    {
        if (raw.Length < 8 || !raw[..8].All(char.IsAsciiDigit)) return "";
        return DateTime.TryParseExact(raw[..8], "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd") : "";
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
