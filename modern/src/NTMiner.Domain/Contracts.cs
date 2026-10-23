using System.Text.RegularExpressions;

namespace NTMiner.Modern;

public enum MinerKind { SrbMiner, BzMiner }
public enum MiningDevice { Gpu, Cpu, IntegratedGpu }
public enum AppTheme { System, Light, Dark }

public sealed record MiningOptions
{
    public int SchemaVersion { get; init; } = 5;
    /// <summary>已启用的内核参数名；含义见 MinerSwitchCatalog。</summary>
    public string[] KernelSwitches { get; init; } = [];
    /// <summary>界面主题，与挖矿无关但一并保存在本机配置。</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;
    public MinerKind Miner { get; init; } = MinerKind.SrbMiner;
    public MiningDevice Device { get; init; } = MiningDevice.Gpu;
    public int CpuThreads { get; init; } = 1;
    public bool BenchmarkMode { get; init; }
    public string KernelPath { get; init; } = "";
    public string Wallet { get; init; } = "";
    public string Pool { get; init; } = "";
    /// <summary>
    /// 备用矿池，按顺序追加到主矿池之后。内核自身支持连接失败时自动切换
    /// （SRBMiner 的 --pool 接受多个值，BzMiner 的 -p 同理），客户端只负责传参。
    /// </summary>
    public string[] BackupPools { get; init; } = [];
    /// <summary>已保存的钱包地址列表，用于在多地址之间切换；Wallet 始终是当前使用的那个。</summary>
    public string[] SavedWallets { get; init; } = [];
    /// <summary>所选算法，取自内核自身的算法列表；空表示尚未选择。</summary>
    public string Algorithm { get; init; } = "";
    public string Worker { get; init; } = "rig01";
    public bool AutoRecover { get; init; } = true;
    public bool DemoMode { get; init; } = true;
    public string EffectiveAlgorithm => string.IsNullOrWhiteSpace(Algorithm)
        ? (Miner == MinerKind.BzMiner ? "pearl" : "pearlhash")
        : Miner == MinerKind.BzMiner && Algorithm.Trim().Equals("pearlhash", StringComparison.OrdinalIgnoreCase) ? "pearl" : Algorithm.Trim().ToLowerInvariant();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != 5) errors.Add("配置版本不受支持，请重新填写配置。");
        if (!Enum.IsDefined(Miner) || !Enum.IsDefined(Device)) errors.Add("不支持的内核或设备模式。");
        if (Device == MiningDevice.Cpu && Miner != MinerKind.BzMiner) errors.Add("PRL 的 CPU 模式需要选择 BzMiner。");
        if (Device == MiningDevice.IntegratedGpu && Miner != MinerKind.BzMiner) errors.Add("仅核显模式需要选择 BzMiner。");
        if (Device == MiningDevice.Cpu && (CpuThreads < 1 || CpuThreads > Environment.ProcessorCount))
            errors.Add($"CPU 线程数应为 1–{Environment.ProcessorCount}；建议先使用 1 个线程验证。");
        if (BenchmarkMode && Miner != MinerKind.BzMiner)
            errors.Add("离线自测需要选择 BzMiner，支持 CPU、全部 GPU 或仅核显。");
        if (!Regex.IsMatch(EffectiveAlgorithm, @"^[a-z0-9][a-z0-9_/-]{0,63}$", RegexOptions.CultureInvariant))
            errors.Add("算法名限一个算法，不可包含空格、分隔符或额外参数。");
        if (!BenchmarkMode && EffectiveAlgorithm != (Miner == MinerKind.BzMiner ? "pearl" : "pearlhash"))
            errors.Add("当前收款与矿池流程仅支持 PRL；其他算法只可使用 BzMiner 离线自测。");
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
        if (!IsValidPoolAddress(Pool))
            errors.Add("矿池地址需为 stratum+tcp://主机:端口 或 stratum+ssl://主机:端口。");
        // 备用矿池与主矿池使用同一套结构校验；空的备用项直接忽略。
        for (var i = 0; i < BackupPools.Length; i++)
            if (!string.IsNullOrWhiteSpace(BackupPools[i]) && !IsValidPoolAddress(BackupPools[i]))
                errors.Add($"备用矿池 {i + 1} 需为 stratum+tcp://主机:端口 或 stratum+ssl://主机:端口。");
        // 已保存的钱包地址只做结构校验，避免把明显错误的地址存进列表。
        for (var i = 0; i < SavedWallets.Length; i++)
            if (!string.IsNullOrWhiteSpace(SavedWallets[i])
                && !Regex.IsMatch(SavedWallets[i], @"^prl1[a-z0-9]{20,120}$", RegexOptions.CultureInvariant))
                errors.Add($"已保存的钱包地址 {i + 1} 格式不正确。");
        if (!Regex.IsMatch(Worker, @"^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant))
            errors.Add("矿机名限 1–32 个英文字母、数字、下划线或短横线。");
        return errors;
    }

    /// <summary>矿池地址的结构校验，主矿池与备用矿池共用，避免两套规则漂移。</summary>
    public static bool IsValidPoolAddress(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && Uri.TryCreate(candidate, UriKind.Absolute, out var pool)
        && pool.Scheme is "stratum+tcp" or "stratum+ssl"
        && !string.IsNullOrEmpty(pool.Host) && pool.HostNameType != UriHostNameType.Unknown
        && pool.Port is >= 1 and <= 65535
        && string.IsNullOrEmpty(pool.UserInfo) && string.IsNullOrEmpty(pool.Query)
        && string.IsNullOrEmpty(pool.Fragment) && pool.AbsolutePath == "/"
        && !candidate.Any(char.IsWhiteSpace);

    /// <summary>主矿池在前、备用矿池按顺序追加，供适配器一次性传给内核。</summary>
    public string[] AllPools() => new[] { Pool }
        .Concat(BackupPools.Where(p => !string.IsNullOrWhiteSpace(p)))
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .ToArray();
}

public enum MiningState { Idle, Starting, Running, Mining, Recovering, Stopping, Failed }
public sealed record LogEntry(long Sequence, DateTimeOffset Time, string Level, string Message);
public sealed record MiningSnapshot(
    MiningState State, bool DemoMode, int? ProcessId, DateTimeOffset? StartedAt,
    double? Hashrate, string? HashrateUnit, DateTimeOffset? LastSpeedAt,
    int Accepted, int Rejected, int Restarts, string? KernelSha256, string? Error,
    LogEntry[] Logs, MinerKind Miner = MinerKind.SrbMiner, MiningDevice Device = MiningDevice.Gpu, int CpuThreads = 1, bool BenchmarkMode = false,
    string[]? DeviceNames = null, string Algorithm = "");
public sealed record AgentRequest(string Command, MiningOptions? Options = null, long AfterSequence = 0);
public sealed record AgentResponse(bool Success, string? Error, MiningSnapshot Snapshot, HardwareSnapshot? Hardware = null,
    GpuSensorSnapshot? Sensors = null, MinerAlgorithmCatalog? Algorithms = null);
public sealed record MinerObservation(double? Hashrate = null, string? Unit = null,
    bool Accepted = false, bool Rejected = false, string? FatalError = null,
    int? AcceptedTotal = null, int? RejectedTotal = null, string? DeviceName = null);
