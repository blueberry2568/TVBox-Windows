using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;
using TVBoxForWindows.Player;
using TVBoxForWindows.Server;

namespace TVBoxForWindows.UI.Pages;

/// <summary>点播播放页（契约 §5.2，移植自 VideoActivity 播放部分）：FlyleafHost + 弹幕层 + 自动隐藏控制层
/// + 选集面板；解析走 PlayResolver，失败自动换线路同名集；历史记录 5 秒落盘、跳片头尾、全屏/画中画/快捷键。</summary>
public sealed partial class PlayerPage : Page, INavigationPlayback
{
    const string TAG = "PlayerPage";
    const long SeekStepMs = 10000;                 // ←→ 步长
    const int SeekDebounceMs = 110;
    const int SeekFallbackMs = 750;
    const string ChevronDown = "\uE70D";
    const string ChevronUp = "\uE70E";
    static readonly string[] ScaleNames = { "原始", "拉伸", "16:9", "4:3", "填充" };

    PlaySession _session;
    PlayerCore _core;
    FlyleafHostBinding _hostBinding;
    DanmakuView _danmaku;
    DanmakuEngine _danmakuEngine;
    PlayItem _item;
    CancellationTokenSource _cts;
    Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;   // 3 秒无操作淡出控制层
    Microsoft.UI.Dispatching.DispatcherQueueTimer _toastTimer;  // Toast 自动关闭
    Microsoft.UI.Dispatching.DispatcherQueueTimer _seekTimer;   // 合并拖动和连续快进/快退
    readonly HashSet<int> _triedFlags = new();     // 自动换源已尝试过的线路
    string _refreshRetryKey;                       // 失效签名/临时 HTTP 错误时同线路只刷新一次
    List<DanmakuSourceItem> _danmakuSources = new();

    float _speed = 1;
    int _scale;
    bool _danmakuVisible;
    bool _updatingSlider;                          // 抑制程序化改 Slider 触发 Seek
    bool _updatingGear;                            // 抑制播放设置面板程序化赋值
    bool _endingFired;                             // 片尾自动下一集只触发一次
    bool _fullscreen, _compact;
    bool _closed;                                  // 已离开页面，晚到事件全部忽略
    bool _pauseWhenOpened;                         // 隐藏期间晚到的解析结果起播后立即暂停
    bool _isBuffering;
    int _menuOpen;                                 // 打开中的 Flyout 数（暂停自动隐藏）
    int _playGeneration;                           // 只允许最新一次解析结果提交给播放器
    bool _sourceTransitionInProgress;              // 防止 Flyleaf OpenAsync 重叠换源
    PlayerSelectionKind _selectionKind;
    FrameworkElement _selectionAnchor;
    int _selectionMutationVersion;
    List<Sub> _pendingSubtitleItems = new();
    CompositionRoundedRectangleGeometry _playerAreaClipGeometry;
    CompositionGeometricClip _playerAreaClip;
    readonly Microsoft.UI.Xaml.Media.SolidColorBrush _compactCornerMaskBrush =
        new(Microsoft.UI.Colors.Black);
    long _lastSaveTick;                            // 上次历史落盘（墙钟）
    long? _pendingSeekMs;
    long _seekIssuedTick;
    long _seekOriginMs;
    int _seekDirection;
    long _subtitleLoadGeneration;
    long _subtitleOpenRequestId;
    string _subtitleOpenLabel;
    long _displayPositionMs;
    long _displayDurationMs;

    // 局域网推送（弹幕/字幕）事件委托，离开页面时退订
    Action<string> _danmakuPush;
    Action<Sub> _subPush;

