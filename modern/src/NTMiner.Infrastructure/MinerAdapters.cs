using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NTMiner.Modern;

public sealed class SrbMinerAdapter(string demoExecutable) : IMinerAdapter
{
    public ProcessStartInfo CreateStartInfo(MiningOptions options)
    {
        var executable = options.DemoMode ? demoExecutable : options.KernelPath;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (options.DemoMode) return info;
        var pool = new Uri(options.Pool);
        foreach (var argument in new[] { "--algorithm", "pearlhash", "--pool", pool.Authority,
            "--tls", pool.Scheme == "stratum+ssl" ? "true" : "false",
            "--wallet", options.Wallet, "--worker", options.Worker, "--password", "x", "--disable-cpu",
            "--gpu-disable-oc", "--disable-msr-tweaks" })
            info.ArgumentList.Add(argument);
        // 已启用的内核参数；未知名称不在映射表中，不会拼进启动命令。
        foreach (var name in options.KernelSwitches)
            if (SrbSwitchMap.TryGetValue(name, out var extra))
                foreach (var part in extra) info.ArgumentList.Add(part);
        return info;
    }

    /// <summary>
    /// 开关名到真实参数的映射，依据 SRBMiner-Multi 官方 Parameters 文档。
    /// 名称与 MinerSwitchCatalog 中的定义一一对应，不在表中的名称不产生任何参数。
    /// </summary>
    private static readonly Dictionary<string, string[]> SrbSwitchMap = new(StringComparer.Ordinal)
    {
        ["DisableWorkerWatchdog"] = ["--disable-worker-watchdog"],
        ["GpuSensorsDisable"] = ["--gpu-sensors-disable"],
        ["DisableHugePages"] = ["--disable-huge-pages"],
        ["ExtendedLog"] = ["--extended-log"],
        ["DisableExtranonceSubscribe"] = ["--disable-extranonce-subscribe"],
        ["DisableAutoAffinity"] = ["--disable-auto-affinity"],
        ["GpuTableSlowBuild"] = ["--gpu-table-slow-build"],
        ["DisableGpuDualKernels"] = ["--disable-gpu-dual-kernels"],
        ["ForcedShutdown"] = ["--forced-shutdown"],
        ["EnableRestartOnRejected"] = ["--enable-restart-on-rejected"]
    };

