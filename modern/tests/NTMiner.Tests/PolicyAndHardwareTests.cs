using System.Diagnostics;
using System.Text;
using NTMiner.Modern;
using static NTMiner.Tests.Harness;

namespace NTMiner.Tests;

/// <summary>Pure logic: state machine, retry policy, hardware classification and kernel switches.</summary>
internal static class PolicyAndHardwareTests
{
    internal static async Task RunAsync()
    {
        await Test("hardware adapter classification separates virtual, integrated and dedicated", () =>
        {
            // 虚拟显示适配器（远程桌面/投屏类）必须排除在可挖矿设备之外。
            var virtualByName = new HardwareGraphics("Todesk Virtual Display Adapter", "1.0", "", null, @"ROOT\DISPLAY\0000");
            Check(virtualByName.IsVirtual, "Remote-control virtual display must be flagged");
            Check(!virtualByName.IsIntegrated, "A virtual adapter is never an integrated GPU");
            var virtualByPnp = new HardwareGraphics("Some Card", "1.0", "", null, @"ROOT\DISPLAY\0001");
            Check(virtualByPnp.IsVirtual, "ROOT enumerated adapters are software adapters");
            var rdp = new HardwareGraphics("Microsoft Basic Display Adapter", "1.0", "", null, @"PCI\VEN_1234");
            Check(rdp.IsVirtual, "Microsoft Basic Display Adapter is not usable for mining");

            var integrated = new HardwareGraphics("Intel(R) UHD Graphics 730", "32.0", "", 2_147_479_552, @"PCI\VEN_8086&DEV_4682");
            Check(!integrated.IsVirtual && integrated.IsIntegrated, "Intel UHD is a physical integrated GPU");
            var amdApu = new HardwareGraphics("AMD Radeon(TM) Graphics", "1.0", "", null, @"PCI\VEN_1002");
            Check(amdApu.IsIntegrated, "Radeon(TM) Graphics is the APU naming used by AMD");

            // 关键回归：独显名称包含 "Radeon" 时不能被当成核显。
            var dedicated = new HardwareGraphics("AMD Radeon RX 7600 XT", "1.0", "", null, @"PCI\VEN_1002");
            Check(!dedicated.IsIntegrated, "A discrete Radeon must not be classified as integrated");
            var nvidia = new HardwareGraphics("NVIDIA GeForce RTX 4070", "1.0", "", null, @"PCI\VEN_10DE");
            Check(!nvidia.IsIntegrated && !nvidia.IsVirtual, "NVIDIA GeForce is a discrete GPU");

            var all = new[] { virtualByName, integrated, dedicated };
            Check(HardwareDisplay.Mineable(all).Length == 2, "Only physical adapters count as mineable");
            Check(HardwareDisplay.SupportsIntegratedOnly([virtualByName]) == false, "Virtual-only host does not support igpu mode");
            Check(HardwareDisplay.SupportsIntegratedOnly([integrated]) == true, "Intel UHD host supports igpu mode");
            Check(HardwareDisplay.SupportsIntegratedOnly([dedicated]) == false, "Discrete-only host does not support igpu mode");
            Check(HardwareDisplay.SupportsIntegratedOnly([]) is null, "Unknown hardware makes no claim");
            Check(HardwareDisplay.FormatBytes(0) == "—", "Missing size is shown as unknown, never as 0 B");
            Check(HardwareDisplay.FormatBytes(17_179_869_184) == "16 GB", "Memory is shown in binary units");
            return Task.CompletedTask;
        });

        await Test("hardware probe reports this machine without throwing", () =>
        {
            var snapshot = HardwareProbe.Capture();
            Check(snapshot.CapturedAt != default, "Captured time is set");
            // 探测允许分区缺失（部分虚拟化环境限制 WMI），但不允许把异常抛给调用方。
            Check(snapshot.Processor is null || snapshot.Processor.Reported, "Processor block is either absent or reported");
            if (snapshot.Processor is { } cpu)
                Check(cpu.Cores > 0 && cpu.LogicalProcessors >= cpu.Cores, "Core counts are consistent");
            if (snapshot.Memory is { } memory)
                Check(memory.TotalBytes > 0, "Total memory is positive");
            foreach (var graphics in snapshot.Graphics)
                Check(!string.IsNullOrWhiteSpace(graphics.Name), "Every adapter has a name");
            return Task.CompletedTask;
        });

        await Test("state machine rejects illegal transitions and never leaves Failed", () =>
        {
            var machine = new SessionStateMachine();
            Check(machine.Current == MiningState.Idle, "Starts idle");
            machine.Transition(MiningState.Starting);
            machine.Transition(MiningState.Running);
            machine.Transition(MiningState.Mining);
            machine.Fault();
            Check(machine.Current == MiningState.Failed, "Fault enters terminal state");
            // 关键回归：迟到的份额事件不得把已失败的任务改回"正在挖矿"。
            Check(!machine.TryTransition(MiningState.Mining), "Failed must not fall back to Mining");
            Check(!machine.TryTransition(MiningState.Running), "Failed must not fall back to Running");
            Check(machine.Current == MiningState.Failed, "Rejected transition leaves state unchanged");
            Check(machine.IsTerminal, "Failed is terminal");
            // 只有显式重新开始才能离开终态。
            machine.Reset();
            machine.Transition(MiningState.Starting);
            Check(machine.Current == MiningState.Starting, "Explicit restart leaves the terminal state");
            Check(!SessionStateMachine.IsAllowed(MiningState.Idle, MiningState.Running), "Idle cannot jump to Running");
            Check(!SessionStateMachine.IsAllowed(MiningState.Starting, MiningState.Mining), "Cannot mine before running");
            Check(SessionStateMachine.IsAllowed(MiningState.Running, MiningState.Recovering), "Recovery is reachable from Running");
            Check(SessionStateMachine.IsAllowed(MiningState.Recovering, MiningState.Starting), "Recovery returns to Starting");
            var stopping = new SessionStateMachine();
            stopping.Transition(MiningState.Starting);
            stopping.Transition(MiningState.Stopping);
            stopping.Fault();
            Check(stopping.Current == MiningState.Idle, "A user-requested stop is not reported as a failure");
            return Task.CompletedTask;
        });

        await Test("retry policy encodes the documented backoff without waiting", () =>
        {
            var policy = new RetryPolicy();
            var demo = new MiningOptions();
            Check(policy.MaxRestarts == 3, "Three restarts by default");
            Check(policy.ShouldRetry(demo, 0, false), "First failure retries");
            Check(policy.ShouldRetry(demo, 2, false), "Third failure still retries");
            Check(!policy.ShouldRetry(demo, 3, false), "Budget exhausted after three restarts");
            Check(!policy.ShouldRetry(demo with { AutoRecover = false }, 0, false), "Disabled recovery never retries");
            Check(!policy.ShouldRetry(demo with { BenchmarkMode = true }, 0, false), "Offline self-test never retries");
            Check(!policy.ShouldRetry(demo, 0, fatal: true), "Fatal configuration errors never retry");
            Check(policy.DelayFor(0) == TimeSpan.FromSeconds(2) && policy.DelayFor(1) == TimeSpan.FromSeconds(4)
                && policy.DelayFor(2) == TimeSpan.FromSeconds(8), "Backoff is 2, 4, 8 seconds");
            Check(policy.DelayFor(100) <= TimeSpan.FromDays(1), "Extreme attempt numbers do not overflow");
            Check(RetryPolicy.NextRestartNumber(0) == 1, "Restart numbers are one-based for display");
            return Task.CompletedTask;
        });

        await Test("kernel switches map onto real command-line arguments", () =>
        {
            var options = new MiningOptions
            {
                DemoMode = false, Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu, CpuThreads = 1,
                KernelPath = FakeMiner, BenchmarkMode = true, KernelSwitches = ["NoWatchdog", "LogDebug", "NoDuplicates"]
            };
            var start = new BzMinerAdapter(FakeMiner, Path.Combine(AppContext.BaseDirectory, "bz-switch-test")).CreateStartInfo(options);
            Check(start.ArgumentList.Contains("--no-watchdog"), "NoWatchdog reaches the command line");
            Check(start.ArgumentList.Contains("--log-level") && start.ArgumentList.Contains("debug"), "LogDebug passes its value");
            Check(start.ArgumentList.Contains("--duplicate-devices") && start.ArgumentList.Contains("0"), "NoDuplicates passes its value");
            Check(!start.ArgumentList.Contains("--internal-log"), "Unselected switches are not applied");

            // 未知名称不能进入真实启动参数。
            var unknown = options with { KernelSwitches = ["NotARealSwitch", "--wallet evil"] };
            var safe = new BzMinerAdapter(FakeMiner, Path.Combine(AppContext.BaseDirectory, "bz-switch-test")).CreateStartInfo(unknown);
            Check(!safe.ArgumentList.Contains("NotARealSwitch") && !safe.ArgumentList.Contains("--wallet evil"), "Unknown switch names are dropped");

            // 换内核后旧开关要被过滤掉，避免把 BzMiner 参数带给 SRBMiner。
            Check(MinerSwitchCatalog.Normalize(MinerKind.SrbMiner, ["NoWatchdog"]).Length == 0, "Switches are filtered per miner");
            Check(MinerSwitchCatalog.Normalize(MinerKind.BzMiner, ["NoWatchdog", "Bogus"]).SequenceEqual(["NoWatchdog"]), "Valid switches survive normalization");

            // 预览与真实参数必须一致，否则界面会误导用户。
            var preview = MinerSwitchCatalog.Preview(options, options.KernelSwitches);
            Check(preview.Contains("--no-watchdog") && preview.Contains("--log-level debug"), "Preview shows the same switches");
            return Task.CompletedTask;
        });

        await Test("SRBMiner switches map onto real arguments and reject unknown names", () =>
        {
            var options = new MiningOptions
            {
                DemoMode = false, Miner = MinerKind.SrbMiner, Device = MiningDevice.Gpu,
                KernelPath = FakeMiner, Wallet = "prl1" + new string('q', 58), Pool = "stratum+tcp://pool.example.org:3333",
                Worker = "rig01", KernelSwitches = ["ExtendedLog", "DisableWorkerWatchdog"]
            };
            var start = new SrbMinerAdapter(FakeMiner).CreateStartInfo(options);
            Check(start.ArgumentList.Contains("--extended-log"), "ExtendedLog reaches the command line");
            Check(start.ArgumentList.Contains("--disable-worker-watchdog"), "DisableWorkerWatchdog reaches the command line");
            Check(!start.ArgumentList.Contains("--gpu-sensors-disable"), "Unselected SRBMiner switches are not applied");

            var injected = options with { KernelSwitches = ["Bogus", "--wallet evil"] };
            var safe = new SrbMinerAdapter(FakeMiner).CreateStartInfo(injected);
            Check(!safe.ArgumentList.Contains("Bogus") && !safe.ArgumentList.Contains("--wallet evil"), "Unknown SRBMiner switch names are dropped");

            // 两个内核的开关集合不能互相污染。
            Check(MinerSwitchCatalog.Normalize(MinerKind.SrbMiner, ["NoWatchdog"]).Length == 0, "BzMiner switch is not valid for SRBMiner");
            Check(MinerSwitchCatalog.Normalize(MinerKind.BzMiner, ["ExtendedLog"]).Length == 0, "SRBMiner switch is not valid for BzMiner");
            return Task.CompletedTask;
        });
    }
}