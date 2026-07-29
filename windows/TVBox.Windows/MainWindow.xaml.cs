using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Live;
using TVBoxForWindows.Models;
using TVBoxForWindows.Server;
using TVBoxForWindows.UI;
using TVBoxForWindows.UI.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TVBoxForWindows;

/// <summary>应用壳：Mica 背景、自绘标题栏、紧凑导航、配置加载与 LocalServer 事件接线。</summary>
public sealed partial class MainWindow : Window
{
    const double TitleBarHeight = 52;
    const double CompactPaneWidth = 48;
    readonly WindowPresentationManager _presentation;
    bool _immersive;
    bool _immersiveBorderless;
    bool _paneOpenBeforeImmersive;
    bool _navigationPaneOpen;
    bool _closed;
    string _activeSection = "vod";
    string _loadedVodConfigUrl;

    public MainWindow()
    {
        InitializeComponent();
        _presentation = new WindowPresentationManager(this);
        WindowFrameStyle.Attach(this);
        Title = "TVBox";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        // 设置任务栏 / Alt-Tab 图标
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        // The old key was also written by transient NavigationView template events,
        // so it can say "open" even when the user selected collapsed. Only the
        // settings page writes this new authoritative preference.
        _navigationPaneOpen = Setting.GetBool("nav_pane_user_open", false);
        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.IsPaneOpen = _navigationPaneOpen;
        VodConfigService.Instance.Loaded += OnConfigLoaded;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += (s, e) =>
        {
            _closed = true;
            VodConfigService.Instance.Loaded -= OnConfigLoaded;
            AppWindow.Changed -= OnAppWindowChanged;
            UnhookServer();
            LocalServer.Instance.Stop();
            WindowFrameStyle.Detach(this);
            SpiderRuntime.TerminateForExit();
            Logger.Shutdown();
        };
        HookServer();
        Startup();
    }

