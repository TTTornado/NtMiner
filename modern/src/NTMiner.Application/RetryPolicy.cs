namespace NTMiner.Modern;

/// <summary>
/// 内核异常退出后的重试规则。纯计算，不涉及进程、不等待、不读时钟，
/// 因此可以在验收中直接断言，而不必真的启动进程等待退避时间。
/// </summary>
public sealed class RetryPolicy(int maxRestarts = 3)
{
    public int MaxRestarts { get; } = maxRestarts;

    /// <summary>
    /// 是否应该在第 <paramref name="attempt"/> 次重试（从 0 开始）后继续重启。
    /// 离线自测与关闭自动恢复时不重试；配置类错误从不重试。
    /// </summary>
    public bool ShouldRetry(MiningOptions options, int attempt, bool fatal) =>
        !fatal && !options.BenchmarkMode && options.AutoRecover && attempt < MaxRestarts;

    /// <summary>等待时间上限。正常配置只会用到 2、4、8 秒，上限用于防止异常入参产生荒谬的延迟。</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>第 <paramref name="attempt"/> 次重试前的等待时间：2、4、8 秒（指数退避），不超过 <see cref="MaxDelay"/>。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        // 限制指数上界，避免调用方传入大数值时溢出。
        var seconds = Math.Pow(2, Math.Min(attempt, 20) + 1);
        var delay = TimeSpan.FromSeconds(seconds);
        return delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>第 <paramref name="attempt"/> 次重试对应的人类可读序号，从 1 开始。</summary>
    public static int NextRestartNumber(int attempt) => attempt + 1;
}
