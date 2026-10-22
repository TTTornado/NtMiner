using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NTMiner.Modern;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "ui-artifacts");
        Directory.CreateDirectory(output);
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow(new ProfileStore(Path.Combine(output, "test-profile.json")));
        app.MainWindow = window;
        var success = false;
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                T Control<T>(string name) where T : class => (T)window.FindName(name);
                async Task Wait(Func<bool> condition)
                {
                    var timer = Stopwatch.StartNew();
                    while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(); await Task.Delay(100); }
                }
                void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
                await Wait(() => Control<Button>("StartButton").IsEnabled);
                Check(Control<CheckBox>("DemoMode").IsChecked == true, "First launch must default to demo");
                Check(!Control<StackPanel>("RealFields").IsEnabled, "Real configuration locked in demo");
                Render(window, Path.Combine(output, "preview-idle.png"));
                Control<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => Control<TextBlock>("StateLabel").Text.Contains("接受份额"));
                await Wait(() => Control<Button>("StopButton").IsEnabled);
                Check(Control<TextBlock>("ModeBanner").Text.Contains("模拟数据"), "Simulation must remain visibly labeled");
                Check(Control<TextBlock>("SpeedLabel").Text.Contains("MH/s"), "Telemetry shown in UI");
                Render(window, Path.Combine(output, "preview-demo.png"));
                Control<Button>("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => Control<TextBlock>("StateLabel").Text == "待启动" && Control<Button>("StartButton").IsEnabled);
                Control<ComboBox>("DeviceChoice").SelectedIndex = 1;
                Check(Control<ComboBox>("MinerChoice").SelectedIndex == 1, "CPU selection must select BzMiner");
                Check(Control<TextBox>("CpuThreads").Text == "1", "CPU defaults to one thread");
                Check(Control<CheckBox>("BenchmarkMode").IsChecked == true, "CPU defaults to offline self-test");
                Control<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => Control<TextBlock>("SpeedLabel").Text.EndsWith("H/s") && Control<Button>("StopButton").IsEnabled);
                Check(Control<TextBlock>("ProcessLabel").Text.Contains("BzMiner / CPU / 1"), "Active CPU mode is visible");
                Check(Control<TextBlock>("SharesLabel").Text == "不适用", "Offline mode does not imply pool shares");
                Render(window, Path.Combine(output, "preview-bz-cpu-demo.png"));
                Control<Button>("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => Control<TextBlock>("StateLabel").Text == "待启动" && Control<Button>("StartButton").IsEnabled);
                var cpuDemo = Control<CheckBox>("DemoMode");
                cpuDemo.IsChecked = false;
                cpuDemo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!Control<StackPanel>("WalletFields").IsEnabled, "Offline self-test does not ask for wallet/pool");
                Check(Control<Button>("StartButton").Content.ToString()!.Contains("CPU 自测"), "Real CPU self-test is distinct from demo");
                Render(window, Path.Combine(output, "preview-bz-cpu-ready.png"));
                Control<ComboBox>("MinerChoice").SelectedIndex = 0;
                Check(Control<ComboBox>("DeviceChoice").SelectedIndex == 0 && !Control<TextBox>("CpuThreads").IsEnabled, "SRBMiner restores GPU mode");
                var demo = Control<CheckBox>("DemoMode");
                demo.IsChecked = false;
                demo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Control<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Control<TextBlock>("Feedback").Text.Contains("收款地址"), "Invalid real config rejected visibly");
                Check(Control<TextBlock>("StateLabel").Text == "待启动", "Invalid config must not start mining");
                success = true;
                Console.WriteLine("PASS WPF initialization, SRB demo, Bz CPU demo, offline mode, device switching, validation and screenshots.");
            }
            catch (Exception ex) { Console.WriteLine("FAIL WPF smoke test: " + ex); }
            finally { window.Close(); }
        };
        app.Run(window);
        return success ? 0 : 1;
    }

    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
