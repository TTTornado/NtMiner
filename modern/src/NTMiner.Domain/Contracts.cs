using System.Text.RegularExpressions;

namespace NTMiner.Modern;

public enum MinerKind { SrbMiner, BzMiner }
public enum MiningDevice { Gpu, Cpu, IntegratedGpu }

public sealed record MiningOptions
{
    public int SchemaVersion { get; init; } = 3;
    public MinerKind Miner { get; init; } = MinerKind.SrbMiner;
    public MiningDevice Device { get; init; } = MiningDevice.Gpu;
    public int CpuThreads { get; init; } = 1;
    public bool BenchmarkMode { get; init; }
    public string KernelPath { get; init; } = "";
    public string Wallet { get; init; } = "";
    public string Pool { get; init; } = "";
    public string Worker { get; init; } = "rig01";
    public bool AutoRecover { get; init; } = true;
    public bool DemoMode { get; init; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != 3) errors.Add("配置版本不受支持，请重新填写配置。");
        if (!Enum.IsDefined(Miner) || !Enum.IsDefined(Device)) errors.Add("不支持的内核或设备模式。");
        if (Device == MiningDevice.Cpu && Miner != MinerKind.BzMiner) errors.Add("PRL 的 CPU 模式需要选择 BzMiner。");
        if (Device == MiningDevice.IntegratedGpu && Miner != MinerKind.BzMiner) errors.Add("仅核显模式需要选择 BzMiner。");
        if (Device == MiningDevice.Cpu && (CpuThreads < 1 || CpuThreads > Environment.ProcessorCount))
            errors.Add($"CPU 线程数应为 1–{Environment.ProcessorCount}；建议先使用 1 个线程验证。");
        if (BenchmarkMode && Miner != MinerKind.BzMiner)
            errors.Add("离线自测需要选择 BzMiner，支持 CPU、全部 GPU 或仅核显。");
        if (DemoMode) return errors;
        var expectedName = Miner == MinerKind.BzMiner ? "bzminer.exe" : "SRBMiner-MULTI.exe";
        if (string.IsNullOrWhiteSpace(KernelPath) || !Path.IsPathFullyQualified(KernelPath)
            || !File.Exists(KernelPath) || !Path.GetExtension(KernelPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            errors.Add($"请选择本机有效的 {expectedName} 文件。");
        else if (!Path.GetFileName(KernelPath).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            errors.Add($"当前选择的内核需要 {expectedName}，请检查内核类型和文件。");
        if (BenchmarkMode) return errors;
        // Structural validation only; this is not a Bech32 checksum verifier.
        if (!Regex.IsMatch(Wallet, @"^prl1[a-z0-9]{20,120}$", RegexOptions.CultureInvariant))
            errors.Add("请填写 prl1 开头的 PRL 收款地址（不含矿机名）。");
        if (!Uri.TryCreate(Pool, UriKind.Absolute, out var pool)
            || pool.Scheme is not ("stratum+tcp" or "stratum+ssl")
            || string.IsNullOrEmpty(pool.Host) || pool.HostNameType == UriHostNameType.Unknown || pool.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(pool.UserInfo) || !string.IsNullOrEmpty(pool.Query)
            || !string.IsNullOrEmpty(pool.Fragment) || pool.AbsolutePath != "/"
            || Pool.Any(char.IsWhiteSpace))
            errors.Add("矿池地址需为 stratum+tcp://主机:端口 或 stratum+ssl://主机:端口。");
        if (!Regex.IsMatch(Worker, @"^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant))
            errors.Add("矿机名限 1–32 个英文字母、数字、下划线或短横线。");
        return errors;
    }
}

public enum MiningState { Idle, Starting, Running, Mining, Recovering, Stopping, Failed }
public sealed record LogEntry(long Sequence, DateTimeOffset Time, string Level, string Message);
public sealed record MiningSnapshot(
    MiningState State, bool DemoMode, int? ProcessId, DateTimeOffset? StartedAt,
    double? Hashrate, string? HashrateUnit, DateTimeOffset? LastSpeedAt,
    int Accepted, int Rejected, int Restarts, string? KernelSha256, string? Error,
    LogEntry[] Logs, MinerKind Miner = MinerKind.SrbMiner, MiningDevice Device = MiningDevice.Gpu, int CpuThreads = 1, bool BenchmarkMode = false,
    string[]? DeviceNames = null);
public sealed record AgentRequest(string Command, MiningOptions? Options = null, long AfterSequence = 0);
public sealed record AgentResponse(bool Success, string? Error, MiningSnapshot Snapshot);
public sealed record MinerObservation(double? Hashrate = null, string? Unit = null,
    bool Accepted = false, bool Rejected = false, string? FatalError = null,
    int? AcceptedTotal = null, int? RejectedTotal = null, string? DeviceName = null);
