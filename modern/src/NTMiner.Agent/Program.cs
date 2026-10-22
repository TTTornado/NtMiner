using System.IO.Pipes;
using System.Text;
using NTMiner.Modern;

if (args.Length != 2 || args[0] != "--pipe" || !Guid.TryParseExact(args[1], "N", out _)) return 2;
var pipeName = "NtMiner.Modern." + args[1];
await using var session = new MiningSession(
    new MinerAdapterRouter(Path.Combine(AppContext.BaseDirectory, "NTMiner.FakeMiner.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtMiner.Modern", "BzMiner")), () => new WindowsJob());
try
{
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        await pipe.WaitForConnectionAsync(connectTimeout.Token);
    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
    while (pipe.IsConnected)
    {
        // If the UI hangs or disappears, stop mining rather than leave an orphan process.
        using var lease = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = await PipeProtocol.ReadAsync<AgentRequest>(reader, lease.Token);
        if (request is null) break;
        string? error = null;
        try
        {
            switch (request.Command)
            {
                case "status": break;
                case "start": await session.StartAsync(request.Options ?? throw new ArgumentException("缺少配置。")); break;
                case "stop": await session.StopAsync(); break;
                case "shutdown": await session.StopAsync(); break;
                default: throw new ArgumentException("不支持的操作。");
            }
        }
        catch (Exception ex) { error = ex.Message; }
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await PipeProtocol.WriteAsync(writer, new AgentResponse(error is null, error, session.Snapshot(request.AfterSequence)), sendTimeout.Token);
        if (request.Command == "shutdown") break;
    }
}
catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException)
{ Console.Error.WriteLine("控制连接已断开，结束任务。"); }
return 0;
