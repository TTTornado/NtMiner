using System.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--child")) { await Task.Delay(Timeout.Infinite); return 0; }
if (args.Contains("--bz-format"))
{
    string device = args.Contains("--demo-igpu") ? "演示核显（模拟设备）" : args.Contains("--demo-cpu") ? "演示 CPU（模拟设备）" : "演示 GPU（模拟设备）";
    Console.WriteLine($"device: [0] {device} (0000:00:02) [opencl]");
}
if (args.Contains("--spawn-child"))
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--child" } });
    Console.WriteLine($"CHILD_PID={child!.Id}");
}
if (args.Contains("--fatal")) { Console.WriteLine("Invalid wallet"); await Task.Delay(Timeout.Infinite); }
if (args.Contains("--low-memory")) { Console.WriteLine("pearl not enough host memory for this device"); await Task.Delay(Timeout.Infinite); }
if (args.Contains("--bz-summary"))
{
    Console.WriteLine("| # | cfg | tbs | a/r/p | eff | pool hr | miner hr | status | pool |");
    Console.WriteLine("| smry | | -- | 7/1/0 | -- | -- | 24.23gh | connected: yes | diff 281.47T |");
    Console.WriteLine("| smry | | -- | 7/1/0 | -- | -- | 24.23gh | connected: yes | diff 281.47T |");
    await Task.Delay(Timeout.Infinite);
}
if (args.Contains("--long-line")) { Console.WriteLine(new string('X', 10000)); await Task.Delay(Timeout.Infinite); }
if (args.Contains("--no-shares")) { Console.WriteLine("Waiting for simulated work"); await Task.Delay(Timeout.Infinite); }
if (args.Contains("--fail")) { Console.Error.WriteLine("Simulated transient exit"); return 7; }
Console.WriteLine("DEMO ONLY - no network connection or GPU work");
var random = new Random(42);
for (var i = 0; ; i++)
{
    if (args.Contains("--bz-format"))
    {
        Console.WriteLine(FormattableString.Invariant($"[pearl] Hashrate: {100.0 + random.NextDouble() * 5:F2} H/s"));
        if (!args.Contains("--bench")) Console.WriteLine($"[pearl] Share #{i + 1} {(i % 9 == 8 ? "rejected" : "accepted")} (demo)");
    }
    else
    {
        Console.WriteLine(FormattableString.Invariant($"Total hashrate: {100.0 + random.NextDouble() * 5:F2} MH/s"));
        Console.WriteLine(i % 9 == 8 ? "Result rejected (demo)" : "Result accepted (demo)");
    }
    await Task.Delay(1000);
}
