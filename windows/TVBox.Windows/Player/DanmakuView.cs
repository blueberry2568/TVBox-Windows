using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TVBoxForWindows.Player;

/// <summary>弹幕渲染层（自研，替代 DanmakuFlameMaster）：纯 C# Grid + Canvas，
/// DispatcherQueueTimer ~60fps 步进；滚动(mode1)/顶部(5)/底部(4)三种，轨道分配防重叠；
/// 按 PlayerCore.TimeChanged 同步（跳变&gt;1.5s 视为 Seek，重扫窗口）。</summary>
public class DanmakuView : Grid
{
    const double BaseFontSize = 22;
    const double ScrollDurationMs = 8000;  // 基准滚动时长（除以 SpeedFactor）
    const double FixedDurationMs = 5000;   // 顶部/底部驻留时长
    const double TrackGap = 24;            // 滚动轨道判定的尾距

    readonly Canvas _canvas = new() { IsHitTestVisible = false };
    readonly List<Active> _scrolls = new();
    readonly Dictionary<int, Active> _lastInTrack = new();  // 滚动轨道 → 最后进入的弹幕
    readonly Dictionary<int, Active> _topTracks = new();
    readonly Dictionary<int, Active> _bottomTracks = new();
    readonly Stopwatch _posSw = new();     // TimeChanged 之间的插值
    readonly Stopwatch _frameSw = new();

    DispatcherQueueTimer _timer;
    PlayerCore _core;
    DanmakuEngine _engine;
    long _baseMs;
    int _index;
    bool _visible = true;
    bool _suspended;
    double _fontScale = 1;
    double _speed = 1;
    double _area = 0.85;

    public DanmakuView()
    {
        IsHitTestVisible = false;
        Children.Add(_canvas);
        SizeChanged += (s, e) => Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    // ---- 外观属性（任务面板绑定用）----
    public double FontScale { get => _fontScale; set => SetFontScale(value); }
    public double SpeedFactor { get => _speed; set => SetSpeed(value); }
    public double AreaPercent { get => _area; set => SetArea(value); }
    public bool Visible { get => _visible; set => SetVisible(value); }

    /// <summary>绑定播放核心与弹幕数据源并开始渲染。</summary>
    public void Bind(PlayerCore core, DanmakuEngine engine)
    {
        Unbind();
        _core = core;
        _engine = engine;
        if (core != null) core.TimeChanged += OnTimeChanged;
        if (_timer == null && DispatcherQueue != null)
        {
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += (s, e) => OnTick();
        }
        _frameSw.Restart();
        if (_visible && !_suspended) _timer?.Start();
    }

    /// <summary>解绑并清屏。</summary>
    public void Unbind()
    {
        if (_core != null) _core.TimeChanged -= OnTimeChanged;
        _core = null;
        _engine = null;
        _suspended = false;
        _timer?.Stop();
        ClearScreen();
        _baseMs = 0;
        _index = 0;
        _posSw.Reset();
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _timer?.Stop();
            ClearScreen();
        }
        else if (!_suspended && _core != null)
        {
            _frameSw.Restart();
            _timer?.Start();
        }
    }

    public void Suspend()
    {
        _suspended = true;
        _timer?.Stop();
        _frameSw.Stop();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        _frameSw.Restart();
        if (_visible && _core != null) _timer?.Start();
    }

    public void SetOpacity(double value) => _canvas.Opacity = Math.Clamp(value, 0.1, 1);

    public void SetFontScale(double value) { _fontScale = Math.Clamp(value, 0.5, 2); }

    public void SetSpeed(double value) { _speed = Math.Clamp(value, 0.5, 2.5); }

    public void SetArea(double value) { _area = Math.Clamp(value, 0.1, 1); }

    // ---- 时间同步 ----

    void OnTimeChanged(long ms)
    {
        long predicted = _baseMs + _posSw.ElapsedMilliseconds;
        if (Math.Abs(ms - predicted) > 1500) Reseek(ms);
        _baseMs = ms;
        _posSw.Restart();
    }

