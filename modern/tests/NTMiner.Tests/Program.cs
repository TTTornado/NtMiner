using System.Diagnostics;
using System.Text;
using NTMiner.Modern;

var fake = Path.Combine(AppContext.BaseDirectory, "NTMiner.FakeMiner.exe");
if (args.Length == 2 && args[0] == "--abrupt-client")
{
    // Exit without disposing the client: OS handle cleanup must kill both jobs.
    var client = new AgentClient();
    await client.ConnectAsync();
    await client.SendAsync(new("start", new()));
    AgentResponse reply;
    do { await Task.Delay(100); reply = await client.SendAsync(new("status")); } while (reply.Snapshot.ProcessId is null);
    await File.WriteAllTextAsync(args[1], reply.Snapshot.ProcessId.Value.ToString());
    Environment.Exit(0);
}
var failures = 0;
var passed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + " : " + ex); failures++; }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Wait(Func<bool> check, int milliseconds = 5000)
{
    var timer = Stopwatch.StartNew();
    while (!check()) { if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException(); await Task.Delay(50); }
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
bool Alive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

await Test("configuration rejects unsafe/missing real fields", () =>
{
    var invalid = new MiningOptions { DemoMode = false, KernelPath = fake, Wallet = "--wallet evil", Pool = "http://localhost:80", Worker = "rig & calc" };
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
        var start = new SrbMinerAdapter(fake).CreateStartInfo(options);
        Check(!start.UseShellExecute && start.Arguments == "" && start.ArgumentList.Contains("pearlhash"), "No shell or raw command string");
        Check(start.ArgumentList.Contains(options.Wallet) && start.ArgumentList.Contains("pool.example.org:4444"), "Preserve explicit wallet and pool");
        Check(start.ArgumentList.Contains("--tls") && start.ArgumentList.Contains("true"), "TLS must be explicit for SRBMiner");
    }
    finally { File.Delete(executable); Directory.Delete(folder); }
    return Task.CompletedTask;
});
await Test("parser excludes GPU-only speed, summaries and unrelated messages", () =>
{
    var adapter = new SrbMinerAdapter(fake);
    Check(adapter.Parse("GPU0: 123 MH/s").Hashrate is null, "Do not mistake GPU speed for total");
    Check(!adapter.Parse("Accepted: 100 / Rejected: 0").Accepted, "Do not recount cumulative summaries");
    Check(adapter.Parse("Total hashrate: 123.45 MH/s").Hashrate == 123.45, "Parse total speed");
    Check(adapter.Parse("Result accepted").Accepted, "Accepted event");
    Check(adapter.Parse("Result rejected").Rejected, "Rejected event");
    Check(adapter.Parse("Invalid wallet").FatalError is not null, "Fatal config error");
    return Task.CompletedTask;
});
await Test("profile round-trip and future schema protection", async () =>
{
    var folder = Path.Combine(AppContext.BaseDirectory, "NTMiner-profile-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(folder, "profile.json");
    try
    {
        var store = new ProfileStore(path);
        var options = new MiningOptions { Worker = "a_01" };
        await store.SaveAsync(options);
        Check(await store.LoadAsync() == options, "Profile round-trip");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":99}");
        await Throws<InvalidDataException>(async () => await store.LoadAsync());
        Check((await File.ReadAllTextAsync(path)).Contains("99"), "Future profile preserved");
    }
    finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
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
        var adapter = new BzMinerAdapter(fake, folder);
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
        Check((options with { KernelPath = fake }).Validate().Count > 0, "Wrong miner file refused");
    }
    finally { File.Delete(executable); if (File.Exists(config)) File.Delete(config); Directory.Delete(folder); }
    return Task.CompletedTask;
});
await Test("BzMiner parser recognizes distinct events and CPU-only no-GPU startup", () =>
{
    var adapter = new BzMinerAdapter(fake, AppContext.BaseDirectory);
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
await Test("v1 profile migrates to v4 without enabling a new CPU or offline task", async () =>
{
    var folder = Path.Combine(AppContext.BaseDirectory, "profile-v1-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "profile.json");
    try
    {
        const string legacy = "{\"SchemaVersion\":1,\"Worker\":\"old_rig\",\"Wallet\":\"old_address\",\"DemoMode\":false}";
        await File.WriteAllTextAsync(path, legacy);
        var store = new ProfileStore(path);
        var migrated = await store.LoadAsync();
        Check(migrated.SchemaVersion == 4 && migrated.Miner == MinerKind.SrbMiner && migrated.Device == MiningDevice.Gpu && !migrated.BenchmarkMode, "Keep previous semantics");
        Check(migrated.Worker == "old_rig" && migrated.Wallet == "old_address" && !migrated.DemoMode, "Preserve legacy fields");
        Check(await File.ReadAllTextAsync(path) == legacy, "Migration does not automatically overwrite");
        var cpu = migrated with { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu, CpuThreads = 1, BenchmarkMode = true };
        await store.SaveAsync(cpu);
        Check(System.Text.Json.JsonSerializer.Serialize(await store.LoadAsync()) == System.Text.Json.JsonSerializer.Serialize(cpu), "CPU and benchmark settings round-trip");
    }
    finally { File.Delete(path); Directory.Delete(folder); }
});
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
await Test("repeated BzMiner summary counters are not counted twice", async () =>
{
    await using var session = new MiningSession(new BzTestAdapter(fake, "--bz-summary"), () => new WindowsJob());
    await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu });
    await Wait(() => session.Snapshot().Logs.Count(l => l.Message.Contains("| smry |")) >= 2);
    var snapshot = session.Snapshot();
    Check(snapshot.Accepted == 7 && snapshot.Rejected == 1 && snapshot.Hashrate == 24.23 && snapshot.State == MiningState.Mining, "Repeated cumulative counters stay exact");
});
await Test("BzMiner insufficient memory stops without retry", async () =>
{
    await using var session = new MiningSession(new BzTestAdapter(fake, "--low-memory"), () => new WindowsJob());
    await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu });
    await Wait(() => session.Snapshot().State == MiningState.Failed);
    Check(session.Snapshot().Restarts == 0 && session.Snapshot().Error!.Contains("内存不足"), "Fatal low-memory device skip is reported correctly");
});
await Test("GPU and igpu offline arguments exclude CPU, pools and payout", () =>
{
    var folder = Path.Combine(AppContext.BaseDirectory, "igpu-args-" + Guid.NewGuid().ToString("N"));
    try
    {
        var adapter = new BzMinerAdapter(fake, folder);
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
await Test("v2 migration preserves CPU and GPU semantics; igpu round-trips at v4", async () =>
{
    var folder = Path.Combine(AppContext.BaseDirectory, "profile-v2-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "profile.json");
    try
    {
        var store = new ProfileStore(path);
        foreach (var device in new[] { MiningDevice.Cpu, MiningDevice.Gpu })
        {
            var previous = new MiningOptions { SchemaVersion = 2, Miner = MinerKind.BzMiner, Device = device, BenchmarkMode = device == MiningDevice.Cpu, DemoMode = false, Wallet = "keep_address" };
            var serialized = System.Text.Json.JsonSerializer.Serialize(previous);
            await File.WriteAllTextAsync(path, serialized);
            Check(System.Text.Json.JsonSerializer.Serialize(await store.LoadAsync()) == System.Text.Json.JsonSerializer.Serialize(previous with { SchemaVersion = 4 }), "Migration cannot silently widen device mode");
            Check(await File.ReadAllTextAsync(path) == serialized, "Loading preserves old file until explicit save");
        }
        var igpu = new MiningOptions { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu, BenchmarkMode = true };
        await store.SaveAsync(igpu); Check(System.Text.Json.JsonSerializer.Serialize(await store.LoadAsync()) == System.Text.Json.JsonSerializer.Serialize(igpu), "igpu setting survives reopening either frontend");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":2,\"Device\":2}");
        await Throws<InvalidDataException>(async () => await store.LoadAsync());
    }
    finally { File.Delete(path); Directory.Delete(folder); }
});
await Test("actual igpu device and host-memory error are parsed precisely", () =>
{
    var adapter = new BzMinerAdapter(fake, AppContext.BaseDirectory);
    adapter.CreateStartInfo(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu });
    Check(adapter.Parse("INFO: device: [0] Intel(R) UHD Graphics 730 (0000:00:02) [opencl]").DeviceName == "Intel(R) UHD Graphics 730", "Device name comes from actual miner output");
    Check(adapter.Parse("backend cpu: disabled (configuration)").DeviceName is null, "Backend flags are not device discovery");
    var error = adapter.Parse("ERROR: pearl opencl: not enough host memory for this device: the attempt needs 1353 MB plus 384 MB working room, and the system has 1636 MB available. the device is skipped").FatalError!;
    Check(error.Contains("1737 MB") && error.Contains("1636 MB") && error.Contains("不自动重试"), "Expose required and available system memory");
    Check(adapter.Parse("No GPUs found").FatalError is not null, "Missing GPU must fail in igpu mode");
    return Task.CompletedTask;
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
    await using var session = new MiningSession(new BzTestAdapter(fake, "--low-memory"), () => new WindowsJob());
    await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu, BenchmarkMode = true });
    await Wait(() => session.Snapshot().State == MiningState.Failed);
    var snapshot = session.Snapshot();
    Check(snapshot.Restarts == 0 && snapshot.ProcessId is null && snapshot.Hashrate is null && snapshot.Error!.Contains("内存不足"), "No silent zero-rate loop or recovery");
});
await Test("offline self-test never automatically restarts even over IPC", async () =>
{
    await using var session = new MiningSession(new BzTestAdapter(fake, "--fail"), () => new WindowsJob());
    await session.StartAsync(new() { Miner = MinerKind.BzMiner, Device = MiningDevice.Gpu, BenchmarkMode = true, AutoRecover = true });
    await Wait(() => session.Snapshot().State == MiningState.Failed);
    Check(session.Snapshot().Restarts == 0, "Agent enforces offline no-restart independently of UI");
});
await Test("bounded message framing rejects malformed input", async () =>
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345\n"));
    using var reader = new StreamReader(stream);
    await Throws<InvalidDataException>(async () => await BoundedLines.ReadAsync(reader, 4, CancellationToken.None));
});
await Test("start, accepted shares, duplicate start, stop and restart", async () =>
{
    await using var session = new MiningSession(new TestAdapter(fake), () => new WindowsJob());
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
    await using var session = new MiningSession(new TestAdapter(fake, "--spawn-child"), () => new WindowsJob());
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
    await using var session = new MiningSession(new TestAdapter(fake, "--no-shares"), () => new WindowsJob());
    await session.StartAsync(new());
    await Wait(() => session.Snapshot().ProcessId is not null);
    Check(session.Snapshot().State == MiningState.Running && session.Snapshot().Accepted == 0, "Only process liveness confirmed");
});
await Test("oversized miner output terminates the task safely", async () =>
{
    await using var session = new MiningSession(new TestAdapter(fake, "--long-line"), () => new WindowsJob());
    await session.StartAsync(new());
    await Wait(() => session.Snapshot().State == MiningState.Failed);
    Check(session.Snapshot().Restarts == 0 && session.Snapshot().Error!.Contains("日志"), "Malformed output is terminal");
});
await Test("disabled recovery never restarts an exited process", async () =>
{
    await using var session = new MiningSession(new TestAdapter(fake, "--fail"), () => new WindowsJob());
    await session.StartAsync(new() { AutoRecover = false });
    await Wait(() => session.Snapshot().State == MiningState.Failed);
    Check(session.Snapshot().Restarts == 0, "User recovery preference respected");
});
await Test("retry budget stops after three restarts", async () =>
{
    await using var session = new MiningSession(new TestAdapter(fake, "--fail"), () => new WindowsJob());
    await session.StartAsync(new());
    await Wait(() => session.Snapshot().State == MiningState.Failed, 22000);
    Check(session.Snapshot().Restarts == 3 && session.Snapshot().ProcessId is null, "Retry budget exhausted");
});
await Test("stop cancels recovery delay", async () =>
{
    await using var session = new MiningSession(new TestAdapter(fake, "--fail"), () => new WindowsJob());
    await session.StartAsync(new());
    await Wait(() => session.Snapshot().State == MiningState.Recovering);
    var timer = Stopwatch.StartNew();
    await session.StopAsync();
    Check(timer.ElapsedMilliseconds < 1500 && session.Snapshot().State == MiningState.Idle, "Recovery canceled promptly");
});
await Test("fatal configuration error is never retried", async () =>
{
    await using var session = new MiningSession(new TestAdapter(fake, "--fatal"), () => new WindowsJob());
    await session.StartAsync(new());
    await Wait(() => session.Snapshot().State == MiningState.Failed);
    Check(session.Snapshot().Restarts == 0, "Do not retry fatal errors");
});
await Test("agent IPC start/status/stop/shutdown", async () =>
{
    await using var client = new AgentClient();
    await client.ConnectAsync();
    Check((await client.SendAsync(new("status"))).Snapshot.State == MiningState.Idle, "No automatic mining");
    Check((await client.SendAsync(new("start", new()))).Success, "Start via private pipe");
    AgentResponse result;
    var timer = Stopwatch.StartNew();
    do { result = await client.SendAsync(new("status")); await Task.Delay(100); }
    while (result.Snapshot.Accepted == 0 && timer.ElapsedMilliseconds < 5000);
    Check(result.Snapshot.Accepted > 0 && result.Snapshot.DemoMode, "Demo telemetry through IPC");
    int pid = result.Snapshot.ProcessId!.Value;
    Check((await client.SendAsync(new("stop"))).Snapshot.State == MiningState.Idle, "Stop via pipe");
    Check(!Alive(pid), "IPC stop terminated miner");
});
await Test("agent job cleanup on client disposal", async () =>
{
    var client = new AgentClient();
    await client.ConnectAsync();
    await client.SendAsync(new("start", new()));
    await Task.Delay(500);
    var pid = (await client.SendAsync(new("status"))).Snapshot.ProcessId!.Value;
    await client.DisposeAsync();
    await Wait(() => !Alive(pid));
});
await Test("abrupt client exit terminates the miner via job handles", async () =>
{
    var file = Path.Combine(AppContext.BaseDirectory, "crash-pid-" + Guid.NewGuid().ToString("N") + ".txt");
    using var child = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NTMiner.Tests.exe"))
    { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--abrupt-client", file } })!;
    try
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(child.ExitCode == 0 && File.Exists(file), "Helper completed IPC setup");
        int minerPid = int.Parse(await File.ReadAllTextAsync(file));
        await Wait(() => !Alive(minerPid));
    }
    finally
    {
        if (!child.HasExited) child.Kill(true);
        if (File.Exists(file)) File.Delete(file);
    }
});

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
        KernelPath = fake, BenchmarkMode = true, KernelSwitches = ["NoWatchdog", "LogDebug", "NoDuplicates"]
    };
    var start = new BzMinerAdapter(fake, Path.Combine(AppContext.BaseDirectory, "bz-switch-test")).CreateStartInfo(options);
    Check(start.ArgumentList.Contains("--no-watchdog"), "NoWatchdog reaches the command line");
    Check(start.ArgumentList.Contains("--log-level") && start.ArgumentList.Contains("debug"), "LogDebug passes its value");
    Check(start.ArgumentList.Contains("--duplicate-devices") && start.ArgumentList.Contains("0"), "NoDuplicates passes its value");
    Check(!start.ArgumentList.Contains("--internal-log"), "Unselected switches are not applied");

    // 未知名称不能进入真实启动参数。
    var unknown = options with { KernelSwitches = ["NotARealSwitch", "--wallet evil"] };
    var safe = new BzMinerAdapter(fake, Path.Combine(AppContext.BaseDirectory, "bz-switch-test")).CreateStartInfo(unknown);
    Check(!safe.ArgumentList.Contains("NotARealSwitch") && !safe.ArgumentList.Contains("--wallet evil"), "Unknown switch names are dropped");

    // 换内核后旧开关要被过滤掉，避免把 BzMiner 参数带给 SRBMiner。
    Check(MinerSwitchCatalog.Normalize(MinerKind.SrbMiner, ["NoWatchdog"]).Length == 0, "Switches are filtered per miner");
    Check(MinerSwitchCatalog.Normalize(MinerKind.BzMiner, ["NoWatchdog", "Bogus"]).SequenceEqual(["NoWatchdog"]), "Valid switches survive normalization");

    // 预览与真实参数必须一致，否则界面会误导用户。
    var preview = MinerSwitchCatalog.Preview(options, options.KernelSwitches);
    Check(preview.Contains("--no-watchdog") && preview.Contains("--log-level debug"), "Preview shows the same switches");
    return Task.CompletedTask;
});

Console.WriteLine($"\n{passed} passed; {failures} failed. No real miner was downloaded or started.");
return failures == 0 ? 0 : 1;

sealed class TestAdapter(string executable, params string[] arguments) : IMinerAdapter
{
    private readonly SrbMinerAdapter _parser = new(executable);
    public ProcessStartInfo CreateStartInfo(MiningOptions options)
    {
        var result = _parser.CreateStartInfo(options);
        foreach (var argument in arguments) result.ArgumentList.Add(argument);
        return result;
    }
    public MinerObservation Parse(string line) => _parser.Parse(line);
}

sealed class BzTestAdapter(string executable, string argument) : IMinerAdapter
{
    private readonly BzMinerAdapter _adapter = new(executable, AppContext.BaseDirectory);
    public ProcessStartInfo CreateStartInfo(MiningOptions options)
    {
        var info = _adapter.CreateStartInfo(options);
        info.ArgumentList.Add(argument);
        return info;
    }
    public MinerObservation Parse(string line) => _adapter.Parse(line);
}
