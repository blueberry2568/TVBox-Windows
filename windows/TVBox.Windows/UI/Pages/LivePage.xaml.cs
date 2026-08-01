using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;
using TVBoxForWindows.Live;
using TVBoxForWindows.Player;

namespace TVBoxForWindows.UI.Pages;

/// <summary>直播页（移植自 LiveActivity.java）：三栏 = 直播源/分组、频道列表、播放区。</summary>
public sealed partial class LivePage : Page, INavigationPlayback
{
    PlayerCore _core;                          // 本页私有播放内核
    FlyleafHostBinding _hostBinding;
    Models.Live _live;                         // 当前直播源
    LiveChannel _current;                      // 当前播放频道
    LiveChannelItem _currentItem;              // 当前播放频道的列表项
    LiveGroupItem _currentGroupItem;           // 当前选中分组项
    List<LiveChannel> _allChannels = new();    // 当前直播源全部可见频道（数字选台/上下换台用）
    List<LiveChannelItem> _channelItems = new();
    List<LiveChannelItem> _channelPickerItems = new();
    CancellationTokenSource _playCts;          // 播放解析取消
    CancellationTokenSource _epgCts;           // 频道列表节目名填充取消
    CancellationTokenSource _liveLoadCts;      // 直播源频道加载取消/代次校验
    int _liveLoadGeneration;
    DispatcherTimer _numberTimer;              // 数字选台缓冲计时器
    DispatcherTimer _chromeTimer;
    DispatcherTimer _programTimer;
    string _numberBuffer = "";
    bool _updating;                            // 抑制程序化选择触发的事件
    bool _fullscreen;
    bool _compact;
    bool _updatingSeek;
    bool _lineAvailable;
    bool _pauseWhenOpened;
    bool _isNavigatedActive;
    bool _sourceTransitionInProgress;
    bool _catalogLoading;
    bool _playbackLoading;
    bool _showPlaybackSpeed;
    bool _playbackTransferRateKnown;
    CompositionRoundedRectangleGeometry _playerAreaClipGeometry;
    CompositionGeometricClip _playerAreaClip;
    readonly Microsoft.UI.Xaml.Media.SolidColorBrush _compactCornerMaskBrush =
        new(Microsoft.UI.Colors.Black);
    FrameworkElement _linePanelAnchor;
    FrameworkElement _channelPanelAnchor;
    int _playGeneration;
    int _linePanelMutationVersion;
    int _channelPanelMutationVersion;
    int _navigationGeneration;
    double _bottomBarWidth;
    long _displayPositionMs;
    long _displayDurationMs;
    long? _pendingSeekMs;
    long _pendingSeekRequestId;
    readonly Thickness _normalPagePadding;
    readonly Thickness _normalContentMargin;

    public LivePage()
    {
        InitializeComponent();
        _normalPagePadding = RootGrid.Padding;
        _normalContentMargin = MainGrid.Margin;
        _numberTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _numberTimer.Tick += (s, e) => CommitNumber();
        _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chromeTimer.Tick += (s, e) => HidePlayerChrome();
        _programTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _programTimer.Tick += (s, e) =>
        {
            if (_currentItem != null) _ = UpdateProgramAsync(_currentItem);
        };
        PreviewKeyDown += OnPreviewKey;
        LiveSeekSlider.ThumbToolTipValueConverter = new MsTimeConverter();
        Loaded += (s, e) =>
        {
            UpdatePaneWidths(ActualWidth);
            ApplyPresentationMode();
            Focus(FocusState.Programmatic);
        };
        SizeChanged += (s, e) => UpdatePaneWidths(e.NewSize.Width);
    }

    // ---------- 生命周期 ----------

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isNavigatedActive = true;
        var navigationGeneration = ++_navigationGeneration;
        InitPlayer();
        GuidePanel.Visibility = Visibility.Collapsed;
        GuideInfo.IsOpen = false;

        // A saved video configuration can populate embedded live sources during
        // startup. Wait for that one-time restore before deciding this is an empty
        // live installation, otherwise the source guide flashes over valid content.
        await App.Main.InitialVodRestoreTask;
        if (!_isNavigatedActive || navigationGeneration != _navigationGeneration) return;
        await App.Main.InitialLiveRestoreTask;
        if (!_isNavigatedActive || navigationGeneration != _navigationGeneration) return;

        LiveConfigService.Instance.Loaded += OnConfigLoaded;
        if (LiveConfigService.Instance.Lives.Count > 0)
        {
            GuidePanel.Visibility = Visibility.Collapsed;
            RefreshLives();
            return;
        }

