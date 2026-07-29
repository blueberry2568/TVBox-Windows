using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using FongMi.TV.Core;
using FongMi.TV.Player;
using FongMi.TV.Server;

namespace FongMi.TV;

public partial class App : Application
{
    public static MainWindow Main { get; private set; }
    public static DispatcherQueue Dispatcher { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) => { Logger.E("App", e.Exception?.ToString() ?? e.Message); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        AppPaths.Init();
        Setting.Load();
        if (Setting.Migrate()) Stores.NormalizePlaybackSpeed();
        // FFmpeg 引擎启动（缺 dll 时 EngineReady=false，由设置页/播放页给出指引）
        try { PlayerCore.StartEngine(); } catch (Exception e) { Logger.E("App", "StartEngine: " + e.Message); }
        // Stores 预热：后台线程提前加载三张 JSON 表，避免启动后首次浏览卡顿
        _ = Task.Run(() =>
        {
            try { _ = Stores.Configs.Count + Stores.Histories.Count + Stores.Keeps.Count; } catch { }
            AppPaths.CleanupLegacyRuntimes();
        });
        LocalServer.Instance.Start();
        Main = new MainWindow();
        Main.Activate();
    }

    public static void Post(Action action)
    {
        if (Dispatcher == null || Dispatcher.HasThreadAccess) action();
        else Dispatcher.TryEnqueue(() => action());
    }
}
