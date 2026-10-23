namespace NTMiner.Modern;

/// <summary>所有应用创建的持久数据都放在程序所在目录下。</summary>
public static class AppStoragePaths
{
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static string ProfilePath => Path.Combine(InstallDirectory, "profile.json");
    public static string KernelsDirectory => Path.Combine(InstallDirectory, "Kernels");
    public static string RuntimeDirectory => Path.Combine(InstallDirectory, "Runtime");
    public static string BzMinerWorkingDirectory => Path.Combine(RuntimeDirectory, "BzMiner");
    public static string TemporaryDirectory => Path.Combine(RuntimeDirectory, "Temp");

    /// <summary>仅供首次升级时搬入旧版本数据；正常读写不会再使用此目录。</summary>
    public static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtMiner.Modern");
}