    public void SetImmersive(bool immersive)
    {
        if (_immersive == immersive)
        {
            // ApplyPresentationMode runs before presenter changes. Reset a previous
            // borderless full-screen style so a following compact presenter remains resizable.
            if (immersive && _immersiveBorderless)
            {
                _immersiveBorderless = false;
                WindowFrameStyle.SetImmersive(this, true, false);
            }
            return;
        }
        _immersive = immersive;
        _immersiveBorderless = false;
        WindowFrameStyle.SetImmersive(this, immersive, false);
        if (immersive)
        {
            _paneOpenBeforeImmersive = _navigationPaneOpen;
            Nav.IsPaneVisible = false;
        }
        else
        {
            _navigationPaneOpen = _paneOpenBeforeImmersive;
        }
        RootGrid.RowDefinitions[0].Height = immersive ? new GridLength(0) : new GridLength(TitleBarHeight);
        TitleBarArea.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        // Left mode always reserves the compact rail in the NavigationView template.
        // LeftMinimal removes that rail while keeping the content frame alive.
        Nav.PaneDisplayMode = immersive
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.Left;
        Nav.CompactPaneLength = immersive ? 0 : CompactPaneWidth;
        var background = immersive ? new SolidColorBrush(Colors.Black) : null;
        RootGrid.Background = background;
        Nav.Background = background;
        PageHost.Background = background;
        SystemBackdrop = immersive ? null : new MicaBackdrop();
        if (immersive)
        {
            Nav.IsPaneVisible = false;
        }
        else
        {
            RestoreNavigationPaneVisualState();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_immersive) return;
                WindowFrameStyle.SetImmersive(this, false, false);
                RestoreNavigationPaneVisualState();
            });
        }
    }

    public bool IsNavigationPaneOpen => _navigationPaneOpen;

    public bool EnterPlaybackFullScreen()
    {
        try { _presentation.EnterFullScreen(); return true; }
        catch (Exception e)
        {
            Logger.E("Presentation", "进入全屏失败：" + e.Message);
            try { _presentation.Restore(); } catch { }
            return false;
        }
    }

    public bool EnterPlaybackCompact()
    {
        try { _presentation.EnterCompact(760, 460); return true; }
        catch (Exception e)
        {
            Logger.E("Presentation", "进入小窗失败：" + e.Message);
            try { _presentation.Restore(); } catch { }
            return false;
        }
    }

    public void BeginCompactDrag() => _presentation.BeginCompactDrag();

    public bool RestorePlaybackWindow()
    {
        try
        {
            if (_immersive)
            {
                _immersiveBorderless = false;
                WindowFrameStyle.SetImmersive(this, true, false);
            }
            _presentation.Restore();
            return true;
        }
        catch (Exception e)
        {
            Logger.E("Presentation", "恢复主窗口失败：" + e.Message);
            try { AppWindow.SetPresenter(AppWindowPresenterKind.Default); } catch { }
            return false;
        }
    }

    public void RefreshImmersiveFrame(bool borderless = false)
    {
        if (!_immersive) return;
        _immersiveBorderless = borderless;
        WindowFrameStyle.SetImmersive(this, true, borderless);
    }

    void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closed || !_immersive || !args.DidPresenterChange) return;

        WindowFrameStyle.SetImmersive(this, true, _immersiveBorderless);
        // AppWindow presenter transitions finish asynchronously. Reapply on the next
        // layout turn so the presenter cannot restore its default light frame afterward.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_immersive) WindowFrameStyle.SetImmersive(this, true, _immersiveBorderless);
        });
    }

    public void SetNavigationPaneOpen(bool open)
    {
        _navigationPaneOpen = open;
        Setting.Put("nav_pane_user_open", open);
        if (_immersive)
        {
            _paneOpenBeforeImmersive = open;
            return;
        }
        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.CompactPaneLength = CompactPaneWidth;
        Nav.IsPaneVisible = true;
        Nav.IsPaneOpen = open;
    }

    void RestoreNavigationPaneVisualState()
    {
        if (_immersive) return;

        // Left keeps the compact rail in layout; LeftCompact opens over the page and
        // can leave the rail visually covering playback after a presenter transition.
        // Update the hidden template first so no intermediate open state is painted.
        Nav.IsPaneVisible = false;
        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.CompactPaneLength = CompactPaneWidth;
        Nav.UpdateLayout();
        Nav.IsPaneOpen = _navigationPaneOpen;
        Nav.UpdateLayout();
        Nav.IsPaneVisible = true;
        Nav.IsPaneOpen = _navigationPaneOpen;
    }

    // ---------- 启动流程 ----------

    void Startup()
    {
        if (string.IsNullOrEmpty(Setting.ConfigVod)) ShowWelcome(null);
        else _ = LoadLatestAsync();
    }

    async Task LoadLatestAsync()
    {
        try
        {
            SetLoading(true);
            await VodConfigService.Instance.LoadLatestAsync();
        }
        catch (Exception e) { if (!_closed) ShowWelcome(e.Message); }
        finally { if (!_closed) SetLoading(false); }
    }

    async Task LoadConfigAsync(ConfigRecord config)
    {
        try
        {
            SetLoading(true);
            LoadErrorBar.IsOpen = false;
            await VodConfigService.Instance.LoadAsync(config);
        }
        catch (Exception e) { if (!_closed) ShowWelcome(e.Message); }
        finally { if (!_closed) SetLoading(false); }
    }

    void ShowWelcome(string error)
    {
        WelcomePanel.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(ConfigUrlBox.Text)) ConfigUrlBox.Text = Setting.ConfigVod ?? "";
        if (!string.IsNullOrEmpty(error))
        {
            LoadErrorBar.Message = error;
            LoadErrorBar.IsOpen = true;
        }
    }

    void SetLoading(bool loading)
    {
        LoadRing.IsActive = loading;
        LoadConfigButton.IsEnabled = !loading;
    }

    void OnLoadConfig(object sender, RoutedEventArgs e)
    {
        var url = (ConfigUrlBox.Text ?? "").Trim();
        if (url.Length == 0) { ShowWelcome("请输入配置地址"); return; }
        _ = LoadConfigAsync(Stores.FindConfig(url, 0));
    }

    void OnConfigUrlKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnLoadConfig(sender, null);
    }

    /// <summary>配置加载成功（UI 线程）：进入点播页并显示公告。</summary>
    void OnConfigLoaded()
    {
        if (_closed) return;
        WelcomePanel.Visibility = Visibility.Collapsed;
        LoadErrorBar.IsOpen = false;
        var config = VodConfigService.Instance.Config;
        var loadedUrl = config?.Url ?? Setting.ConfigVod ?? "";
        var firstLoad = _loadedVodConfigUrl == null;
        var changed = !firstLoad &&
            !string.Equals(_loadedVodConfigUrl, loadedUrl, StringComparison.OrdinalIgnoreCase);
        _loadedVodConfigUrl = loadedUrl;
        if (changed)
        {
            VodFrame.Navigate(typeof(VodPage));
            VodFrame.BackStack.Clear();
        }
        if (firstLoad || changed) ActivateSection("vod", VodFrame, typeof(VodPage));
        var notice = config?.Notice;
        if (!string.IsNullOrEmpty(notice))
        {
            NoticeBar.Message = notice;
            NoticeBar.IsOpen = true;
        }
    }

    // ---------- 导航 ----------

    void OnNavInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            ActivateSection("settings", SettingsFrame, typeof(SettingsPage));
            return;
        }
        var tag = args.InvokedItemContainer?.Tag as string;
        var target = tag switch
        {
            "vod" => (VodFrame, typeof(VodPage)),
            "live" => (LiveFrame, typeof(LivePage)),
            "search" => (SearchFrame, typeof(SearchPage)),
            "keep" => (KeepFrame, typeof(KeepPage)),
            "history" => (HistoryFrame, typeof(HistoryPage)),
            _ => ((Frame)null, (Type)null),
        };
        if (target.Item1 != null) ActivateSection(tag, target.Item1, target.Item2);
    }

    void ActivateSection(string tag, Frame target, Type rootPage, object parameter = null, bool navigate = false)
    {
        var current = FrameFor(_activeSection);
        if (!ReferenceEquals(current, target)) PausePlayback(current);

        foreach (var frame in SectionFrames()) frame.Visibility = ReferenceEquals(frame, target)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _activeSection = tag;
        if (navigate || parameter != null) target.Navigate(rootPage, parameter);
        else if (target.Content == null) target.Navigate(rootPage);
        if (target.Content is INavigationPlayback playback) playback.ActivateAfterNavigation();
        if (target.Content is SearchPage search) search.RefreshDisplayStyle();
        if (target.Content is KeepPage keep) keep.RefreshIfChanged();
        if (target.Content is HistoryPage history) history.RefreshIfChanged();
        SelectNav(tag);
    }

    Frame FrameFor(string tag) => tag switch
    {
        "live" => LiveFrame,
        "search" => SearchFrame,
        "keep" => KeepFrame,
        "history" => HistoryFrame,
        "settings" => SettingsFrame,
        _ => VodFrame,
    };

    IEnumerable<Frame> SectionFrames()
    {
        yield return VodFrame;
        yield return LiveFrame;
        yield return SearchFrame;
        yield return KeepFrame;
        yield return HistoryFrame;
        yield return SettingsFrame;
    }

    static void PausePlayback(Frame frame)
    {
        if (frame?.Content is INavigationPlayback playback) playback.PauseForNavigation();
    }

    void SelectNav(string tag)
    {
        if (tag == "settings") { Nav.SelectedItem = Nav.SettingsItem; return; }
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if ((item.Tag as string) == tag) { Nav.SelectedItem = item; return; }
    }

    /// <summary>标题栏全局搜索 → SearchPage（带初始关键词）。</summary>
    void OnGlobalSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var keyword = (args.QueryText ?? "").Trim();
        if (keyword.Length == 0) return;
        ActivateSection("search", SearchFrame, typeof(SearchPage), keyword, navigate: true);
        sender.Text = "";
    }

    // ---------- LocalServer 事件接线（契约 §3.1：事件已切 UI 线程） ----------

    void HookServer()
    {
        var server = LocalServer.Instance;
        server.PushArrived += OnServerPush;
        server.RefreshConfig += OnServerRefreshConfig;
        server.CastArrived += OnServerCast;
    }

    void UnhookServer()
    {
        var server = LocalServer.Instance;
        server.PushArrived -= OnServerPush;
        server.RefreshConfig -= OnServerRefreshConfig;
        server.CastArrived -= OnServerCast;
    }

    void OnServerPush(string url)
    {
        if (_closed) return;
        ActivateSection("vod", VodFrame, typeof(VodPage));
        VodFrame.Navigate(typeof(DetailPage), new DetailArgs { PushUrl = url });
    }

    void OnServerRefreshConfig(ConfigRecord config)
    {
        if (_closed) return;
        if (config?.Type == 1)
        {
            _ = ReloadLiveConfigAsync(config);
            return;
        }
        var target = config ?? (string.IsNullOrEmpty(Setting.ConfigVod) ? null : Stores.FindConfig(Setting.ConfigVod, 0));
        if (target != null) _ = LoadConfigAsync(target);
    }

    void OnServerCast(string configUrl, string historyJson)
    {
        if (!_closed) _ = HandleCastAsync(configUrl, historyJson);
    }

    static async Task ReloadLiveConfigAsync(ConfigRecord config)
    {
        try { await LiveConfigService.Instance.LoadAsync(config); }
        catch (Exception e) { Logger.E("LiveConfig", "自动重载失败：" + e.Message); }
    }

    /// <summary>接收投屏：必要时先切配置，再进详情页播放该历史条目。</summary>
    async Task HandleCastAsync(string configUrl, string historyJson)
    {
        try
        {
            var history = JsonUtil.Deserialize<History>(historyJson);
            if (history == null || string.IsNullOrEmpty(history.Key)) return;
            if (!string.IsNullOrEmpty(configUrl) && configUrl != VodConfigService.Instance.Config?.Url)
                await VodConfigService.Instance.LoadAsync(Stores.FindConfig(configUrl, 0));
            if (_closed) return;
            ActivateSection("vod", VodFrame, typeof(VodPage));
            VodFrame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = history.SiteKey, VodId = history.VodId, Name = history.VodName });
        }
        catch (Exception e) { Logger.E("Cast", e.Message); }
    }
}
