using System.Runtime.InteropServices;

namespace NTMiner.Modern;

/// <summary>
/// ADL（AMD Display Library，atiadlxx.dll）互操作声明。
/// 全部是只读接口：适配器枚举与 Overdrive5/OverdriveN 的 *_Get；
/// 不声明任何 *_Set 写入接口，因此既不需要管理员权限，也不会改变显卡状态。
/// 每个声明都显式指定 EntryPoint，入口点缺失时由调用方兜住 EntryPointNotFoundException。
/// </summary>
internal static class Adl
{
    /// <summary>DLL 名。实际加载走 NativeLibrary.TryLoad(驱动目录下的完整路径)，这里只用于声明的默认值。</summary>
    private const string Library = "atiadlxx.dll";

    /// <summary>ADL 的"成功"返回值，其余均为错误码。</summary>
    public const int Ok = 0;

    /// <summary>ADL_Adapter_AdapterInfo_Get 的 sizeof(AdapterInfo) = 1504 字节。</summary>
    public const int AdapterInfoSize = 1504;

    /// <summary>扇速控制方式：百分比。</summary>
    public const int FanModePercent = 0;

    /// <summary>温度传感器类型：边缘（GPU 核心）温度。ADL 中核心温度就是边缘温度。</summary>
    public const int TemperatureEdge = 1;

    /// <summary>适配器条数上限；ADL 报出离谱条数时视为读取失败，不按错误条数分配缓冲区。</summary>
    public const int MaxAdapterCount = 256;

    /// <summary>Overdrive5 时钟单位是 10 kHz，数值除以 100 得到 MHz。</summary>
    public const double ClockTicksPerMhz = 100d;

    [DllImport(Library, EntryPoint = "ADL2_Main_Control_Create", ExactSpelling = true)]
    public static extern int MainControlCreate(IntPtr module, int enumConnectedAdapters, IntPtr callback, out IntPtr context);

    [DllImport(Library, EntryPoint = "ADL2_Main_Control_Destroy", ExactSpelling = true)]
    public static extern int MainControlDestroy(IntPtr module, IntPtr context);

    [DllImport(Library, EntryPoint = "ADL2_Adapter_NumberOfAdapters_Get", ExactSpelling = true)]
    public static extern int NumberOfAdaptersGet(IntPtr module, IntPtr context, out int numberAdapters);

    [DllImport(Library, EntryPoint = "ADL2_Adapter_AdapterInfo_Get", ExactSpelling = true)]
    public static extern int AdapterInfoGet(IntPtr module, IntPtr context, IntPtr info, nuint inputSize);

    [DllImport(Library, EntryPoint = "ADL2_Overdrive5_Temperature_Get", ExactSpelling = true)]
    public static extern int TemperatureGet(IntPtr module, IntPtr context, int adapterIndex,
        int thermalControllerIndex, int thermalSensorType, out AdlTemperature temperature);

    [DllImport(Library, EntryPoint = "ADL2_Overdrive5_FanSpeed_Get", ExactSpelling = true)]
    public static extern int FanSpeedGet(IntPtr module, IntPtr context, int adapterIndex,
        int thermalControllerIndex, out AdlFanSpeedValue fanSpeed);

    [DllImport(Library, EntryPoint = "ADL2_Overdrive5_CurrentActivity_Get", ExactSpelling = true)]
    public static extern int CurrentActivityGet(IntPtr module, IntPtr context, int adapterIndex, out AdlCurrentActivity activity);

    [DllImport(Library, EntryPoint = "ADL2_OverdriveN_PowerLimit_Get", ExactSpelling = true)]
    public static extern int PowerLimitGet(IntPtr module, IntPtr context, int adapterIndex,
        out AdlOverdriveNPowerLimit powerLimit);

    /// <summary>
    /// ADL_AdapterInfo：ADL 的固定布局，10 个字段，sizeof = 1504。
    /// 字符串字段是定长 ASCII 字节数组，不保证以 NUL 结尾。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct AdlAdapterInfo
    {
        public int ISize;
        public int IAdapterIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] StrAdapterName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] StrDisplayName;
        /// <summary>1 表示该适配器当前存在且可被驱动枚举，0 表示列表里的残留/虚拟条目。</summary>
        public int IPresent;
        public int IExist;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] StrDriverPath;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] StrDriverPathExt;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] StrPnpString;
        public int IOSDisplayIndex;
    }

    /// <summary>ADL_Overdrive5_Temperature / ADLODTemperature。iTemperature 单位摄氏度。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdlTemperature
    {
        public int ITemperature;
    }

    /// <summary>ADL_Overdrive5_FanSpeed / ADLODFanSpeed。iSpeed 按 iSpeedMode 解释。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdlFanSpeedValue
    {
        public int ISpeedMode;
        public int ISpeed;
    }

    /// <summary>
    /// ADL_Overdrive5_CurrentActivity 返回的最内层活动结构（ADLODCurrentActivity 的 iActivity）。
    /// 字段顺序与 ADL 公共头文件一致；本代码只读取时钟与功率三项，其余字段保持完整以便布局对齐。
    /// iPower 单位毫瓦，-1 表示该型号不上报。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdlActivity
    {
        public int IEngineClock;
        public int IMemoryClock;
        public int IVddc;
        public int IBusVoltage;
        public int IBusSpeed;
        public int ILaneCount;
        public int IActivity_Level1;
        public int IActivity_Level2;
        public int IActivity_Level3;
        public int IActivity_Level4;
        public int IActivity_Level5;
        public int IFanSpeed;
        public int IPower;
    }

    /// <summary>
    /// ADL_Overdrive5_CurrentActivity 的返回结构。
    /// iActivity 与 ADLODCurrentActivity 定义一致，iPerformanceLevel 之后是温度和性能参数。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdlCurrentActivity
    {
        public AdlActivity Activity;
        public int IPerformanceLevel;
    }

    /// <summary>ADL_OverdriveN_PowerLimit / ADLODNPowerLimit。adlPowerLimitValue 单位瓦，iEnabledInfo == 1 才有效。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdlOverdriveNPowerLimit
    {
        public int IEnabledInfo;
        public int IValue;
    }
}
