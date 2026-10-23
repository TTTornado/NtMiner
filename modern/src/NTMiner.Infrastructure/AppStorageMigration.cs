using System.Text.Json;
using System.Text.RegularExpressions;

namespace NTMiner.Modern;

/// <summary>把旧版 LocalAppData 中已存在的配置和已导入内核复制到程序目录。</summary>
public static class AppStorageMigration
{
    public static async Task<bool> MigrateLegacyDataAsync(ProfileStore store, KernelLibrary kernels)
    {
        var legacyRoot = Path.GetFullPath(AppStoragePaths.LegacyDataDirectory);
        var installRoot = Path.GetFullPath(AppStoragePaths.InstallDirectory);
        if (string.Equals(legacyRoot, installRoot, StringComparison.OrdinalIgnoreCase)
            || File.Exists(store.FilePath)
            || !File.Exists(Path.Combine(legacyRoot, "profile.json")))
            return false;

        var oldOptions = await new ProfileStore(Path.Combine(legacyRoot, "profile.json")).LoadAsync();
        var oldKernelRoot = Path.Combine(legacyRoot, "Kernels");
        var migratedSelectedPath = "";
        if (Directory.Exists(oldKernelRoot))
        {
            var selectedPath = string.IsNullOrWhiteSpace(oldOptions.KernelPath) ? null : Path.GetFullPath(oldOptions.KernelPath);
            foreach (var directory in Directory.EnumerateDirectories(oldKernelRoot))
            {
                try
                {
                    if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var executable = Path.Combine(directory, "bzminer.exe");
                    if (!File.Exists(executable)) continue;
                    var imported = await kernels.ImportAsync(executable, ReadLegacyVersion(directory));
                    if (string.Equals(Path.GetFullPath(executable), selectedPath, StringComparison.OrdinalIgnoreCase))
                        migratedSelectedPath = imported.ExecutablePath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // A damaged legacy kernel should not prevent the rest of the profile from moving.
                }
            }
        }

        await store.SaveAsync(oldOptions with { KernelPath = migratedSelectedPath });
        return true;
    }

    private static string? ReadLegacyVersion(string directory)
    {
        var metadata = Path.Combine(directory, "import.json");
        if (!File.Exists(metadata)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            if (!document.RootElement.TryGetProperty("Label", out var labelValue)) return null;
            var label = labelValue.GetString();
            if (string.IsNullOrWhiteSpace(label)) return null;
            var match = Regex.Match(label, @"(?i)^BzMiner\s+(\d+(?:\.\d+){1,3})\s+·");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
