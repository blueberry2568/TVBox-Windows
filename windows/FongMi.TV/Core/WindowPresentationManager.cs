using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace FongMi.TV.Core;

internal enum PlaybackWindowMode
{
    Normal,
    FullScreen,
    Compact,
}

/// <summary>Owns the native window geometry while playback temporarily changes its presenter.</summary>
internal sealed partial class WindowPresentationManager
{
    const uint SwShowMaximized = 3;
    const uint WmNonClientLeftButtonDown = 0x00A1;
    const nuint HitCaption = 2;
    const double CompactMarginDip = 20;

    readonly MainWindow _window;
    Snapshot _snapshot;

    public PlaybackWindowMode Mode { get; private set; }

    public WindowPresentationManager(MainWindow window) => _window = window;

    public void EnterFullScreen()
    {
        CaptureNormalWindow();
        _window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Mode = PlaybackWindowMode.FullScreen;
        Logger.D("Presentation", "进入播放全屏");
    }

    public void EnterCompact(double widthDip, double heightDip)
    {
        CaptureNormalWindow();

        var presenter = _window.AppWindow.Presenter as OverlappedPresenter;
        var reusedPresenter = presenter != null;
        presenter ??= OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        if (!reusedPresenter) _window.AppWindow.SetPresenter(presenter);
        try { presenter.Restore(false); } catch { }

        var bounds = GetCompactBounds(widthDip, heightDip);
        _window.AppWindow.MoveAndResize(bounds);
        Mode = PlaybackWindowMode.Compact;
        Logger.D("Presentation", $"进入小窗 bounds={bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}");
        _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (Mode == PlaybackWindowMode.Compact) _window.AppWindow.MoveAndResize(bounds);
        });
    }

    public void BeginCompactDrag()
    {
        if (Mode != PlaybackWindowMode.Compact) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (hwnd == 0) return;
        _ = ReleaseCapture();
        _ = SendMessage(hwnd, WmNonClientLeftButtonDown, HitCaption, 0);
    }

    public void Restore()
    {
        var snapshot = _snapshot;
        if (snapshot == null)
        {
            Mode = PlaybackWindowMode.Normal;
            return;
        }

        try
        {
            RestorePresenter(snapshot);
            RestoreOverlappedProperties(snapshot);
            ApplySnapshot(snapshot);
        }
        catch (Exception e)
        {
            // Keep the snapshot: presenter transitions can still be settling when the
            // first restore runs. The caller may retry, and one dispatcher retry handles
            // that transient case without losing the original placement.
            Logger.E("Presentation", "恢复窗口失败，将保留快照重试: " + e.Message);
            QueueRestoreRetry(snapshot);
            throw;
        }

        Mode = PlaybackWindowMode.Normal;
        if (ReferenceEquals(_snapshot, snapshot)) _snapshot = null;
        Logger.D("Presentation", $"恢复窗口 presenter={snapshot.PresenterKind}, maximized={snapshot.WasMaximized}");

        // Presenter changes can finish after SetPresenter returns. Reapply the native
        // placement before the next layout settles so maximized windows never expose
        // their restored rectangle.
        _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (Mode != PlaybackWindowMode.Normal || _snapshot != null) return;
            try { ApplySnapshot(snapshot); }
            catch (Exception e) { Logger.E("Presentation", "窗口位置二次校正失败: " + e.Message); }
        });
    }

    void RestorePresenter(Snapshot snapshot)
    {
        var current = _window.AppWindow.Presenter;
        if (snapshot.OverlappedPresenter != null && ReferenceEquals(current, snapshot.OverlappedPresenter)) return;
        if (current?.Kind == snapshot.PresenterKind && snapshot.OverlappedPresenter == null) return;

        Exception originalError = null;
        if (snapshot.OverlappedPresenter != null)
        {
            try
            {
                _window.AppWindow.SetPresenter(snapshot.OverlappedPresenter);
                return;
            }
            catch (Exception e)
            {
                originalError = e;
                current = _window.AppWindow.Presenter;
                if (ReferenceEquals(current, snapshot.OverlappedPresenter)) return;
            }
        }

        try
        {
            if (_window.AppWindow.Presenter?.Kind != snapshot.PresenterKind)
                _window.AppWindow.SetPresenter(snapshot.PresenterKind);
        }
        catch (Exception fallbackError)
        {
            if (originalError == null)
                throw new InvalidOperationException("无法恢复原窗口 presenter", fallbackError);
            throw new AggregateException("无法恢复原窗口 presenter", originalError, fallbackError);
        }
    }

    void QueueRestoreRetry(Snapshot snapshot)
    {
        if (++snapshot.RestoreAttempts > 1) return;
        _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (!ReferenceEquals(_snapshot, snapshot)) return;
            try { Restore(); }
            catch (Exception e) { Logger.E("Presentation", "窗口恢复重试失败: " + e.Message); }
        });
    }

    void CaptureNormalWindow()
    {
        if (_snapshot != null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var placement = WindowPlacement.Create();
        var hasPlacement = hwnd != 0 && GetWindowPlacement(hwnd, ref placement);
        _snapshot = new Snapshot
        {
            PresenterKind = _window.AppWindow.Presenter.Kind,
            Placement = placement,
            HasPlacement = hasPlacement,
            Bounds = new RectInt32(
                _window.AppWindow.Position.X,
                _window.AppWindow.Position.Y,
                _window.AppWindow.Size.Width,
                _window.AppWindow.Size.Height),
            WasMaximized = hasPlacement && placement.ShowCommand == SwShowMaximized,
        };
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            _snapshot.OverlappedPresenter = presenter;
            _snapshot.IsAlwaysOnTop = presenter.IsAlwaysOnTop;
            _snapshot.IsResizable = presenter.IsResizable;
            _snapshot.IsMinimizable = presenter.IsMinimizable;
            _snapshot.IsMaximizable = presenter.IsMaximizable;
            _snapshot.HasBorder = presenter.HasBorder;
            _snapshot.HasTitleBar = presenter.HasTitleBar;
        }
    }

    void RestoreOverlappedProperties(Snapshot snapshot)
    {
        if (snapshot.OverlappedPresenter == null || _window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        Exception firstError = null;
        Try(() => presenter.IsAlwaysOnTop = snapshot.IsAlwaysOnTop);
        Try(() => presenter.IsResizable = snapshot.IsResizable);
        Try(() => presenter.IsMinimizable = snapshot.IsMinimizable);
        Try(() => presenter.IsMaximizable = snapshot.IsMaximizable);
        Try(() => presenter.SetBorderAndTitleBar(snapshot.HasBorder, snapshot.HasTitleBar));
        if (firstError != null) Logger.E("Presentation", "部分窗口属性恢复失败: " + firstError.Message);

        void Try(Action restore)
        {
            try { restore(); }
            catch (Exception e) { firstError ??= e; }
        }
    }

    void ApplySnapshot(Snapshot snapshot)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (snapshot.HasPlacement && hwnd != 0)
        {
            var placement = snapshot.Placement;
            placement.Length = (uint)Marshal.SizeOf<WindowPlacement>();
            if (SetWindowPlacement(hwnd, ref placement)) return;
        }

        _window.AppWindow.MoveAndResize(snapshot.Bounds);
        if (snapshot.WasMaximized && _window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    RectInt32 GetCompactBounds(double widthDip, double heightDip)
    {
        var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = area?.WorkArea ?? new RectInt32(0, 0, 1280, 720);
        var scale = GetWindowScale();
        var margin = Math.Max(8, (int)Math.Round(CompactMarginDip * scale));
        var width = Math.Clamp((int)Math.Round(widthDip * scale), 320, Math.Max(320, work.Width - margin * 2));
        var height = Math.Clamp((int)Math.Round(heightDip * scale), 220, Math.Max(220, work.Height - margin * 2));
        return new RectInt32(
            work.X + work.Width - width - margin,
            work.Y + margin,
            width,
            height);
    }

    double GetWindowScale()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var dpi = hwnd == 0 ? 0u : GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96d : 1d;
        }
        catch { return 1d; }
    }

    sealed class Snapshot
    {
        public AppWindowPresenterKind PresenterKind { get; init; }
        public WindowPlacement Placement { get; init; }
        public bool HasPlacement { get; init; }
        public RectInt32 Bounds { get; init; }
        public bool WasMaximized { get; init; }
        public OverlappedPresenter OverlappedPresenter { get; set; }
        public bool IsAlwaysOnTop { get; set; }
        public bool IsResizable { get; set; }
        public bool IsMinimizable { get; set; }
        public bool IsMaximizable { get; set; }
        public bool HasBorder { get; set; }
        public bool HasTitleBar { get; set; }
        public int RestoreAttempts { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
        public NativeRect Device;

        public static WindowPlacement Create() => new() { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
