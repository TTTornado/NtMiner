using System.Diagnostics;

namespace NTMiner.Modern;

public interface IMinerAdapter
{
    ProcessStartInfo CreateStartInfo(MiningOptions options);
    MinerObservation Parse(string line);
}

public interface IProcessContainment : IDisposable
{
    void Attach(Process process);
}

/// <summary>Single owner of a mining session. All user commands are serialized.</summary>
public sealed class MiningSession(IMinerAdapter adapter, Func<IProcessContainment> containmentFactory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<LogEntry> _logs = new();
    private readonly List<string> _deviceNames = new();
    private readonly SessionStateMachine _stateMachine = new();
    private readonly RetryPolicy _retry = new();
    private CancellationTokenSource? _sessionCts;
    private Task? _supervisor;
    private Process? _process;
    private MiningOptions _options = new();
    private long _sequence;
    private int _accepted, _rejected, _restarts;
    private int _attemptAcceptedBase, _attemptRejectedBase;
    private double? _hashrate;
    private string? _unit, _error, _sha256;
    private DateTimeOffset? _startedAt, _lastSpeedAt;

    public MiningSnapshot Snapshot(long after = 0)
    {
        lock (_sync)
            return new(_stateMachine.Current, _options.DemoMode, _process?.Id, _startedAt,
                _hashrate, _unit, _lastSpeedAt, _accepted, _rejected, _restarts, _sha256, _error,
                _logs.Where(x => x.Sequence > after).Take(100).ToArray(), _options.Miner, _options.Device, _options.CpuThreads, _options.BenchmarkMode, _deviceNames.ToArray());
    }

    public async Task StartAsync(MiningOptions options, CancellationToken token = default)
    {
        await _commands.WaitAsync(token);
        try
        {
            if (_supervisor is { IsCompleted: false }) throw new InvalidOperationException("任务已在运行，请先停止。");
            var errors = options.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            // Resolve the adapter before creating a supervisor: bad paths are not retried.
            var startInfo = adapter.CreateStartInfo(options);
            string sha;
            await using (var file = File.OpenRead(startInfo.FileName))
                sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, token));
            _sessionCts?.Dispose();
            _sessionCts = new();
            lock (_sync)
            {
                _options = options;
                _error = null;
                _accepted = _rejected = _restarts = 0;
                _hashrate = null; _unit = null; _lastSpeedAt = null;
                _deviceNames.Clear();
                _startedAt = DateTimeOffset.Now;
                _sha256 = sha;
                // 从上一次会话的终态（Failed）重新开始需要显式复位。
                _stateMachine.Reset();
                _stateMachine.Transition(MiningState.Starting);
            }
            var device = options.Device == MiningDevice.Cpu ? $"CPU / {options.CpuThreads} 线程" : MiningDisplay.DeviceName(options.Device);
            Log("info", options.DemoMode ? $"演示模式：{options.Miner} / {device}，模拟日志和份额，不连接矿池。"
                : options.BenchmarkMode ? $"启动 PRL / BzMiner / {device} 离线自测，无矿池、无收益，完成验证后请点击停止。"
                : $"启动 PRL / {options.Miner} / {device}，等待矿池接受份额。");
            Log("info", $"内核 SHA-256：{sha}（文件标识，不代表来源认证）");
            _supervisor = SuperviseAsync(startInfo, _sessionCts.Token);
        }
        finally { _commands.Release(); }
    }

    public async Task StopAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (_supervisor is null || _supervisor.IsCompleted)
            {
                lock (_sync) { _stateMachine.Reset(); _error = null; }
                return;
            }
            lock (_sync) _stateMachine.TryTransition(MiningState.Stopping);
            _sessionCts!.Cancel();
            Process? process;
            lock (_sync) process = _process;
            TryKill(process);
            await _supervisor;
            lock (_sync)
            {
                _stateMachine.Reset();
                _error = null; _hashrate = null; _lastSpeedAt = null;
            }
            Log("info", "任务已停止，内核进程已回收。");
        }
        finally { _commands.Release(); }
    }

    private async Task SuperviseAsync(ProcessStartInfo startInfo, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var containment = containmentFactory();
                using var process = new Process { StartInfo = startInfo };
                try
                {
                    // Refuse to execute a file that changed since user start.
                    await using (var file = File.OpenRead(startInfo.FileName))
                    {
                        var sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, token));
                        if (sha != _sha256) throw new InvalidOperationException("内核文件已变化，请停止并重新核对文件。");
                    }
                    if (!process.Start()) throw new InvalidOperationException("内核进程启动失败。");
                    try { containment.Attach(process); }
                    catch { TryKill(process); throw; }
                    lock (_sync)
                    {
                        _process = process; _stateMachine.TryTransition(MiningState.Running);
                        _hashrate = null; _lastSpeedAt = null;
                        _deviceNames.Clear();
                        _attemptAcceptedBase = _accepted; _attemptRejectedBase = _rejected;
                    }
                    Log("info", _options.BenchmarkMode ? $"离线自测进程 {process.Id} 已启动，不统计矿池份额。"
                        : $"内核进程 {process.Id} 已启动；进程运行不代表已获得有效份额。");
                    var stdout = ReadOutputAsync(process.StandardOutput, token);
                    var stderr = ReadOutputAsync(process.StandardError, token);
                    try { await process.WaitForExitAsync(token); }
                    finally { if (token.IsCancellationRequested) TryKill(process); }
                    // Closing containment also terminates any remaining descendants.
                    containment.Dispose();
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3), token);
                    lock (_sync) { _process = null; _hashrate = null; _lastSpeedAt = null; }
                    string? fatal;
                    lock (_sync) fatal = _error;
                    if (fatal is not null) throw new InvalidOperationException(fatal);
                    if (!_retry.ShouldRetry(_options, attempt, fatal: false))
                        throw new InvalidOperationException($"内核已退出（代码 {process.ExitCode}）。请查看日志和配置。");
                    var delay = _retry.DelayFor(attempt);
                    var restartNumber = RetryPolicy.NextRestartNumber(attempt);
                    lock (_sync) { _stateMachine.TryTransition(MiningState.Recovering); _restarts = restartNumber; }
                    Log("warn", $"内核退出（{process.ExitCode}），{delay.TotalSeconds} 秒后重试 {restartNumber}/{_retry.MaxRestarts}。");
                    await Task.Delay(delay, token);
                    lock (_sync) _stateMachine.TryTransition(MiningState.Starting);
                }
                finally
                {
                    TryKill(process);
                    lock (_sync) _process = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_sync)
            {
                // 失败必须是终态：迟到的日志观察值不得把 Failed 改回 Mining。
                _stateMachine.Fault();
                _error = ex.Message; _hashrate = null; _lastSpeedAt = null;
            }
            Log("error", ex.Message);
        }
    }

    private async Task ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (await BoundedLines.ReadAsync(reader, 8192, token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Log("miner", line);
                var observation = adapter.Parse(line);
                lock (_sync)
                {
                    if (observation.Hashrate is { } speed && double.IsFinite(speed) && speed >= 0)
                    { _hashrate = speed; _unit = observation.Unit; _lastSpeedAt = DateTimeOffset.Now; }
                    if (observation.DeviceName is { Length: > 0 } name && _deviceNames.Count < 32 && !_deviceNames.Contains(name))
                        _deviceNames.Add(name[..Math.Min(name.Length, 160)]);
                    if (!_options.BenchmarkMode)
                    {
                        int previousAccepted = _accepted;
                        if (observation.Accepted) _accepted++;
                        if (observation.Rejected) _rejected++;
                        if (observation.AcceptedTotal is { } acceptedTotal)
                            _accepted = Math.Max(_accepted, (int)Math.Min(int.MaxValue, (long)_attemptAcceptedBase + acceptedTotal));
                        if (observation.RejectedTotal is { } rejectedTotal)
                            _rejected = Math.Max(_rejected, (int)Math.Min(int.MaxValue, (long)_attemptRejectedBase + rejectedTotal));
                        if (_accepted > previousAccepted) _stateMachine.TryTransition(MiningState.Mining);
                    }
                    if (observation.FatalError is not null) { _error = observation.FatalError; _hashrate = null; _lastSpeedAt = null; }
                }
                if (observation.FatalError is not null)
                {
                    Process? process;
                    lock (_sync) process = _process;
                    TryKill(process);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Process? process;
            lock (_sync) { _error = "读取内核日志失败：" + ex.Message; process = _process; }
            Log("error", "读取内核日志失败：" + ex.Message);
            TryKill(process);
        }
    }

    private void Log(string level, string text)
    {
        text = text.Replace('\0', ' ');
        if (text.Length > 2000) text = text[..2000] + "…";
        lock (_sync)
        {
            _logs.Enqueue(new(++_sequence, DateTimeOffset.Now, level, text));
            while (_logs.Count > 400) _logs.Dequeue();
        }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sessionCts?.Dispose();
        _commands.Dispose();
    }
}

public static class BoundedLines
{
    public static async Task<string?> ReadAsync(StreamReader reader, int maxLength, CancellationToken token)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= maxLength) throw new InvalidDataException("消息或日志行超过长度限制。");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : result.ToString().TrimEnd('\r');
    }
}
