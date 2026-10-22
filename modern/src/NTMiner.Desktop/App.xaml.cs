using System.Security.Principal;
using System.Windows;

namespace NTMiner.Modern;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        _instance = new Mutex(true, @"Local\NtMiner.Modern." + sid, out var created);
        if (!created) { MessageBox.Show("新版 NtMiner 已在运行，请使用已有窗口。"); Shutdown(); return; }
        base.OnStartup(e);
        MainWindow ??= new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
