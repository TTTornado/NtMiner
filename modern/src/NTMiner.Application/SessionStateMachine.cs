namespace NTMiner.Modern;

/// <summary>
/// 挖矿会话状态机。所有状态转换只在这里发生，且必须是 <see cref="Allowed"/> 中的合法边，
/// 避免状态散落在各处赋值后被后到的消息"倒退"（例如 Failed 又回到 Mining）。
/// 本类型不含锁：调用方（MiningSession）已在 <c>_sync</c> 下调用。
/// </summary>
public sealed class SessionStateMachine
{
    /// <summary>Failed 是终态：只有显式重新启动才能离开，运行中的观察值不得覆盖它。</summary>
    private static readonly HashSet<(MiningState From, MiningState To)> Allowed = new()
    {
        (MiningState.Idle, MiningState.Starting),
        (MiningState.Idle, MiningState.Idle),
        (MiningState.Idle, MiningState.Stopping),
        (MiningState.Starting, MiningState.Running),
        (MiningState.Starting, MiningState.Stopping),
        (MiningState.Starting, MiningState.Failed),
        (MiningState.Running, MiningState.Mining),
        (MiningState.Running, MiningState.Recovering),
        (MiningState.Running, MiningState.Stopping),
        (MiningState.Running, MiningState.Failed),
        (MiningState.Mining, MiningState.Mining),
        (MiningState.Mining, MiningState.Recovering),
        (MiningState.Mining, MiningState.Stopping),
        (MiningState.Mining, MiningState.Failed),
        (MiningState.Recovering, MiningState.Starting),
        (MiningState.Recovering, MiningState.Stopping),
        (MiningState.Recovering, MiningState.Failed),
        (MiningState.Stopping, MiningState.Idle),
        (MiningState.Stopping, MiningState.Failed),
        (MiningState.Failed, MiningState.Starting),
        (MiningState.Failed, MiningState.Idle),
        // 重复调用停止是一个幂等操作，不应因此报错。
        (MiningState.Stopping, MiningState.Stopping)
    };

    public MiningState Current { get; private set; } = MiningState.Idle;

    public bool IsTerminal => Current == MiningState.Failed;

    /// <summary>尝试转换；非法转换返回 false 且不改变状态，供调用方决定是否记录。</summary>
    public bool TryTransition(MiningState to)
    {
        if (!Allowed.Contains((Current, to))) return false;
        Current = to;
        return true;
    }

    /// <summary>转换到 <paramref name="to"/>；非法转换抛出，用于表达"这里不应发生"。</summary>
    public void Transition(MiningState to)
    {
        if (TryTransition(to)) return;
        throw new InvalidOperationException($"非法状态转换：{Current} → {to}。");
    }

    /// <summary>新会话开始时重置到 Idle。</summary>
    public void Reset() => Current = MiningState.Idle;

    /// <summary>
    /// 会话主管体抛出异常时的去向。用户已经请求停止属于正常收尾，不应显示为失败；
    /// 其余情况一律进入 Failed 并保持为终态。
    /// </summary>
    public void Fault()
    {
        if (Current == MiningState.Stopping) { Current = MiningState.Idle; return; }
        if (Current == MiningState.Failed) return;
        Current = MiningState.Failed;
    }

    public static bool IsAllowed(MiningState from, MiningState to) => Allowed.Contains((from, to));
}
