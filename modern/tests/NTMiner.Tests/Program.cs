using NTMiner.Modern;
using NTMiner.Tests;
using static NTMiner.Tests.Harness;

// Standalone acceptance program: no test framework package, exit code carries the result.
// One file per topic: ConfigurationTests, ProfileTests, SessionTests, IpcTests, PolicyAndHardwareTests.

// Child-process entry: exit without disposing the client, so OS handle cleanup must kill the
// miner and its job. Used by "abrupt client exit terminates the miner via job handles".
if (args.Length == 2 && args[0] == "--abrupt-client")
{
    var client = new AgentClient();
    await client.ConnectAsync();
    await client.SendAsync(new("start", new()));
    AgentResponse reply;
    do { await Task.Delay(100); reply = await client.SendAsync(new("status")); } while (reply.Snapshot.ProcessId is null);
    await File.WriteAllTextAsync(args[1], reply.Snapshot.ProcessId.Value.ToString());
    Environment.Exit(0);
}

await ConfigurationTests.RunAsync();
await ProfileTests.RunAsync();
await SessionTests.RunAsync();
await IpcTests.RunAsync();
await PolicyAndHardwareTests.RunAsync();

Console.WriteLine($"\n{Passed} passed; {Failures} failed. No real miner was downloaded or started.");
return Failures == 0 ? 0 : 1;