        var config = Stores.ResolveConfig(Setting.ConfigLive, 1);
        var address = config?.Url?.Trim();
        GuideUrlBox.Text = address ?? "";
        if (!string.IsNullOrEmpty(address))
        {
            if (!string.Equals(Setting.ConfigLive, address, StringComparison.OrdinalIgnoreCase))
                Setting.ConfigLive = address;
            await LoadGuideAddress(address, false, true);
        }
        else GuidePanel.Visibility = Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isNavigatedActive = false;
        _catalogLoading = false;
        _playbackLoading = false;
        _showPlaybackSpeed = false;
        _pendingSeekMs = null;
        _pendingSeekRequestId = 0;
        UpdateLoadingIndicator();
        _navigationGeneration++;
        LiveConfigService.Instance.Loaded -= OnConfigLoaded;
        _linePanelMutationVersion++;
        _channelPanelMutationVersion++;
        InvalidateLiveLoad();
        CancelPlayRequest();
        _epgCts?.Cancel();
        CloseChannelPanel(false);
        CloseLinePanel(false);
        _numberTimer.Stop();
        _chromeTimer.Stop();
        _programTimer.Stop();
        if (_fullscreen || _compact)
        {
            try { App.Main.RestorePlaybackWindow(); } catch { }
            _fullscreen = _compact = false;
            ApplyPresentationMode();
        }
        else App.Main.SetImmersive(false);
        if (_hostBinding != null)
        {
            _hostBinding.Dispose();
        }
        _hostBinding = null;
        if (_core != null)
        {
            _core.Opened -= OnCoreOpened;
            _core.Errored -= OnCoreErrored;
            _core.TimeChanged -= OnCoreTime;
            _core.TransferRateChanged -= OnCoreTransferRateChanged;
            _core.BufferingChanged -= OnCoreBufferingChanged;
            _core.SeekFinished -= OnCoreSeekFinished;
            try { _core.Stop(); _core.Dispose(); } catch { }
        }
        _core = null;
    }

    void InitPlayer()
    {
        if (_core != null) return;
        if (!PlayerCore.EngineReady)
        {
            ShowInfo("FFmpeg 未就绪：请到「设置 → 关于」查看安装指引", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            _core = new PlayerCore();
            if (_core.Fly == null) // 契约 §4.1：EngineReady 但内核创建失败时 Fly 为 null，绑定 FlyleafHost 前判空
            {
                _core.Dispose();
                _core = null;
                ShowInfo("播放器初始化失败：FFmpeg 引擎不可用", InfoBarSeverity.Error);
                return;
            }
            _hostBinding = new FlyleafHostBinding(VideoHost);
            _hostBinding.Attach(_core);
            _core.Opened += OnCoreOpened;
            _core.Errored += OnCoreErrored;
            _core.TimeChanged += OnCoreTime;
            _core.TransferRateChanged += OnCoreTransferRateChanged;
            _core.BufferingChanged += OnCoreBufferingChanged;
            _core.SeekFinished += OnCoreSeekFinished;
        }
        catch (Exception ex)
        {
            _core = null;
            ShowInfo("播放器初始化失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    void OnCoreOpened()
    {
        App.Post(() =>
        {
            if (!_isNavigatedActive) return;
            if (_sourceTransitionInProgress && _playCts != null)
            {
                Logger.D("LivePage", "已忽略新频道解析期间旧媒体的打开事件");
                return;
            }
            var buffering = _core?.IsBuffering == true;
            SetPlaybackLoading(buffering, buffering);
            SetSourceTransition(false);
            if (_pauseWhenOpened && _core?.IsPlaying == true) _core.PlayPause();
            _hostBinding?.RequestSynchronize();
            UpdateLivePlayPauseIcon();
        });
    }

    void OnCoreErrored(string message)
    {
        App.Post(() =>
        {
            if (!_isNavigatedActive) return;
            if (_sourceTransitionInProgress && _playCts != null)
            {
                Logger.D("LivePage", "已忽略新频道解析期间旧媒体的错误: " + message);
                return;
            }
            SetPlaybackLoading(false);
            LiveBufferBar.IsIndeterminate = false;
            _pendingSeekMs = null;
            _pendingSeekRequestId = 0;
            SetSourceTransition(false);
            UpdateLivePlayPauseIcon();
            ShowInfo("播放错误：" + message, InfoBarSeverity.Error);
        });
    }

    void OnCoreTransferRateChanged(double bytesPerSecond)
    {
        if (!_isNavigatedActive || LiveLoadingSpeedPanel.Visibility != Visibility.Visible) return;
        if (bytesPerSecond <= 0 && !_playbackTransferRateKnown) return;
        if (bytesPerSecond > 0) _playbackTransferRateKnown = true;
        LiveLoadingSpeedText.Text = PlayerCore.FormatTransferRate(bytesPerSecond);
    }

    void OnCoreBufferingChanged(bool buffering)
    {
        App.Post(() =>
        {
            if (!_isNavigatedActive || _sourceTransitionInProgress) return;
            SetPlaybackLoading(buffering, buffering);
        });
    }

    void SetCatalogLoading(bool loading)
    {
        _catalogLoading = loading;
        UpdateLoadingIndicator();
    }

    void SetPlaybackLoading(bool loading, bool showSpeed = false)
    {
        var resetSpeed = loading && showSpeed && (!_playbackLoading || !_showPlaybackSpeed);
        _playbackLoading = loading;
        _showPlaybackSpeed = loading && showSpeed;
        if (resetSpeed)
        {
            _playbackTransferRateKnown = false;
            LiveLoadingSpeedText.Text = PlayerCore.FormatTransferRate(-1);
        }
        if (!loading) _playbackTransferRateKnown = false;
        UpdateLoadingIndicator();
    }

    void UpdateLoadingIndicator()
    {
        var active = _catalogLoading || _playbackLoading;
        LoadingRing.IsActive = active;
        LoadingRing.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LivePlaybackLoadingBackdrop.Visibility = _playbackLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
        LiveLoadingSpeedPanel.Visibility = _playbackLoading && _showPlaybackSpeed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void OnCoreTime(long positionMs)
    {
        if (!_isNavigatedActive || _core == null) return;
        var durationMs = _core.DurationMs;
        var displayPositionMs = _pendingSeekMs ?? positionMs;
        _displayPositionMs = Math.Max(0, displayPositionMs);
        _displayDurationMs = Math.Max(0, durationMs);
        long bufferedMs = 0;
        try { bufferedMs = _core.Fly.BufferedDuration / 10000; } catch { }

        _updatingSeek = true;
        if (durationMs > 0)
        {
            LiveProgressRow.Visibility = Visibility.Visible;
            LiveSeekSlider.IsHitTestVisible = true;
            LiveSeekSlider.Maximum = durationMs;
            LiveSeekSlider.Value = Math.Min(displayPositionMs, durationMs);
            LiveBufferBar.IsIndeterminate = false;
            LiveBufferBar.Maximum = durationMs;
            LiveBufferBar.Value = Math.Min(positionMs + bufferedMs, durationMs);
        }
        else
        {
            LiveProgressRow.Visibility = Visibility.Collapsed;
            LiveSeekSlider.IsHitTestVisible = false;
            LiveSeekSlider.Maximum = 1;
            LiveSeekSlider.Value = 0;
            LiveBufferBar.IsIndeterminate = false;
        }
        _updatingSeek = false;
        UpdateLiveTimeLabel();
        UpdateLivePlayPauseIcon();
    }

    void OnCoreSeekFinished(long requestId, long targetMs, bool success)
    {
        if (!_isNavigatedActive || requestId != _pendingSeekRequestId ||
            _pendingSeekMs is not long pending || pending != targetMs) return;
        _pendingSeekMs = null;
        _pendingSeekRequestId = 0;
        if (!success) ShowInfo("定位失败，请重试", InfoBarSeverity.Warning);
    }

    public void PauseForNavigation()
    {
        _hostBinding?.CancelPresentationTransition();
        _linePanelMutationVersion++;
        _channelPanelMutationVersion++;
        CloseChannelPanel(false);
        CloseLinePanel(false);
        _pauseWhenOpened = true;
        if (_core?.IsPlaying == true) _core.PlayPause();
        _core?.SetUiUpdatesEnabled(false);
        UpdateLivePlayPauseIcon();
        ShowPlayerChrome();
        _chromeTimer.Stop();
        _programTimer.Stop();
    }

    public void ActivateAfterNavigation()
    {
        _pauseWhenOpened = false;
        _core?.SetUiUpdatesEnabled(true);
        UpdateLivePlayPauseIcon();
        ShowPlayerChrome();
        if (_currentItem != null)
        {
            _ = UpdateProgramAsync(_currentItem);
            _programTimer.Stop();
            _programTimer.Start();
        }
    }

    public void SynchronizePlaybackWindow()
    {
        if (!_isNavigatedActive || XamlRoot == null) return;
        try
        {
            RootGrid.InvalidateMeasure();
            RootGrid.InvalidateArrange();
            PlayerArea.InvalidateMeasure();
            PlayerArea.InvalidateArrange();
            RootGrid.UpdateLayout();
            UpdateBottomBarLayout();
            UpdatePlayerAreaClip();
            _hostBinding?.SynchronizeAfterLayout();
        }
        catch (Exception e)
        {
            Logger.E("LivePage", "同步播放窗口布局失败：" + e.Message);
        }
    }

    static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString("h\\:mm\\:ss") : time.ToString("mm\\:ss");
    }

    void UpdateLiveTimeLabel()
    {
        if (_compact && _bottomBarWidth > 0 && _bottomBarWidth < 390)
            LiveTimeText.Text = FormatTime(_displayPositionMs);
        else if (_displayDurationMs > 0)
            LiveTimeText.Text = FormatTime(_displayPositionMs) + " / " + FormatTime(_displayDurationMs);
        else
            LiveTimeText.Text = FormatTime(_displayPositionMs) + " / 直播";
    }

    // ---------- 配置与直播源 ----------

    void OnConfigLoaded()
    {
        GuidePanel.Visibility = Visibility.Collapsed;
        RefreshLives();
    }

    async void OnGuideLoad(object sender, RoutedEventArgs e)
    {
        await LoadGuideAddress(GuideUrlBox.Text?.Trim(), true, false);
    }

    async Task LoadGuideAddress(string address, bool showSuccess, bool preferCache)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            GuidePanel.Visibility = Visibility.Visible;
            GuideInfo.Severity = InfoBarSeverity.Warning;
            GuideInfo.Message = "请输入直播配置地址";
            GuideInfo.IsOpen = true;
            return;
        }

        // Automatic startup restore stays behind the existing live layout. The
        // guide is only shown immediately for an explicit user-initiated load.
        GuidePanel.Visibility = showSuccess ? Visibility.Visible : Visibility.Collapsed;
        GuideInfo.IsOpen = false;
        GuideLoadButton.IsEnabled = false;
        try
        {
            var config = Stores.FindConfig(address, 1);
            if (preferCache) await LiveConfigService.Instance.LoadStartupAsync(config);
            else await LiveConfigService.Instance.LoadAsync(config);
            if (showSuccess) ShowInfo("直播配置加载成功", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            if (!_isNavigatedActive || LiveConfigService.Instance.Lives.Count > 0) return;
            GuidePanel.Visibility = Visibility.Visible;
            GuideInfo.Severity = InfoBarSeverity.Error;
            GuideInfo.Message = "直播配置加载失败：" + ex.Message;
            GuideInfo.IsOpen = true;
        }
        finally { GuideLoadButton.IsEnabled = true; }
    }

    void RefreshLives()
    {
        InvalidateLiveLoad();
        var lives = LiveConfigService.Instance.Lives;
        if (lives.Count == 0)
        {
            SetCatalogLoading(false);
            _live = null;
            _current = null;
            _currentItem = null;
            _allChannels.Clear();
            RebuildChannelPickerItems();
            GroupList.ItemsSource = null;
            ChannelList.ItemsSource = null;
            UpdateLineButton();
            GuidePanel.Visibility = Visibility.Visible;
            return;
        }
        _updating = true;
        LiveCombo.ItemsSource = lives;
        var home = LiveConfigService.Instance.Home;
        LiveCombo.SelectedItem = lives.Contains(home) ? home : lives[0];
        _updating = false;
        _ = LoadLive(LiveCombo.SelectedItem as Models.Live);
    }

    void OnLiveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var live = LiveCombo.SelectedItem as Models.Live;
        if (live == null) return;
        LiveConfigService.Instance.SetHome(live);
        _ = LoadLive(live);
    }

    async Task LoadLive(Models.Live live)
    {
        if (live == null) return;
        InvalidateLiveLoad();
        var generation = _liveLoadGeneration;
        var loadCts = _liveLoadCts = new CancellationTokenSource();
        SetCatalogLoading(true);
        try
        {
            await LiveConfigService.Instance.GetChannels(live);
            if (!IsCurrentLiveLoad(live, loadCts, generation)) return;

            _live = live;
            RebuildAllChannels();
            var items = live.Groups.Where(g => g.Channel.Count > 0).Select(g => new LiveGroupItem(g)).ToList();
            _updating = true;
            GroupList.ItemsSource = items;
            GroupList.SelectedIndex = -1;
            _updating = false;
            _currentGroupItem = null;
            if (items.Count == 0) { ChannelList.ItemsSource = null; return; }
            int idx = items.FindIndex(i => !i.Group.IsHidden);
            GroupList.SelectedIndex = idx < 0 ? 0 : idx; // 触发 OnGroupChanged
        }
        catch (Exception ex)
        {
            if (IsCurrentLiveLoad(live, loadCts, generation))
                ShowInfo("频道加载失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_liveLoadCts, loadCts))
            {
                _liveLoadCts = null;
                SetCatalogLoading(false);
            }
            loadCts.Dispose();
        }
    }

    void InvalidateLiveLoad()
    {
        _liveLoadGeneration++;
        var loading = _liveLoadCts;
        _liveLoadCts = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
    }

    bool IsCurrentLiveLoad(Models.Live live, CancellationTokenSource loadCts, int generation) =>
        !loadCts.IsCancellationRequested &&
        generation == _liveLoadGeneration &&
        ReferenceEquals(_liveLoadCts, loadCts) &&
        ReferenceEquals(LiveCombo.SelectedItem, live);

    void RebuildAllChannels()
    {
        _allChannels = new List<LiveChannel>();
        foreach (var group in _live?.Groups ?? new List<LiveGroup>())
        {
            if (group.IsHidden && !group.Unlocked) continue;
            _allChannels.AddRange(group.Channel);
        }
        RebuildChannelPickerItems();
    }

    // ---------- 分组与密码 ----------

    async void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var item = GroupList.SelectedItem as LiveGroupItem;
        if (item == null) return;
        if (item.Group.IsHidden && !item.Group.Unlocked)
        {
            var ok = await UnlockGroup(item.Group);
            if (!ok)
            {
                _updating = true;
                GroupList.SelectedItem = _currentGroupItem;
                _updating = false;
                return;
            }
            item.Refresh();
            RebuildAllChannels();
        }
        _currentGroupItem = item;
        ShowChannels(item.Group);
    }

    async Task<bool> UnlockGroup(LiveGroup group)
    {
        var box = new PasswordBox { PlaceholderText = "分组密码" };
        var dialog = new ContentDialog
        {
            Title = "该分组已加密",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        try
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return false;
            if (box.Password == group.Pass) { group.Unlocked = true; return true; }
            ShowInfo("密码错误", InfoBarSeverity.Error);
        }
        catch { }
        return false;
    }

    // ---------- 频道列表 ----------

    void ShowChannels(LiveGroup group)
    {
        GroupTitle.Text = group.Name;
        var keeps = LiveSetting.Keeps;
        _channelItems = group.Channel.Select(c => new LiveChannelItem(c) { IsKeep = keeps.Contains(KeepKey(c)) }).ToList();
        ChannelList.ItemsSource = _channelItems;
        _epgCts?.Cancel();
        _epgCts = new CancellationTokenSource();
        _ = FillNowPlaying(_channelItems, _epgCts.Token);
    }

    void RebuildChannelPickerItems()
    {
        var keeps = LiveSetting.Keeps;
        _channelPickerItems = _allChannels
            .Select(channel => new LiveChannelItem(channel) { IsKeep = keeps.Contains(KeepKey(channel)) })
            .ToList();
        ChannelPaneList.ItemsSource = _channelPickerItems;
        SyncChannelPaneSelection(false);
        UpdateSourceControlAvailability();
    }

    void SyncChannelPaneSelection(bool scrollIntoView)
    {
        var selected = _channelPickerItems.FirstOrDefault(item => ReferenceEquals(item.Channel, _current));
        foreach (var item in _channelPickerItems)
            item.IsCurrent = ReferenceEquals(item, selected);
        ChannelPaneList.SelectedItem = selected;
        if (scrollIntoView && selected != null)
        {
            try { ChannelPaneList.ScrollIntoView(selected); } catch { }
        }
    }

    void OnOpenChannelPane(object sender, RoutedEventArgs e)
    {
        var anchor = sender as FrameworkElement;
        QueueChannelPanelMutation(() => OpenChannelPanelCore(anchor));
    }

    void OpenChannelPanelCore(FrameworkElement anchor)
    {
        if (LiveChannelOverlay.Visibility == Visibility.Visible)
        {
            CloseChannelPanel();
            return;
        }
        if (_channelPickerItems.Count == 0) return;

        _linePanelMutationVersion++;
        CloseLinePanel(false);
        _channelPanelAnchor = anchor ?? ChannelButton;
        RefreshChannelPanel(false);
        LiveChannelOverlay.Opacity = 0;
        LiveChannelOverlay.Visibility = Visibility.Visible;
        LiveChannelOverlay.UpdateLayout();
        SyncChannelPaneSelection(true);
        PositionChannelPanel();
        LiveChannelOverlay.Opacity = 1;
        ShowPlayerChrome();
    }

    void RefreshChannelPanel(bool scrollIntoView)
    {
        ChannelPaneList.ItemsSource = _channelPickerItems;
        SyncChannelPaneSelection(scrollIntoView);
    }

    void PositionChannelPanel()
    {
        if (LiveChannelOverlay.Visibility != Visibility.Visible) return;

        const double edge = 12;
        const double gap = 8;
        var overlayWidth = LiveChannelOverlay.ActualWidth > 0
            ? LiveChannelOverlay.ActualWidth
            : PlayerArea.ActualWidth;
        var overlayHeight = LiveChannelOverlay.ActualHeight > 0
            ? LiveChannelOverlay.ActualHeight
            : PlayerArea.ActualHeight;
        if (overlayWidth <= 0 || overlayHeight <= 0) return;

        var panelWidth = Math.Min(280, Math.Max(1, overlayWidth - edge * 2));
        var anchor = _channelPanelAnchor ?? ChannelButton;
        double anchorLeft;
        double anchorTop;
        double anchorWidth;
        try
        {
            if (anchor == null || anchor.ActualWidth <= 0)
                throw new InvalidOperationException();
            var point = anchor.TransformToVisual(LiveChannelOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            anchorLeft = point.X;
            anchorTop = point.Y;
            anchorWidth = anchor.ActualWidth;
        }
        catch
        {
            anchorLeft = overlayWidth - edge - panelWidth;
            anchorTop = overlayHeight - (_compact ? 74 : 112);
            anchorWidth = panelWidth;
        }

        var availableHeight = Math.Max(1, anchorTop - edge - gap);
        var panelMaxHeight = Math.Min(420, availableHeight);
        LiveChannelPanel.Width = panelWidth;
        LiveChannelPanel.MaxHeight = panelMaxHeight;
        LiveChannelPanel.Margin = new Thickness(0);
        LiveChannelPanel.Measure(new Windows.Foundation.Size(panelWidth, panelMaxHeight));

        var panelHeight = Math.Min(panelMaxHeight, Math.Max(1, LiveChannelPanel.DesiredSize.Height));
        var left = anchorLeft + (anchorWidth - panelWidth) / 2;
        left = Math.Clamp(left, edge, Math.Max(edge, overlayWidth - panelWidth - edge));
        var top = Math.Max(edge, anchorTop - gap - panelHeight);
        LiveChannelPanel.Margin = new Thickness(left, top, 0, 0);
    }

    void OnChannelOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LiveChannelOverlay.Visibility == Visibility.Visible) PositionChannelPanel();
    }

    void OnChannelOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        QueueChannelPanelMutation(() => CloseChannelPanel());
        e.Handled = true;
    }

    void OnChannelPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    void OnCloseChannelPane(object sender, RoutedEventArgs e) =>
        QueueChannelPanelMutation(() => CloseChannelPanel());

    void CloseChannelPanel(bool showChrome = true)
    {
        if (LiveChannelOverlay.Visibility != Visibility.Visible) return;
        LiveChannelOverlay.Visibility = Visibility.Collapsed;
        _channelPanelAnchor = null;
        ChannelPaneList.ItemsSource = null;
        if (showChrome) ShowPlayerChrome();
    }

    void QueueChannelPanelMutation(Action mutation)
    {
        var version = ++_channelPanelMutationVersion;
        void CommitMutation()
        {
            if (!_isNavigatedActive || version != _channelPanelMutationVersion) return;
            mutation();
        }
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            CommitMutation);
    }

    void OnChannelPaneItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LiveChannelItem item) return;
        var channel = item.Channel;
        QueueChannelPanelMutation(() =>
        {
            CloseChannelPanel();
            if (channel == null || !_allChannels.Contains(channel) || ReferenceEquals(channel, _current)) return;
            PlayByChannel(channel);
        });
    }

    /// <summary>逐个异步填充「当前节目名」（EpgService 内部有缓存，串行避免拥塞）。</summary>
    async Task FillNowPlaying(List<LiveChannelItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var epg = await EpgService.Instance.Get(item.Channel);
                var now = epg?.Now();
                if (now != null && !ct.IsCancellationRequested) App.Post(() => item.NowText = now.Title);
            }
            catch { }
        }
    }

    void OnChannelClick(object sender, ItemClickEventArgs e) => PlayChannel(e.ClickedItem as LiveChannelItem);

    void OnChannelRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as LiveChannelItem;
        if (item == null) return;
        var flyout = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = "播放" };
        play.Click += (s, a) => PlayChannel(item);
        flyout.Items.Add(play);
        var keep = new MenuFlyoutItem { Text = item.IsKeep ? "取消收藏" : "收藏" };
        keep.Click += (s, a) => ToggleKeep(item);
        flyout.Items.Add(keep);
        if (item.Channel.Urls.Count > 1)
        {
            var line = new MenuFlyoutItem { Text = "下一线路" };
            line.Click += (s, a) =>
            {
                item.Channel.UrlIndex = (item.Channel.UrlIndex + 1) % item.Channel.Urls.Count;
                PlayChannel(item);
            };
            flyout.Items.Add(line);
        }
        var copy = new MenuFlyoutItem { Text = "复制地址" };
        copy.Click += (s, a) => CopyUrl(item.Channel);
        flyout.Items.Add(copy);
        flyout.ShowAt(ChannelList, e.GetPosition(ChannelList));
    }

    void OnKeepClick(object sender, RoutedEventArgs e) => ToggleKeep((sender as FrameworkElement)?.DataContext as LiveChannelItem);

    void ToggleKeep(LiveChannelItem item)
    {
        if (item == null) return;
        var keeps = LiveSetting.Keeps;
        var key = KeepKey(item.Channel);
        if (keeps.Contains(key)) keeps.Remove(key);
        else keeps.Add(key);
        LiveSetting.Keeps = keeps;
        item.IsKeep = keeps.Contains(key);
    }

    string KeepKey(LiveChannel channel) => $"{channel.Live?.Name ?? _live?.Name}@{channel.Name}";

    static void CopyUrl(LiveChannel channel)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(channel.CurrentUrl());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch { }
    }

    // ---------- 播放 ----------

    void PlayChannel(LiveChannelItem item)
    {
        if (item == null) return;
        _ = PlayChannelAsync(item);
    }

    async Task PlayChannelAsync(LiveChannelItem item)
    {
        if (item == null || !_isNavigatedActive) return;
        _pauseWhenOpened = false;
        if (_core == null) { InitPlayer(); if (_core == null) return; }
        var cts = StartPlayRequest(out var generation);
        var ct = cts.Token;
        try
        {
            // Cancel both resolver work and a Flyleaf open that may already have
            // started for the previous channel before resolving the new address.
            _core.Stop();
            _pendingSeekMs = null;
            _pendingSeekRequestId = 0;
            SetSourceTransition(true);
            _currentItem = item;
            _current = item.Channel;
            SyncChannelPaneSelection(false);
            ChannelNameText.Text = item.Channel.Name;
            ProgramText.Text = item.NowText;
            NextProgramTopText.Visibility = Visibility.Collapsed;
            UpdateLineButton();
            PlayInfo.IsOpen = false;
            SetPlaybackLoading(true, false);
            _updatingSeek = true;
            LiveProgressRow.Visibility = Visibility.Collapsed;
            LiveBufferBar.IsIndeterminate = false;
            LiveBufferBar.Value = 0;
            LiveSeekSlider.Value = 0;
            _updatingSeek = false;
            _displayPositionMs = _displayDurationMs = 0;
            UpdateLiveTimeLabel();
            var play = await PlayResolver.ResolveLive(item.Channel, ct);
            if (!IsCurrentPlayRequest(cts, generation)) return;
            SetPlaybackLoading(true, true);
            _core.Open(play);
            _ = UpdateProgramAsync(item);
            _programTimer.Stop();
            _programTimer.Start();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentPlayRequest(cts, generation))
            {
                SetPlaybackLoading(false);
                SetSourceTransition(false);
                ShowInfo("播放失败：" + ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_playCts, cts)) _playCts = null;
            cts.Dispose();
        }
    }

    CancellationTokenSource StartPlayRequest(out int generation)
    {
        var previous = _playCts;
        var current = new CancellationTokenSource();
        _playCts = current;
        generation = ++_playGeneration;
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        return current;
    }

    bool IsCurrentPlayRequest(CancellationTokenSource cts, int generation) =>
        _isNavigatedActive && !cts.IsCancellationRequested && generation == _playGeneration &&
        ReferenceEquals(_playCts, cts) && _core != null;

    void CancelPlayRequest()
    {
        _playGeneration++;
        var current = _playCts;
        _playCts = null;
        try { current?.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { current?.Dispose(); }
    }

    async Task UpdateProgramAsync(LiveChannelItem item)
    {
        try
        {
            var epg = await EpgService.Instance.Get(item.Channel);
            var now = epg?.Now();
            long nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var next = epg?.List
                .Where(d => d.StartTime > nowMs && (now == null || d.StartTime >= now.EndTime))
                .OrderBy(d => d.StartTime)
                .FirstOrDefault();
            App.Post(() =>
            {
                if (!_isNavigatedActive) return;
                item.NowText = now?.Title ?? "";
                if (_currentItem == item) UpdateProgramDisplay(now, next);
            });
        }
        catch { }
    }

    void UpdateProgramDisplay(EpgData now, EpgData next)
    {
        if (now == null)
        {
            ProgramText.Text = "暂无当前节目数据";
        }
        else
        {
            ProgramText.Text = $"{CompactTimeRange(now)}  {now.Title}";
        }

        if (next == null)
        {
            NextProgramTopText.Text = "";
            NextProgramTopText.Visibility = Visibility.Collapsed;
        }
        else
        {
            var nextText = $"接下来 {StartText(next)}  {next.Title}";
            NextProgramTopText.Text = nextText;
            NextProgramTopText.Visibility = Visibility.Visible;
        }
    }

    static string CompactTimeRange(EpgData data) => data?.TimeRange?.Replace(" ~ ", " - ") ?? "";

    static string StartText(EpgData data)
    {
        if (data?.StartTime <= 0) return data?.Start ?? "";
        return DateTimeOffset.FromUnixTimeMilliseconds(data.StartTime).ToLocalTime().ToString("HH:mm");
    }

    void PlayByChannel(LiveChannel channel)
    {
        if (channel == null) return;
        var groupItems = GroupList.ItemsSource as List<LiveGroupItem>;
        var gi = groupItems?.FirstOrDefault(g => g.Group == channel.Group);
        if (gi != null && GroupList.SelectedItem != gi)
        {
            _updating = true;
            GroupList.SelectedItem = gi;
            _updating = false;
            _currentGroupItem = gi;
            ShowChannels(channel.Group);
        }
        var item = _channelItems.FirstOrDefault(i => i.Channel == channel);
        if (item == null) return;
        ChannelList.SelectedItem = item;
        ChannelList.ScrollIntoView(item);
        PlayChannel(item);
    }

    void ChangeChannel(int delta)
    {
        if (_allChannels.Count == 0) return;
        int idx = _current != null ? _allChannels.IndexOf(_current) : -1;
        idx = idx < 0 ? 0 : ((idx + delta) % _allChannels.Count + _allChannels.Count) % _allChannels.Count;
        PlayByChannel(_allChannels[idx]);
    }

    void OnPrevChannel(object sender, RoutedEventArgs e) => ChangeChannel(-1);

    void OnNextChannel(object sender, RoutedEventArgs e) => ChangeChannel(1);

    void OnLivePlayPause(object sender, RoutedEventArgs e)
    {
        if (_core == null) return;
        _pauseWhenOpened = false;
        _core.PlayPause();
        UpdateLivePlayPauseIcon();
        ShowPlayerChrome();
    }

    void UpdateLivePlayPauseIcon()
    {
        LivePlayPauseIcon.Glyph = _core?.IsPlaying == true ? "\uE769" : "\uE768";
    }

    void OnLiveSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || _core == null || _core.DurationMs <= 0) return;
        var target = (long)Math.Clamp(e.NewValue, 0, _core.DurationMs);
        if (_pendingSeekMs == null && Math.Abs(target - _core.PositionMs) < 800) return;
        var requestId = _core.SeekMs(target);
        if (requestId <= 0) return;
        _pendingSeekMs = target;
        _pendingSeekRequestId = requestId;
        _displayPositionMs = target;
        UpdateLiveTimeLabel();
    }

    void OnBottomBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _bottomBarWidth = e.NewSize.Width;
        UpdateBottomBarLayout();
    }

    void UpdateBottomBarLayout()
    {
        var width = _bottomBarWidth > 0 ? _bottomBarWidth : BottomBar.ActualWidth;
        var compact = _compact;
        ChannelButton.Visibility = !compact || width >= 360
            ? Visibility.Visible
            : Visibility.Collapsed;
        LineButton.Visibility = _lineAvailable && (!compact || width >= 440)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LineText.Visibility = !compact || width >= 560 ? Visibility.Visible : Visibility.Collapsed;
        LiveTimeText.MaxWidth = compact && width < 390 ? 54 : 132;
        UpdateLiveTimeLabel();
    }

    void SetSourceTransition(bool inProgress)
    {
        _sourceTransitionInProgress = inProgress;
        UpdateSourceControlAvailability();
    }

    void UpdateSourceControlAvailability()
    {
        var hasChannels = _allChannels.Count > 0;
        ChannelList.IsEnabled = hasChannels;
        ChannelPaneList.IsEnabled = hasChannels;
        ChannelButton.IsEnabled = hasChannels;
        PrevChannelButton.IsEnabled = hasChannels;
        NextChannelButton.IsEnabled = hasChannels;
        LineButton.IsEnabled = !_sourceTransitionInProgress && _lineAvailable;
    }

    void OnOpenLinePanel(object sender, RoutedEventArgs e)
    {
        var anchor = sender as FrameworkElement;
        QueueLinePanelMutation(() => OpenLinePanelCore(anchor));
    }

    void OpenLinePanelCore(FrameworkElement anchor)
    {
        if (LiveLineOverlay.Visibility == Visibility.Visible)
        {
            CloseLinePanel();
            return;
        }

        var channel = _current;
        if (_sourceTransitionInProgress || channel == null || channel.Urls.Count <= 1) return;
        _channelPanelMutationVersion++;
        CloseChannelPanel(false);
        _linePanelAnchor = anchor ?? LineButton;
        RefreshLinePanel();
        LiveLineOverlay.Opacity = 0;
        LiveLineOverlay.Visibility = Visibility.Visible;
        LiveLineOverlay.UpdateLayout();
        PositionLinePanel();
        LiveLineOverlay.Opacity = 1;
        LineChevron.Glyph = "\uE70E";
        ShowPlayerChrome();
    }

    void PositionLinePanel()
    {
        if (LiveLineOverlay.Visibility != Visibility.Visible) return;

        const double edge = 12;
        const double gap = 8;
        var overlayWidth = LiveLineOverlay.ActualWidth > 0
            ? LiveLineOverlay.ActualWidth
            : PlayerArea.ActualWidth;
        var overlayHeight = LiveLineOverlay.ActualHeight > 0
            ? LiveLineOverlay.ActualHeight
            : PlayerArea.ActualHeight;
        if (overlayWidth <= 0 || overlayHeight <= 0) return;

        var panelWidth = Math.Min(260, Math.Max(1, overlayWidth - edge * 2));
        var anchor = _linePanelAnchor ?? LineButton;
        double anchorLeft;
        double anchorTop;
        double anchorWidth;
        try
        {
            if (anchor == null || anchor.ActualWidth <= 0)
                throw new InvalidOperationException();
            var point = anchor.TransformToVisual(LiveLineOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            anchorLeft = point.X;
            anchorTop = point.Y;
            anchorWidth = anchor.ActualWidth;
        }
        catch
        {
            anchorLeft = overlayWidth - edge - panelWidth;
            anchorTop = overlayHeight - (_compact ? 74 : 112);
            anchorWidth = panelWidth;
        }

        var availableHeight = Math.Max(1, anchorTop - edge - gap);
        var panelMaxHeight = Math.Min(380, availableHeight);
        LiveLinePanel.Width = panelWidth;
        LiveLinePanel.MaxHeight = panelMaxHeight;
        LiveLinePanel.Margin = new Thickness(0);
        LiveLinePanel.Measure(new Windows.Foundation.Size(panelWidth, panelMaxHeight));

        var panelHeight = Math.Min(panelMaxHeight, Math.Max(1, LiveLinePanel.DesiredSize.Height));
        var left = anchorLeft + (anchorWidth - panelWidth) / 2;
        left = Math.Clamp(left, edge, Math.Max(edge, overlayWidth - panelWidth - edge));
        var top = Math.Max(edge, anchorTop - gap - panelHeight);
        LiveLinePanel.Margin = new Thickness(left, top, 0, 0);
    }

    void OnLineOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LiveLineOverlay.Visibility == Visibility.Visible) PositionLinePanel();
    }

    void RefreshLinePanel()
    {
        var channel = _current;
        var items = channel == null
            ? new List<LiveLineSelectionItem>()
            : channel.Urls.Select((_, index) => new LiveLineSelectionItem(
                channel.CurrentLineName(index), index, index == channel.UrlIndex)).ToList();
        LiveLineList.ItemsSource = items;
        LiveLineList.SelectedItem = items.FirstOrDefault(item => item.IsSelected);
    }

    void OnLinePanelItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LiveLineSelectionItem item) return;
        var channel = _current;
        var index = item.Index;
        QueueLinePanelMutation(() =>
        {
            CloseLinePanel();
            if (channel == null || !ReferenceEquals(channel, _current) ||
                index < 0 || index >= channel.Urls.Count || index == channel.UrlIndex)
            {
                return;
            }

            var currentItem = _currentItem;
            if (currentItem == null || !ReferenceEquals(currentItem.Channel, channel)) return;
            channel.UrlIndex = index;
            UpdateLineButton();
            PlayChannel(currentItem);
        });
    }

    // Keep overlay collapse/rebinding out of the WinUI routed-input stack.
    void QueueLinePanelMutation(Action mutation)
    {
        var version = ++_linePanelMutationVersion;
        void CommitMutation()
        {
            if (!_isNavigatedActive || version != _linePanelMutationVersion) return;
            mutation();
        }
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            CommitMutation);
    }

    void OnLineOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        QueueLinePanelMutation(() => CloseLinePanel());
        e.Handled = true;
    }

    void OnLinePanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    void OnCloseLinePanel(object sender, RoutedEventArgs e) =>
        QueueLinePanelMutation(() => CloseLinePanel());

    void CloseLinePanel(bool showChrome = true)
    {
        if (LiveLineOverlay.Visibility != Visibility.Visible) return;
        LiveLineOverlay.Visibility = Visibility.Collapsed;
        _linePanelAnchor = null;
        LiveLineList.ItemsSource = null;
        LineChevron.Glyph = "\uE70D";
        if (showChrome) ShowPlayerChrome();
    }

    void UpdateLineButton()
    {
        var ch = _current;
        _lineAvailable = ch != null && ch.Urls.Count > 1;
        LineText.Text = ch != null && ch.Urls.Count > 0 ? ch.CurrentLineName(ch.UrlIndex) : "线路";
        if (LiveLineOverlay.Visibility == Visibility.Visible)
        {
            QueueLinePanelMutation(() =>
            {
                if (LiveLineOverlay.Visibility != Visibility.Visible) return;
                if (_lineAvailable) RefreshLinePanel();
                else CloseLinePanel(false);
            });
        }
        UpdateSourceControlAvailability();
        UpdateBottomBarLayout();
    }

    // ---------- 全屏与按键 ----------

    void OnPlayerTapped(object sender, TappedRoutedEventArgs e)
    {
        ShowPlayerChrome();
        Focus(FocusState.Programmatic);
    }

    void OnPlayerPointerMoved(object sender, PointerRoutedEventArgs e) => ShowPlayerChrome();

    void OnCompactDragPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_compact || !e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        App.Main.BeginCompactDrag();
    }

    void OnPlayerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LiveLineOverlay.Visibility == Visibility.Visible)
        {
            QueueLinePanelMutation(() =>
            {
                CloseLinePanel(false);
                ToggleFullscreen();
            });
        }
        else if (LiveChannelOverlay.Visibility == Visibility.Visible)
        {
            QueueChannelPanelMutation(() =>
            {
                CloseChannelPanel(false);
                ToggleFullscreen();
            });
        }
        else ToggleFullscreen();
        e.Handled = true;
    }

    void OnFullClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    void ToggleFullscreen()
    {
        if (!_isNavigatedActive || App.Main.IsPlaybackWindowTransitionActive) return;
        _hostBinding?.BeginPresentationTransition();
        _channelPanelMutationVersion++;
        _linePanelMutationVersion++;
        CloseChannelPanel(false);
        CloseLinePanel(false);
        var entering = !_fullscreen;
        if (entering)
        {
            _fullscreen = true;
            _compact = false;
            ApplyPresentationMode();
            try
            {
                if (!App.Main.EnterPlaybackFullScreen())
                {
                    _fullscreen = false;
                    ApplyPresentationMode();
                }
            }
            catch
            {
                _fullscreen = false;
                ApplyPresentationMode();
            }
        }
        else
        {
            if (!App.Main.RestorePlaybackWindow()) return;
            _fullscreen = false;
            ApplyPresentationMode();
        }
        if (entering) App.Main.RefreshImmersiveFrame(_fullscreen);
        ShowPlayerChrome();
        Focus(FocusState.Programmatic);
    }

    void OnLivePip(object sender, RoutedEventArgs e)
    {
        if (!_isNavigatedActive || App.Main.IsPlaybackWindowTransitionActive) return;
        _hostBinding?.BeginPresentationTransition();
        _channelPanelMutationVersion++;
        _linePanelMutationVersion++;
        CloseChannelPanel(false);
        CloseLinePanel(false);
        var entering = !_compact;
        if (entering)
        {
            _compact = true;
            _fullscreen = false;
            ApplyPresentationMode();
            try
            {
                if (!App.Main.EnterPlaybackCompact())
                {
                    _compact = false;
                    ApplyPresentationMode();
                }
            }
            catch
            {
                _compact = false;
                ApplyPresentationMode();
            }
        }
        else
        {
            if (!App.Main.RestorePlaybackWindow()) return;
            _compact = false;
            ApplyPresentationMode();
        }
        if (entering && _compact) App.Main.RefreshImmersiveFrame(_fullscreen);
        ShowPlayerChrome();
        Focus(FocusState.Programmatic);
    }

    void ApplyPresentationMode()
    {
        var immersive = _fullscreen || _compact;
        PageHeader.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        LeftPane.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        MidPane.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        if (immersive)
        {
            LeftCol.Width = new GridLength(0);
            MidCol.Width = new GridLength(0);
        }
        else UpdatePaneWidths(ActualWidth);

        RootGrid.Padding = immersive ? new Thickness(0) : _normalPagePadding;
        MainGrid.Margin = immersive ? new Thickness(0) : _normalContentMargin;
        RootGrid.Background = _fullscreen
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
            : null;
        LivePlayerSurface.Margin = immersive
            ? new Thickness(0)
            : (Thickness)Application.Current.Resources["LivePlayerSurfaceMargin"];
        UpdatePlayerAreaClip();
        TopBar.Padding = _compact ? new Thickness(10, 8, 10, 22) : new Thickness(20, 16, 20, 28);
        BottomBar.Padding = _compact ? new Thickness(8, 32, 8, 8) : new Thickness(20, 52, 20, 14);
        LiveFullIcon.Glyph = _fullscreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullButton, _fullscreen ? "退出全屏" : "全屏");
        LivePipEnterIcon.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        LivePipExitIcon.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        var pipLabel = _compact ? "退出小窗模式" : "小窗模式";
        ToolTipService.SetToolTip(LivePipButton, pipLabel);
        AutomationProperties.SetName(LivePipButton, pipLabel);
        _core?.SetViewportFill(_fullscreen);
        App.Main.SetImmersive(immersive);
        UpdateBottomBarLayout();
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdatePlayerAreaClip();
            _hostBinding?.RequestSynchronize();
        });
    }

    void OnPlayerAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayerAreaClip();

    void UpdatePlayerAreaClip()
    {
        UpdateCornerMaskLayer();
        var visual = ElementCompositionPreview.GetElementVisual(PlayerArea);
        if (_fullscreen)
        {
            visual.Clip = null;
            _hostBinding?.SetSurfaceCornerRadius(0);
            return;
        }

        var width = PlayerArea.ActualWidth;
        var height = PlayerArea.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            visual.Clip = null;
            return;
        }

        var offsetX = 0d;
        var offsetY = 0d;
        var clipWidth = width;
        var clipHeight = height;
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1d;
            var origin = PlayerArea.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var left = Math.Ceiling(origin.X * scale - 0.001d) / scale;
            var top = Math.Ceiling(origin.Y * scale - 0.001d) / scale;
            var right = Math.Floor((origin.X + width) * scale + 0.001d) / scale;
            var bottom = Math.Floor((origin.Y + height) * scale + 0.001d) / scale;
            if (right > left && bottom > top)
            {
                offsetX = left - origin.X;
                offsetY = top - origin.Y;
                clipWidth = right - left;
                clipHeight = bottom - top;
            }
        }
        catch { }

        _playerAreaClipGeometry ??= visual.Compositor.CreateRoundedRectangleGeometry();
        _playerAreaClip ??= visual.Compositor.CreateGeometricClip(_playerAreaClipGeometry);
        _playerAreaClipGeometry.Offset = new Vector2((float)offsetX, (float)offsetY);
        _playerAreaClipGeometry.Size = new Vector2((float)clipWidth, (float)clipHeight);
        var radius = (float)SurfaceCornerRadius().TopLeft;
        _playerAreaClipGeometry.CornerRadius = new Vector2(radius);
        visual.Clip = _playerAreaClip;
        _hostBinding?.SetSurfaceCornerRadius(radius);
    }

    void UpdateCornerMaskLayer()
    {
        LiveCornerMaskLayer.Visibility = _fullscreen
            ? Visibility.Collapsed
            : Visibility.Visible;
        var radius = Math.Max(0, SurfaceCornerRadius().TopLeft);
        var fill = _compact
            ? _compactCornerMaskBrush
            : Application.Current.Resources["LayerOnMicaBaseAltFillColorTertiaryBrush"]
                as Microsoft.UI.Xaml.Media.Brush;
        foreach (var corner in new Microsoft.UI.Xaml.Shapes.Path[]
        {
            LiveCornerMaskTopLeft,
            LiveCornerMaskTopRight,
            LiveCornerMaskBottomRight,
            LiveCornerMaskBottomLeft,
        })
        {
            corner.Width = radius;
            corner.Height = radius;
            if (fill != null) corner.Fill = fill;
        }
    }

    static CornerRadius SurfaceCornerRadius() =>
        Application.Current.Resources["SurfaceCornerRadius"] is CornerRadius radius
            ? radius
            : new CornerRadius(8);

    void UpdatePaneWidths(double width)
    {
        if (_fullscreen || _compact) return;
        if (width >= 1450)
        {
            LeftCol.Width = new GridLength(232);
            MidCol.Width = new GridLength(320);
        }
        else if (width >= 1050)
        {
            LeftCol.Width = new GridLength(210);
            MidCol.Width = new GridLength(280);
        }
        else
        {
            LeftCol.Width = new GridLength(176);
            MidCol.Width = new GridLength(240);
        }
    }

    void ShowPlayerChrome()
    {
        TopBar.Opacity = 1;
        TopBar.IsHitTestVisible = true;
        BottomBar.Opacity = 1;
        BottomBar.IsHitTestVisible = true;
        _chromeTimer.Stop();
        if (_fullscreen || _compact) _chromeTimer.Start();
    }

    void HidePlayerChrome()
    {
        _chromeTimer.Stop();
        if (!_fullscreen && !_compact) return;
        if (LiveChannelOverlay.Visibility == Visibility.Visible ||
            LiveLineOverlay.Visibility == Visibility.Visible) return;
        if (_core?.IsPlaying != true) return;
        TopBar.Opacity = 0;
        TopBar.IsHitTestVisible = false;
        BottomBar.Opacity = 0;
        BottomBar.IsHitTestVisible = false;
    }

    void OnPreviewKey(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox || focused is PasswordBox || focused is AutoSuggestBox) return;
        int digit = KeyToDigit(e.Key);
        if (digit >= 0) { PushNumber(digit); e.Handled = true; return; }
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (LiveLineOverlay.Visibility == Visibility.Visible)
                {
                    QueueLinePanelMutation(() => CloseLinePanel());
                    e.Handled = true;
                }
                else if (LiveChannelOverlay.Visibility == Visibility.Visible)
                {
                    QueueChannelPanelMutation(() => CloseChannelPanel());
                    e.Handled = true;
                }
                else if (_compact) { OnLivePip(null, null); e.Handled = true; }
                else if (_fullscreen) { ToggleFullscreen(); e.Handled = true; }
                break;
            case VirtualKey.Space:
                OnLivePlayPause(null, null); e.Handled = true; break;
            case VirtualKey.F:
                if (LiveLineOverlay.Visibility == Visibility.Visible)
                {
                    QueueLinePanelMutation(() =>
                    {
                        CloseLinePanel(false);
                        ToggleFullscreen();
                    });
                }
                else if (LiveChannelOverlay.Visibility == Visibility.Visible)
                {
                    QueueChannelPanelMutation(() =>
                    {
                        CloseChannelPanel(false);
                        ToggleFullscreen();
                    });
                }
                else ToggleFullscreen();
                e.Handled = true;
                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
                // 焦点在列表内时保留列表自身的方向键导航
                if (_fullscreen || _compact || ReferenceEquals(focused, this) || focused == null)
                {
                    ChangeChannel(e.Key == VirtualKey.Up ? -1 : 1);
                    e.Handled = true;
                }
                break;
            case VirtualKey.Enter:
                if (!string.IsNullOrEmpty(_numberBuffer)) { CommitNumber(); e.Handled = true; }
                break;
        }
    }

    static int KeyToDigit(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9) return key - VirtualKey.Number0;
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9) return key - VirtualKey.NumberPad0;
        return -1;
    }

    void PushNumber(int n)
    {
        _numberBuffer += n.ToString();
        if (_numberBuffer.Length > 4) _numberBuffer = _numberBuffer[^4..];
        NumberText.Text = _numberBuffer;
        NumberOverlay.Visibility = Visibility.Visible;
        _numberTimer.Stop();
        _numberTimer.Start();
    }

    void CommitNumber()
    {
        _numberTimer.Stop();
        NumberOverlay.Visibility = Visibility.Collapsed;
        var buf = _numberBuffer;
        _numberBuffer = "";
        if (string.IsNullOrEmpty(buf)) return;
        var target = _allChannels.FirstOrDefault(c => !string.IsNullOrEmpty(c.Number) && c.Number.TrimStart('0') == buf.TrimStart('0'));
        if (target == null && int.TryParse(buf, out var idx) && idx >= 1 && idx <= _allChannels.Count) target = _allChannels[idx - 1];
        if (target != null) PlayByChannel(target);
    }

    void ShowInfo(string msg, InfoBarSeverity severity)
    {
        PlayInfo.Severity = severity;
        PlayInfo.Message = msg;
        PlayInfo.IsOpen = true;
    }
}

