using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Live;
using TVBoxForWindows.Models;
using TVBoxForWindows.Server;
using TVBoxForWindows.UI;
using TVBoxForWindows.UI.Pages;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TVBoxForWindows;

/// <summary>应用壳：Mica 背景、自绘标题栏、紧凑导航、配置加载与 LocalServer 事件接线。</summary>
public sealed partial class MainWindow : Window
{
    const double TitleBarHeight = 52;
    const double CompactPaneWidth = 48;
    readonly WindowPresentationManager _presentation;
    bool _immersive;
    bool _immersiveBorderless;
    bool _navigationPaneOpen;
    bool _shellRestorePending;
    int _shellLayoutRefreshGeneration;
    bool _closed;
    bool _sourceSetupRequired;
    bool _sourceSetupLoading;
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
        // 设置任务栏 / Alt-Tab 图标。
        var iconPath = System.IO.Path.Combine(AppPaths.IconDir, "icon.ico");
        try
        {
            if (!System.IO.File.Exists(iconPath)) throw new System.IO.FileNotFoundException("图标文件不存在", iconPath);
            AppWindow.SetIcon(iconPath);
        }
        catch (Exception e) { Logger.E("WindowIcon", e.Message); }
        // The old key was also written by transient NavigationView template events,
        // so it can say "open" even when the user selected collapsed. Only the
        // settings page writes this new authoritative preference.
        _navigationPaneOpen = Setting.GetBool("nav_pane_user_open", false);
        ApplyNavigationPaneState();
        RootGrid.Loaded += OnRootGridLoaded;
        Nav.Loaded += OnNavigationViewLoaded;
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

