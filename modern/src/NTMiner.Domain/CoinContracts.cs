namespace NTMiner.Modern;

/// <summary>
/// 内核报告的一个算法。来源是内核自身的 --list-algos 输出，不是我们维护的固定表，
/// 这样内核升级后新增或移除算法都能自动反映，不需要改代码。
/// </summary>
public sealed record MinerAlgorithm(
    string Name,
    double DevFeePercent,
    string[] Backends)
{
    /// <summary>该算法是否可以由 CPU 挖，取决于内核是否声明了 cpu 后端。</summary>
    public bool SupportsCpu => Backends.Any(b => b.Equals("cpu", StringComparison.OrdinalIgnoreCase));

    /// <summary>是否支持 GPU（cuda 或 opencl）。</summary>
    public bool SupportsGpu => Backends.Any(b =>
        b.Equals("cuda", StringComparison.OrdinalIgnoreCase) || b.Equals("opencl", StringComparison.OrdinalIgnoreCase));
    public bool SupportsIntegratedGpu => Backends.Any(b => b.Equals("opencl", StringComparison.OrdinalIgnoreCase));
}

/// <summary>一次算法枚举的结果。读取失败时保留问题原因，界面据此说明而不是显示空列表。</summary>
public sealed record MinerAlgorithmCatalog(
    MinerKind Miner,
    string? Version,
    MinerAlgorithm[] Algorithms,
    string? Error)
{
    public static MinerAlgorithmCatalog Failed(MinerKind miner, string error) => new(miner, null, [], error);

    public bool IsUsable => Error is null && Algorithms.Length > 0;

    /// <summary>按当前设备类型过滤：CPU 挖矿只看 cpu 后端，GPU 挖矿看 cuda/opencl。</summary>
    public MinerAlgorithm[] ForDevice(MiningDevice device) => Algorithms.Where(a => device switch
    { MiningDevice.Cpu => a.SupportsCpu, MiningDevice.IntegratedGpu => a.SupportsIntegratedGpu, _ => a.SupportsGpu }).ToArray();
}

/// <summary>把算法解析成命令行参数时的币种/算法选择。</summary>
public static class CoinSelection
{
    /// <summary>
    /// 不同内核的算法参数名不同：SRBMiner 用 --algorithm，BzMiner 用 -a。
    /// 这里只负责给出参数名，值由用户选择的算法决定。
    /// </summary>
    public static string ArgumentName(MinerKind miner) => miner == MinerKind.BzMiner ? "-a" : "--algorithm";

    /// <summary>
    /// 校验所选算法是否在目录中且支持当前设备。返回 null 表示可用，否则是具体原因。
    /// 未选择算法时返回提示，避免启动时静默报内核错误。
    /// </summary>
    public static string? Validate(MinerAlgorithmCatalog catalog, string? algorithm, MiningDevice device)
    {
        if (string.IsNullOrWhiteSpace(algorithm)) return "请先选择挖矿算法。";
        if (!catalog.IsUsable) return catalog.Error ?? "无法从内核读取算法列表。";
        var match = catalog.Algorithms.FirstOrDefault(a => a.Name.Equals(algorithm, StringComparison.OrdinalIgnoreCase));
        if (match is null) return $"内核不支持算法 {algorithm}。";
        if (device == MiningDevice.Cpu && !match.SupportsCpu) return $"算法 {algorithm} 不支持 CPU 挖矿。";
        if (device == MiningDevice.IntegratedGpu && !match.SupportsIntegratedGpu) return $"算法 {algorithm} 未声明 OpenCL 支持，不能用于当前仅核显模式。";
        if (device != MiningDevice.Cpu && !match.SupportsGpu) return $"算法 {algorithm} 不支持 GPU 挖矿。";
        return null;
    }
}