    /// <summary>Seek 后重扫：清屏并把发射指针移到 ms 位置。</summary>
    void Reseek(long ms)
    {
        ClearScreen();
        var items = _engine?.Items;
        if (items == null) { _index = 0; return; }
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (items[mid].TimeMs < ms) lo = mid + 1; else hi = mid;
        }
        _index = lo;
    }

    void ClearScreen()
    {
        _canvas.Children.Clear();
        _scrolls.Clear();
        _lastInTrack.Clear();
        _topTracks.Clear();
        _bottomTracks.Clear();
    }

    // ---- 帧步进 ----

    void OnTick()
    {
        double dt = _frameSw.ElapsedMilliseconds;
        _frameSw.Restart();
        if (dt <= 0 || dt > 200) dt = 16;
        if (!_visible || _engine == null || _core == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        bool playing = false;
        try { playing = _core.IsPlaying; } catch { }
        if (!playing) return;
        long now = _baseMs + _posSw.ElapsedMilliseconds;
        Emit(now);
        StepScrolls(dt);
        ExpireFixed(now);
    }

    void Emit(long now)
    {
        var items = _engine.Items;
        while (_index < items.Count && items[_index].TimeMs <= now)
        {
            var item = items[_index++];
            if (item.TimeMs >= now - 400) Spawn(item, now);
        }
    }

    void Spawn(DanmakuItem item, long now)
    {
        double fontSize = BaseFontSize * _fontScale;
        double trackHeight = fontSize * 1.5;
        int trackCount = Math.Max(1, (int)(ActualHeight * _area / trackHeight));
        var el = new TextBlock
        {
            Text = item.Text,
            FontSize = fontSize,
            Foreground = BrushOf(item.Color),
            IsHitTestVisible = false,
        };
        el.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = el.DesiredSize.Width;
        double canvasW = ActualWidth;

        if (item.Mode == 5 || item.Mode == 4)
        {
            var tracks = item.Mode == 5 ? _topTracks : _bottomTracks;
            int track = -1;
            for (int t = 0; t < trackCount; t++) if (!tracks.ContainsKey(t)) { track = t; break; }
            if (track < 0) return; // 无空轨道 → 丢弃
            double y = item.Mode == 5 ? track * trackHeight : ActualHeight - (track + 1) * trackHeight;
            var active = new Active { El = el, X = (canvasW - w) / 2, Y = y, W = w, Track = track, Mode = item.Mode, Expire = now + (long)(FixedDurationMs / _speed) };
            Place(active);
            tracks[track] = active;
        }
        else
        {
            double v = (canvasW + w) / (ScrollDurationMs / _speed); // px/ms
            int track = -1;
            for (int t = 0; t < trackCount; t++)
            {
                if (!_lastInTrack.TryGetValue(t, out var last) || last.El.Parent == null) { track = t; break; }
                if (last.X + last.W < canvasW - TrackGap && last.V >= v * 0.8) { track = t; break; }
            }
            if (track < 0) return; // 无空轨道 → 丢弃
            var active = new Active { El = el, X = canvasW, Y = track * trackHeight, W = w, V = v, Track = track, Mode = 1 };
            Place(active);
            _scrolls.Add(active);
            _lastInTrack[track] = active;
        }
    }

    void Place(Active active)
    {
        Canvas.SetLeft(active.El, active.X);
        Canvas.SetTop(active.El, active.Y);
        _canvas.Children.Add(active.El);
    }

    void StepScrolls(double dt)
    {
        for (int i = _scrolls.Count - 1; i >= 0; i--)
        {
            var a = _scrolls[i];
            a.X -= a.V * dt;
            if (a.X + a.W < 0)
            {
                _canvas.Children.Remove(a.El);
                _scrolls.RemoveAt(i);
                if (_lastInTrack.TryGetValue(a.Track, out var last) && last == a) _lastInTrack.Remove(a.Track);
            }
            else Canvas.SetLeft(a.El, a.X);
        }
    }

    void ExpireFixed(long now)
    {
        foreach (var tracks in new[] { _topTracks, _bottomTracks })
        {
            foreach (var key in tracks.Keys.ToList())
            {
                if (tracks[key].Expire <= now)
                {
                    _canvas.Children.Remove(tracks[key].El);
                    tracks.Remove(key);
                }
            }
        }
    }

    static SolidColorBrush BrushOf(uint rgb) =>
        new(Color.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));

    /// <summary>屏上活动弹幕。</summary>
    class Active
    {
        public TextBlock El;
        public double X, Y, W, V; // V: px/ms（仅滚动）
        public int Track, Mode;
        public long Expire;       // 仅顶部/底部
    }
}
