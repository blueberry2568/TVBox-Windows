using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace TVBoxForWindows.Core;

internal enum PlaybackWindowMode
{
    Normal,
    FullScreen,
    Compact,
}

/// <summary>Owns the native window geometry while playback temporarily changes its presenter.</summary>
internal sealed partial class WindowPresentationManager
{
    const uint SwShowNormal = 1;
    const uint SwShowMaximized = 3;
    const uint SwShowMinimized = 2;
    const uint SwMinimize = 6;
    const uint SwShowMinNoActive = 7;
    const uint SwForceMinimize = 11;
    const uint WmNonClientLeftButtonDown = 0x00A1;
    const nuint HitCaption = 2;
    const double CompactMarginDip = 20;
    const int PlacementStabilizerIntervalMs = 50;
    const int PlacementStabilizerDurationMs = 3000;
    const int PlacementRepairQuietPeriodMs = 500;

    readonly MainWindow _window;
    Snapshot _snapshot;
    MaximizedPlacementStabilization _placementStabilization;
    Microsoft.UI.Dispatching.DispatcherQueueTimer _placementStabilizer;
    int _transitionGeneration;

    public PlaybackWindowMode Mode { get; private set; }

    public WindowPresentationManager(MainWindow window) => _window = window;

    public void Dispose() => StopMaximizedPlacementStabilization("window closed");

    /// <summary>
    /// Runs from the native SC_RESTORE message before Windows consumes the HWND's
    /// restore rectangle. Presenter changes share that rectangle, so this is the
    /// final guard against a compact presenter overwriting the application's bounds.
    /// </summary>
    public void PrepareSystemRestore()
    {
        var state = _placementStabilization;
        if (!IsGuardApplicable(state)) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var current = WindowPlacement.Create();
        if (hwnd == 0 || !GetWindowPlacement(hwnd, ref current)) return;

        state.AwaitingMaximize = false;
        state.RestorePrepared = true;
        RepairNormalPosition(state, hwnd, ref current, "before SC_RESTORE");
    }

    public void EnterFullScreen()
    {
        CaptureNormalWindow();
        StopMaximizedPlacementStabilization("new full-screen transition");
        _transitionGeneration++;
        _window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Mode = PlaybackWindowMode.FullScreen;
        Logger.D("Presentation", "进入播放全屏");
    }

