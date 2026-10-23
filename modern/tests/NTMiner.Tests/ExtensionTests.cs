using System.Text;
using NTMiner.Modern;
using static NTMiner.Tests.Harness;

namespace NTMiner.Tests;

/// <summary>Pure logic: sensor display rules, algorithm catalog, pool and wallet lists.</summary>
internal static class ExtensionTests
{
    internal static async Task RunAsync()
    {
        await Test("kernel import is empty by default and keeps an isolated executable copy", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "kernel-import-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(folder, "source");
            var managed = Path.Combine(folder, "managed");
            Directory.CreateDirectory(source);
            try
            {
                var library = new KernelLibrary(managed);
                Check(library.List().Length == 0, "No bundled or auto-imported kernel");
                var executable = Path.Combine(source, "bzminer.exe");
                File.Copy(FakeMiner, executable);
                await File.WriteAllTextAsync(Path.Combine(source, "config.txt"), "user pool secrets");
                await File.WriteAllTextAsync(Path.Combine(source, "start.bat"), "echo do not copy");
                Directory.CreateDirectory(Path.Combine(source, "plugins"));
                await File.WriteAllBytesAsync(Path.Combine(source, "plugins", "sample.bin"), [1, 2, 3]);
                var entry = await library.ImportAsync(executable);
                Check(entry.ExecutablePath.StartsWith(managed, StringComparison.OrdinalIgnoreCase) && File.Exists(executable), "Import preserves the source and uses app storage");
                var sourceBytes = await File.ReadAllBytesAsync(executable);
                var managedBytes = await File.ReadAllBytesAsync(entry.ExecutablePath);
                Check(sourceBytes.SequenceEqual(managedBytes), "Executable copy is exact");
                var target = Path.GetDirectoryName(entry.ExecutablePath)!;
                Check(File.Exists(Path.Combine(target, "plugins", "sample.bin")) && !File.Exists(Path.Combine(target, "config.txt")) && !File.Exists(Path.Combine(target, "start.bat")), "Companions copy; user configs and launchers do not");
                Check(new KernelLibrary(managed).List().Single().ExecutablePath == entry.ExecutablePath, "Library persists without absolute metadata trust");
                await Throws<InvalidDataException>(() => library.ImportAsync(FakeMiner));
            }
            finally { Directory.Delete(folder, true); }
        });
        await Test("BzMiner pearl aliases collapse into one selectable algorithm", () =>
        {
            var catalog = MinerAlgorithmProbe.ParseJson(MinerKind.BzMiner, """{"algorithms":[{"name":"pearl","backends":["cpu","opencl"],"dev_fee":2},{"name":"pearlhash","backends":["cpu","opencl"],"dev_fee":2}]}""");
            Check(catalog.Algorithms.Length == 1 && catalog.Algorithms[0].Name == "pearl", "One PRL choice");
            Check(new MiningOptions { Miner = MinerKind.BzMiner, Algorithm = "pearlhash" }.EffectiveAlgorithm == "pearl", "Legacy alias keeps the same runtime algorithm");
            return Task.CompletedTask;
        });
        await Test("algorithm probe timeout also covers blocked output", () =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = MinerAlgorithmProbe.Capture(MinerKind.BzMiner, FakeMiner, TimeSpan.FromMilliseconds(250));
            Check(!result.IsUsable && timer.Elapsed < TimeSpan.FromSeconds(3), "Hanging probe is bounded and killed");
            return Task.CompletedTask;
        });
        await Test("backup TLS and algorithm previews match adapter semantics", () =>
        {
            var options = new MiningOptions { DemoMode = false, Pool = "stratum+tcp://first.example:3333", BackupPools = ["stratum+ssl://second.example:443"], Wallet = "prl1" + new string('q', 58) };
            var info = new SrbMinerAdapter(FakeMiner).CreateStartInfo(options);
            string Arg(string flag) => info.ArgumentList[info.ArgumentList.IndexOf(flag) + 1];
            Check(Arg("--pool") == "first.example:3333,second.example:443" && Arg("--tls") == "false,true", "Per-pool TLS stays paired");
            Check(Arg("--algorithm") == "pearlhash" && MinerSwitchCatalog.Preview(options, []).Contains("false,true"), "Default and preview agree");
            Check((options with { Algorithm = "cn" }).Validate().Any(e => e.Contains("仅支持 PRL")), "Other algorithms cannot use PRL payout silently");
            return Task.CompletedTask;
        });
        await Test("v3 profile reaches v5 without rewriting the original", async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "migration-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                const string json = "{\"SchemaVersion\":3,\"Miner\":1,\"Device\":2}";
                await File.WriteAllTextAsync(path, json);
                var loaded = await new ProfileStore(path).LoadAsync();
                Check(loaded.SchemaVersion == 5 && loaded.Device == MiningDevice.IntegratedGpu, "Upgrade finishes in current schema");
                Check(await File.ReadAllTextAsync(path) == json, "Load leaves original intact");
            }
            finally { File.Delete(path); }
        });
        await Test("sensor display never presents a missing reading as zero", () =>
        {
            Check(GpuSensorDisplay.Temperature(null) == "—", "Missing temperature is a dash, not 0");
            Check(GpuSensorDisplay.Fan(null) == "—", "Missing fan speed is a dash");
            Check(GpuSensorDisplay.Power(null, 250) == "—", "Missing power is a dash");
            Check(GpuSensorDisplay.Temperature(72.4) == "72 °C", "Temperature is rounded");
            Check(GpuSensorDisplay.Power(180, 250) == "180 / 250 W", "Power shows usage and limit");
            Check(GpuSensorDisplay.Power(180, null) == "180 W", "Power without a limit omits the limit");
            Check(GpuSensorDisplay.Memory(2048, 8192).Contains("25%"), "Memory usage is relative to total");
            return Task.CompletedTask;
        });

        await Test("heat levels escalate before the thermal limit", () =>
        {
            Check(GpuSensorDisplay.HeatLevel(null) == GpuHeatLevel.Unknown, "No reading is unknown, not normal");
            Check(GpuSensorDisplay.HeatLevel(60) == GpuHeatLevel.Normal, "60 C is normal");
            Check(GpuSensorDisplay.HeatLevel(80) == GpuHeatLevel.Warning, "80 C warns");
            Check(GpuSensorDisplay.HeatLevel(88) == GpuHeatLevel.Critical, "88 C is critical");
            var hot = new GpuSensorReading("Test GPU", GpuVendor.Nvidia, 85, null, null, null, null, null, null, null, null, null);
            Check(hot.IsHot, "A reading at 85 C is flagged");
            var empty = hot with { TemperatureC = null };
            Check(!empty.HasAnyReading, "A reading with no values is not usable");
            return Task.CompletedTask;
        });

        await Test("vendor detection covers the three supported families", () =>
        {
            Check(GpuSensorDisplay.VendorFromName("NVIDIA GeForce RTX 4070") == GpuVendor.Nvidia, "NVIDIA");
            Check(GpuSensorDisplay.VendorFromName("AMD Radeon RX 7600") == GpuVendor.Amd, "AMD");
            Check(GpuSensorDisplay.VendorFromName("Intel(R) UHD Graphics 730") == GpuVendor.Intel, "Intel");
            Check(GpuSensorDisplay.VendorFromName("Todesk Virtual Display Adapter") == GpuVendor.Unknown, "Unknown vendor");
            Check(GpuSensorDisplay.VendorFromName("") == GpuVendor.Unknown, "Empty name is unknown");
            return Task.CompletedTask;
        });

        await Test("algorithm catalog parses kernel JSON and filters by device", () =>
        {
            // 形状取自 BzMiner v100.46 --list-algos 的真实输出。
            const string json = """
            {
              "version": "v100.46",
              "algorithms": [
                { "name": "pearl", "dev_fee": 2.00, "backends": ["cuda", "opencl"], "multi_device": false },
                { "name": "cn", "dev_fee": 1.00, "backends": ["cpu", "cuda", "opencl"], "multi_device": false },
                { "name": "cpupower", "dev_fee": 0.85, "backends": ["cpu"], "multi_device": false }
              ]
            }
            """;
            var catalog = MinerAlgorithmProbe.ParseJson(MinerKind.BzMiner, json);
            Check(catalog.IsUsable && catalog.Version == "v100.46", "Version and algorithms are read");
            Check(catalog.Algorithms.Length == 3, "All three algorithms are captured");
            var pearl = catalog.Algorithms.Single(a => a.Name == "pearl");
            Check(!pearl.SupportsCpu && pearl.SupportsGpu, "pearl is GPU-only per the kernel's own declaration");
            Check(pearl.DevFeePercent == 2.00, "Dev fee comes from the kernel, not a hard-coded table");
            Check(catalog.ForDevice(MiningDevice.Cpu).Length == 2, "CPU view excludes GPU-only algorithms");
            Check(catalog.ForDevice(MiningDevice.Gpu).Length == 2, "GPU view excludes CPU-only algorithms");
            return Task.CompletedTask;
        });

        await Test("algorithm catalog degrades instead of throwing", () =>
        {
            Check(MinerAlgorithmProbe.ParseJson(MinerKind.BzMiner, "not json").IsUsable == false, "Invalid JSON is reported");
            Check(MinerAlgorithmProbe.ParseJson(MinerKind.BzMiner, "{\"algorithms\":[]}").IsUsable == false, "Empty list is reported");
            var missing = MinerAlgorithmProbe.Capture(MinerKind.BzMiner, "Z:\\not-a-kernel.exe");
            Check(!missing.IsUsable && missing.Error is not null, "Missing kernel file yields an explanation");
            var srb = MinerAlgorithmProbe.Capture(MinerKind.SrbMiner, FakeMiner);
            Check(!srb.IsUsable && srb.Error is not null, "SRBMiner has no structured enum and says so");
            return Task.CompletedTask;
        });

        await Test("algorithm selection is validated against the kernel catalog", () =>
        {
            const string json = """{ "algorithms": [ { "name": "cn", "dev_fee": 1.0, "backends": ["cpu","opencl"] } ] }""";
            var catalog = MinerAlgorithmProbe.ParseJson(MinerKind.BzMiner, json);
            Check(CoinSelection.Validate(catalog, "cn", MiningDevice.Cpu) is null, "CPU-capable algorithm passes");
            Check(CoinSelection.Validate(catalog, "cn", MiningDevice.Gpu) is null, "GPU-capable algorithm passes");
            Check(CoinSelection.Validate(catalog, "pearl", MiningDevice.Gpu) is not null, "Unknown algorithm is rejected");
            Check(CoinSelection.Validate(catalog, "", MiningDevice.Gpu) is not null, "Empty selection is rejected");
            Check(CoinSelection.Validate(MinerAlgorithmCatalog.Failed(MinerKind.BzMiner, "boom"), "cn", MiningDevice.Gpu) is not null,
                "A failed catalog cannot validate anything");
            Check(CoinSelection.ArgumentName(MinerKind.BzMiner) == "-a", "BzMiner uses -a");
            Check(CoinSelection.ArgumentName(MinerKind.SrbMiner) == "--algorithm", "SRBMiner uses --algorithm");
            return Task.CompletedTask;
        });

        await Test("backup pools append after the primary and are validated the same way", async () =>
        {
            // BzMiner 的校验要求文件名是 bzminer.exe，这里用临时空文件代替模拟内核。
            var folder = Path.Combine(AppContext.BaseDirectory, "pool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var kernel = Path.Combine(folder, "bzminer.exe");
            await File.WriteAllBytesAsync(kernel, []);
            try
            {
                var options = new MiningOptions
                {
                    DemoMode = false, Miner = MinerKind.BzMiner, Device = MiningDevice.Gpu, KernelPath = kernel,
                    Wallet = "prl1" + new string('q', 58), Worker = "rig01",
                    Pool = "stratum+tcp://primary.example.org:3333",
                    BackupPools = ["stratum+ssl://backup.example.org:443", ""]
                };
                Check(options.Validate().Count == 0, "Valid primary plus backup passes");
                var pools = options.AllPools();
                Check(pools.Length == 2 && pools[0].Contains("primary") && pools[1].Contains("backup"),
                    "Primary comes first, empty entries dropped");

                var bad = options with { BackupPools = ["http://not-a-pool:3333"] };
                Check(bad.Validate().Count > 0, "A malformed backup pool is rejected");
                var injected = options with { BackupPools = ["stratum+tcp://evil; rm -rf /"] };
                Check(injected.Validate().Count > 0, "Shell-looking backup pool is rejected by structure");
            }
            finally { Directory.Delete(folder, true); }
        });

        await Test("wallet book keeps the list and current address consistent", () =>
        {
            var a = "prl1" + new string('a', 58);
            var b = "prl1" + new string('b', 58);
            Check(WalletBook.Validate(a) is null, "Valid address passes");
            Check(WalletBook.Validate("prl1 short") is not null, "Short address is rejected");
            Check(WalletBook.Validate("") is not null, "Empty address is rejected");
            Check(WalletBook.Validate(a + " rig") is not null, "Address with a worker suffix is rejected");

            var (list1, error1) = WalletBook.Add([], a);
            Check(error1 is null && list1.Length == 1, "First address is added");
            var (list2, error2) = WalletBook.Add(list1, a);
            Check(error2 is null && list2.Length == 1, "Duplicate address is not added twice");
            var (list3, error3) = WalletBook.Add(list2, b);
            Check(error3 is null && list3.Length == 2, "Second address is appended");
            Check(WalletBook.Add(list3, "bad").Error is not null, "Invalid address is rejected with a reason");

            Check(WalletBook.Remove(list3, a).Length == 1, "Address can be removed");
            Check(WalletBook.Remove(list3, "prl1none").Length == 2, "Removing an unknown address is a no-op");

            var (reconciled, current) = WalletBook.Reconcile([a], b);
            Check(reconciled.Length == 2 && current == b, "Current address missing from the list is inserted");
            var (fromList, picked) = WalletBook.Reconcile([a, b], "");
            Check(picked == a, "Empty current address falls back to the first entry");
            Check(WalletBook.Shorten(a).Contains('…'), "Long addresses are shortened for display");
            return Task.CompletedTask;
        });
    }
}
