using System.Diagnostics;
using System.Text.Json;
using NTMiner.Modern;

// Explicit opt-in hardware check, never invoked by the normal test suite.
if (args.Length != 2) { Console.Error.WriteLine("Usage: NTMiner.CpuProbe <official bzminer.exe> <output directory>"); return 2; }
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var options = new MiningOptions
{
    DemoMode = false, Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu,
    CpuThreads = 1, BenchmarkMode = true, AutoRecover = false, KernelPath = Path.GetFullPath(args[0])
};
var errors = options.Validate();
if (errors.Count > 0) { Console.Error.WriteLine(string.Join("\n", errors)); return 2; }
var adapter = new BzMinerAdapter("", output);
var preflight = adapter.CreateStartInfo(options);
preflight.ArgumentList.Add("--print-config");
using (var job = new WindowsJob())
using (var process = new Process { StartInfo = preflight })
{
    process.Start(); job.Attach(process);
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    catch { process.Kill(true); throw; }
    var configText = await stdout;
    await File.WriteAllTextAsync(Path.Combine(output, "resolved-config.json"), configText);
    await File.WriteAllTextAsync(Path.Combine(output, "preflight-stderr.txt"), await stderr);
    if (process.ExitCode != 0) { Console.Error.WriteLine("BzMiner preflight failed."); return 1; }
    using var config = JsonDocument.Parse(configText);
    var root = config.RootElement;
    var devices = root.GetProperty("device_types");
    if (!devices.GetProperty("cpu").GetBoolean() || devices.GetProperty("nvidia").GetBoolean()
        || devices.GetProperty("amd").GetBoolean() || devices.GetProperty("intel").GetBoolean()
        || root.GetProperty("cpu_threads").GetInt32() != 1 || root.GetProperty("http_enabled").GetBoolean()
        || !root.GetProperty("disable_udp").GetBoolean()
        || root.GetProperty("backends").GetProperty("cuda").GetBoolean()
        || root.GetProperty("backends").GetProperty("opencl").GetBoolean()
        || root.GetProperty("backends").GetProperty("metal").GetBoolean()
        || root.GetProperty("pools").EnumerateArray().Any(p => p.GetProperty("url").GetString() != "benchmark"))
    { Console.Error.WriteLine("Resolved configuration does not match CPU-only constraints; not starting."); return 1; }
}

await using var session = new MiningSession(adapter, () => new WindowsJob());
await session.StartAsync(options);
var timer = Stopwatch.StartNew();
var entries = new List<LogEntry>();
long sequence = 0;
MiningSnapshot snapshot;
do
{
    await Task.Delay(500);
    snapshot = session.Snapshot(sequence);
    entries.AddRange(snapshot.Logs);
    if (snapshot.Logs.Length > 0) sequence = snapshot.Logs[^1].Sequence;
    foreach (var line in snapshot.Logs) Console.WriteLine(line.Message);
    await File.WriteAllLinesAsync(Path.Combine(output, "cpu-probe.log"), entries.Select(l => $"{l.Time:O} [{l.Level}] {l.Message}"));
    if (snapshot.State == MiningState.Failed) break;
} while (timer.Elapsed < TimeSpan.FromSeconds(30));
var beforeStop = session.Snapshot(sequence);
await session.StopAsync();
var afterStop = session.Snapshot(sequence);
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
{
    DurationSeconds = timer.Elapsed.TotalSeconds, CpuThreads = 1, Offline = true,
    BeforeStop = beforeStop, AfterStop = afterStop
}, new JsonSerializerOptions { WriteIndented = true }));
bool passed = beforeStop.State == MiningState.Running && beforeStop.Hashrate is > 0
    && beforeStop.Accepted == 0 && afterStop.State == MiningState.Idle && afterStop.ProcessId is null;
Console.WriteLine(passed ? "PASS actual CPU-only PRL offline run and process cleanup." : "CPU probe incomplete: inspect cpu-probe.log and result.json.");
return passed ? 0 : 1;
