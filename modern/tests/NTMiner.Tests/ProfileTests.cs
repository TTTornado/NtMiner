using System.Diagnostics;
using System.Text;
using NTMiner.Modern;
using static NTMiner.Tests.Harness;

namespace NTMiner.Tests;

/// <summary>Profile round-trip and migration of older schema versions.</summary>
internal static class ProfileTests
{
    internal static async Task RunAsync()
    {
        await Test("profile round-trip and future schema protection", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "NTMiner-profile-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "profile.json");
            try
            {
                var store = new ProfileStore(path);
                var options = new MiningOptions { Worker = "a_01" };
                await store.SaveAsync(options);
                Check(await store.LoadAsync() == options, "Profile round-trip");
                await File.WriteAllTextAsync(path, "{\"SchemaVersion\":99}");
                await Throws<InvalidDataException>(async () => await store.LoadAsync());
                Check((await File.ReadAllTextAsync(path)).Contains("99"), "Future profile preserved");
            }
            finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
        });

        await Test("v1 profile migrates to v4 without enabling a new CPU or offline task", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "profile-v1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "profile.json");
            try
            {
                const string legacy = "{\"SchemaVersion\":1,\"Worker\":\"old_rig\",\"Wallet\":\"old_address\",\"DemoMode\":false}";
                await File.WriteAllTextAsync(path, legacy);
                var store = new ProfileStore(path);
                var migrated = await store.LoadAsync();
                Check(migrated.SchemaVersion == 4 && migrated.Miner == MinerKind.SrbMiner && migrated.Device == MiningDevice.Gpu && !migrated.BenchmarkMode, "Keep previous semantics");
                Check(migrated.Worker == "old_rig" && migrated.Wallet == "old_address" && !migrated.DemoMode, "Preserve legacy fields");
                Check(await File.ReadAllTextAsync(path) == legacy, "Migration does not automatically overwrite");
                var cpu = migrated with { Miner = MinerKind.BzMiner, Device = MiningDevice.Cpu, CpuThreads = 1, BenchmarkMode = true };
                await store.SaveAsync(cpu);
                Check(System.Text.Json.JsonSerializer.Serialize(await store.LoadAsync()) == System.Text.Json.JsonSerializer.Serialize(cpu), "CPU and benchmark settings round-trip");
            }
            finally { File.Delete(path); Directory.Delete(folder); }
        });

        await Test("v2 migration preserves CPU and GPU semantics; igpu round-trips at v4", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "profile-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "profile.json");
            try
            {
                var store = new ProfileStore(path);
                foreach (var device in new[] { MiningDevice.Cpu, MiningDevice.Gpu })
                {
                    var previous = new MiningOptions { SchemaVersion = 2, Miner = MinerKind.BzMiner, Device = device, BenchmarkMode = device == MiningDevice.Cpu, DemoMode = false, Wallet = "keep_address" };
                    var serialized = System.Text.Json.JsonSerializer.Serialize(previous);
                    await File.WriteAllTextAsync(path, serialized);
                    Check(System.Text.Json.JsonSerializer.Serialize(await store.LoadAsync()) == System.Text.Json.JsonSerializer.Serialize(previous with { SchemaVersion = 4 }), "Migration cannot silently widen device mode");
                    Check(await File.ReadAllTextAsync(path) == serialized, "Loading preserves old file until explicit save");
                }
                var igpu = new MiningOptions { Miner = MinerKind.BzMiner, Device = MiningDevice.IntegratedGpu, BenchmarkMode = true };
                await store.SaveAsync(igpu); Check(System.Text.Json.JsonSerializer.Serialize(await store.LoadAsync()) == System.Text.Json.JsonSerializer.Serialize(igpu), "igpu setting survives reopening either frontend");
                await File.WriteAllTextAsync(path, "{\"SchemaVersion\":2,\"Device\":2}");
                await Throws<InvalidDataException>(async () => await store.LoadAsync());
            }
            finally { File.Delete(path); Directory.Delete(folder); }
        });
    }
}