    public MinerObservation Parse(string line)
    {
        // SRBMiner output differs by version. Only unambiguous total lines are used.
        line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var speed = Regex.Match(line,
            @"\btotal(?:\s+(?:hashrate|speed))?\s*[:=]\s*(\d+(?:\.\d+)?)\s*((?:[kMGTPE]?H/s)|(?:[kMGTPE]?T/s))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        double? rate = null;
        string? unit = null;
        if (speed.Success && double.TryParse(speed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        { rate = value; unit = speed.Groups[2].Value; }
        bool accepted = Regex.IsMatch(line, @"\b(?:result\s+accepted|share\s+accepted|accepted\s+share)\b", RegexOptions.IgnoreCase);
        bool rejected = Regex.IsMatch(line, @"\b(?:result\s+rejected|share\s+rejected|rejected\s+share)\b", RegexOptions.IgnoreCase);
        string? fatal = Regex.IsMatch(line,
            @"\b(?:invalid\s+(?:wallet|address|algorithm|pool)|unknown\s+(?:argument|algorithm)|no\s+(?:compatible\s+)?(?:gpu|device)s?\s+found|authentication\s+failed|unauthorized)\b",
            RegexOptions.IgnoreCase) ? "内核报告配置、认证或设备错误，请检查日志后重新启动。" : null;
        return new(rate, unit, accepted, rejected, fatal);
    }
}

public sealed class BzMinerAdapter(string demoExecutable, string workingDirectory) : IMinerAdapter
{
    private readonly SrbMinerAdapter _commonParser = new(demoExecutable);
    private bool _cpuOnly;
    private string[]? _tableColumns;

    public ProcessStartInfo CreateStartInfo(MiningOptions options)
    {
        _cpuOnly = options.Device == MiningDevice.Cpu;
        _tableColumns = null;
        if (options.DemoMode)
        {
            var demo = _commonParser.CreateStartInfo(options);
            demo.ArgumentList.Add("--bz-format");
            demo.ArgumentList.Add(options.Device == MiningDevice.IntegratedGpu ? "--demo-igpu" : options.Device == MiningDevice.Cpu ? "--demo-cpu" : "--demo-gpu");
            if (options.BenchmarkMode) demo.ArgumentList.Add("--bench");
            return demo;
        }
        Directory.CreateDirectory(workingDirectory);
        // Do not inherit config.txt beside bzminer, which could contain pools, OC or other algorithms.
        var config = Path.Combine(workingDirectory, "bzminer-empty.json");
        File.WriteAllText(config, "{}");
        var info = new ProcessStartInfo(options.KernelPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        var arguments = new List<string>
        {
            "--config", config, "-a", "pearl", "-o", "log",
            "--set", "http_enabled=false", "--disable_udp", "--auto-update", "off", "--plugin-update", "off",
            "--set", "log_table_interval=5000", "--no-bc250-gpu", "--no-bc250-cpu", "--no-bc250-cu"
        };
        if (options.BenchmarkMode) arguments.Add("--bench");
        else arguments.AddRange(["-p", options.Pool, "-w", options.Wallet, "--worker", options.Worker]);
        if (options.Device == MiningDevice.Cpu)
            arguments.AddRange(["--cpu", "1", "--nvidia", "0", "--amd", "0", "--intel", "0",
                "--devices", "none", "--cpu_threads", options.CpuThreads.ToString(CultureInfo.InvariantCulture),
                "--cpu-metrics", "0", "--disable_huge_pages",
                "--set", "backends.cuda=false", "--set", "backends.opencl=false", "--set", "backends.metal=false",
                "--set", "backends.cpu=true", "--set", "superio_fans=false", "--set", "cache_qos=false"]);
        else if (options.Device == MiningDevice.IntegratedGpu)
            arguments.AddRange(["--igpu", "--cpu", "0", "--nvidia", "1", "--amd", "1", "--intel", "1",
                "--cpu_threads", "1", "--cpu-metrics", "0", "--disable_huge_pages",
                "--set", "backends.cpu=false", "--set", "backends.cuda=false", "--set", "backends.metal=false",
                "--set", "backends.opencl=true", "--set", "superio_fans=false", "--set", "cache_qos=false"]);
        else
            arguments.AddRange(["--cpu", "0", "--nvidia", "1", "--amd", "1", "--intel", "1", "--devices", "all"]);
        // 已启用的内核参数：按适配器定义的映射追加真实命令行片段。
        // 未知名称被忽略，不会拼进启动命令。
        foreach (var name in options.KernelSwitches)
            if (AppendSwitch(arguments, name))
                continue;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>
    /// 把开关名翻译成真实命令行片段。返回 true 表示额外参数已并入 arguments。
    /// 这里的映射必须与 MinerSwitchCatalog 中的 Argument 文字保持一致，
    /// 否则界面预览会与真实启动参数不符。
    /// </summary>
    private static bool AppendSwitch(List<string> arguments, string name)
    {
        switch (name)
        {
            case "NoWatchdog": arguments.Add("--no-watchdog"); return true;
            case "CpuMetrics": arguments.AddRange(["--cpu-metrics", "0"]); return true;
            case "DisableHugePages": arguments.Add("--disable_huge_pages"); return true;
            case "NoBandwidthTest": arguments.Add("--no-bandwidth-test"); return true;
            case "LogDebug": arguments.AddRange(["--log-level", "debug"]); return true;
            case "InternalLog": arguments.Add("--internal-log"); return true;
            case "NoDuplicates": arguments.AddRange(["--duplicate-devices", "0"]); return true;
            case "DisableSse": arguments.Add("--disable_sse"); return true;
            case "DisableAvx2": arguments.Add("--disable_avx2"); return true;
            case "DisableAvx512": arguments.Add("--disable_avx512"); return true;
            case "WatchdogRestarts": arguments.AddRange(["--watchdog-restarts", "3"]); return true;
            case "HashrateWindow": arguments.AddRange(["--hashrate-window", "30"]); return true;
            case "NoColor": arguments.Add("--no-color"); return true;
            default: return false;
        }
    }

    public MinerObservation Parse(string line)
    {
        var result = _commonParser.Parse(line);
        var plain = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var device = Regex.Match(plain, @"\bdevice:\s*\[\d+\]\s*(?<name>.+?)\s+\([0-9a-f:.]+\)\s+\[(?:opencl|cuda|metal|cpu)\]", RegexOptions.IgnoreCase);
        if (device.Success) result = result with { DeviceName = device.Groups["name"].Value.Trim()[..Math.Min(device.Groups["name"].Value.Trim().Length, 160)] };
        var cells = plain.Split('|').Skip(1).SkipLast(1).Select(x => x.Trim()).ToArray();
        if (cells.Length > 0 && cells[0] == "#" && cells.Contains("miner hr")) _tableColumns = cells;
        if (cells.Length > 0 && cells[0] == "smry" && _tableColumns is not null)
        {
            int rateIndex = Array.IndexOf(_tableColumns, "miner hr");
            int sharesIndex = Array.IndexOf(_tableColumns, "a/r/p");
            if (rateIndex >= 0 && rateIndex < cells.Length)
            {
                var compact = Regex.Match(cells[rateIndex], @"^(\d+(?:\.\d+)?)\s*([kMGTPE]?)h(?:/s)?$", RegexOptions.IgnoreCase);
                if (compact.Success && double.TryParse(compact.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalRate))
                {
                    var prefix = compact.Groups[2].Value;
                    prefix = prefix.Equals("k", StringComparison.OrdinalIgnoreCase) ? "k" : prefix.ToUpperInvariant();
                    result = result with { Hashrate = totalRate, Unit = prefix + "H/s" };
                }
            }
            if (sharesIndex >= 0 && sharesIndex < cells.Length)
            {
                var counts = cells[sharesIndex].Split('/');
                if (counts.Length == 3 && int.TryParse(counts[0], out var acceptedTotal) && acceptedTotal >= 0
                    && int.TryParse(counts[1], out var rejectedTotal) && rejectedTotal >= 0)
                    result = result with { AcceptedTotal = acceptedTotal, RejectedTotal = rejectedTotal };
            }
        }
        if (_cpuOnly && Regex.IsMatch(plain, @"\bno\s+(?:compatible\s+)?GPUs?\s+found\b", RegexOptions.IgnoreCase))
            result = result with { FatalError = null };
        var speed = Regex.Match(plain,
            @"\b(?:total\s+)?(?:hashrate|speed)\s*[:=]\s*(\d+(?:\.\d+)?)\s*([kMGTPE]?[HT]/s)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Per-device rows must not overwrite the total, even in a CPU-only run.
        if (result.Hashrate is null && speed.Success && !Regex.IsMatch(plain, @"\b(?:GPU|CPU)\s*#?\d+\b", RegexOptions.IgnoreCase)
            && double.TryParse(speed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            result = result with { Hashrate = rate, Unit = speed.Groups[2].Value };
        bool accepted = Regex.IsMatch(plain, @"\bshare\s+#?\d+\s+(?:was\s+)?accepted\b", RegexOptions.IgnoreCase);
        bool rejected = Regex.IsMatch(plain, @"\bshare\s+#?\d+\s+(?:was\s+)?rejected\b", RegexOptions.IgnoreCase);
        bool unsupported = Regex.IsMatch(plain, @"\b(?:unknown|unrecognized|unrecognised|unsupported|invalid)\s+(?:option|flag|argument)\b", RegexOptions.IgnoreCase);
        bool insufficientMemory = Regex.IsMatch(plain, @"\b(?:not enough host memory|out of (?:host )?memory|failed to allocate)\b", RegexOptions.IgnoreCase);
        var memory = Regex.Match(plain, @"needs\s+(\d+)\s+MB\s+plus\s+(\d+)\s+MB.*?system has\s+(\d+)\s+MB available", RegexOptions.IgnoreCase);
        string memoryError = "BzMiner 可用内存不足，已停止任务。请按内核日志中的需求释放内存后重新启动。";
        if (memory.Success && long.TryParse(memory.Groups[1].Value, out var needed) && long.TryParse(memory.Groups[2].Value, out var room)
            && long.TryParse(memory.Groups[3].Value, out var available) && needed <= long.MaxValue - room)
            memoryError = $"BzMiner 可用系统内存不足：需要 {needed + room} MB（{needed} + {room} MB），当前可用 {available} MB。已停止任务且不自动重试；请释放更多内存后重新启动。";
        return result with
        {
            Accepted = result.Accepted || accepted, Rejected = result.Rejected || rejected,
            FatalError = insufficientMemory ? memoryError
                : unsupported ? "BzMiner 参数不受支持，请使用支持 CPU、--bench 和设备选择参数的当前官方版本。" : result.FatalError
        };
    }
}

public sealed class MinerAdapterRouter(string demoExecutable, string workingDirectory) : IMinerAdapter
{
    private IMinerAdapter? _active;
    public ProcessStartInfo CreateStartInfo(MiningOptions options)
    {
        _active = options.Miner switch
        {
            MinerKind.SrbMiner => new SrbMinerAdapter(demoExecutable),
            MinerKind.BzMiner => new BzMinerAdapter(demoExecutable, workingDirectory),
            _ => throw new ArgumentException("不支持的挖矿内核。")
        };
        return _active.CreateStartInfo(options);
    }
    public MinerObservation Parse(string line) => _active?.Parse(line) ?? new();
}
