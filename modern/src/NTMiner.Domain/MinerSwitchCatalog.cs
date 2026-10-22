namespace NTMiner.Modern;

/// <summary>
/// 一个可勾选的内核参数。Name 用于配置持久化，Description 是界面上的checkbox文字，
/// Argument 是它对应的真实命令行片段（只用于悬浮提示与预览，不参与拼接）。
/// 只收录当前适配器确认支持的参数；没有实现的参数不写进来。
/// </summary>
public sealed record MinerSwitch(
    string Name,
    string Description,
    string Argument,
    bool IsAdvanced = false,
    bool DefaultOn = false)
{
    /// <summary>该开关是否只对 CPU 挖矿生效。</summary>
    public bool CpuOnly { get; init; }
    /// <summary>该开关是否只对 GPU 挖矿生效。</summary>
    public bool GpuOnly { get; init; }
}

/// <summary>按内核列出可用开关。参数依据 BzMiner v100.46 的 --help 输出。</summary>
public static class MinerSwitchCatalog
{
    public static IReadOnlyList<MinerSwitch> For(MinerKind miner) => miner switch
    {
        MinerKind.BzMiner => BzMiner,
        _ => SrbMiner
    };

    /// <summary>SRBMiner 当前适配器只使用固定参数集，暂不暴露可选项。</summary>
    private static readonly MinerSwitch[] SrbMiner = [];

    private static readonly MinerSwitch[] BzMiner =
    [
        // ---- 常用 ----
        new("NoWatchdog", "内核崩溃后不自动重启看门狗（便于排查问题）", "--no-watchdog"),
        new("CpuMetrics", "采集 CPU 温度与占用（关闭可减少干扰）", "--cpu-metrics 0"),
        new("DisableHugePages", "禁用大页内存分配（排查内存问题时使用）", "--disable_huge_pages"),
        new("NoBandwidthTest", "跳过启动时的 PCIe 带宽自检（每张卡约省 1 秒）", "--no-bandwidth-test"),

        // ---- 高级 ----
        new("LogDebug", "输出调试级别的详细日志", "--log-level debug", IsAdvanced: true),
        new("InternalLog", "输出开发者内部日志（内核分块与指令路径）", "--internal-log", IsAdvanced: true),
        new("NoDuplicates", "禁止内核为同一设备创建多个实例", "--duplicate-devices 0", IsAdvanced: true, DefaultOn: true),
        new("DisableSse", "禁用 SSE4.1/AES/PCLMUL 指令集支持", "--disable_sse", IsAdvanced: true),
        new("DisableAvx2", "禁用 AVX/AVX2 指令集支持", "--disable_avx2", IsAdvanced: true),
        new("DisableAvx512", "禁用 AVX-512 指令集支持", "--disable_avx512", IsAdvanced: true),
        new("WatchdogRestarts", "崩溃工作线程的重启次数上限", "--watchdog-restarts 3", IsAdvanced: true),
        new("HashrateWindow", "算力统计的滑动平均窗口（秒）", "--hashrate-window 30", IsAdvanced: true),
        new("NoColor", "关闭控制台 ANSI 颜色输出", "--no-color", IsAdvanced: true)
    ];

    /// <summary>默认勾选状态：取自 Each 的 DefaultOn，未知名称一律 false。</summary>
    public static bool IsDefaultOn(MinerKind miner, string name) =>
        For(miner).Any(s => s.Name == name && s.DefaultOn);

    /// <summary>按名称取开关定义；不存在时返回 null，调用方应忽略未知项而不是报错。</summary>
    public static MinerSwitch? Find(MinerKind miner, string name) =>
        For(miner).FirstOrDefault(s => s.Name == name);

    /// <summary>把已保存的开关名过滤成当前内核的合法集合，丢弃其他内核的残留项。</summary>
    public static string[] Normalize(MinerKind miner, IEnumerable<string>? names)
    {
        var allowed = For(miner).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var source = names?.ToArray() ?? [];
        var cleaned = source.Where(n => !string.IsNullOrWhiteSpace(n) && allowed.Contains(n))
            .Distinct(StringComparer.Ordinal).ToArray();
        // 没有需要清理的项时返回原数组，避免无谓的分配，也让调用方能用引用比较判断"未变化"。
        return cleaned.Length == source.Length && cleaned.SequenceEqual(source) ? source : cleaned;
    }

    /// <summary>
    /// 生成命令行预览：把基础参数与已启用的开关并排展示。
    /// 这是预览，不是真实启动参数；真实启动参数由各适配器构造。
    /// </summary>
    public static string Preview(MiningOptions options, IEnumerable<string> enabledNames)
    {
        // 演示模式不执行真实内核，也不应用任何内核开关，预览只说明这一点。
        if (options.DemoMode)
            return "<模拟内核> --demo （演示模式不加载所选内核文件，以下参数不生效）";
        var parts = new List<string> { string.IsNullOrWhiteSpace(options.KernelPath) ? "<未选择内核>" : options.KernelPath };
        if (options.Miner == MinerKind.BzMiner)
        {
            parts.AddRange(["-a", "pearl"]);
            parts.Add(options.BenchmarkMode ? "--bench" : "-p " + Quote(options.Pool));
            if (!options.BenchmarkMode)
            {
                parts.Add("-w " + Quote(options.Wallet));
                parts.Add("--worker " + Quote(options.Worker));
            }
            parts.AddRange(["--cpu", options.Device == MiningDevice.Cpu ? "1" : "0"]);
            if (options.Device == MiningDevice.Cpu) parts.AddRange(["--cpu_threads", options.CpuThreads.ToString()]);
            if (options.Device == MiningDevice.IntegratedGpu) parts.Add("--igpu");
            if (options.Device == MiningDevice.Gpu) parts.Add("--devices all");
        }
        else
        {
            parts.AddRange(["--algorithm", "pearlhash", "--pool", options.Pool, "--wallet", options.Wallet]);
        }
        foreach (var name in enabledNames)
            if (Find(options.Miner, name) is { } item && !parts.Contains(item.Argument))
                parts.Add(item.Argument);
        return string.Join(" ", parts);
    }

    private static string Quote(string value) =>
        string.IsNullOrWhiteSpace(value) ? "\"\"" : (value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value);
}
