using System.Text.Json;

namespace NTMiner.Modern;

public sealed class ProfileStore(string path)
{
    public string FilePath => path;
    public static string DefaultPath => AppStoragePaths.ProfilePath;
    public async Task<MiningOptions> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("配置文件过大，请检查本地配置。");
        var options = JsonSerializer.Deserialize<MiningOptions>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("配置文件内容无效。");
        if (options.SchemaVersion == 1)
            options = options with { SchemaVersion = 5, Miner = MinerKind.SrbMiner, Device = MiningDevice.Gpu, CpuThreads = 1, BenchmarkMode = false };
        if (options.SchemaVersion == 2)
        {
            if (options.Device is not (MiningDevice.Gpu or MiningDevice.Cpu) || !Enum.IsDefined(options.Miner)
                || options.BenchmarkMode && (options.Miner != MinerKind.BzMiner || options.Device != MiningDevice.Cpu))
                throw new InvalidDataException("旧版配置包含不支持的设备或自测组合，未覆盖原文件。");
            options = options with { SchemaVersion = 5 };
        }
        // v3 → v4：新增内核开关与主题；v4 → v5：新增备用矿池、钱包列表与算法选择。
        // 逐级升级只补默认值，不改变原有挖矿语义。
        var upgraded = options.SchemaVersion switch
        {
            3 => options with { SchemaVersion = 5 },
            4 => options with { SchemaVersion = 5 },
            5 => options,
            _ => null
        } ?? throw new InvalidDataException("配置版本或设备不受支持，未覆盖原文件。");
        if (!Enum.IsDefined(upgraded.Device) || !Enum.IsDefined(upgraded.Miner) || !Enum.IsDefined(upgraded.Theme))
            throw new InvalidDataException("配置版本或设备不受支持，未覆盖原文件。");
        // 换过内核后旧开关可能不再适用，读取时按当前内核过滤，避免把无效参数带进启动命令。
        // 钱包列表与当前地址也做一次对齐，避免"当前地址不在列表里"的中间状态。
        // 只在列表非空且当前地址与之一致时才对账；单一地址的用户不因为读配置就多出一个列表项。
        upgraded = upgraded with { KernelSwitches = upgraded.KernelSwitches ?? [], BackupPools = upgraded.BackupPools ?? [], SavedWallets = upgraded.SavedWallets ?? [], Algorithm = upgraded.Algorithm ?? "" };
        var walletNeedsReconcile = upgraded.SavedWallets.Length > 0 && !string.IsNullOrWhiteSpace(upgraded.Wallet)
            && !upgraded.SavedWallets.Contains(upgraded.Wallet, StringComparer.Ordinal);
        var (wallets, current) = walletNeedsReconcile
            ? WalletBook.Reconcile(upgraded.SavedWallets, upgraded.Wallet)
            : (upgraded.SavedWallets, upgraded.Wallet);
        return upgraded with
        {
            KernelSwitches = MinerSwitchCatalog.Normalize(upgraded.Miner, upgraded.KernelSwitches),
            SavedWallets = wallets,
            Wallet = current
        };
    }
    public async Task SaveAsync(MiningOptions options)
    {
        if (options.SchemaVersion != 5 || !Enum.IsDefined(options.Device) || !Enum.IsDefined(options.Miner) || !Enum.IsDefined(options.Theme))
            throw new InvalidDataException("不支持的配置版本或设备。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
