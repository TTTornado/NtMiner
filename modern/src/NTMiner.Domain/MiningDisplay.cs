namespace NTMiner.Modern;

public static class MiningDisplay
{
    public static string DeviceName(MiningDevice device) => device switch
    { MiningDevice.Cpu => "CPU", MiningDevice.IntegratedGpu => "仅核显", _ => "全部 GPU" };
    public static string MinerName(MinerKind miner) => miner == MinerKind.BzMiner ? "BzMiner" : "SRBMiner";
    public static bool Fresh(MiningSnapshot snapshot, DateTimeOffset now) =>
        snapshot.Error is null && (snapshot.State is MiningState.Running or MiningState.Mining)
        && snapshot.LastSpeedAt is { } time && time <= now && now - time < TimeSpan.FromSeconds(30)
        && snapshot.Hashrate is { } rate && double.IsFinite(rate) && rate >= 0;
    public static bool Positive(MiningSnapshot snapshot, DateTimeOffset now) => Fresh(snapshot, now) && snapshot.Hashrate > 0;
    public static string StateText(MiningSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Error is not null) return "任务失败";
        string state = snapshot.State switch
        {
            MiningState.Idle => "待启动", MiningState.Starting => "正在启动",
            MiningState.Running when snapshot.BenchmarkMode => Positive(snapshot, now) ? "已产生算力" : "尚无有效算力",
            MiningState.Running => Positive(snapshot, now) ? "已产生算力 · 等待份额" : "初始化 / 等待算力与份额",
            MiningState.Mining => "已收到接受份额", MiningState.Recovering => "等待恢复",
            MiningState.Stopping => "正在停止", _ => "任务失败"
        };
        return snapshot.State == MiningState.Idle ? state :
            (snapshot.DemoMode ? "演示 · " : snapshot.BenchmarkMode ? "离线自测 · " : "") + state;
    }
    public static string Evidence(MiningSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Error is not null) return snapshot.Error;
        if (snapshot.State is not (MiningState.Running or MiningState.Mining))
            return "任务运行后显示内核报告的设备和算力。";
        if (snapshot.LastSpeedAt is { } time && now - time >= TimeSpan.FromSeconds(30))
            return "算力超过 30 秒未更新，已隐藏旧数值；请检查日志，尚不能确认当前计算正常。";
        if (!Positive(snapshot, now)) return "内核进程已启动，但尚无正算力；可能正在初始化，请查看日志。";
        if (snapshot.DemoMode) return "当前为模拟设备与模拟算力，不能证明真实硬件可用。";
        return snapshot.BenchmarkMode ? "已检测到正算力，仅验证离线计算，不代表矿池接受份额或收益。"
            : "已检测到内核报告的正算力，有效份额与最终结算以矿池为准。";
    }
}
