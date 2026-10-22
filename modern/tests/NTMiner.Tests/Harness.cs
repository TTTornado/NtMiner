using System.Diagnostics;
using NTMiner.Modern;

namespace NTMiner.Tests;

/// <summary>
/// Shared assertions and waiting helpers for the acceptance program.
/// Test files import them with using static, so call sites read the same as before the split.
/// </summary>
public static class Harness
{
    public static int Failures { get; private set; }
    public static int Passed { get; private set; }

    /// <summary>Path to the simulated miner; the test project copies it next to the output.</summary>
    public static string FakeMiner { get; } = Path.Combine(AppContext.BaseDirectory, "NTMiner.FakeMiner.exe");

    public static async Task Test(string name, Func<Task> body)
    {
        try { await body(); Console.WriteLine("PASS " + name); Passed++; }
        catch (Exception ex) { Console.WriteLine("FAIL " + name + " : " + ex); Failures++; }
    }

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task Wait(Func<bool> check, int milliseconds = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!check())
        {
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    public static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>SRBMiner adapter with extra fixed arguments, used to build failure scenarios.</summary>
public sealed class TestAdapter(string executable, params string[] arguments) : IMinerAdapter
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

/// <summary>BzMiner adapter with one extra fixed argument, used to build failure scenarios.</summary>
public sealed class BzTestAdapter(string executable, string argument) : IMinerAdapter
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