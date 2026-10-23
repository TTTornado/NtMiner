using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NTMiner.Modern;

/// <summary>
/// AMD 传感器，基于 ADL（atiadlxx.dll，随驱动安装）。
/// DLL 不存在时 IsAvailable 返回 false。
/// </summary>
/// <remarks>
/// 与 NVML 提供者相同的失败策略：DLL 缺失、入口点缺失（驱动自带的 ADL 版本过旧）、
/// 驱动不加载 ADL、没有真实 AMD 适配器、任何一次 ADL 调用返回非 ADL_OK，
/// 都只体现为对应字段为 null 或 Read 返回空数组，绝不抛异常，也不写日志到控制台。
/// 只调用只读接口，不碰 Overdrive 写入接口，不写注册表，不需要管理员权限。
/// </remarks>
public sealed partial class AmdGpuSensorProvider : IGpuSensorProvider
{
    public string Name => "ADL";

    public bool IsAvailable() => AdlAvailable();

    public IReadOnlyList<GpuSensorReading> Read() => ReadCore();

    /// <summary>
    /// 能否加载并初始化 ADL。本机没有 AMD 驱动时只是返回 false。
    /// 探活与采样一样成对释放主控制，避免每问一次就泄漏一份 ADL 上下文。
    /// </summary>
    private static bool AdlAvailable()
    {
        try
        {
            using var main = MainControl.Open();
            return main is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<GpuSensorReading> ReadCore()
    {
        var readings = new List<GpuSensorReading>();
        try
        {
            using var main = MainControl.Open();
            if (main is null) return readings;
            ReadInto(main.Module, main.Context, readings);
        }
        catch (Exception)
        {
            // DLL 缺失、入口点缺失、驱动异常等一律视为"读不到"；已读到的条目保持原样。
            // 不向外抛异常：调用方会依次尝试其他提供者，缺一块 AMD 读数不应影响整个采样。
        }
        return readings;
    }

    /// <summary>
    /// 一次 ADL 会话：已加载的 atiadlxx.dll 模块句柄 + ADL2 主控制上下文。
    /// 所有 ADL2_* 调用都必须带上这同一个上下文，且 Create 与 Destroy 必须成对。
    /// </summary>
    private sealed class MainControl : IDisposable
    {
        private MainControl(IntPtr module, IntPtr context)
        {
            Module = module;
            Context = context;
        }

        public IntPtr Module { get; }

        public IntPtr Context { get; }

        /// <summary>
        /// 打开会话。DLL 不存在、驱动不加载 ADL、入口点缺失、Create 返回非 ADL_OK 时返回 null。
        /// </summary>
        public static MainControl? Open()
        {
            var directory = AmdDriverDirectory();
            if (directory is null) return null;
            var path = Path.Combine(directory, "atiadlxx.dll");
            if (!File.Exists(path)) return null;
            if (!NativeLibrary.TryLoad(path, out var module) || module == IntPtr.Zero) return null;

            IntPtr context = IntPtr.Zero;
            try
            {
                // 入口点缺失会抛 EntryPointNotFoundException，由调用方兜住。
                if (Adl.MainControlCreate(module, 0, IntPtr.Zero, out context) != Adl.Ok)
                {
                    NativeLibrary.Free(module);
                    return null;
                }
                return new MainControl(module, context);
            }
            catch (Exception)
            {
                NativeLibrary.Free(module);
                return null;
            }
        }

        public void Dispose()
        {
            try { Adl.MainControlDestroy(Module, Context); }
            catch (Exception) { }
            try { NativeLibrary.Free(Module); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// ADL 的适配器列表里除了物理显卡，还包含显示器输出、驱动虚拟设备等条目。
    /// 过滤规则：
    /// 1. 只取 iPresent == 1 的条目——它们是当前已连接、真正参与渲染的适配器；
    ///    其余条目仍会出现在列表中，但 Overdrive 接口对它们返回非 OK。
    /// 2. 用条目自带的 iAdapterIndex 调 Overdrive，而不是列表下标：iPresent 过滤后下标会错位，
    ///    用下标去调接口会读成别的设备。
    /// </summary>
    private static void ReadInto(IntPtr module, IntPtr context, List<GpuSensorReading> readings)
    {
        if (Adl.NumberOfAdaptersGet(module, context, out var count) != Adl.Ok) return;
        if (count <= 0 || count > Adl.MaxAdapterCount) return;

        var size = (nuint)(Adl.AdapterInfoSize * count);
        var buffer = Marshal.AllocHGlobal((IntPtr)size);
        try
        {
            if (Adl.AdapterInfoGet(module, context, buffer, size) != Adl.Ok) return;
            var stride = Marshal.SizeOf<Adl.AdlAdapterInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<Adl.AdlAdapterInfo>(buffer + i * stride);
                if (info.IPresent != 1) continue;
                if (info.IAdapterIndex < 0) continue;
                readings.Add(ReadAdapter(module, context, info));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static GpuSensorReading ReadAdapter(IntPtr module, IntPtr context, Adl.AdlAdapterInfo info)
    {
        var index = info.IAdapterIndex;
        var name = ReadString(info.StrAdapterName);
        if (name.Length == 0) name = $"AMD GPU {index}";
        // 三项时钟与功耗都来自同一次 CurrentActivity 调用，取一次即可，失败则这几项一起为空。
        var activity = TryCurrentActivity(module, context, index);
        return new GpuSensorReading(
            name, GpuVendor.Amd,
            TemperatureC: ReadTemperature(module, context, index),
            // ADL 没有公开的显存结温接口（结温只在 ADLX / 私有的 OverdriveN Thermal 里），留空不猜。
            MemoryTemperatureC: null,
            FanSpeedPercent: ReadFanPercent(module, context, index),
            PowerWatts: MilliwattsToWatts(activity?.Activity.IPower),
            PowerLimitWatts: ReadPowerLimitWatts(module, context, index),
            CoreClockMhz: ClockToMhz(activity?.Activity.IEngineClock),
            MemoryClockMhz: ClockToMhz(activity?.Activity.IMemoryClock),
            // ADL 的 Overdrive5 接口不报告显存占用与 GPU 利用率，这两个值只能由挖矿程序自己汇报。
            MemoryUsedMb: null,
            MemoryTotalMb: null,
            UtilizationPercent: null);
    }

    /// <summary>核心温度，摄氏度。传感器类型取边缘温度（ADL 里就是 GPU 核心温度）。</summary>
    private static double? ReadTemperature(IntPtr module, IntPtr context, int index)
    {
        var result = Adl.TemperatureGet(module, context, index, 0, Adl.TemperatureEdge, out var temperature);
        // 0 度不可能是真实读数，视为"未上报"。
        return result == Adl.Ok && temperature.ITemperature > 0 ? temperature.ITemperature : null;
    }

    /// <summary>
    /// 风扇转速百分比。iSpeed 的含义由 iSpeedMode 决定：
    /// 百分比模式直接可用；RPM 模式没有可查询的风扇额定转速，无法换算成百分比，
    /// 此时返回 null，不凭经验折算。
    /// </summary>
    private static int? ReadFanPercent(IntPtr module, IntPtr context, int index)
    {
        var result = Adl.FanSpeedGet(module, context, index, 0, out var fan);
        if (result != Adl.Ok) return null;
        if (fan.ISpeedMode != Adl.FanModePercent) return null;
        return fan.ISpeed is > 0 and <= 100 ? fan.ISpeed : null;
    }

    private static Adl.AdlCurrentActivity? TryCurrentActivity(IntPtr module, IntPtr context, int index)
    {
        try
        {
            return Adl.CurrentActivityGet(module, context, index, out var activity) == Adl.Ok ? activity : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 功耗（瓦）。ADL 文档：ADLODCurrentActivity.iPower 单位为毫瓦，-1 表示该型号不上报。
    /// </summary>
    private static double? MilliwattsToWatts(int? milliwatts) =>
        milliwatts is > 0 ? Math.Round(milliwatts.Value / 1000d, 2) : null;

    /// <summary>功耗上限（瓦）。ADLODNPowerLimit.adlPowerLimitValue 已经是瓦，iEnabledInfo == 1 才有效。</summary>
    private static double? ReadPowerLimitWatts(IntPtr module, IntPtr context, int index)
    {
        var result = Adl.PowerLimitGet(module, context, index, out var limit);
        if (result != Adl.Ok || limit.IEnabledInfo != 1) return null;
        return limit.IValue > 0 ? limit.IValue : null;
    }

    /// <summary>Overdrive5 时钟的单位是 10 kHz，即数值除 100 为 MHz。</summary>
    private static double? ClockToMhz(int? clock) =>
        clock is > 0 ? Math.Round(clock.Value / Adl.ClockTicksPerMhz) : null;

    /// <summary>ADL 的 char[] 不保证以 NUL 结尾，取第一个 NUL 之前的内容。</summary>
    private static string ReadString(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, length).Trim();
    }

    /// <summary>
    /// AMD 显示驱动目录。优先取本进程已加载的显示运行时 amdxx64.dll 所在目录
    /// （它一定与 atiadlxx.dll 同目录）；取不到时再退到 DisplayDriver 目录下的版本化子目录。
    /// 本机没有 AMD 显卡时两者都不存在，返回 null。
    /// </summary>
    private static string? AmdDriverDirectory()
    {
        var fromLoaded = LoadedModuleDirectory("amdxx64.dll");
        if (fromLoaded is not null) return fromLoaded;

        try
        {
            var system = Environment.SystemDirectory;
            if (string.IsNullOrEmpty(system)) return null;
            var root = Path.Combine(system, "DriverStore", "FileRepository");
            if (!Directory.Exists(root)) return null;
            // 只枚举一层版本化目录：形如 xxx.inf_amd64_<hash>。
            foreach (var candidate in Directory.EnumerateDirectories(root, "u*.inf_amd64_*"))
            {
                if (File.Exists(Path.Combine(candidate, "atiadlxx.dll"))) return candidate;
            }
        }
        catch (Exception)
        {
            // 目录不可枚举时视为没有 AMD 驱动，交由调用方按"不可用"处理。
        }
        return null;
    }

    private static string? LoadedModuleDirectory(string moduleName)
    {
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (!string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                var path = module.FileName;
                if (!string.IsNullOrEmpty(path)) return Path.GetDirectoryName(path);
            }
        }
        catch (Exception)
        {
            // 模块枚举在受限环境可能失败，交给下一种定位方式。
        }
        return null;
    }
}
