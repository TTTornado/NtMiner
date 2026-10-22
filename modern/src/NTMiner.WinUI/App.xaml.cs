using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml;

namespace NTMiner.Modern.WinUI;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Mutex? _instance;
    private MainWindow? _window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        _instance = new Mutex(true, @"Local\NtMiner.Modern." + sid, out var created);
        if (!created)
        {
            MessageBoxW(IntPtr.Zero, "NtMiner 已在运行。请先关闭 WPF 或 WinUI 工作台，再打开此版本。", "NtMiner", 0);
            _instance.Dispose(); Exit(); return;
        }
        var commandLine = Environment.GetCommandLineArgs();
        string? smokeDirectory = commandLine.Length == 3 && commandLine[1] == "--smoke-test" ? Path.GetFullPath(commandLine[2]) : null;
        _window = new MainWindow(smokeDirectory);
        _window.Closed += (_, _) => _instance.Dispose();
        _window.Activate();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
