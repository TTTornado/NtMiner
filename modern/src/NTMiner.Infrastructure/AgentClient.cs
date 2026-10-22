using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace NTMiner.Modern;

public sealed class AgentClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _agent;
    private WindowsJob? _agentJob;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public async Task ConnectAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NTMiner.Agent.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add("--pipe"); info.ArgumentList.Add(id);
        _agentJob = new WindowsJob();
        _agent = Process.Start(info) ?? throw new IOException("后台服务启动失败。");
        try
        {
            _agentJob.Attach(_agent);
            _pipe = new NamedPipeClientStream(".", "NtMiner.Modern." + id, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await _pipe.ConnectAsync(timeout.Token);
            _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
        }
        catch { _agentJob.Dispose(); throw; }
    }

    public async Task<AgentResponse> SendAsync(AgentRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            if (_writer is null || _reader is null) throw new IOException("后台连接不可用。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await PipeProtocol.WriteAsync(_writer, request, timeout.Token);
            return await PipeProtocol.ReadAsync<AgentResponse>(_reader, timeout.Token)
                ?? throw new IOException("后台连接已断开。");
        }
        catch
        {
            // Timed-out replies cannot be matched to later requests: retire the connection.
            _pipe?.Dispose();
            _agentJob?.Dispose();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_pipe?.IsConnected == true) await SendAsync(new("shutdown")); }
        catch (Exception) { /* Job disposal below is the final cleanup barrier. */ }
        _agentJob?.Dispose();
        _pipe?.Dispose();
        try { _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _reader?.Dispose();
        if (_agent is not null)
        {
            try { await _agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { if (!_agent.HasExited) _agent.Kill(true); }
            _agent.Dispose();
        }
        _gate.Dispose();
    }
}
