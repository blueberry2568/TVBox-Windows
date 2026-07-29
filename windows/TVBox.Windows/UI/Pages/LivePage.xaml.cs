using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    double _bottomBarWidth;
    long _displayPositionMs;
    long _displayDurationMs;
    readonly Microsoft.UI.Xaml.Media.Brush _normalSurfaceBackground;

    public LivePage()
    {
        InitializeComponent();
        _normalSurfaceBackground = LiveSurface.Background;
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
        InitPlayer();
        LiveConfigService.Instance.Loaded += OnConfigLoaded;
        if (LiveConfigService.Instance.Lives.Count > 0)
        {
            RefreshLives();
            return;
        }

        var address = Setting.ConfigLive?.Trim();
        GuideUrlBox.Text = address ?? "";
        if (!string.IsNullOrEmpty(address)) await LoadGuideAddress(address, false);
        else GuidePanel.Visibility = Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        LiveConfigService.Instance.Loaded -= OnConfigLoaded;
        InvalidateLiveLoad();
        _playCts?.Cancel();
        _epgCts?.Cancel();
        _numberTimer.Stop();
        _chromeTimer.Stop();
        _programTimer.Stop();
        if (_fullscreen || _compact)
        {
            _fullscreen = _compact = false;
            try { App.Main.RestorePlaybackWindow(); } catch { }
            ApplyPresentationMode();
        }
        else App.Main.SetImmersive(false);
        _hostBinding?.Dispose();
        _hostBinding = null;
        if (_core != null)
        {
            _core.Opened -= OnCoreOpened;
            _core.Errored -= OnCoreErrored;
            _core.TimeChanged -= OnCoreTime;
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
            LoadingRing.IsActive = false;
            if (_pauseWhenOpened && _core?.IsPlaying == true) _core.PlayPause();
            _hostBinding?.RequestSynchronize();
            UpdateLivePlayPauseIcon();
        });
    }

    void OnCoreErrored(string message)
    {
        App.Post(() =>
        {
            LoadingRing.IsActive = false;
            LiveBufferBar.IsIndeterminate = false;
            UpdateLivePlayPauseIcon();
            ShowInfo("播放错误：" + message, InfoBarSeverity.Error);
        });
    }

    void OnCoreTime(long positionMs)
    {
        if (_core == null) return;
        var durationMs = _core.DurationMs;
        _displayPositionMs = Math.Max(0, positionMs);
        _displayDurationMs = Math.Max(0, durationMs);
        long bufferedMs = 0;
        try { bufferedMs = _core.Fly.BufferedDuration / 10000; } catch { }

        _updatingSeek = true;
        if (durationMs > 0)
        {
            LiveProgressRow.Visibility = Visibility.Visible;
            LiveSeekSlider.IsHitTestVisible = true;
            LiveSeekSlider.Maximum = durationMs;
            LiveSeekSlider.Value = Math.Min(positionMs, durationMs);
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

    public void PauseForNavigation()
    {
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
        await LoadGuideAddress(GuideUrlBox.Text?.Trim(), true);
    }

    async Task LoadGuideAddress(string address, bool showSuccess)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            GuidePanel.Visibility = Visibility.Visible;
            GuideInfo.Severity = InfoBarSeverity.Warning;
            GuideInfo.Message = "请输入直播配置地址";
            GuideInfo.IsOpen = true;
            return;
        }

        GuidePanel.Visibility = Visibility.Visible;
        GuideInfo.IsOpen = false;
        GuideLoadButton.IsEnabled = false;
        try
        {
            await LiveConfigService.Instance.LoadAsync(Stores.FindConfig(address, 1));
            if (showSuccess) ShowInfo("直播配置加载成功", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
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
            LoadingRing.IsActive = false;
            _live = null;
            GroupList.ItemsSource = null;
            ChannelList.ItemsSource = null;
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
        LoadingRing.IsActive = true;
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
                LoadingRing.IsActive = false;
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

    async void PlayChannel(LiveChannelItem item)
    {
        if (item == null) return;
        _pauseWhenOpened = false;
        if (_core == null) { InitPlayer(); if (_core == null) return; }
        _currentItem = item;
        _current = item.Channel;
        ChannelNameText.Text = item.Channel.Name;
        ProgramText.Text = item.NowText;
        NextProgramTopText.Visibility = Visibility.Collapsed;
        UpdateLineButton();
        PlayInfo.IsOpen = false;
        LoadingRing.IsActive = true;
        _updatingSeek = true;
        LiveProgressRow.Visibility = Visibility.Collapsed;
        LiveBufferBar.IsIndeterminate = false;
        LiveBufferBar.Value = 0;
        LiveSeekSlider.Value = 0;
        _updatingSeek = false;
        _displayPositionMs = _displayDurationMs = 0;
        UpdateLiveTimeLabel();
        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        var ct = _playCts.Token;
        try
        {
            var play = await PlayResolver.ResolveLive(item.Channel, ct);
            if (ct.IsCancellationRequested) return;
            _core.Open(play);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                LoadingRing.IsActive = false;
                ShowInfo("播放失败：" + ex.Message, InfoBarSeverity.Error);
            }
        }
        _ = UpdateProgramAsync(item);
        _programTimer.Stop();
        _programTimer.Start();
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
        if (Math.Abs(e.NewValue - _core.PositionMs) < 800) return;
        _core.SeekMs((long)e.NewValue);
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
        LineButton.Visibility = _lineAvailable && (!compact || width >= 440)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LineText.Visibility = !compact || width >= 560 ? Visibility.Visible : Visibility.Collapsed;
        LiveTimeText.MaxWidth = compact && width < 390 ? 54 : 132;
        UpdateLiveTimeLabel();
    }

    void OnLineItemClick(object sender, RoutedEventArgs e)
    {
        var ch = _current;
        if (ch == null || sender is not FrameworkElement item || item.Tag is not int index) return;
        if (index < 0 || index >= ch.Urls.Count || index == ch.UrlIndex) return;
        ch.UrlIndex = index;
        UpdateLineButton();
        if (_currentItem != null) PlayChannel(_currentItem);
    }

    void OnLineMenuOpened(object sender, object e) => LineChevron.Glyph = "\uE70E";

    void OnLineMenuClosed(object sender, object e) => LineChevron.Glyph = "\uE70D";

    void UpdateLineButton()
    {
        var ch = _current;
        _lineAvailable = ch != null && ch.Urls.Count > 1;
        if (ch != null && ch.Urls.Count > 0) LineText.Text = ch.CurrentLineName(ch.UrlIndex);
        LineMenu.Items.Clear();
        if (ch != null)
        {
            for (var i = 0; i < ch.Urls.Count; i++)
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = ch.CurrentLineName(i),
                    Tag = i,
                    GroupName = "live-lines",
                    IsChecked = i == ch.UrlIndex,
                };
                item.Click += OnLineItemClick;
                LineMenu.Items.Add(item);
            }
        }
        LineButton.IsEnabled = _lineAvailable;
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

    void OnPlayerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullscreen();

    void OnFullClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    void ToggleFullscreen()
    {
        var entering = !_fullscreen;
        _fullscreen = entering;
        if (entering) _compact = false;
        if (entering) ApplyPresentationMode();
        try
        {
            if (_fullscreen)
            {
                if (!App.Main.EnterPlaybackFullScreen())
                {
                    _fullscreen = false;
                    ApplyPresentationMode();
                }
            }
            else App.Main.RestorePlaybackWindow();
        }
        catch { }
        if (entering) App.Main.RefreshImmersiveFrame(_fullscreen);
        if (!entering) ApplyPresentationMode();
        ShowPlayerChrome();
        _hostBinding?.RequestSynchronize();
        Focus(FocusState.Programmatic);
    }

    void OnLivePip(object sender, RoutedEventArgs e)
    {
        var entering = !_compact;
        _compact = entering;
        if (entering) _fullscreen = false;
        if (entering) ApplyPresentationMode();
        try
        {
            if (_compact)
            {
                if (!App.Main.EnterPlaybackCompact())
                {
                    _compact = false;
                    ApplyPresentationMode();
                }
            }
            else App.Main.RestorePlaybackWindow();
        }
        catch { }
        if (entering) App.Main.RefreshImmersiveFrame(_fullscreen);
        if (!entering) ApplyPresentationMode();
        ShowPlayerChrome();
        _hostBinding?.RequestSynchronize();
        Focus(FocusState.Programmatic);
    }

    void ApplyPresentationMode()
    {
        var immersive = _fullscreen || _compact;
        LeftPane.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        MidPane.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        if (immersive)
        {
            LeftCol.Width = new GridLength(0);
            MidCol.Width = new GridLength(0);
        }
        else UpdatePaneWidths(ActualWidth);

        RootGrid.Padding = immersive ? new Thickness(0) : new Thickness(16);
        RootGrid.Background = immersive
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
            : null;
        LiveSurface.Background = immersive
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
            : _normalSurfaceBackground;
        LiveSurface.CornerRadius = immersive ? new CornerRadius(0) : new CornerRadius(8);
        LiveSurface.BorderThickness = immersive ? new Thickness(0) : new Thickness(1);
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
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _hostBinding?.RequestSynchronize());
    }

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
                if (_compact) { OnLivePip(null, null); e.Handled = true; }
                else if (_fullscreen) { ToggleFullscreen(); e.Handled = true; }
                break;
            case VirtualKey.Space:
                OnLivePlayPause(null, null); e.Handled = true; break;
            case VirtualKey.F:
                ToggleFullscreen(); e.Handled = true; break;
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

    public LiveChannelItem(LiveChannel channel) => Channel = channel;

    public LiveChannel Channel { get; }
    public string Name => Channel.Name;
    public string Number => Channel.Number;
    public string Logo => Channel.GetLogo();
    public string NowText { get => _now; set { _now = value ?? ""; Notify(nameof(NowText)); } }
    public bool IsKeep { get => _keep; set { _keep = value; Notify(nameof(KeepGlyph)); } }
    public string KeepGlyph => _keep ? "\uE735" : "\uE734";

    public event PropertyChangedEventHandler PropertyChanged;
    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