    async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        var path = System.IO.Path.Combine(AppPaths.IconDir, "icon-title.png");
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("标题栏图标文件不存在", path);
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (!_closed) BrandIcon.Source = image;
        }
        catch (Exception exception)
        {
            Logger.E("TitleIcon", exception.Message);
        }
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
            _shellRestorePending = false;
            _shellLayoutRefreshGeneration++;
            // Close while hidden so an expanded pane cannot be painted for one frame
            // when the shell is restored after a presenter transition.
            Nav.IsPaneOpen = false;
            Nav.IsPaneVisible = false;
        }
        RootGrid.RowDefinitions[0].Height = immersive ? new GridLength(0) : new GridLength(TitleBarHeight);
        TitleBarArea.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        var background = immersive ? new SolidColorBrush(Colors.Black) : null;
        RootGrid.Background = background;
        Nav.Background = background;
        PageHost.Background = background;
        SystemBackdrop = immersive ? null : new MicaBackdrop();
        if (!immersive)
        {
            // The playback page leaves immersive mode before restoring the native
            // presenter and bounds. Keep the pane hidden until those changes settle.
            _shellRestorePending = true;
            QueueShellLayoutRefresh();
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
            QueueShellLayoutRefresh();
            return true;
        }
        catch (Exception e)
        {
            Logger.E("Presentation", "恢复主窗口失败：" + e.Message);
            QueueShellLayoutRefresh();
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
        if (_closed) return;

        if (!_immersive)
        {
            if (_shellRestorePending &&
                (args.DidPresenterChange || args.DidSizeChange || args.DidPositionChange))
                QueueShellLayoutRefresh();
            return;
        }

        if (!args.DidPresenterChange) return;

        WindowFrameStyle.SetImmersive(this, true, _immersiveBorderless);
        // AppWindow presenter transitions finish asynchronously. Reapply on the next
        // layout turn so the presenter cannot restore its default light frame afterward.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_immersive) WindowFrameStyle.SetImmersive(this, true, _immersiveBorderless);
        });
    }

    void QueueShellLayoutRefresh()
    {
        if (_closed || _immersive) return;

        _shellRestorePending = true;
        var generation = ++_shellLayoutRefreshGeneration;

        // Presenter restoration emits presenter, position, and size notifications on
        // separate turns. Only the newest queued refresh may reveal the shell.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closed || _immersive || generation != _shellLayoutRefreshGeneration) return;
            ApplyNavigationPaneState();

            // Give NavigationView one turn to rebuild its template at the restored
            // window width, then arrange the compact rail and content together.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => CompleteShellLayoutRefresh(generation));
        });
    }

    void CompleteShellLayoutRefresh(int generation)
    {
        if (_closed || _immersive || generation != _shellLayoutRefreshGeneration) return;

        try
        {
            RootGrid.InvalidateMeasure();
            RootGrid.InvalidateArrange();
            Nav.InvalidateMeasure();
            Nav.InvalidateArrange();
            PageHost.InvalidateMeasure();
            PageHost.InvalidateArrange();
            foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            {
                item.InvalidateMeasure();
                item.InvalidateArrange();
            }
            RootGrid.UpdateLayout();
        }
        finally
        {
            if (generation == _shellLayoutRefreshGeneration)
                _shellRestorePending = false;
        }
    }

    public void SetNavigationPaneOpen(bool open)
    {
        _navigationPaneOpen = open;
        Setting.Put("nav_pane_user_open", open);
        ApplyNavigationPaneState();
    }

    void OnNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        // NavigationView applies template defaults during its first measure. Reapply
        // the persisted preference so a collapsed cold start stays collapsed.
        ApplyNavigationPaneState();
    }

    void ApplyNavigationPaneState()
    {
        if (_immersive) return;

        // The pane mode is authoritative as well as IsPaneOpen. Left can otherwise
        // reopen while its template is recreated after a presenter transition.
        Nav.PaneDisplayMode = _navigationPaneOpen
            ? NavigationViewPaneDisplayMode.Left
            : NavigationViewPaneDisplayMode.LeftCompact;
        Nav.CompactPaneLength = CompactPaneWidth;
        Nav.IsPaneVisible = true;
        Nav.IsPaneOpen = _navigationPaneOpen;
    }

    // ---------- 启动流程 ----------

    void Startup()
    {
        ActivateSection("vod", VodFrame, typeof(VodPage));
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
            if (_sourceSetupRequired) CompleteSourceSetup();
        }
        catch (Exception e) { if (!_closed) ShowWelcome(e.Message); }
        finally { if (!_closed) SetLoading(false); }
    }

    void ShowWelcome(string error)
    {
        _sourceSetupRequired = true;
        WelcomePanel.Visibility = Visibility.Visible;
        Nav.IsEnabled = false;
        GlobalSearch.IsEnabled = false;
        if (string.IsNullOrEmpty(ConfigUrlBox.Text)) ConfigUrlBox.Text = Setting.ConfigVod ?? "";
        if (string.IsNullOrEmpty(InitialLiveUrlBox.Text)) InitialLiveUrlBox.Text = Setting.ConfigLive ?? "";
        if (!string.IsNullOrEmpty(error))
        {
            LoadErrorBar.Message = error;
            LoadErrorBar.IsOpen = true;
        }
        DispatcherQueue.TryEnqueue(() => ConfigUrlBox.Focus(FocusState.Programmatic));
    }

    void SetLoading(bool loading)
    {
        _sourceSetupLoading = loading;
        LoadRing.IsActive = loading;
        LoadConfigButton.IsEnabled = !loading;
        ConfigUrlBox.IsEnabled = !loading;
        InitialLiveUrlBox.IsEnabled = !loading;
    }

    void OnLoadConfig(object sender, RoutedEventArgs e)
    {
        var url = (ConfigUrlBox.Text ?? "").Trim();
        if (url.Length == 0) { ShowWelcome("请输入点播配置地址"); return; }
        _ = LoadInitialSourcesAsync(url, (InitialLiveUrlBox.Text ?? "").Trim());
    }

    void OnConfigUrlKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnLoadConfig(sender, null);
    }

    async Task LoadInitialSourcesAsync(string vodUrl, string liveUrl)
    {
        if (_sourceSetupLoading) return;
        try
        {
            SetLoading(true);
            LoadErrorBar.IsOpen = false;
            await VodConfigService.Instance.LoadAsync(Stores.FindConfig(vodUrl, 0));

            string liveError = null;
            if (!string.IsNullOrWhiteSpace(liveUrl))
            {
                try { await LiveConfigService.Instance.LoadAsync(Stores.FindConfig(liveUrl, 1)); }
                catch (Exception e) { liveError = e.Message; }
            }

            CompleteSourceSetup();
            if (!string.IsNullOrEmpty(liveError))
            {
                NoticeBar.Title = "直播源加载失败";
                NoticeBar.Message = "点播源已加载，可以正常使用。直播源错误：" + liveError;
                NoticeBar.Severity = InfoBarSeverity.Warning;
                NoticeBar.IsOpen = true;
            }
        }
        catch (Exception e)
        {
            if (!_closed) ShowWelcome(e.Message);
        }
        finally
        {
            if (!_closed) SetLoading(false);
        }
    }

    void CompleteSourceSetup()
    {
        _sourceSetupRequired = false;
        WelcomePanel.Visibility = Visibility.Collapsed;
        LoadErrorBar.IsOpen = false;
        Nav.IsEnabled = true;
        GlobalSearch.IsEnabled = true;
    }

    /// <summary>配置加载成功（UI 线程）：进入点播页并显示公告。</summary>
    void OnConfigLoaded()
    {
        if (_closed) return;
        if (_sourceSetupRequired && !_sourceSetupLoading) CompleteSourceSetup();
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
            NoticeBar.Title = "公告";
            NoticeBar.Message = notice;
            NoticeBar.Severity = InfoBarSeverity.Informational;
            NoticeBar.IsOpen = true;
        }
    }

    // ---------- 导航 ----------

    void OnNavInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_sourceSetupRequired) return;
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
        if (_sourceSetupRequired) return;
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
