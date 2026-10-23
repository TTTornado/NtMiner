using System.Diagnostics;
using System.Text.Json;

namespace NTMiner.Modern;

/// <summary>
/// 从内核自身获取算法列表，而不是维护一张固定表。
/// BzMiner 的 --list-algos 输出结构化 JSON，包含算法名、开发费和支持后端，
/// 内核升级后新增或移除算法都能自动反映。
/// </summary>
public static class MinerAlgorithmProbe
{
    /// <summary>
    /// 运行内核枚举算法。内核路径无效、执行失败、输出不是预期 JSON 时，
    /// 返回带 Error 的目录而非抛异常，界面据此说明原因。
    /// 只执行枚举参数，不启动挖矿，不连接网络。
    /// </summary>
    public static MinerAlgorithmCatalog Capture(MinerKind miner, string kernelPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(kernelPath) || !File.Exists(kernelPath))
            return MinerAlgorithmCatalog.Failed(miner, "请先选择内核文件，才能读取它支持的算法。");

        // 目前只有 BzMiner 提供结构化的算法枚举；SRBMiner 的 --list-algorithms 是纯文本。
        if (miner != MinerKind.BzMiner)
            return MinerAlgorithmCatalog.Failed(miner, "当前内核不支持结构化算法枚举，请手动填写算法名。");

        string? isolatedConfig = null;
        try
        {
            Directory.CreateDirectory(AppStoragePaths.TemporaryDirectory);
            isolatedConfig = Path.Combine(AppStoragePaths.TemporaryDirectory, "algorithm-probe-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(isolatedConfig, "{}");
            var info = new ProcessStartInfo(kernelPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(kernelPath)!
            };
            info.ArgumentList.Add("--list-algos");
            foreach (var argument in new[] { "--config", isolatedConfig, "--auto-update", "off", "--plugin-update", "off", "--disable_udp", "--set", "http_enabled=false" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return MinerAlgorithmCatalog.Failed(miner, "无法启动内核进程。");

            using var job = new WindowsJob();
            try { job.Attach(process); } catch { TryKill(process); throw; }
            var stdoutTask = ReadBoundedAsync(process.StandardOutput);
            var stderrTask = ReadBoundedAsync(process.StandardError);
            try
            {
                Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync()).WaitAsync(timeout ?? TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                TryKill(process);
                return MinerAlgorithmCatalog.Failed(miner, "内核枚举算法超时。");
            }
            finally { TryKill(process); }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return MinerAlgorithmCatalog.Failed(miner,
                    "内核未能输出算法列表" + (string.IsNullOrWhiteSpace(stderr) ? "。" : "：" + stderr.Trim().Split('\n')[0].Trim()));

            return ParseJson(miner, stdout);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return MinerAlgorithmCatalog.Failed(miner, "读取算法列表失败：" + ex.Message);
        }
        finally
        {
            if (isolatedConfig is not null)
                try { File.Delete(isolatedConfig); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>解析 --list-algos 的 JSON。公开以便用固定样本验收，无需真实内核。</summary>
    public static MinerAlgorithmCatalog ParseJson(MinerKind miner, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (!root.TryGetProperty("algorithms", out var list) || list.ValueKind != JsonValueKind.Array)
                return MinerAlgorithmCatalog.Failed(miner, "内核输出中没有 algorithms 数组。");

            var algorithms = new List<MinerAlgorithm>();
            foreach (var item in list.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var fee = item.TryGetProperty("dev_fee", out var f) && f.TryGetDouble(out var feeValue) ? feeValue : 0;
                var backends = item.TryGetProperty("backends", out var b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : [];
                algorithms.Add(new MinerAlgorithm(name, fee, backends));
            }
            return algorithms.Count == 0
                ? MinerAlgorithmCatalog.Failed(miner, "内核报告了空的算法列表。")
                : new MinerAlgorithmCatalog(miner, version, algorithms
                    .GroupBy(a => miner == MinerKind.BzMiner && a.Name.Equals("pearlhash", StringComparison.OrdinalIgnoreCase) ? "pearl" : a.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.FirstOrDefault(a => a.Name.Equals(g.Key, StringComparison.OrdinalIgnoreCase)) ?? g.First()) with { Name = g.Key, Backends = g.SelectMany(a => a.Backends).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray(), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return MinerAlgorithmCatalog.Failed(miner, "内核输出的算法列表不是有效 JSON。");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        {
            if (text.Length + count > 256 * 1024) throw new InvalidDataException("算法列表输出过大。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
