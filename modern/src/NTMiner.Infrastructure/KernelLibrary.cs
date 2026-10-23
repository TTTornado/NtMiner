using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NTMiner.Modern;

public sealed record ImportedKernel(string Id, string Name, string Version, string Sha256, string ExecutablePath)
{
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? $"{Name} · 版本未知" : $"{Name} · {Version}";
}

public sealed class KernelLibrary(string root)
{
    public static string DefaultRoot => AppStoragePaths.KernelsDirectory;
    public ImportedKernel[] List()
    {
        if (!Directory.Exists(root)) return [];
        var entries = new List<ImportedKernel>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var exe = Path.Combine(directory, "bzminer.exe");
            var metadata = Path.Combine(directory, "import.json");
            if (!File.Exists(exe) || !File.Exists(metadata)) continue;
            if (new FileInfo(exe).Attributes.HasFlag(FileAttributes.ReparsePoint)
                || new FileInfo(metadata).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ImportedKernel>(File.ReadAllText(metadata));
                if (entry is not null && entry.Id == Path.GetFileName(directory)
                    && entry.Name == "BzMiner" && !string.IsNullOrWhiteSpace(entry.Version)
                    && !string.IsNullOrWhiteSpace(entry.Sha256))
                    entries.Add(entry with { ExecutablePath = exe });
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Version, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ImportedKernel> ImportAsync(string executable, string? versionHint = null)
    {
        executable = Path.GetFullPath(executable);
        if (!Path.GetFileName(executable).Equals("bzminer.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new InvalidDataException("目前仅支持导入 bzminer.exe，请选择官方完整解压包中的文件。");
        var sourceRoot = Path.GetDirectoryName(executable)!;
        if (new FileInfo(executable).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("不支持导入链接文件。");
        await using (var stream = File.OpenRead(executable))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("所选文件不是 Windows 可执行文件。");
        }
        await using (var source = File.OpenRead(executable))
        {
            var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source));
            var existing = List().FirstOrDefault(entry => entry.Sha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
        }
        // Only runtime companions and notices are copied; never inherit a pool config or launcher.
        var sources = new List<string> { executable };
        sources.AddRange(Directory.EnumerateFiles(sourceRoot).Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p).Equals("THIRD_PARTY_NOTICES.txt", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)));
        foreach (var name in new[] { "plugins", "kernels", "licenses" })
        {
            var directory = Path.Combine(sourceRoot, name);
            if (!Directory.Exists(directory)) continue;
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("配套目录不能是链接。");
            sources.AddRange(Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }));
        }
        if (sources.Any(p => new FileInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint))) throw new InvalidDataException("配套文件不能是链接。");
        if (sources.Sum(p => new FileInfo(p).Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("内核及配套文件超过 2 GB，请检查解压目录。");
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(root, id);
        Directory.CreateDirectory(destination);
        // A failed import is not listed: the metadata is written only after every copy succeeds.
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(target);
            await input.CopyToAsync(output);
        }
        var managedExe = Path.Combine(destination, "bzminer.exe");
        await using var binary = File.OpenRead(managedExe);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(binary));
        var version = string.IsNullOrWhiteSpace(versionHint) ? ResolveVersion(managedExe, sourceRoot) : versionHint;
        var entry = new ImportedKernel(id, "BzMiner", version, hash, managedExe);
        await File.WriteAllTextAsync(Path.Combine(destination, "import.json"), JsonSerializer.Serialize(entry));
        return entry;
    }

    public void Delete(ImportedKernel kernel)
    {
        if (!Guid.TryParseExact(kernel.Id, "N", out _)) throw new InvalidDataException("内核条目标识无效。");
        var rootPath = Path.GetFullPath(root);
        if (Directory.Exists(rootPath) && new DirectoryInfo(rootPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("内核库目录不能是链接目录。");
        var directory = Path.GetFullPath(Path.Combine(rootPath, kernel.Id));
        if (!string.Equals(Path.GetDirectoryName(directory), rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内核目录无效。");
        var knownEntry = List().FirstOrDefault(entry => entry.Id == kernel.Id);
        if (knownEntry is null || !Directory.Exists(directory)) throw new FileNotFoundException("所选内核已不存在。");
        if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("内核目录不能是链接目录。");
        Directory.Delete(directory, recursive: true);
    }

    private static string ResolveVersion(string executable, string sourceRoot)
    {
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);
        var version = fileVersion.ProductVersion ?? fileVersion.FileVersion;
        if (!string.IsNullOrWhiteSpace(version)) return version.Trim();

        var folderMatch = Regex.Match(Path.GetFileName(sourceRoot), @"(?i)bzminer[_ -]?v?(\d+(?:\.\d+){1,3})");
        if (folderMatch.Success) return folderMatch.Groups[1].Value;

        var readme = Path.Combine(sourceRoot, "readme.txt");
        if (File.Exists(readme))
        {
            using var reader = new StreamReader(readme);
            var header = new char[8192];
            var count = reader.ReadBlock(header, 0, header.Length);
            var match = Regex.Match(new string(header, 0, count), @"(?i)\bbzminer\s+v?(\d+(?:\.\d+){1,3})");
            if (match.Success) return match.Groups[1].Value;
        }
        return "版本未知";
    }
}
