using System.Diagnostics;
using System.Text;
using NTMiner.Modern;
using static NTMiner.Tests.Harness;

namespace NTMiner.Tests;

/// <summary>Validation, argument construction and log parsing.</summary>
internal static class ConfigurationTests
{
    internal static async Task RunAsync()
    {
        await Test("configuration rejects unsafe/missing real fields", () =>
        {
            var invalid = new MiningOptions { DemoMode = false, KernelPath = FakeMiner, Wallet = "--wallet evil", Pool = "http://localhost:80", Worker = "rig & calc" };
            Check(invalid.Validate().Count == 4, "All invalid fields must be rejected");
            Check(new MiningOptions().Validate().Count == 0, "Demo needs no wallet or pool");
            Check(new MiningOptions { SchemaVersion = 99 }.Validate().Count > 0, "Unknown schema rejected");
            return Task.CompletedTask;
        });

        await Test("wallet/pool validation and shell-free arguments", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "NTMiner-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var executable = Path.Combine(folder, "SRBMiner-MULTI.exe");
            try
            {
                File.WriteAllBytes(executable, []);
                var options = new MiningOptions { DemoMode = false, KernelPath = executable, Wallet = "prl1" + new string('q', 58), Pool = "stratum+ssl://pool.example.org:4444", Worker = "rig_01" };
                Check(options.Validate().Count == 0, "Valid structural configuration");
                foreach (var bad in new[] { "stratum+tcp://user@host:3333", "stratum+tcp://host:3333/path", "stratum+tcp://host:3333?x=1", "stratum+tcp://host" })
                    Check((options with { Pool = bad }).Validate().Count > 0, "Bad pool accepted: " + bad);
                var start = new SrbMinerAdapter(FakeMiner).CreateStartInfo(options);
                Check(!start.UseShellExecute && start.Arguments == "" && start.ArgumentList.Contains("pearlhash"), "No shell or raw command string");
                Check(start.ArgumentList.Contains(options.Wallet) && start.ArgumentList.Contains("pool.example.org:4444"), "Preserve explicit wallet and pool");
                Check(start.ArgumentList.Contains("--tls") && start.ArgumentList.Contains("true"), "TLS must be explicit for SRBMiner");
            }
            finally { File.Delete(executable); Directory.Delete(folder); }
            return Task.CompletedTask;
        });

        await Test("parser excludes GPU-only speed, summaries and unrelated messages", () =>
        {
            var adapter = new SrbMinerAdapter(FakeMiner);
            Check(adapter.Parse("GPU0: 123 MH/s").Hashrate is null, "Do not mistake GPU speed for total");
            Check(!adapter.Parse("Accepted: 100 / Rejected: 0").Accepted, "Do not recount cumulative summaries");
            Check(adapter.Parse("Total hashrate: 123.45 MH/s").Hashrate == 123.45, "Parse total speed");
            Check(adapter.Parse("Result accepted").Accepted, "Accepted event");
            Check(adapter.Parse("Result rejected").Rejected, "Rejected event");
            Check(adapter.Parse("Invalid wallet").FatalError is not null, "Fatal config error");
            return Task.CompletedTask;
        });

        await Test("BzMiner CPU validation and unsupported combinations", () =>
        {
            var cpu = new MiningOptions { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu };
            Check(cpu.Validate().Count == 0 && cpu.CpuThreads == 1, "CPU starts conservatively at one thread");
            Check((cpu with { Miner = MinerKind.SrbMiner }).Validate().Count > 0, "SRBMiner PRL CPU is forbidden");
            Check((cpu with { CpuThreads = 0 }).Validate().Count > 0, "Do not use BzMiner's auto/all-thread count");
            Check((cpu with { CpuThreads = Environment.ProcessorCount + 1 }).Validate().Count > 0, "Respect actual CPU budget");
            Check((cpu with { Miner = (MinerKind)99 }).Validate().Count > 0, "Reject unknown miner enums");
            Check((cpu with { Device = (MiningDevice)99 }).Validate().Count > 0, "Reject unknown device enums");
            Check((cpu with { Device = MiningDevice.Gpu, BenchmarkMode = true }).Validate().Count == 0, "BzMiner GPU supports offline mode");
            Check((cpu with { Device = MiningDevice.IntegratedGpu, BenchmarkMode = true }).Validate().Count == 0, "BzMiner supports igpu offline mode");
            Check((cpu with { Miner = MinerKind.SrbMiner, Device = MiningDevice.IntegratedGpu }).Validate().Count > 0, "Only BzMiner supports igpu selection");
            Check((cpu with { Miner = MinerKind.SrbMiner, Device = MiningDevice.Gpu, BenchmarkMode = true }).Validate().Count > 0, "SRBMiner cannot use BzMiner benchmark");
            return Task.CompletedTask;
        });

