using System.Text.Json;

namespace NTMiner.Modern;

public sealed class ProfileStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtMiner.Modern", "profile.json");
    public async Task<MiningOptions> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("配置文件过大，请检查本地配置。");
        var options = JsonSerializer.Deserialize<MiningOptions>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidDataException("配置文件内容无效。");
        if (options.SchemaVersion == 1)
            return options with { SchemaVersion = 4, Miner = MinerKind.SrbMiner, Device = MiningDevice.Gpu, CpuThreads = 1, BenchmarkMode = false };
        if (options.SchemaVersion == 2)
        {
            if (options.Device is not (MiningDevice.Gpu or MiningDevice.Cpu) || !Enum.IsDefined(options.Miner)
                || options.BenchmarkMode && (options.Miner != MinerKind.BzMiner || options.Device != MiningDevice.Cpu))
                throw new InvalidDataException("旧版配置包含不支持的设备或自测组合，未覆盖原文件。");
            return options with { SchemaVersion = 4 };
        }
        // v3 → v4：新增内核开关与主题，两者都有安全默认值，直接升级即可。
        if (options.SchemaVersion == 3)
            return options with { SchemaVersion = 4, KernelSwitches = MinerSwitchCatalog.Normalize(options.Miner, options.KernelSwitches) };
        if (options.SchemaVersion != 4 || !Enum.IsDefined(options.Device) || !Enum.IsDefined(options.Miner) || !Enum.IsDefined(options.Theme))
            throw new InvalidDataException("配置版本或设备不受支持，未覆盖原文件。");
        // 换过内核后旧开关可能不再适用，读取时按当前内核过滤，避免把无效参数带进启动命令。
        return options with { KernelSwitches = MinerSwitchCatalog.Normalize(options.Miner, options.KernelSwitches) };
    }
    public async Task SaveAsync(MiningOptions options)
    {
        if (options.SchemaVersion != 4 || !Enum.IsDefined(options.Device) || !Enum.IsDefined(options.Miner) || !Enum.IsDefined(options.Theme))
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
