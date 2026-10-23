using System.Runtime.InteropServices;
using System.Text;

namespace NTMiner.Modern;

/// <summary>
/// NVIDIA 传感器，基于 NVML（nvml.dll，随驱动安装）。
/// DLL 不存在时 IsAvailable 返回 false，调用方会跳过而不报错。
/// 所有调用都是只读查询：不初始化需要权限的子系统、不调整任何设备状态、不写注册表。
/// </summary>
public sealed partial class NvidiaGpuSensorProvider : IGpuSensorProvider
{
    /// <summary>NVML_SUCCESS。其余返回值一律视为"这一项读不到"。</summary>
    private const int NvmlSuccess = 0;

    /// <summary>NVML_TEMPERATURE_GPU，见 nvmlTemperatureSensors_t。</summary>
    private const int NvmlTemperatureGpu = 0;

    /// <summary>NVML_CLOCK_SM 是 SM（流处理器）时钟，NVML_CLOCK_MEM 是显存时钟。</summary>
    private const int NvmlClockSm = 1;
    private const int NvmlClockMem = 2;

    /// <summary>nvmlDeviceGetName 要求的缓冲长度（NVML_DEVICE_NAME_V2_BUFFER_SIZE）。</summary>
    private const int DeviceNameBufferSize = 96;

    private const double BytesPerMegabyte = 1024d * 1024d;
    private const double MilliwattsPerWatt = 1000d;

    public string Name => "NVML";

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int NvmlDeviceGetCount(out uint deviceCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    // 驱动 418 及以后提供 nvmlDeviceGetName_v2（写入调用方缓冲区，带长度参数）。
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName_v2")]
    private static extern int NvmlDeviceGetNameV2(IntPtr device, [Out] byte[] name, uint length);

    // 更早的驱动（以及部分驱动的符号别名）只有 v1：返回 const char*。缺失时 P/Invoke 抛
    // EntryPointNotFoundException，由 ReadName 退回 v1。
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern IntPtr NvmlDeviceGetNameV1(IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
    private static extern int NvmlDeviceGetFanSpeed(IntPtr device, out uint speed);

    /// <summary>返回值单位是毫瓦。</summary>
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint power);

    /// <summary>功耗上限，单位同样是毫瓦。</summary>
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    private static extern int NvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);

    // 新版驱动头文件里是 uint* 出参。
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit_v2")]
    private static extern int NvmlDeviceGetPowerManagementLimitV2(IntPtr device, ref uint limit);

    // v1 只作为老驱动的最后备选，见 ReadPowerLimitMilliwatts。
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
    private static extern int NvmlDeviceGetPowerManagementLimitV1(IntPtr device, out uint limit);