public sealed class LiveLineSelectionItem
{
    public LiveLineSelectionItem(string label, int index, bool isSelected)
    {
        Label = label ?? "";
        Index = index;
        IsSelected = isSelected;
    }

    public string Label { get; }
    public int Index { get; }
    public bool IsSelected { get; }
    public string CheckGlyph => IsSelected ? "\uE73E" : "";
}

/// <summary>直播页私有设置包装（不改 Core/Setting.cs）：收藏频道存 JSON 列表，键 "live_keep"。</summary>
static class LiveSetting
{
    public static List<string> Keeps
    {
        get
        {
            try { return JsonUtil.Deserialize<List<string>>(Setting.GetString("live_keep", "[]")) ?? new(); }
            catch { return new(); }
        }
        set => Setting.Put("live_keep", JsonUtil.Serialize(value ?? new List<string>()));
    }
}

/// <summary>分组列表项视图。</summary>
public class LiveGroupItem : INotifyPropertyChanged
{
    public LiveGroupItem(LiveGroup group) => Group = group;

    public LiveGroup Group { get; }
    public string Name => Group.Name;
    public string LockGlyph => Group.IsHidden && !Group.Unlocked ? "\uE72E" : "";

    public event PropertyChangedEventHandler PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockGlyph)));
}

/// <summary>频道列表项视图（当前节目名异步填充）。</summary>
public class LiveChannelItem : INotifyPropertyChanged
{
    string _now = "";
    bool _keep;
    bool _isCurrent;

    public LiveChannelItem(LiveChannel channel) => Channel = channel;

    public LiveChannel Channel { get; }
    public string Name => Channel.Name;
    public string Number => Channel.Number;
    public string Logo => Channel.GetLogo();
    public string GroupName => Channel.Group?.Name ?? "";
    public string NowText { get => _now; set { _now = value ?? ""; Notify(nameof(NowText)); } }
    public bool IsKeep { get => _keep; set { _keep = value; Notify(nameof(KeepGlyph)); } }
    public string KeepGlyph => _keep ? "\uE735" : "\uE734";
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            Notify(nameof(CheckGlyph));
        }
    }
    public string CheckGlyph => _isCurrent ? "\uE73E" : "";

    public event PropertyChangedEventHandler PropertyChanged;
    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
