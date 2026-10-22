using System.Diagnostics;
using System.Text;
using NTMiner.Modern;
using static NTMiner.Tests.Harness;

namespace NTMiner.Tests;

/// <summary>Session lifecycle: start, stop, retry, recovery and abnormal exit.</summary>
internal static class SessionTests
{
    internal static async Task RunAsync()
    {
        await Test("BzMiner CPU pooled demo routes through agent and stops", async () =>
        {
            await using var client = new AgentClient();
            await client.ConnectAsync();
            var options = new MiningOptions { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu, CpuThreads = 1 };
            Check((await client.SendAsync(new("start", options))).Success, "Start Bz CPU demo");
            AgentResponse result;
            var timer = Stopwatch.StartNew();
            do { await Task.Delay(100); result = await client.SendAsync(new("status")); }
            while (result.Snapshot.Accepted == 0 && timer.ElapsedMilliseconds < 5000);
            Check(result.Snapshot.Accepted > 0 && result.Snapshot.HashrateUnit == "H/s", "Bz format parsed by actual agent route");
            Check(result.Snapshot.Miner == MinerKind.BzMiner && result.Snapshot.Device == MiningDevice.Cpu && result.Snapshot.CpuThreads == 1, "IPC describes the active task");
            int pid = result.Snapshot.ProcessId!.Value;
            await client.SendAsync(new("stop"));
            Check(!Alive(pid), "Bz demo stopped");
        });

        await Test("BzMiner CPU offline demo has hashrate without pool shares", async () =>
        {
            await using var client = new AgentClient();
            await client.ConnectAsync();
            var options = new MiningOptions { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu, CpuThreads = 1, BenchmarkMode = true };
            Check((await client.SendAsync(new("start", options))).Success, "Start offline demo");
            AgentResponse result;
            var timer = Stopwatch.StartNew();
            do { await Task.Delay(100); result = await client.SendAsync(new("status")); }
            while (result.Snapshot.Hashrate is null && timer.ElapsedMilliseconds < 5000);
            Check(result.Snapshot.Hashrate is not null && result.Snapshot.BenchmarkMode, "Benchmark telemetry");
            Check(result.Snapshot.Accepted == 0 && result.Snapshot.Rejected == 0 && result.Snapshot.State == MiningState.Running, "Do not present bench as accepted pool work");
        });

        await Test("BzMiner insufficient memory stops without retry", async () =>
        {
            await using var session = new MiningSession(new BzTestAdapter(FakeMiner, "--low-memory"), () => new WindowsJob());
            await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu });
            await Wait(() => session.Snapshot().State == MiningState.Failed);
            Check(session.Snapshot().Restarts == 0 && session.Snapshot().Error!.Contains("内存不足"), "Fatal low-memory device skip is reported correctly");
        });

        await Test("status distinguishes zero, fresh positive, stale and failed hashrate", () =>
        {
            var now = DateTimeOffset.Now;
            var snapshot = new MiningSnapshot(MiningState.Running, false, 1, now, 0, "GH/s", now, 0, 0, 0, null, null, [], MinerKind.BzMiner, MiningDevice.IntegratedGpu, BenchmarkMode: true);
            Check(!MiningDisplay.Positive(snapshot, now) && MiningDisplay.StateText(snapshot, now).Contains("尚无有效算力"), "Process liveness and zero speed are not success");
            snapshot = snapshot with { Hashrate = 27.58 };
            Check(MiningDisplay.Positive(snapshot, now) && MiningDisplay.StateText(snapshot, now).Contains("已产生算力"), "Fresh positive speed proves computation only");
            Check(MiningDisplay.Evidence(snapshot, now).Contains("不代表矿池"), "Offline computation is not accepted shares");
            snapshot = snapshot with { LastSpeedAt = now.AddSeconds(-31) };
            Check(!MiningDisplay.Fresh(snapshot, now) && MiningDisplay.Evidence(snapshot, now).Contains("30 秒"), "Stale speed is hidden");
            snapshot = snapshot with { LastSpeedAt = now, State = MiningState.Failed, Error = "内存不足" };
            Check(!MiningDisplay.Fresh(snapshot, now) && MiningDisplay.StateText(snapshot, now) == "任务失败", "Never present cached speed as successful after failure");
            return Task.CompletedTask;
        });

        await Test("igpu offline telemetry and device identity survive IPC and stop", async () =>
        {
            await using var client = new AgentClient();
            await client.ConnectAsync();
            Check((await client.SendAsync(new("start", new() { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu, BenchmarkMode = true }))).Success, "Start igpu demo");
            AgentResponse result;
            var timer = Stopwatch.StartNew();
            do { await Task.Delay(100); result = await client.SendAsync(new("status")); }
            while ((result.Snapshot.Hashrate is null || result.Snapshot.DeviceNames is not { Length: > 0 }) && timer.ElapsedMilliseconds < 5000);
            Check(result.Snapshot.Device == MiningDevice.IntegratedGpu && result.Snapshot.DeviceNames!.Single().Contains("演示核显"),
                "Do not fabricate actual hardware in demo: " + string.Join(", ", result.Snapshot.DeviceNames ?? []));
            Check(MiningDisplay.Positive(result.Snapshot, DateTimeOffset.Now) && result.Snapshot.State == MiningState.Running && result.Snapshot.Accepted == 0, "Offline positive speed is not mining/share success");
            int pid = result.Snapshot.ProcessId!.Value;
            await client.SendAsync(new("stop")); Check(!Alive(pid), "igpu demo process stopped");
        });

        await Test("igpu low-memory failure stops and clears speed without retry", async () =>
        {
            await using var session = new MiningSession(new BzTestAdapter(FakeMiner, "--low-memory"), () => new WindowsJob());
            await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu, BenchmarkMode = true });
            await Wait(() => session.Snapshot().State == MiningState.Failed);
            var snapshot = session.Snapshot();
            Check(snapshot.Restarts == 0 && snapshot.ProcessId is null && snapshot.Hashrate is null && snapshot.Error!.Contains("内存不足"), "No silent zero-rate loop or recovery");
        });

        await Test("offline self-test never automatically restarts even over IPC", async () =>
        {
            await using var session = new MiningSession(new BzTestAdapter(FakeMiner, "--fail"), () => new WindowsJob());
            await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Gpu, BenchmarkMode = true, AutoRecover = true });
            await Wait(() => session.Snapshot().State == MiningState.Failed);
            Check(session.Snapshot().Restarts == 0, "Agent enforces offline no-restart independently of UI");
        });

        await Test("start, accepted shares, duplicate start, stop and restart", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().Accepted > 0);
            var pid = session.Snapshot().ProcessId!.Value;
            Check(session.Snapshot().State == MiningState.Mining, "Accepted shares advance state");
            await Throws<InvalidOperationException>(() => session.StartAsync(new()));
            Check(session.Snapshot().ProcessId == pid, "Duplicate start must not replace miner");
            await session.StopAsync();
            Check(!Alive(pid) && session.Snapshot().State == MiningState.Idle, "Stop kills miner");
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().Accepted > 0);
        });

        await Test("stop kills descendant processes", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--spawn-child"), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().Logs.Any(l => l.Message.StartsWith("CHILD_PID=")));
            var line = session.Snapshot().Logs.Single(l => l.Message.StartsWith("CHILD_PID=")).Message;
            int child = int.Parse(line.Split('=')[1]);
            Check(Alive(child), "Child started");
            await session.StopAsync();
            await Wait(() => !Alive(child));
        });

        await Test("a live process without shares is not reported as mining", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--no-shares"), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().ProcessId is not null);
            Check(session.Snapshot().State == MiningState.Running && session.Snapshot().Accepted == 0, "Only process liveness confirmed");
        });

        await Test("oversized miner output terminates the task safely", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--long-line"), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().State == MiningState.Failed);
            Check(session.Snapshot().Restarts == 0 && session.Snapshot().Error!.Contains("日志"), "Malformed output is terminal");
        });

        await Test("disabled recovery never restarts an exited process", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--fail"), () => new WindowsJob());
            await session.StartAsync(new() { AutoRecover = false });
            await Wait(() => session.Snapshot().State == MiningState.Failed);
            Check(session.Snapshot().Restarts == 0, "User recovery preference respected");
        });

        await Test("retry budget stops after three restarts", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--fail"), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().State == MiningState.Failed, 22000);
            Check(session.Snapshot().Restarts == 3 && session.Snapshot().ProcessId is null, "Retry budget exhausted");
        });

        await Test("stop cancels recovery delay", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--fail"), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().State == MiningState.Recovering);
            var timer = Stopwatch.StartNew();
            await session.StopAsync();
            Check(timer.ElapsedMilliseconds < 1500 && session.Snapshot().State == MiningState.Idle, "Recovery canceled promptly");
        });

        await Test("fatal configuration error is never retried", async () =>
        {
            await using var session = new MiningSession(new TestAdapter(FakeMiner, "--fatal"), () => new WindowsJob());
            await session.StartAsync(new());
            await Wait(() => session.Snapshot().State == MiningState.Failed);
            Check(session.Snapshot().Restarts == 0, "Do not retry fatal errors");
        });
    }
}