    public PlayerPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKey;
        Loaded += (s, e) =>
        {
            ApplyPresentationMode();
            Focus(FocusState.Programmatic);
        }; // 保证快捷键开箱可用
        var timeConverter = new MsTimeConverter();
        SeekSlider.ThumbToolTipValueConverter = timeConverter;
        CompactSeekSlider.ThumbToolTipValueConverter = timeConverter;
        HookFlyout(DanmakuFlyout);
    }

    // ---------- 生命周期 ----------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _session = (e.Parameter as PlayerArgs)?.Session;
        if (_session == null || _session.CurrentEpisode == null)
        {
            ShowError("缺少播放参数");
            return;
        }
        _closed = false;
        TitleText.Text = _session.Vod?.CleanName ?? "";
        LoadingPoster.Source = _session.Vod?.Pic ?? "";
        RestoreFromHistory();
        InitTimers();
        InitPlayer();
        BuildFlagMenu();
        SubscribeServer();
        if (_core == null) return; // FFmpeg 缺失：只显示指引
        // 进入时恢复：仅当历史指向当前集且位置 > 5s 才续播
        var h = _session.History;
        var ep = _session.CurrentEpisode;
        long resume = h != null && h.Position > 5000 && h.EpisodeUrl == ep.Url ? h.Position : 0;
        _ = PlayCurrent(resume, showRestoreToast: resume > 0);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _closed = true;
        _isBuffering = false;
        HideLoading();
        _selectionMutationVersion++;
        CloseSelectionPanel(false);
        InvalidateSubtitleLoad();
        CancelPlayRequest();
        _hideTimer?.Stop();
        _toastTimer?.Stop();
        DisposeSeekTimer();
        UnsubscribeServer();
        SaveHistoryNow();
        try { _danmaku?.Unbind(); } catch { }
        // 恢复窗口与应用壳
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
            _core.Ended -= OnCoreEnded;
            _core.TimeChanged -= OnCoreTime;
            _core.TransferRateChanged -= OnCoreTransferRateChanged;
            _core.BufferingChanged -= OnCoreBufferingChanged;
            _core.SubtitleOpened -= OnSubtitleOpened;
            try { _core.Stop(); _core.Dispose(); } catch { }
            _core = null;
        }
    }

    void InitTimers()
    {
        if (_hideTimer == null)
        {
            _hideTimer = DispatcherQueue.CreateTimer();
            _hideTimer.Interval = TimeSpan.FromSeconds(3);
            _hideTimer.Tick += (s, e) => MaybeHideControls();
        }
        _hideTimer.Start();
        if (_toastTimer == null)
        {
            _toastTimer = DispatcherQueue.CreateTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(2);
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += (s, e) => Toast.IsOpen = false;
        }
        if (_seekTimer == null)
        {
            _seekTimer = DispatcherQueue.CreateTimer();
            _seekTimer.Interval = TimeSpan.FromMilliseconds(SeekDebounceMs);
            _seekTimer.IsRepeating = false;
            _seekTimer.Tick += OnSeekTimerTick;
        }
    }

    void OnSeekTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => CommitPendingSeek();

    void InitPlayer()
    {
        if (_core != null) return;
        if (!PlayerCore.EngineReady) { FfmpegPanel.Visibility = Visibility.Visible; return; }
        try
        {
            _core = new PlayerCore();
            if (_core.Fly == null) // EngineReady 为真但内核创建失败时 Fly 仍可能为 null，绑定前必须判空
            {
                _core.Dispose();
                _core = null;
                FfmpegPanel.Visibility = Visibility.Visible;
                return;
            }
            _hostBinding = new FlyleafHostBinding(VideoHost);
            _hostBinding.Attach(_core);
            _core.Opened += OnCoreOpened;
            _core.Errored += OnCoreErrored;
            _core.Ended += OnCoreEnded;
            _core.TimeChanged += OnCoreTime;
            _core.TransferRateChanged += OnCoreTransferRateChanged;
            _core.BufferingChanged += OnCoreBufferingChanged;
            _core.SubtitleOpened += OnSubtitleOpened;
        }
        catch (Exception ex)
        {
            _core = null;
            ShowError("播放器初始化失败：" + ex.Message);
            return;
        }
        // 弹幕层：纯 C# 控件代码后置注入，按设置页同名键应用样式
        _danmakuEngine = new DanmakuEngine();
        _danmaku = new DanmakuView();
        DanmakuLayer.Children.Add(_danmaku);
        ApplyDanmakuSettings();
        _danmakuVisible = Setting.DanmakuLoad;
        _danmaku.SetVisible(_danmakuVisible);
        UpdateDanmakuButtonVisual();
    }

    /// <summary>历史恢复：已有记录用其倍速/缩放，否则用设置页默认（浮点读取容错）。</summary>
    void RestoreFromHistory()
    {
        var h = _session.History;
        bool existing = h != null && (h.Position > 0 || h.Duration > 0);
        _speed = existing && h.Speed > 0 ? h.Speed : ReadFloat("speed", 1f);
        _speed = Math.Clamp(_speed, 1f, 4f);
        _scale = existing && h.Scale >= 0 ? h.Scale : Setting.Scale;
        _scale = Math.Clamp(_scale, 0, 4);
        SyncSpeedMenu();
        SyncScaleMenu();
    }

    // ---------- 播放流程 ----------

    /// <summary>解析并起播当前集。resumeMs>0 时续播到该位置；否则按跳片头设置起播。</summary>
    async Task PlayCurrent(long resumeMs, bool showRestoreToast = false)
    {
        var ep = _session.CurrentEpisode;
        if (ep == null || _core == null) return;
        _isBuffering = false;
        SetSourceTransition(true);
        CancelPendingSeek();
        InvalidateSubtitleLoad();
        _endingFired = false;
        ResetProgressVisuals();
        var cts = StartPlayRequest(out var generation);
        var ct = cts.Token;
        UpdateEpisodeUi();
        ErrorBar.IsOpen = false;
        ShowLoading("解析中…", false);
        try
        {
            var item = await PlayResolver.Resolve(_session.Site, _session.CurrentFlag?.Flag, ep, ct);
            if (!IsCurrentPlayRequest(cts, generation)) return;
            _item = item;
            long start = item.StartPositionMs;
            if (resumeMs > 0) start = Math.Max(start, resumeMs);
            else if (Setting.GetBool("skip_start_end") && _session.History is { Opening: > 0 } his)
                start = Math.Max(start, his.Opening);
            item.StartPositionMs = start;
            ShowLoading("起播中…", true);
            _core.Open(item);
            RefreshDanmakuSources(item);
            RefreshSubMenu(item);
            if (Setting.DanmakuLoad && item.Danmaku.Count > 0 && !string.IsNullOrEmpty(item.Danmaku[0].Url))
                _ = LoadDanmaku(item.Danmaku[0].Url);
            else
                try { _danmakuEngine.Clear(); _danmaku.Unbind(); } catch { }
            if (showRestoreToast && resumeMs > 0) ShowToast("已恢复到 " + Fmt(resumeMs));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentPlayRequest(cts, generation)) OnPlayError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    CancellationTokenSource StartPlayRequest(out int generation)
    {
        var previous = _cts;
        var current = new CancellationTokenSource();
        _cts = current;
        generation = ++_playGeneration;
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { previous?.Dispose(); }
        return current;
    }

    bool IsCurrentPlayRequest(CancellationTokenSource cts, int generation) =>
        !_closed && !cts.IsCancellationRequested &&
        generation == _playGeneration && ReferenceEquals(_cts, cts);

    void CancelPlayRequest()
    {
        _playGeneration++;
        var current = _cts;
        _cts = null;
        try { current?.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { current?.Dispose(); }
    }

    void OnCoreOpened()
    {
        if (_closed) return;
        _isBuffering = false;
        HideLoading();
        _triedFlags.Clear();
        SetSourceTransition(false);
        try { _core.Speed = _speed; _core.Scale = _scale; } catch { }
        if (_pauseWhenOpened && _core?.IsPlaying == true) _core.PlayPause();
        _hostBinding?.RequestSynchronize();
        UpdatePlayPauseIcon();
    }

    void OnCoreErrored(string msg)
    {
        if (_closed) return;
        if (_sourceTransitionInProgress && _cts != null)
        {
            Logger.D(TAG, "已忽略新线路解析期间旧媒体的错误: " + msg);
            return;
        }
        OnPlayError(msg);
    }

    void OnCoreTransferRateChanged(double bytesPerSecond)
    {
        if (_closed || LoadingSpeedPanel.Visibility != Visibility.Visible) return;
        LoadingSpeedText.Text = PlayerCore.FormatTransferRate(bytesPerSecond);
    }

    void OnCoreBufferingChanged(bool buffering)
    {
        if (_closed || _sourceTransitionInProgress) return;
        _isBuffering = buffering;
        if (buffering) ShowLoading("缓冲中…", true);
        else HideLoading();
    }

    /// <summary>自然播完：自动下一集；没有下一集则返回。</summary>
    void OnCoreEnded()
    {
        if (_closed) return;
        if (_sourceTransitionInProgress && _cts != null) return;
        var eps = _session.CurrentFlag?.Episodes;
        if (eps != null && _session.EpisodeIndex + 1 < eps.Count)
        {
            ShowToast("自动播放下一集");
            ChangeEpisode(1);
        }
        else
        {
            SaveHistoryNow();
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }

    /// <summary>播放/解析失败 → 自动换源：先在其他线路找同名集，全部试过仍失败才报错手动选。</summary>
    void OnPlayError(string msg)
    {
        _isBuffering = false;
        HideLoading();
        Logger.E(TAG, "播放失败: " + msg);
        var retryKey = _session.FlagIndex + ":" + _session.EpisodeIndex;
        if (_refreshRetryKey != retryKey && IsRefreshableError(msg))
        {
            _refreshRetryKey = retryKey;
            var position = _core?.PositionMs ?? 0;
            ShowToast("播放地址失效，正在刷新");
            _ = PlayCurrent(position > 5000 ? position : 0);
            return;
        }
        var epName = _session.CurrentEpisode?.Name;
        _triedFlags.Add(_session.FlagIndex);
        for (int i = 1; i < _session.Flags.Count; i++)
        {
            int idx = (_session.FlagIndex + i) % _session.Flags.Count;
            if (_triedFlags.Contains(idx)) continue;
            var match = _session.Flags[idx].Find(epName, true);
            if (match == null) continue;
            long pos = _core?.PositionMs ?? 0;
            ShowToast("播放失败，自动切换线路「" + _session.Flags[idx].Flag + "」");
            _session.FlagIndex = idx;
            _session.EpisodeIndex = Math.Max(0, _session.Flags[idx].Episodes.IndexOf(match));
            UpdateFlagUi();
            _ = PlayCurrent(pos > 5000 ? pos : 0);
            return;
        }
        SetSourceTransition(false);
        ShowError((string.IsNullOrEmpty(msg) ? "未知错误" : msg) + "（可手动切换线路或选集重试）");
    }

    static bool IsRefreshableError(string message)
    {
        var text = message ?? "";
        return text.Contains("404", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("403", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("401", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("server returned", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 进度 / 历史 / 片头尾 ----------

    void ResetProgressVisuals()
    {
        _updatingSlider = true;
        SeekSlider.Maximum = CompactSeekSlider.Maximum = 1;
        SeekSlider.Value = CompactSeekSlider.Value = 0;
        BufferBar.Maximum = CompactBufferBar.Maximum = 1;
        BufferBar.Value = CompactBufferBar.Value = 0;
        _updatingSlider = false;
        UpdateTimeLabels(0, 0);
    }

    void OnCoreTime(long ms)
    {
        if (_closed || _core == null) return;
        if (!_isBuffering && !_sourceTransitionInProgress && LoadingOverlay.Visibility == Visibility.Visible)
            HideLoading();
        long dur = _core.DurationMs;
        long displayMs = ms;
        if (_pendingSeekMs is long pending)
        {
            if (IsSeekUpdateValid(ms, pending))
            {
                _pendingSeekMs = null;
                _seekIssuedTick = 0;
                _seekOriginMs = 0;
                _seekDirection = 0;
            }
            else displayMs = pending;
        }
        _updatingSlider = true;
        if (dur > 0)
        {
            SeekSlider.Maximum = dur;
            SeekSlider.Value = Math.Min(displayMs, dur);
            CompactSeekSlider.Maximum = dur;
            CompactSeekSlider.Value = Math.Min(displayMs, dur);
            long buffered = 0;
            try { buffered = _core.Fly.BufferedDuration / 10000; } catch { }
            BufferBar.Maximum = dur;
            BufferBar.Value = Math.Min(ms + buffered, dur);
            CompactBufferBar.Maximum = dur;
            CompactBufferBar.Value = Math.Min(ms + buffered, dur);
        }
        _updatingSlider = false;
        UpdateTimeLabels(displayMs, dur);
        UpdatePlayPauseIcon();
        // 每 5 秒落盘一次历史
        if (Environment.TickCount64 - _lastSaveTick >= 5000)
        {
            _lastSaveTick = Environment.TickCount64;
            SaveHistoryNow();
        }
        // 片尾自动下一集（跳片头尾开关开启且本片设置了 ending）
        var h = _session.History;
        if (h != null && h.Ending > 0 && dur > 0 && Setting.GetBool("skip_start_end"))
        {
            if (!_endingFired && ms >= dur - h.Ending)
            {
                _endingFired = true;
                ShowToast("已到片尾，自动下一集");
                OnCoreEnded();
            }
            else if (_endingFired && ms < dur - h.Ending - 2000) _endingFired = false; // 手动回拉后重新武装
        }
    }

    void UpdateTimeLabels(long positionMs, long durationMs)
    {
        _displayPositionMs = Math.Max(0, positionMs);
        _displayDurationMs = Math.Max(0, durationMs);
        TimeText.Text = Fmt(_displayPositionMs) + " / " + Fmt(_displayDurationMs);
        UpdateCompactTimeLabel(CompactBottomBar.ActualWidth);
    }

    void UpdateCompactTimeLabel(double width)
    {
        CompactTimeText.Text = width > 0 && width < 390
            ? Fmt(_displayPositionMs)
            : Fmt(_displayPositionMs) + " / " + Fmt(_displayDurationMs);
    }

    void OnCompactBottomBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCompactLayout(e.NewSize.Width);

    void UpdateCompactLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0) return;

        // Play/pause, previous/next, time, speed, exit-PIP and fullscreen always
        // stay visible. Add secondary controls as the measured bar grows; all of
        // them fit comfortably once a resizable PIP reaches a practical width.
        CompactScaleButton.Visibility = width >= 400 ? Visibility.Visible : Visibility.Collapsed;
        CompactFlagButton.Visibility = width >= 470 ? Visibility.Visible : Visibility.Collapsed;
        CompactEpisodeButton.Visibility = width >= 530 ? Visibility.Visible : Visibility.Collapsed;
        CompactDanmakuButton.Visibility = width >= 570 ? Visibility.Visible : Visibility.Collapsed;
        CompactDanmakuMenuButton.Visibility = width >= 610 ? Visibility.Visible : Visibility.Collapsed;
        CompactSubButton.Visibility = width >= 650 ? Visibility.Visible : Visibility.Collapsed;
        CompactGearButton.Visibility = width >= 710 ? Visibility.Visible : Visibility.Collapsed;

        CompactTimeText.MaxWidth = width < 390 ? 58 : width < 520 ? 96 : 132;
        UpdateCompactTimeLabel(width);
    }

    /// <summary>写入历史（Key=siteKey@vodId 由 PlaySession 构造，push 场景为 "@推送URL"）。</summary>
    void SaveHistoryNow()
    {
        var h = _session?.History;
        if (h == null) return;
        try
        {
            var ep = _session.CurrentEpisode;
            h.VodFlag = _session.CurrentFlag?.Flag ?? h.VodFlag;
            h.VodRemarks = ep?.Name ?? h.VodRemarks;
            h.EpisodeUrl = ep?.Url ?? h.EpisodeUrl;   // DetailPage 按 Url 匹配续播集
            h.VodName = string.IsNullOrEmpty(_session.Vod?.CleanName) ? h.VodName : _session.Vod.CleanName;
            h.VodPic = string.IsNullOrEmpty(_session.Vod?.Pic) ? h.VodPic : _session.Vod.Pic;
            long pos = _core?.PositionMs ?? 0, dur = _core?.DurationMs ?? 0;
            if (dur > 0) { h.Position = pos; h.Duration = dur; }
            h.Speed = _speed;
            h.Scale = _scale;
            Stores.SaveHistory(h);
        }
        catch (Exception e) { Logger.E(TAG, "历史保存失败: " + e.Message); }
    }

    void OnSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _core == null) return;
        QueueSeek((long)e.NewValue);
    }

    // ---------- 集数 / 线路 ----------

    void ChangeEpisode(int delta)
    {
        if (_sourceTransitionInProgress) return;
        var eps = _session.CurrentFlag?.Episodes;
        if (eps == null || eps.Count == 0) return;
        int idx = _session.EpisodeIndex + delta;
        if (idx < 0) { ShowToast("已经是第一集"); return; }
        if (idx >= eps.Count) { ShowToast("已经是最后一集"); return; }
        SaveHistoryNow();
        _session.EpisodeIndex = idx;
        if (_session.History != null) _session.History.Position = 0;
        _triedFlags.Clear();
        _ = PlayCurrent(0);
    }

    void SwitchFlag(int idx)
    {
        if (_sourceTransitionInProgress) return;
        if (idx < 0 || idx >= _session.Flags.Count || idx == _session.FlagIndex) return;
        var name = _session.CurrentEpisode?.Name;
        long pos = _core?.PositionMs ?? 0;
        SaveHistoryNow();
        _session.FlagIndex = idx;
        var flag = _session.CurrentFlag;
        var match = flag?.Find(name, false);
        _session.EpisodeIndex = match != null ? Math.Max(0, flag.Episodes.IndexOf(match)) : 0;
        _triedFlags.Clear();
        UpdateFlagUi();
        _ = PlayCurrent(pos > 5000 ? pos : 0);
    }

    void BuildFlagMenu()
    {
        UpdateFlagUi(false);
    }

    void UpdateFlagUi(bool syncMenus = true)
    {
        var label = _session.CurrentFlag?.Flag ?? "线路";
        FlagText.Text = label;
        CompactFlagText.Text = label;
        UpdateSourceControlAvailability();
        if (syncMenus && _selectionKind == PlayerSelectionKind.Flag) RefreshSelectionPanel();
    }

    void SetSourceTransition(bool inProgress)
    {
        _sourceTransitionInProgress = inProgress;
        UpdateSourceControlAvailability();
    }

    void UpdateSourceControlAvailability()
    {
        var enabled = !_sourceTransitionInProgress;
        var hasFlags = _session?.Flags?.Count > 0;
        var episodes = _session?.CurrentFlag?.Episodes;
        FlagButton.IsEnabled = enabled && hasFlags;
        CompactFlagButton.IsEnabled = enabled && hasFlags;
        EpisodeButton.IsEnabled = enabled && episodes is { Count: > 0 };
        CompactEpisodeButton.IsEnabled = enabled && episodes is { Count: > 0 };
        PrevEpisodeButton.IsEnabled = enabled && _session?.EpisodeIndex > 0;
        CompactPrevEpisodeButton.IsEnabled = PrevEpisodeButton.IsEnabled;
        var hasNext = episodes is { Count: > 0 } && _session.EpisodeIndex + 1 < episodes.Count;
        NextEpisodeButton.IsEnabled = enabled && hasNext;
        CompactNextEpisodeButton.IsEnabled = NextEpisodeButton.IsEnabled;
    }

    void UpdateEpisodeUi()
    {
        EpNameText.Text = _session.CurrentEpisode?.Name ?? "";
        var flag = _session.CurrentFlag?.Flag ?? "线路";
        FlagText.Text = flag;
        CompactFlagText.Text = flag;
        UpdateSourceControlAvailability();
        if (_selectionKind == PlayerSelectionKind.Episode) RefreshSelectionPanel();
    }

    void SelectEpisode(Episode ep)
    {
        if (_sourceTransitionInProgress || ep == null) return;
        var eps = _session.CurrentFlag?.Episodes;
        int idx = eps?.IndexOf(ep) ?? -1;
        if (idx < 0) return;
        if (idx == _session.EpisodeIndex) return; // 点当前集不重播
        SaveHistoryNow();
        _session.EpisodeIndex = idx;
        if (_session.History != null) _session.History.Position = 0;
        _triedFlags.Clear();
        _ = PlayCurrent(0);
    }

    void OnOpenEpisodePane(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.Episode, sender as FrameworkElement);

    void OnPrevEpisode(object sender, RoutedEventArgs e) => ChangeEpisode(-1);

    void OnNextEpisode(object sender, RoutedEventArgs e) => ChangeEpisode(1);

    // ---------- 倍速 / 缩放 ----------

    void SyncSpeedMenu()
    {
        var label = _speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        SpeedText.Text = label;
        CompactSpeedText.Text = label;
        if (_selectionKind == PlayerSelectionKind.Speed) RefreshSelectionPanel();
    }

    void SyncScaleMenu()
    {
        var label = ScaleNames[Math.Clamp(_scale, 0, 4)];
        ScaleText.Text = label;
        CompactScaleText.Text = label;
        if (_selectionKind == PlayerSelectionKind.Scale) RefreshSelectionPanel();
    }

    static void SetChevron(FontIcon icon, bool open) => icon.Glyph = open ? ChevronUp : ChevronDown;

    void OnOpenSpeedMenu(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.Speed, sender as FrameworkElement);

    void OnOpenScaleMenu(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.Scale, sender as FrameworkElement);

    void OnOpenFlagMenu(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.Flag, sender as FrameworkElement);

    void OnOpenSubtitleMenu(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.Subtitle, sender as FrameworkElement);

    void OnOpenCompactMoreMenu(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.More, sender as FrameworkElement);

    void OnOpenSettingsPanel(object sender, RoutedEventArgs e) =>
        OpenSelectionPanel(PlayerSelectionKind.Settings, sender as FrameworkElement);

    void OpenSelectionPanel(PlayerSelectionKind kind, FrameworkElement anchor = null) =>
        QueueSelectionMutation(() => OpenSelectionPanelCore(kind, anchor));

    void OpenSelectionPanelCore(PlayerSelectionKind kind, FrameworkElement anchor)
    {
        if (_closed || _session == null) return;
        if (_selectionKind == kind && PlayerSelectionOverlay.Visibility == Visibility.Visible)
        {
            CloseSelectionPanel();
            return;
        }
        if (_selectionKind != PlayerSelectionKind.None) CloseSelectionPanel(false);
        _selectionKind = kind;
        _selectionAnchor = anchor ?? ResolveSelectionAnchor(kind);
        RefreshSelectionPanel();
        PlayerSelectionOverlay.Opacity = 0;
        PlayerSelectionOverlay.Visibility = Visibility.Visible;
        PlayerSelectionOverlay.UpdateLayout();
        PositionSelectionPanel();
        PlayerSelectionOverlay.Opacity = 1;
        _menuOpen++;
        SetSelectionChevrons(kind, true);
        ShowControls();
    }

    void RefreshSelectionPanel()
    {
        if (_selectionKind == PlayerSelectionKind.None) return;

        IEnumerable<PlayerSelectionItem> items;
        PlayerSelectionAction.Visibility = Visibility.Collapsed;
        PlayerSelectionAction.Tag = null;
        PlayerSettingsScroller.Visibility = Visibility.Collapsed;
        switch (_selectionKind)
        {
            case PlayerSelectionKind.Speed:
                PlayerSelectionTitle.Text = "播放速度";
                items = new[] { 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f, 4f }
                    .Select(value => new PlayerSelectionItem(
                        value.ToString("0.##", CultureInfo.InvariantCulture) + "x",
                        value,
                        Math.Abs(value - _speed) < 0.01f));
                break;
            case PlayerSelectionKind.Scale:
                PlayerSelectionTitle.Text = "画面比例";
                items = ScaleNames.Select((label, index) =>
                    new PlayerSelectionItem(label, index, index == _scale));
                break;
            case PlayerSelectionKind.Flag:
                PlayerSelectionTitle.Text = "播放线路";
                items = _session.Flags.Select((flag, index) =>
                    new PlayerSelectionItem(flag.Flag, index, index == _session.FlagIndex));
                break;
            case PlayerSelectionKind.Subtitle:
                PlayerSelectionTitle.Text = "字幕";
                items = _pendingSubtitleItems.Select((sub, index) =>
                    new PlayerSelectionItem(SubtitleLabel(sub, index), sub, false));
                PlayerSelectionAction.Content = "加载本地字幕…";
                PlayerSelectionAction.Tag = PlayerSelectionActionKind.LocalSubtitle;
                PlayerSelectionAction.Visibility = Visibility.Visible;
                break;
            case PlayerSelectionKind.Episode:
                PlayerSelectionTitle.Text = "选集";
                var episodes = _session.CurrentFlag?.Episodes ?? new List<Episode>();
                items = episodes.Select((episode, index) =>
                    new PlayerSelectionItem(episode.Name, episode, index == _session.EpisodeIndex));
                break;
            case PlayerSelectionKind.Settings:
                PlayerSelectionTitle.Text = "跳过片头片尾";
                RefreshGearSettings();
                PlayerSettingsScroller.Visibility = Visibility.Visible;
                items = Array.Empty<PlayerSelectionItem>();
                break;
            default:
                PlayerSelectionTitle.Text = "更多";
                items = new[]
                {
                    new PlayerSelectionItem("选择剧集", PlayerSelectionActionKind.Episodes, false),
                    new PlayerSelectionItem("加载本地字幕…", PlayerSelectionActionKind.LocalSubtitle, false),
                };
                break;
        }

        var list = items.ToList();
        PlayerSelectionList.ItemsSource = list;
        PlayerSelectionList.SelectedItem = list.FirstOrDefault(item => item.IsSelected);
        PlayerSelectionList.Visibility = _selectionKind != PlayerSelectionKind.Settings && list.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (PlayerSelectionOverlay.Visibility == Visibility.Visible) PositionSelectionPanel();
    }

    FrameworkElement ResolveSelectionAnchor(PlayerSelectionKind kind) => kind switch
    {
        PlayerSelectionKind.Speed => _compact ? CompactSpeedButton : SpeedButton,
        PlayerSelectionKind.Scale => _compact ? CompactScaleButton : ScaleButton,
        PlayerSelectionKind.Flag => _compact ? CompactFlagButton : FlagButton,
        PlayerSelectionKind.Subtitle => _compact ? CompactSubButton : SubButton,
        PlayerSelectionKind.Episode => _compact
            ? (CompactEpisodeButton.Visibility == Visibility.Visible ? CompactEpisodeButton : CompactMoreButton)
            : EpisodeButton,
        PlayerSelectionKind.More => CompactMoreButton,
        PlayerSelectionKind.Settings => _compact ? CompactGearButton : GearButton,
        _ => null,
    };

    void PositionSelectionPanel()
    {
        if (PlayerSelectionOverlay.Visibility != Visibility.Visible) return;

        const double edge = 12;
        const double gap = 8;
        var overlayWidth = PlayerSelectionOverlay.ActualWidth > 0
            ? PlayerSelectionOverlay.ActualWidth
            : PlayerArea.ActualWidth;
        var overlayHeight = PlayerSelectionOverlay.ActualHeight > 0
            ? PlayerSelectionOverlay.ActualHeight
            : PlayerArea.ActualHeight;
        if (overlayWidth <= 0 || overlayHeight <= 0) return;

        var panelWidth = Math.Min(280, Math.Max(1, overlayWidth - edge * 2));
        var anchor = _selectionAnchor ?? ResolveSelectionAnchor(_selectionKind);
        double anchorLeft;
        double anchorTop;
        double anchorWidth;
        try
        {
            if (anchor == null || anchor.ActualWidth <= 0)
                throw new InvalidOperationException();
            var point = anchor.TransformToVisual(PlayerSelectionOverlay)
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
        PlayerSelectionPanel.Width = panelWidth;
        PlayerSelectionPanel.MaxHeight = panelMaxHeight;
        PlayerSelectionPanel.Margin = new Thickness(0);
        PlayerSelectionPanel.Measure(new Windows.Foundation.Size(panelWidth, panelMaxHeight));

        var panelHeight = Math.Min(panelMaxHeight, Math.Max(1, PlayerSelectionPanel.DesiredSize.Height));
        var left = anchorLeft + (anchorWidth - panelWidth) / 2;
        left = Math.Clamp(left, edge, Math.Max(edge, overlayWidth - panelWidth - edge));
        var top = Math.Max(edge, anchorTop - gap - panelHeight);
        PlayerSelectionPanel.Margin = new Thickness(left, top, 0, 0);
    }

    void OnSelectionOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selectionKind != PlayerSelectionKind.None) PositionSelectionPanel();
    }

    static string SubtitleLabel(Sub sub, int index) =>
        !string.IsNullOrEmpty(sub?.Name) ? sub.Name :
        !string.IsNullOrEmpty(sub?.Lang) ? sub.Lang :
        "字幕 " + (index + 1);

    void OnSelectionItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlayerSelectionItem item) return;
        var kind = _selectionKind;
        var value = item.Value;
        QueueSelectionMutation(() =>
        {
            CloseSelectionPanel();
            CommitSelection(kind, value);
        });
    }

    // Keep overlay collapse/rebinding out of the WinUI routed-input stack.
    void QueueSelectionMutation(Action mutation)
    {
        var version = ++_selectionMutationVersion;
        void CommitMutation()
        {
            if (_closed || version != _selectionMutationVersion) return;
            mutation();
        }
        DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            CommitMutation);
    }

    void CommitSelection(PlayerSelectionKind kind, object value)
    {
        if (_closed) return;
        switch (kind)
        {
            case PlayerSelectionKind.Speed when value is float speed:
                _speed = speed;
                try { if (_core != null) _core.Speed = speed; } catch { }
                SyncSpeedMenu();
                SaveHistoryNow();
                break;
            case PlayerSelectionKind.Scale when value is int scale:
                _scale = Math.Clamp(scale, 0, ScaleNames.Length - 1);
                try { if (_core != null) _core.Scale = _scale; } catch { }
                SyncScaleMenu();
                SaveHistoryNow();
                break;
            case PlayerSelectionKind.Flag when value is int flagIndex:
                SwitchFlag(flagIndex);
                break;
            case PlayerSelectionKind.Subtitle when value is Sub subtitle:
                _ = LoadSubtitle(subtitle);
                break;
            case PlayerSelectionKind.Episode when value is Episode episode:
                SelectEpisode(episode);
                break;
            case PlayerSelectionKind.More when value is PlayerSelectionActionKind action:
                if (action == PlayerSelectionActionKind.Episodes) OnOpenEpisodePane(null, null);
                else if (action == PlayerSelectionActionKind.LocalSubtitle) OnPickLocalSubtitle(null, null);
                break;
        }
    }

    void OnSelectionActionClick(object sender, RoutedEventArgs e)
    {
        var action = PlayerSelectionAction.Tag is PlayerSelectionActionKind value
            ? value
            : PlayerSelectionActionKind.None;
        QueueSelectionMutation(() =>
        {
            CloseSelectionPanel();
            if (_closed) return;
            if (action == PlayerSelectionActionKind.Episodes) OnOpenEpisodePane(null, null);
            else if (action == PlayerSelectionActionKind.LocalSubtitle) OnPickLocalSubtitle(null, null);
        });
    }

    void OnSelectionOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        QueueSelectionMutation(() => CloseSelectionPanel());
        e.Handled = true;
    }

    void OnSelectionPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    void OnCloseSelectionPanel(object sender, RoutedEventArgs e) =>
        QueueSelectionMutation(() => CloseSelectionPanel());

    void CloseSelectionPanel(bool showControls = true)
    {
        if (_selectionKind == PlayerSelectionKind.None &&
            PlayerSelectionOverlay.Visibility != Visibility.Visible) return;
        var previous = _selectionKind;
        _selectionKind = PlayerSelectionKind.None;
        _selectionAnchor = null;
        PlayerSelectionOverlay.Visibility = Visibility.Collapsed;
        PlayerSelectionList.ItemsSource = null;
        PlayerSettingsScroller.Visibility = Visibility.Collapsed;
        PlayerSelectionAction.Tag = null;
        PlayerSelectionAction.Visibility = Visibility.Collapsed;
        _menuOpen = Math.Max(0, _menuOpen - 1);
        SetSelectionChevrons(previous, false);
        if (showControls) ShowControls();
    }

    void SetSelectionChevrons(PlayerSelectionKind kind, bool open)
    {
        switch (kind)
        {
            case PlayerSelectionKind.Speed:
                SetChevron(SpeedChevron, open);
                SetChevron(CompactSpeedChevron, open);
                break;
            case PlayerSelectionKind.Scale:
                SetChevron(ScaleChevron, open);
                SetChevron(CompactScaleChevron, open);
                break;
            case PlayerSelectionKind.Flag:
                SetChevron(FlagChevron, open);
                SetChevron(CompactFlagChevron, open);
                break;
            case PlayerSelectionKind.Subtitle:
                SetChevron(SubChevron, open);
                SetChevron(CompactSubChevron, open);
                break;
        }
    }

    // ---------- 弹幕 ----------

    /// <summary>按设置页同名键（danmaku_alpha/size/speed/area）应用弹幕样式。</summary>
    void ApplyDanmakuSettings()
    {
        if (_danmaku == null) return;
        _danmaku.SetOpacity(ReadFloat("danmaku_alpha", 0.9f));
        _danmaku.SetFontScale(Setting.GetInt("danmaku_size", 24) / 22.0);   // DanmakuView 基准字号 22
        _danmaku.SetSpeed(Setting.GetInt("danmaku_speed", 2) switch { 1 => 0.75, 3 => 1.5, _ => 1.0 });
        _danmaku.SetArea(Math.Clamp(Setting.GetInt("danmaku_area", 50), 10, 100) / 100.0);
    }

    async Task LoadDanmaku(string urlOrText)
    {
        if (_danmakuEngine == null || _danmaku == null || _core == null) return;
        try
        {
            await _danmakuEngine.LoadAsync(urlOrText);
            if (_closed) return;
            if (_danmakuEngine.Items.Count == 0) { ShowToast("弹幕加载失败或为空"); return; }
            _danmaku.Bind(_core, _danmakuEngine);
            _danmaku.SetVisible(_danmakuVisible);
            if (_pauseWhenOpened) _danmaku.Suspend();
            ShowToast("弹幕已加载 " + _danmakuEngine.Items.Count + " 条");
        }
        catch (Exception e) { Logger.E(TAG, "弹幕加载失败: " + e.Message); }
    }

    void RefreshDanmakuSources(PlayItem item)
    {
        _danmakuSources = (item.Danmaku ?? new List<Danmaku>())
            .Where(d => !string.IsNullOrEmpty(d.Url))
            .Select((d, i) => new DanmakuSourceItem { Name = string.IsNullOrEmpty(d.Name) ? "弹幕 " + (i + 1) : d.Name, Url = d.Url })
            .ToList();
        DanmakuList.ItemsSource = _danmakuSources;
        DanmakuEmptyText.Visibility = _danmakuSources.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnDanmakuToggle(object sender, RoutedEventArgs e)
    {
        _danmakuVisible = !_danmakuVisible;
        _danmaku?.SetVisible(_danmakuVisible);
        UpdateDanmakuButtonVisual();
        ShowToast(_danmakuVisible ? "弹幕已开启" : "弹幕已关闭");
    }

    void UpdateDanmakuButtonVisual()
    {
        DanmakuOnIcon.Visibility = _danmakuVisible ? Visibility.Visible : Visibility.Collapsed;
        DanmakuOffIcon.Visibility = _danmakuVisible ? Visibility.Collapsed : Visibility.Visible;
        CompactDanmakuOnIcon.Visibility = _danmakuVisible ? Visibility.Visible : Visibility.Collapsed;
        CompactDanmakuOffIcon.Visibility = _danmakuVisible ? Visibility.Collapsed : Visibility.Visible;
        var tooltip = _danmakuVisible ? "关闭弹幕 (D)" : "开启弹幕 (D)";
        ToolTipService.SetToolTip(DanmakuButton, tooltip);
        ToolTipService.SetToolTip(CompactDanmakuButton, tooltip);
        AutomationProperties.SetName(DanmakuButton, tooltip);
        AutomationProperties.SetName(CompactDanmakuButton, tooltip);
    }

    void OnOpenCompactDanmakuMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target) DanmakuFlyout.ShowAt(target);
    }

    void OnDanmakuFlyoutOpening(object sender, object e)
    {
        DanmakuList.ItemsSource = _danmakuSources;
        DanmakuEmptyText.Visibility = _danmakuSources.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnDanmakuSourceClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DanmakuSourceItem item) _ = LoadDanmaku(item.Url);
    }

    void OnDanmakuUrlLoad(object sender, RoutedEventArgs e)
    {
        var url = (DanmakuUrlBox.Text ?? "").Trim();
        if (url.Length == 0) return;
        DanmakuFlyout.Hide();
        _ = LoadDanmaku(url);
    }

    // ---------- 字幕 ----------

    void RefreshSubMenu(PlayItem item)
    {
        _pendingSubtitleItems = new List<Sub>(item.Subs ?? new List<Sub>());
        SubButton.IsEnabled = true;
        CompactSubButton.IsEnabled = true;
        if (_selectionKind == PlayerSelectionKind.Subtitle) RefreshSelectionPanel();
    }

    async void OnPickLocalSubtitle(object sender, RoutedEventArgs e)
    {
        var generation = BeginSubtitleLoad();
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };
            foreach (var extension in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Main));
            var file = await picker.PickSingleFileAsync();
            if (file == null || !IsSubtitleLoadCurrent(generation)) return;
            OpenSubtitle(file.Path, file.Name, generation);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, "本地字幕加载失败: " + ex.Message);
            if (IsSubtitleLoadCurrent(generation)) ShowToast("本地字幕加载失败");
        }
    }

    async Task LoadSubtitle(Sub sub)
    {
        var generation = BeginSubtitleLoad();
        try
        {
            var path = await SubtitleLoader.Fetch(sub); // 失败返回空串（不抛异常）
            if (!IsSubtitleLoadCurrent(generation)) return;
            if (string.IsNullOrEmpty(path)) { ShowToast("字幕下载失败"); return; }
            var name = !string.IsNullOrEmpty(sub?.Name) ? sub.Name : sub?.Lang;
            OpenSubtitle(path, name, generation);
        }
        catch (Exception e)
        {
            Logger.E(TAG, "字幕加载失败: " + e.Message);
            if (IsSubtitleLoadCurrent(generation)) ShowToast("字幕加载失败");
        }
    }

    long BeginSubtitleLoad()
    {
        _subtitleOpenRequestId = 0;
        _subtitleOpenLabel = null;
        return ++_subtitleLoadGeneration;
    }

    void InvalidateSubtitleLoad()
    {
        _subtitleLoadGeneration++;
        _subtitleOpenRequestId = 0;
        _subtitleOpenLabel = null;
    }

    bool IsSubtitleLoadCurrent(long generation) => !_closed && generation == _subtitleLoadGeneration;

    void OpenSubtitle(string path, string label, long generation)
    {
        if (!IsSubtitleLoadCurrent(generation)) return;
        if (_core?.Fly == null) { ShowToast("播放器尚未就绪"); return; }
        _subtitleOpenLabel = label ?? "";
        _subtitleOpenRequestId = _core.OpenSubtitle(path);
    }

    void OnSubtitleOpened(long requestId, bool success, string error)
    {
        if (_closed || requestId != _subtitleOpenRequestId) return;
        _subtitleOpenRequestId = 0;
        var label = _subtitleOpenLabel;
        _subtitleOpenLabel = null;
        if (success)
        {
            ShowToast("字幕已加载" + (string.IsNullOrEmpty(label) ? "" : "：" + label));
            return;
        }
        Logger.E(TAG, "字幕打开失败: " + (error ?? ""));
        ShowToast("字幕加载失败");
    }

    // ---------- 齿轮：跳片头尾 ----------

    void RefreshGearSettings()
    {
        _updatingGear = true;
        SkipToggle.IsOn = Setting.GetBool("skip_start_end");
        var h = _session?.History;
        OpeningBox.Value = h is { Opening: > 0 } ? h.Opening / 1000.0 : 0;
        EndingBox.Value = h is { Ending: > 0 } ? h.Ending / 1000.0 : 0;
        _updatingGear = false;
    }

    void OnSkipToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingGear) return;
        Setting.Put("skip_start_end", SkipToggle.IsOn);
    }

    void OnOpeningChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingGear || _session?.History == null) return;
        var v = double.IsNaN(args.NewValue) ? 0 : args.NewValue;
        _session.History.Opening = v > 0 ? (long)(v * 1000) : -1;
        SaveHistoryNow();
    }

    void OnEndingChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingGear || _session?.History == null) return;
        var v = double.IsNaN(args.NewValue) ? 0 : args.NewValue;
        _session.History.Ending = v > 0 ? (long)(v * 1000) : -1;
        _endingFired = false;
        SaveHistoryNow();
    }

    // ---------- 控制层显隐 ----------

    void OnPointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();

    void OnPlayerTapped(object sender, TappedRoutedEventArgs e)
    {
        ShowControls();
        Focus(FocusState.Programmatic);
    }

    void OnPlayerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selectionKind != PlayerSelectionKind.None)
        {
            QueueSelectionMutation(() =>
            {
                CloseSelectionPanel(false);
                ToggleFullscreen();
            });
        }
        else ToggleFullscreen();
        e.Handled = true;
    }

    void ShowControls()
    {
        if (ControlLayer.Opacity < 1 || !ControlLayer.IsHitTestVisible)
        {
            ControlLayer.IsHitTestVisible = true;
            FadeInControls.Begin();
        }
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    void MaybeHideControls()
    {
        if (_closed || _menuOpen > 0) return;
        if (_core == null || !_core.IsPlaying) return; // 暂停/加载中不隐藏
        ControlLayer.IsHitTestVisible = false;
        FadeOutControls.Begin();
    }

    void HookFlyout(FlyoutBase flyout)
    {
        if (flyout == null) return;
        flyout.Opened += (s, e) => _menuOpen++;
        flyout.Closed += (s, e) => { _menuOpen = Math.Max(0, _menuOpen - 1); ShowControls(); };
    }

    // ---------- 播放控制 / 全屏 / 画中画 ----------

    void OnPlayPause(object sender, RoutedEventArgs e)
    {
        _pauseWhenOpened = false;
        _core?.PlayPause();
        UpdatePlayPauseIcon();
        ShowControls();
    }

    /// <summary>Temporarily pause while another navigation surface covers this page.</summary>
    public void PauseForNavigation()
    {
        if (_closed) return;
        _hostBinding?.CancelPresentationTransition();
        _selectionMutationVersion++;
        CloseSelectionPanel(false);
        _pauseWhenOpened = true;
        if (_core?.IsPlaying == true) _core.PlayPause();
        _core?.SetUiUpdatesEnabled(false);
        _hideTimer?.Stop();
        _danmaku?.Suspend();
        UpdatePlayPauseIcon();
        SaveHistoryNow();
    }

    public void ActivateAfterNavigation()
    {
        _pauseWhenOpened = false;
        _core?.SetUiUpdatesEnabled(true);
        _danmaku?.Resume();
        UpdatePlayPauseIcon();
        ShowControls();
    }

    public void SynchronizePlaybackWindow()
    {
        if (_closed || XamlRoot == null) return;
        try
        {
            PageRoot.InvalidateMeasure();
            PageRoot.InvalidateArrange();
            PlayerArea.InvalidateMeasure();
            PlayerArea.InvalidateArrange();
            PageRoot.UpdateLayout();
            UpdateCompactLayout(CompactBottomBar.ActualWidth);
            UpdatePlayerAreaClip();
            _hostBinding?.SynchronizeAfterLayout();
        }
        catch (Exception e)
        {
            Logger.E("PlayerPage", "同步播放窗口布局失败：" + e.Message);
        }
    }

    void UpdatePlayPauseIcon()
    {
        var glyph = _core != null && _core.IsPlaying ? "" : "";
        PlayPauseIcon.Glyph = glyph;
        CompactPlayPauseIcon.Glyph = glyph;
    }

    void OnFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    void ToggleFullscreen()
    {
        if (_closed || App.Main.IsPlaybackWindowTransitionActive) return;
        _hostBinding?.BeginPresentationTransition();
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
        Focus(FocusState.Programmatic);
    }

    void OnPip(object sender, RoutedEventArgs e)
    {
        if (_closed || App.Main.IsPlaybackWindowTransitionActive) return;
        _hostBinding?.BeginPresentationTransition();
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
        Focus(FocusState.Programmatic);
    }

    void ApplyPresentationMode()
    {
        bool immersive = _fullscreen || _compact;
        PageRoot.Padding = immersive
            ? new Thickness(0)
            : (Thickness)Application.Current.Resources["PlayerSurfaceMargin"];
        PageRoot.Background = _fullscreen
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BottomBar.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactBottomBar.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        TitlePanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactDragRegion.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        if (_selectionKind != PlayerSelectionKind.None) CloseSelectionPanel(false);
        TopBar.Padding = _compact ? new Thickness(8, 6, 8, 16) : new Thickness(20, 14, 20, 28);
        PipEnterIcon.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        PipExitIcon.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PipButton, _compact ? "退出小窗模式" : "小窗模式");
        AutomationProperties.SetName(PipButton, _compact ? "退出小窗模式" : "开启小窗模式");
        ToolTipService.SetToolTip(BackButton, _compact ? "退出小窗模式" : "返回 (Esc)");
        FullIcon.Glyph = _fullscreen ? "" : "";
        CompactFullIcon.Glyph = _fullscreen ? "" : "";
        _core?.SetViewportFill(_fullscreen);
        App.Main.SetImmersive(immersive);
        UpdatePlayerAreaClip();
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateCompactLayout(CompactBottomBar.ActualWidth);
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
        PlayerCornerMaskLayer.Visibility = _fullscreen
            ? Visibility.Collapsed
            : Visibility.Visible;
        var radius = Math.Max(0, SurfaceCornerRadius().TopLeft);
        var fill = _compact
            ? _compactCornerMaskBrush
            : Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]
                as Microsoft.UI.Xaml.Media.Brush;
        foreach (var corner in new Microsoft.UI.Xaml.Shapes.Path[]
        {
            PlayerCornerMaskTopLeft,
            PlayerCornerMaskTopRight,
            PlayerCornerMaskBottomRight,
            PlayerCornerMaskBottomLeft,
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

    void OnBack(object sender, RoutedEventArgs e)
    {
        if (_compact) { OnPip(sender, e); return; }
        if (_fullscreen) { ToggleFullscreen(); return; }
        if (Frame.CanGoBack) Frame.GoBack();
    }

    void OnCompactDragPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_compact || !e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        App.Main.BeginCompactDrag();
    }

    // ---------- 快捷键 ----------

    void OnPreviewKey(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox || focused is PasswordBox || focused is AutoSuggestBox || focused is NumberBox) return;
        switch (e.Key)
        {
            case VirtualKey.Space:
                _core?.PlayPause(); ShowControls(); e.Handled = true; break;
            case VirtualKey.Left:
                SeekRelative(-SeekStepMs); e.Handled = true; break;
            case VirtualKey.Right:
                SeekRelative(SeekStepMs); e.Handled = true; break;
            case VirtualKey.Up:
                ChangeVolume(5); e.Handled = true; break;
            case VirtualKey.Down:
                ChangeVolume(-5); e.Handled = true; break;
            case VirtualKey.F:
                if (_selectionKind != PlayerSelectionKind.None)
                {
                    QueueSelectionMutation(() =>
                    {
                        CloseSelectionPanel(false);
                        ToggleFullscreen();
                    });
                }
                else ToggleFullscreen();
                e.Handled = true;
                break;
            case VirtualKey.M:
                ToggleMute(); e.Handled = true; break;
            case VirtualKey.D:
                OnDanmakuToggle(null, null); e.Handled = true; break;
            case (VirtualKey)0xDB: // [
                ChangeEpisode(-1); e.Handled = true; break;
            case (VirtualKey)0xDD: // ]
                ChangeEpisode(1); e.Handled = true; break;
            case VirtualKey.Escape:
                if (_selectionKind != PlayerSelectionKind.None)
                {
                    QueueSelectionMutation(() => CloseSelectionPanel());
                }
                else if (_compact) { OnPip(null, null); }
                else if (_fullscreen) { ToggleFullscreen(); }
                else if (Frame.CanGoBack) { Frame.GoBack(); }
                e.Handled = true;
                break;
        }
    }

    void SeekRelative(long delta)
    {
        if (_core == null) return;
        long dur = _core.DurationMs;
        long origin = _pendingSeekMs ?? _core.PositionMs;
        long target = Math.Max(0, origin + delta);
        if (dur > 0) target = Math.Min(target, dur);
        QueueSeek(target);
    }

    void QueueSeek(long target)
    {
        if (_core == null) return;
        long dur = _core.DurationMs;
        target = Math.Max(0, target);
        if (dur > 0) target = Math.Min(target, dur);
        _pendingSeekMs = target;
        _seekIssuedTick = 0;
        _seekOriginMs = 0;
        _seekDirection = 0;

        _updatingSlider = true;
        if (dur > 0)
        {
            SeekSlider.Maximum = dur;
            SeekSlider.Value = target;
            CompactSeekSlider.Maximum = dur;
            CompactSeekSlider.Value = target;
        }
        _updatingSlider = false;
        UpdateTimeLabels(target, dur);

        _seekTimer?.Stop();
        if (_seekTimer == null) CommitPendingSeek();
        else _seekTimer.Start();
        ShowControls();
    }

    void CommitPendingSeek()
    {
        _seekTimer?.Stop();
        if (_closed || _core == null || _pendingSeekMs is not long target)
        {
            _pendingSeekMs = null;
            _seekIssuedTick = 0;
            _seekOriginMs = 0;
            _seekDirection = 0;
            return;
        }
        _seekOriginMs = _core.PositionMs;
        _seekDirection = Math.Sign(target - _seekOriginMs);
        _seekIssuedTick = Environment.TickCount64;
        _core.SeekMs(target);
    }

    bool IsSeekUpdateValid(long position, long target)
    {
        if (_seekIssuedTick <= 0) return false;
        if (Math.Abs(position - target) <= 1500) return true;

        var distance = Math.Abs(target - _seekOriginMs);
        var moved = position - _seekOriginMs;
        var transition = Math.Min(1000L, Math.Max(500L, distance / 4));
        var movedInRequestedDirection = _seekDirection == 0 || Math.Sign(moved) == _seekDirection;
        if (movedInRequestedDirection && Math.Abs(moved) >= transition) return true;

        // A failed backend seek must not pin the UI indefinitely. Normal 250 ms
        // playback ticks are rejected above; this is only a short recovery guard.
        return Environment.TickCount64 - _seekIssuedTick >= SeekFallbackMs;
    }

    void CancelPendingSeek()
    {
        _seekTimer?.Stop();
        _pendingSeekMs = null;
        _seekIssuedTick = 0;
        _seekOriginMs = 0;
        _seekDirection = 0;
    }

    void DisposeSeekTimer()
    {
        CancelPendingSeek();
        if (_seekTimer == null) return;
        _seekTimer.Tick -= OnSeekTimerTick;
        _seekTimer = null;
    }

    void ChangeVolume(int delta)
    {
        try
        {
            var audio = _core?.Fly?.Audio;
            if (audio == null) return;
            audio.Volume = Math.Clamp(audio.Volume + delta, 0, 150);
            ShowToast("音量 " + audio.Volume);
        }
        catch { }
    }

    void ToggleMute()
    {
        try
        {
            var audio = _core?.Fly?.Audio;
            if (audio == null) return;
            audio.Mute = !audio.Mute;
            ShowToast(audio.Mute ? "已静音" : "取消静音");
        }
        catch { }
    }

    // ---------- 局域网推送（弹幕/字幕注入当前播放） ----------

    void SubscribeServer()
    {
        _danmakuPush = url => { if (!_closed && !string.IsNullOrEmpty(url)) _ = LoadDanmaku(url); };
        _subPush = sub => { if (!_closed && sub != null) _ = LoadSubtitle(sub); };
        LocalServer.Instance.DanmakuArrived += _danmakuPush;
        LocalServer.Instance.SubtitleArrived += _subPush;
    }

    void UnsubscribeServer()
    {
        if (_danmakuPush != null) LocalServer.Instance.DanmakuArrived -= _danmakuPush;
        if (_subPush != null) LocalServer.Instance.SubtitleArrived -= _subPush;
        _danmakuPush = null;
        _subPush = null;
    }

    // ---------- 提示与状态 ----------

    void ShowLoading(string status, bool showSpeed)
    {
        StatusText.Text = status ?? "";
        LoadingSpeedPanel.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
        if (showSpeed) LoadingSpeedText.Text = PlayerCore.FormatTransferRate(0);
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    void HideLoading()
    {
        LoadingSpeedPanel.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    void ShowToast(string msg)
    {
        Toast.Message = msg ?? "";
        Toast.IsOpen = true;
        _toastTimer?.Stop();
        _toastTimer?.Start();
    }

    void ShowError(string msg)
    {
        ErrorBar.Message = msg ?? "";
        ErrorBar.IsOpen = true;
    }

    static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString("h\\:mm\\:ss") : ts.ToString("mm\\:ss");
    }

    /// <summary>浮点设置容错读取：Setting.Put 现统一 InvariantCulture 写入；回读先 Invariant，再当前区域性（兼容旧数据），都失败用默认值。</summary>
    static float ReadFloat(string key, float def)
    {
        var s = Setting.GetString(key, null);
        if (string.IsNullOrEmpty(s)) return def;
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        if (float.TryParse(s, out v)) return v;
        return def;
    }
}

enum PlayerSelectionKind
{
    None,
    Speed,
    Scale,
    Flag,
    Subtitle,
    Episode,
    More,
    Settings,
}

enum PlayerSelectionActionKind
{
    None,
    Episodes,
    LocalSubtitle,
}

public sealed class PlayerSelectionItem
{
    public PlayerSelectionItem(string label, object value, bool isSelected)
    {
        Label = label ?? "";
        Value = value;
        IsSelected = isSelected;
    }

    public string Label { get; }
    public object Value { get; }
    public bool IsSelected { get; }
    public string CheckGlyph => IsSelected ? "\uE73E" : "";
}

/// <summary>弹幕源列表项（Flyout 内 DataTemplate 绑定用）。</summary>
public class DanmakuSourceItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Display => Name;
}

/// <summary>Slider 拇指浮窗：毫秒 → mm:ss / h:mm:ss。</summary>
public class MsTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double ms = value is double d ? d : 0;
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1 ? ts.ToString("h\\:mm\\:ss") : ts.ToString("mm\\:ss");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
