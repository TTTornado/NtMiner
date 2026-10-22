using System.Diagnostics;
using System.Text;
using NTMiner.Modern;
using static NTMiner.Tests.Harness;

namespace NTMiner.Tests;

/// <summary>Local IPC: message framing, agent connection and process cleanup.</summary>
internal static class IpcTests
{
    internal static async Task RunAsync()
    {
        await Test("bounded message framing rejects malformed input", async () =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345\n"));
            using var reader = new StreamReader(stream);
            await Throws<InvalidDataException>(async () => await BoundedLines.ReadAsync(reader, 4, CancellationToken.None));
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
    }
}