        await Test("BzMiner explicit CPU flags, isolated config, TLS and offline arguments", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "bz-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var executable = Path.Combine(folder, "bzminer.exe");
            var config = Path.Combine(folder, "bzminer-empty.json");
            try
            {
                File.WriteAllBytes(executable, []);
                var adapter = new BzMinerAdapter(FakeMiner, folder);
                var options = new MiningOptions
                {
                    DemoMode = false, Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu,
                    KernelPath = executable, Wallet = "prl1" + new string('q', 58), Pool = "stratum+ssl://pool.example.org:4444", CpuThreads = 1
                };
                Check(options.Validate().Count == 0, "Valid BzMiner CPU pooled config");
                var start = adapter.CreateStartInfo(options);
                string Arg(string key) => start.ArgumentList[start.ArgumentList.IndexOf(key) + 1];
                Check(!start.UseShellExecute && start.Arguments == "", "Never shell execute");
                Check(Arg("-a") == "pearl" && Arg("-p") == options.Pool && Arg("-w") == options.Wallet, "Do not change user's pool/TLS/wallet");
                Check(Arg("--cpu") == "1" && Arg("--cpu_threads") == "1", "Explicit CPU budget");
                Check(Arg("--nvidia") == "0" && Arg("--amd") == "0" && Arg("--intel") == "0" && Arg("--devices") == "none", "No GPU mining");
                Check(Arg("--config") == config && File.ReadAllText(config) == "{}", "Use an explicit empty config");
                Check(start.ArgumentList.Contains("http_enabled=false") && start.ArgumentList.Contains("--disable_udp") && Arg("--auto-update") == "off", "No default web/UDP/update behavior");
                Check(start.ArgumentList.Contains("backends.cuda=false") && start.ArgumentList.Contains("backends.opencl=false")
                    && start.ArgumentList.Contains("backends.metal=false") && Arg("--plugin-update") == "off", "GPU backends and plugin downloads disabled");
                var bench = options with { BenchmarkMode = true, Wallet = "", Pool = "", Worker = "" };
                Check(bench.Validate().Count == 0, "Offline bench does not require a wallet or pool");
                start = adapter.CreateStartInfo(bench);
                Check(start.ArgumentList.Contains("--bench") && !start.ArgumentList.Contains("-p") && !start.ArgumentList.Contains("-w"), "Offline mode sends no payout or pool arguments");
                start = adapter.CreateStartInfo(options with { Device = MiningDevice.Gpu });
                Check(Arg("--cpu") == "0" && Arg("--devices") == "all", "GPU mode explicitly excludes CPU");
                Check((options with { KernelPath = FakeMiner }).Validate().Count > 0, "Wrong miner file refused");
            }
            finally { File.Delete(executable); if (File.Exists(config)) File.Delete(config); Directory.Delete(folder); }
            return Task.CompletedTask;
        });

        await Test("BzMiner parser recognizes distinct events and CPU-only no-GPU startup", () =>
        {
            var adapter = new BzMinerAdapter(FakeMiner, AppContext.BaseDirectory);
            adapter.CreateStartInfo(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu });
            Check(adapter.Parse("[pearl] Hashrate: 101.25 H/s").Hashrate == 101.25, "CPU hashrate event");
            Check(adapter.Parse("CPU0 Hashrate: 50 H/s").Hashrate is null, "Do not count a per-device row as total");
            Check(adapter.Parse("[pearl] Share #7 accepted").Accepted, "Bz accepted event");
            Check(adapter.Parse("[pearl] Share #8 rejected").Rejected, "Bz rejected event");
            Check(!adapter.Parse("Accepted: 7 / Rejected: 1").Accepted, "No repeated summary count");
            Check(adapter.Parse("Unknown option --cpu").FatalError is not null, "Unsupported binary fails closed");
            Check(adapter.Parse("No GPUs found").FatalError is null, "No GPU is normal in CPU mode");
            Check(adapter.Parse("Invalid wallet").FatalError is not null, "Still enforce actual configuration errors");
            Check(adapter.Parse("pearl not enough host memory for this device: the attempt needs 1097 MB plus 384 MB working room").FatalError!.Contains("内存不足"), "Do not keep a skipped low-memory CPU task running");
            // Header and summary shape captured from the actual v100.46 offline CPU run.
            adapter.Parse("| # | cfg | tbs | a/r/p | eff | pool hr | miner hr | status | pool |");
            var summary = adapter.Parse("| smry | | -- | 7/1/0 | -- | -- | 24.23gh | connected: yes | diff 281.47T |");
            Check(summary.Hashrate == 24.23 && summary.Unit == "GH/s", "Parse actual BzMiner compact summary units");
            Check(summary.AcceptedTotal == 7 && summary.RejectedTotal == 1 && !summary.Accepted, "Cumulative counters are not event increments");
            Check(adapter.Parse("| 255:0 | t1 | -- | 7/1/0 | -- | -- | 24.23gh | Mining | |").Hashrate is null, "Ignore per-device table rows");
            return Task.CompletedTask;
        });

        await Test("repeated BzMiner summary counters are not counted twice", async () =>
        {
            await using var session = new MiningSession(new BzTestAdapter(FakeMiner, "--bz-summary"), () => new WindowsJob());
            await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu });
            await Wait(() => session.Snapshot().Logs.Count(l => l.Message.Contains("| smry |")) >= 2);
            var snapshot = session.Snapshot();
            Check(snapshot.Accepted == 7 && snapshot.Rejected == 1 && snapshot.Hashrate == 24.23 && snapshot.State == MiningState.Mining, "Repeated cumulative counters stay exact");
        });

        await Test("GPU and igpu offline arguments exclude CPU, pools and payout", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "igpu-args-" + Guid.NewGuid().ToString("N"));
            try
            {
                var adapter = new BzMinerAdapter(FakeMiner, folder);
                var options = new MiningOptions { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu, DemoMode = false, BenchmarkMode = true,
                    KernelPath = Path.Combine(folder, "bzminer.exe") };
                Directory.CreateDirectory(folder); File.WriteAllBytes(options.KernelPath, []);
                Check(options.Validate().Count == 0, "Real igpu bench needs no wallet or pool");
                var info = adapter.CreateStartInfo(options);
                string Arg(string flag) => info.ArgumentList[info.ArgumentList.IndexOf(flag) + 1];
                Check(info.ArgumentList.Contains("--igpu") && info.ArgumentList[info.ArgumentList.IndexOf("--igpu") + 1] == "--cpu", "Bare igpu selects only integrated GPUs");
                Check(Arg("--cpu") == "0" && Arg("--intel") == "1" && !info.ArgumentList.Contains("--devices"), "No widening device allowlist or CPU work");
                Check(info.ArgumentList.Contains("--bench") && !info.ArgumentList.Contains("--benchmark") && !info.ArgumentList.Contains("-p") && !info.ArgumentList.Contains("-w"), "Local job only; no stratum server or payout");
                Check(info.ArgumentList.Contains("backends.cpu=false") && info.ArgumentList.Contains("backends.opencl=true"), "Explicit igpu backend");
                info = adapter.CreateStartInfo(options with { Device = MiningDevice.Gpu });
                Check(info.ArgumentList.Contains("--bench") && !info.ArgumentList.Contains("--igpu") && Arg("--devices") == "all" && Arg("--cpu") == "0", "All GPU bench preserves separate selection");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            return Task.CompletedTask;
        });

        await Test("actual igpu device and host-memory error are parsed precisely", () =>
        {
            var adapter = new BzMinerAdapter(FakeMiner, AppContext.BaseDirectory);
            adapter.CreateStartInfo(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu });
            Check(adapter.Parse("INFO: device: [0] Intel(R) UHD Graphics 730 (0000:00:02) [opencl]").DeviceName == "Intel(R) UHD Graphics 730", "Device name comes from actual miner output");
            Check(adapter.Parse("backend cpu: disabled (configuration)").DeviceName is null, "Backend flags are not device discovery");
            var error = adapter.Parse("ERROR: pearl opencl: not enough host memory for this device: the attempt needs 1353 MB plus 384 MB working room, and the system has 1636 MB available. the device is skipped").FatalError!;
            Check(error.Contains("1737 MB") && error.Contains("1636 MB") && error.Contains("不自动重试"), "Expose required and available system memory");
            Check(adapter.Parse("No GPUs found").FatalError is not null, "Missing GPU must fail in igpu mode");
            return Task.CompletedTask;
        });
    }
}
