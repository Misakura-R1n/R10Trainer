using System.Windows;
using R10.Core;
using R10.Bridge.Runtime;

namespace R10.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        _instance = new Mutex(true, demo ? "Local\\R10Trainer.Demo.v1" : "Local\\R10Trainer.v1", out var created);
        if (!created)
        {
            MessageBox.Show(demo ? "演示窗口已经运行。" : "R10 修改器已经运行，请使用已有窗口。", "R10 修改器", MessageBoxButton.OK, MessageBoxImage.Information);
            _instance.Dispose(); _instance = null; Shutdown(); return;
        }
        var settings = AppSettings.Load(demo);
        var themeIndex = Array.FindIndex(e.Args, x => string.Equals(x, "--theme", StringComparison.OrdinalIgnoreCase));
        if (themeIndex >= 0 && themeIndex + 1 < e.Args.Length && e.Args[themeIndex + 1] is "light" or "dark") settings.Theme = e.Args[themeIndex + 1];
        ThemeManager.Apply(settings.Theme);
        var bridgeIndex = Array.FindIndex(e.Args,x=>string.Equals(x,"--bridge-game",StringComparison.OrdinalIgnoreCase));
        if (bridgeIndex>=0 && (demo || bridgeIndex+1>=e.Args.Length || !System.IO.Path.IsPathFullyQualified(e.Args[bridgeIndex+1])))
        {
            MessageBox.Show("--bridge-game 需要游戏的绝对目录，不能与 --demo 同时使用。","R10 修改器",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(); return;
        }
        if (bridgeIndex >= 0) settings.GameDirectory = e.Args[bridgeIndex + 1];
        ITrainerRuntime runtime = demo ? new SimulationRuntime() : new UnifiedTrainerRuntime(settings.GameDirectory);
        var window = new MainWindow(runtime, settings, demo);
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