    public void EnterCompact(double widthDip, double heightDip)
    {
        CaptureNormalWindow();
        StopMaximizedPlacementStabilization("new compact transition");
        var generation = ++_transitionGeneration;

        // PiP owns a separate presenter so compact-only presenter properties cannot
        // mutate the main presenter. WINDOWPLACEMENT is still shared by the HWND, so
        // Restore preserves and guards its original normal rectangle below. MainWindow
        // suppresses native DWM transitions and supplies one shared content transition.
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = true;
        presenter.IsMinimizable = true;
        presenter.IsMaximizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        var bounds = GetCompactBounds(widthDip, heightDip);
        _window.AppWindow.SetPresenter(presenter);
        var placedAtomically = TryApplyCompactPlacement(bounds);
        if (!placedAtomically)
        {
            try { presenter.Restore(false); } catch { }
            _window.AppWindow.MoveAndResize(bounds);
        }
        Mode = PlaybackWindowMode.Compact;
        Logger.D("Presentation", $"进入小窗 bounds={bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}");
        if (!placedAtomically) _window.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
            () =>
        {
            if (generation == _transitionGeneration && Mode == PlaybackWindowMode.Compact)
                _window.AppWindow.MoveAndResize(bounds);
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
            // A redundant restore can arrive after PiP has already returned to a
            // maximized window. Keep its normal-bounds guard alive until Windows
            // consumes the next genuine system Restore command.
            Mode = PlaybackWindowMode.Normal;
            return;
        }

        StopMaximizedPlacementStabilization("new restore transition");
        var generation = ++_transitionGeneration;
        var previousMode = Mode;

        try
        {
            RestorePresenter(snapshot);
            RestoreOverlappedProperties(snapshot);
            // Guard callbacks can arrive reentrantly from Maximize. Publish the
            // target mode before starting the native transition so they never tear
            // down the saved normal-bounds protection as a stale compact state.
            Mode = PlaybackWindowMode.Normal;
            ApplySnapshot(snapshot, generation);
        }
        catch (Exception e)
        {
            Mode = previousMode;
            // Keep the snapshot: presenter transitions can still be settling when the
            // first restore runs. The caller may retry, and one dispatcher retry handles
            // that transient case without losing the original placement.
            Logger.E("Presentation", "恢复窗口失败，将保留快照重试: " + e.Message);
            QueueRestoreRetry(snapshot, generation, Mode);
            throw;
        }

        if (ReferenceEquals(_snapshot, snapshot)) _snapshot = null;
        Logger.D("Presentation", $"恢复窗口 presenter={snapshot.PresenterKind}, maximized={snapshot.WasMaximized}");
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
            // A presenter-kind fallback is only needed when the original presenter
            // instance can no longer be attached after a system transition.
            _window.AppWindow.SetPresenter(snapshot.PresenterKind);
        }
        catch (Exception fallbackError)
        {
            if (originalError == null)
                throw new InvalidOperationException("无法恢复原窗口 presenter", fallbackError);
            throw new AggregateException("无法恢复原窗口 presenter", originalError, fallbackError);
        }
    }

    void QueueRestoreRetry(Snapshot snapshot, int generation, PlaybackWindowMode expectedMode)
    {
        if (++snapshot.RestoreAttempts > 1) return;
        _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            if (generation != _transitionGeneration ||
                Mode != expectedMode ||
                !ReferenceEquals(_snapshot, snapshot)) return;
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
        var guard = _placementStabilization;
        if (hasPlacement &&
            placement.ShowCommand == SwShowMaximized &&
            IsGuardApplicable(guard))
        {
            // A new playback transition can start while a late presenter callback is
            // still settling. Capture the guarded normal bounds, never compact ones.
            placement.NormalPosition = guard.Placement.NormalPosition;
        }
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
        Logger.D(
            "Presentation",
            $"Captured window placement hasPlacement={hasPlacement}, maximized={_snapshot.WasMaximized}, " +
            $"show={placement.ShowCommand}, normal={FormatRect(placement.NormalPosition)}, " +
            $"bounds={_snapshot.Bounds.X},{_snapshot.Bounds.Y},{_snapshot.Bounds.Width}x{_snapshot.Bounds.Height}");
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

    void ApplySnapshot(Snapshot snapshot, int generation)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        if (snapshot.WasMaximized && _window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // MainWindow disables DWM transitions during this presenter swap, so the
            // original maximized placement can be restored atomically without exposing
            // rcNormalPosition. The guard keeps that rectangle authoritative afterward.
            presenter.Maximize();
            if (snapshot.HasPlacement && hwnd != 0)
            {
                var maximizedPlacement = snapshot.Placement;
                maximizedPlacement.Length = (uint)Marshal.SizeOf<WindowPlacement>();
                maximizedPlacement.ShowCommand = SwShowMaximized;
                _ = SetWindowPlacement(hwnd, ref maximizedPlacement);
                StartMaximizedPlacementStabilization(maximizedPlacement, generation);
            }
            return;
        }

        if (snapshot.HasPlacement && hwnd != 0)
        {
            var placement = snapshot.Placement;
            placement.Length = (uint)Marshal.SizeOf<WindowPlacement>();
            if (SetWindowPlacement(hwnd, ref placement)) return;
        }

        _window.AppWindow.MoveAndResize(snapshot.Bounds);
    }

    void StartMaximizedPlacementStabilization(WindowPlacement placement, int generation)
    {
        StopMaximizedPlacementStabilization("restart");
        _placementStabilization = new MaximizedPlacementStabilization
        {
            Generation = generation,
            Placement = placement,
            StartedAt = Environment.TickCount64,
            AwaitingMaximize = true,
        };
        _window.AppWindow.Changed += OnStabilizingAppWindowChanged;

        _placementStabilizer = _window.DispatcherQueue.CreateTimer();
        _placementStabilizer.Interval = TimeSpan.FromMilliseconds(PlacementStabilizerIntervalMs);
        _placementStabilizer.IsRepeating = true;
        _placementStabilizer.Tick += OnPlacementStabilizerTick;
        _placementStabilizer.Start();
        Logger.D(
            "Presentation",
            $"Started maximized placement stabilization generation={generation}, normal={FormatRect(placement.NormalPosition)}");
    }

    void OnStabilizingAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var state = _placementStabilization;
        if (state == null ||
            (!args.DidPresenterChange && !args.DidPositionChange && !args.DidSizeChange)) return;

        state.WindowChangeObserved = true;
        if (state.ValidationQueued) return;
        state.ValidationQueued = true;
        var generation = state.Generation;
        _window.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
            () =>
            {
                var active = _placementStabilization;
                if (active == null || active.Generation != generation) return;
                active.ValidationQueued = false;
                ValidateGuardedPlacement(active, "AppWindow change");
            });
    }

    void OnPlacementStabilizerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _placementStabilizer)) return;

        var state = _placementStabilization;
        if (state == null) return;
        ValidateGuardedPlacement(state, "stabilizer tick");
        if (!ReferenceEquals(state, _placementStabilization)) return;

        var now = Environment.TickCount64;
        var elapsed = now - state.StartedAt;
        var repairSettled = state.LastRepairAt == 0 ||
            now - state.LastRepairAt >= PlacementRepairQuietPeriodMs;
        if (elapsed >= PlacementStabilizerDurationMs && repairSettled)
            StopPlacementTimer("initial presenter transition settled");
    }

    void ValidateGuardedPlacement(MaximizedPlacementStabilization state, string reason)
    {
        if (!IsGuardApplicable(state))
        {
            StopMaximizedPlacementStabilization("window mode changed");
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var current = WindowPlacement.Create();
        if (hwnd == 0 || !GetWindowPlacement(hwnd, ref current))
        {
            StopMaximizedPlacementStabilization("placement unavailable");
            return;
        }

        if (current.ShowCommand == SwShowMaximized)
        {
            state.AwaitingMaximize = false;
            RepairNormalPosition(state, hwnd, ref current, reason);
            state.WindowChangeObserved = false;
            return;
        }

        if (IsMinimized(current.ShowCommand)) return;

        // Maximize is asynchronous. While it is animating the HWND can still report
        // SW_SHOWNORMAL with the compact rectangle. Treating that transient state as
        // a real user restore would replay the old normal bounds and create the
        // visible shrink-then-grow sequence we are trying to avoid.
        if (state.AwaitingMaximize) return;

        // Restore is authoritative: preserve its current ShowCommand and repair only
        // rcNormalPosition. This fallback also covers drag-to-restore and keyboard
        // restore paths that do not pass through WM_SYSCOMMAND/SC_RESTORE.
        RepairNormalPosition(state, hwnd, ref current, reason + " after restore");
        Logger.D(
            "Presentation",
            $"Completed guarded restore show={current.ShowCommand}, " +
            $"normal={FormatRect(current.NormalPosition)}, prepared={state.RestorePrepared}");
        StopMaximizedPlacementStabilization(null);
    }

    bool IsGuardApplicable(MaximizedPlacementStabilization state) =>
        state != null &&
        state.Generation == _transitionGeneration &&
        Mode == PlaybackWindowMode.Normal &&
        _window.AppWindow.Presenter is OverlappedPresenter &&
        _window.AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped;

    static bool IsMinimized(uint showCommand) =>
        showCommand is SwShowMinimized or SwMinimize or SwShowMinNoActive or SwForceMinimize;

    void RepairNormalPosition(
        MaximizedPlacementStabilization state,
        nint hwnd,
        ref WindowPlacement current,
        string reason)
    {
        if (SameRect(current.NormalPosition, state.Placement.NormalPosition)) return;

        var actual = current.NormalPosition;
        current.Length = (uint)Marshal.SizeOf<WindowPlacement>();
        current.NormalPosition = state.Placement.NormalPosition;
        if (SetWindowPlacement(hwnd, ref current))
        {
            state.RepairCount++;
            state.LastRepairAt = Environment.TickCount64;
            Logger.D(
                "Presentation",
                $"Repaired placement #{state.RepairCount} ({reason}): " +
                $"actual={FormatRect(actual)}, expected={FormatRect(current.NormalPosition)}, " +
                $"show={current.ShowCommand}, appWindowChanged={state.WindowChangeObserved}");
        }
        else Logger.E("Presentation", "SetWindowPlacement failed while repairing guarded restore bounds");
    }

    void StopPlacementTimer(string reason)
    {
        var timer = _placementStabilizer;
        _placementStabilizer = null;
        if (timer == null) return;

        timer.Stop();
        timer.Tick -= OnPlacementStabilizerTick;
        if (!string.IsNullOrEmpty(reason))
            Logger.D("Presentation", $"Stopped placement timer reason={reason}; restore guard remains active");
    }

    void StopMaximizedPlacementStabilization(string reason)
    {
        var state = _placementStabilization;
        _placementStabilization = null;
        StopPlacementTimer(null);
        if (state == null) return;

        _window.AppWindow.Changed -= OnStabilizingAppWindowChanged;
        if (!string.IsNullOrEmpty(reason))
            Logger.D("Presentation", $"Stopped placement stabilization reason={reason}, repairs={state.RepairCount}");
    }

    static bool SameRect(NativeRect left, NativeRect right) =>
        left.Left == right.Left &&
        left.Top == right.Top &&
        left.Right == right.Right &&
        left.Bottom == right.Bottom;

    static string FormatRect(NativeRect rect) =>
        $"{rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top}";

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

    bool TryApplyCompactPlacement(RectInt32 bounds)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var placement = WindowPlacement.Create();
        if (hwnd == 0 || !GetWindowPlacement(hwnd, ref placement)) return false;

        try
        {
            // WINDOWPLACEMENT uses work-area coordinates while AppWindow bounds use
            // screen coordinates. Changing the target rectangle and show state in
            // one operation avoids exposing the previous normal size between a
            // Restore call and MoveAndResize.
            var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = area?.WorkArea ?? new RectInt32(0, 0, 0, 0);
            var outer = area?.OuterBounds ?? work;
            var workOffsetX = work.X - outer.X;
            var workOffsetY = work.Y - outer.Y;
            placement.Length = (uint)Marshal.SizeOf<WindowPlacement>();
            placement.Flags = 0;
            placement.ShowCommand = SwShowNormal;
            placement.NormalPosition = new NativeRect
            {
                Left = bounds.X - workOffsetX,
                Top = bounds.Y - workOffsetY,
                Right = bounds.X - workOffsetX + bounds.Width,
                Bottom = bounds.Y - workOffsetY + bounds.Height,
            };
            return SetWindowPlacement(hwnd, ref placement);
        }
        catch
        {
            return false;
        }
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

    sealed class MaximizedPlacementStabilization
    {
        public int Generation { get; init; }
        public WindowPlacement Placement { get; init; }
        public long StartedAt { get; init; }
        public bool WindowChangeObserved { get; set; }
        public bool ValidationQueued { get; set; }
        public bool RestorePrepared { get; set; }
        public bool AwaitingMaximize { get; set; }
        public int RepairCount { get; set; }
        public long LastRepairAt { get; set; }
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