    /// <summary>频率单位 MHz。</summary>
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int NvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint clock);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    /// <summary>nvmlMemory_t，三个字段都是字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>nvmlUtilization_t，两个字段都是 0~100 的百分比。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    public bool IsAvailable()
    {
        try { return NvmlInit() == NvmlSuccess; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch { return false; }
    }

    public IReadOnlyList<GpuSensorReading> Read() => ReadCore();

    /// <summary>
    /// 逐个设备读取；任何一层失败都降级为空数组或不完整读数，从不向调用方抛异常。
    /// 本机无 NVIDIA 驱动时 nvml.dll 不存在，DllNotFoundException 在这里被吞掉。
    /// </summary>
    private static IReadOnlyList<GpuSensorReading> ReadCore()
    {
        var initialized = false;
        try
        {
            // 驱动过旧、内核被禁用、nvml.dll 版本不匹配都可能让初始化失败，此时没有可读数据。
            if (NvmlInit() != NvmlSuccess) return [];
            initialized = true;

            if (NvmlDeviceGetCount(out var count) != NvmlSuccess || count == 0) return [];

            var readings = new List<GpuSensorReading>();
            for (uint index = 0; index < count; index++)
            {
                // 单张卡取不到句柄（设备被独占、热拔除）不影响其余设备。
                try
                {
                    var reading = ReadDevice(index);
                    if (reading is not null) readings.Add(reading);
                }
                catch { /* 该设备的读取整体失败，跳过 */ }
            }
            return readings;
        }
        catch (DllNotFoundException) { return []; }
        catch (EntryPointNotFoundException) { return []; }
        catch (BadImageFormatException) { return []; }
        catch { return []; }
        finally
        {
            // 只做引用计数式的反初始化，不改变任何硬件状态；失败无需处理。
            if (initialized) TryShutdown();
        }
    }

    /// <summary>读取单张显卡。句柄无效时返回 null，否则总返回一条读数（字段读不到即为 null）。</summary>
    private static GpuSensorReading? ReadDevice(uint index)
    {
        if (NvmlDeviceGetHandleByIndex(index, out var device) != NvmlSuccess || device == IntPtr.Zero) return null;

        // 名称只是展示用，读不到时给出可按序号区分的占位名，而不是让整张卡消失。
        var name = ReadName(device) ?? $"NVIDIA GPU {index}";

        var memory = ReadMemory(device);
        return new GpuSensorReading(
            name,
            // 驱动支持 NVML 的设备必然是 NVIDIA 显卡；名称可能是不含厂商字样的 OEM 型号，
            // 因此不按名称推断厂商。
            GpuVendor.Nvidia,
            TemperatureC: ReadTemperature(device),
            // 契约里的显存温度对应显存结温，需要 nvmlDeviceGetTemperature(1)（驱动 428 起）或
            // 温度阈值接口，老驱动上会返回 NOT_SUPPORTED；不用核心温度冒充，统一留空。
            MemoryTemperatureC: null,
            FanSpeedPercent: ReadFanSpeed(device),
            PowerWatts: Watt(ReadPowerUsageMilliwatts(device)),
            PowerLimitWatts: Watt(ReadPowerLimitMilliwatts(device)),
            CoreClockMhz: ReadClock(device, NvmlClockSm),
            MemoryClockMhz: ReadClock(device, NvmlClockMem),
            MemoryUsedMb: Megabyte(memory?.Used),
            MemoryTotalMb: Megabyte(memory?.Total),
            UtilizationPercent: ReadUtilization(device));
    }

    private static void TryShutdown()
    {
        try { _ = NvmlShutdown(); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch { }
    }

    /// <summary>nvmlDeviceGetName：先试 v2（写入缓冲区），缺失则退回 v1（返回 const char*）。</summary>
    private static string? ReadName(IntPtr device)
    {
        var buffer = new byte[DeviceNameBufferSize];
        try
        {
            if (NvmlDeviceGetNameV2(device, buffer, (uint)buffer.Length) == NvmlSuccess)
                return DecodeName(buffer);
        }
        catch (EntryPointNotFoundException) { /* 驱动过旧，退回 v1 */ }
        catch (AccessViolationException) { }

        try
        {
            var text = NvmlDeviceGetNameV1(device);
            if (text == IntPtr.Zero) return null;
            // 名称是 UTF-8，按字节长度解码，避免中文或特殊字符被截断。
            var value = Marshal.PtrToStringUTF8(text);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (AccessViolationException) { return null; }
    }

    /// <summary>缓冲区以 0 结尾；先截到第一个 0 再按 UTF-8 解码，避免多字节字符被切断。</summary>
    private static string? DecodeName(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = buffer.Length;
        if (length == 0) return null;
        var value = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return value.Length == 0 ? null : value;
    }

    private static double? ReadTemperature(IntPtr device)
    {
        if (NvmlDeviceGetTemperature(device, NvmlTemperatureGpu, out var temperature) != NvmlSuccess) return null;
        // NVML 用 0xFFFFFFFF 之类的哨兵值表示不可用；真实温度不可能超过 200 度。
        return temperature is > 0 and < 200 ? temperature : null;
    }

    private static int? ReadFanSpeed(IntPtr device)
    {
        if (NvmlDeviceGetFanSpeed(device, out var speed) != NvmlSuccess) return null;
        // 返回的是百分比而非 RPM；0 在被动散热或风扇停转时是真实读数，超过 100 则是无效值。
        return speed <= 100 ? (int)speed : null;
    }

    /// <summary>nvmlDeviceGetPowerUsage 返回毫瓦。0 表示驱动未上报，按"读不到"处理。</summary>
    private static uint? ReadPowerUsageMilliwatts(IntPtr device)
    {
        try
        {
            if (NvmlDeviceGetPowerUsage(device, out var power) != NvmlSuccess) return null;
            return power > 0 ? power : null;
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (AccessViolationException) { return null; }
    }

    /// <summary>
    /// 功耗上限：优先取实际生效的 nvmlDeviceGetEnforcedPowerLimit（vBIOS 与用户设置都可能改变它），
    /// 取不到再退回 nvmlDeviceGetPowerManagementLimit。
    /// 后者的旧符号在驱动里按 uint 传值、新符号按 uint* 出参，两个出口都试一遍；
    /// 签名不匹配时要么调用失败，要么读到的数值远超任何显卡的合理瓦数，据此判为无效。
    /// 不传调用方缓冲区的指针，驱动侧出错最多让本次调用失败，不会破坏本进程内存。
    /// </summary>
    private static uint? ReadPowerLimitMilliwatts(IntPtr device)
    {
        try
        {
            if (NvmlDeviceGetEnforcedPowerLimit(device, out var enforced) == NvmlSuccess && IsPlausibleMilliwatts(enforced))
                return enforced;
        }
        catch (EntryPointNotFoundException) { /* 驱动过旧 */ }
        catch (AccessViolationException) { }

        var limit = 0u;
        try
        {
            if (NvmlDeviceGetPowerManagementLimitV2(device, ref limit) == NvmlSuccess && IsPlausibleMilliwatts(limit))
                return limit;
        }
        catch (EntryPointNotFoundException) { }
        catch (AccessViolationException) { }

        try
        {
            if (NvmlDeviceGetPowerManagementLimitV1(device, out limit) == NvmlSuccess && IsPlausibleMilliwatts(limit))
                return limit;
        } catch (EntryPointNotFoundException) { }
        catch (AccessViolationException) { }

        return null;
    }

    /// <summary>合理功耗为 1~2000 W；NVML 的"不可用"哨兵值（0xFFFFFFFF 等）远超此范围。</summary>
    private static bool IsPlausibleMilliwatts(uint milliwatts) => milliwatts is > 0 and <= 2_000_000;

    private static double? ReadClock(IntPtr device, int clockType)
    {
        if (NvmlDeviceGetClockInfo(device, clockType, out var clock) != NvmlSuccess) return null;
        // 单位已经是 MHz；vGPU / 休眠状态下会返回 0，此时如实留空。
        return clock > 0 ? clock : null;
    }

    private static NvmlMemory? ReadMemory(IntPtr device)
    {
        try
        {
            if (NvmlDeviceGetMemoryInfo(device, out var memory) != NvmlSuccess) return null;
            return memory.Total > 0 ? memory : null;
        }
        catch (AccessViolationException) { return null; }
    }

    private static double? ReadUtilization(IntPtr device)
    {
        try
        {
            if (NvmlDeviceGetUtilizationRates(device, out var utilization) != NvmlSuccess) return null;
            // 取 GPU 使用率；memory 字段是显存带宽占用率，语义不同，不混用。
            return utilization.Gpu <= 100 ? utilization.Gpu : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static double? Watt(uint? milliwatts) => milliwatts is { } value ? Math.Round(value / MilliwattsPerWatt, 1) : null;

    private static double? Megabyte(ulong? bytes) => bytes is { } value ? Math.Round(value / BytesPerMegabyte) : null;
}
