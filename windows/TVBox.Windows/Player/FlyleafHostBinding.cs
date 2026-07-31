using System.Numerics;
using FlyleafLib.Controls.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace TVBoxForWindows.Player;

/// <summary>在 FlyleafHost 完成有效布局后绑定播放器，并同步视频布局。</summary>
public sealed class FlyleafHostBinding : IDisposable
{
    const string TAG = "FlyleafHostBinding";

    readonly FlyleafHost _host;
    PlayerCore _core;
    int _generation;
    int _renderWidth;
    int _renderHeight;
    double _surfaceCornerRadius;
    Visual _surfaceVisual;
    CompositionRoundedRectangleGeometry _surfaceClipGeometry;
    CompositionGeometricClip _surfaceClip;
    bool _queued;
    bool _disposed;

    public FlyleafHostBinding(FlyleafHost host)
    {
        _host = host;
        _host.Loaded += OnLoaded;
        _host.SizeChanged += OnSizeChanged;
    }

    public void Attach(PlayerCore core)
    {
        if (_disposed) return;
        _core = core;
        _generation++;
        _renderWidth = _renderHeight = 0;
        RequestSynchronize();
    }

    public void RequestSynchronize()
    {
        if (_disposed || _queued) return;
        _queued = true;
        var generation = _generation;
        var queue = _host.DispatcherQueue ?? App.Dispatcher;
        if (queue == null || !queue.TryEnqueue(DispatcherQueuePriority.Low, () => Synchronize(generation)))
            _queued = false;
    }

    /// <summary>
    /// Clips Flyleaf's native swap-chain surface itself. A clip on an ancestor
    /// XAML element does not reliably constrain SwapChainPanel composition.
    /// </summary>
    public void SetSurfaceCornerRadius(double radius)
    {
        if (_disposed) return;
        _surfaceCornerRadius = Math.Max(0, radius);
        ApplySurfaceClip();
        RequestSynchronize();
    }

    void OnLoaded(object sender, RoutedEventArgs e) => RequestSynchronize();

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width >= 2 && e.NewSize.Height >= 2) RequestSynchronize();
    }

    void Synchronize(int generation)
    {
        _queued = false;
        if (_disposed || generation != _generation || _core?.Fly == null || !_host.IsLoaded) return;
        var width = _host.ActualWidth;
        var height = _host.ActualHeight;
        if (width < 2 || height < 2) return;
        try
        {
            if (!ReferenceEquals(_host.Player, _core.Fly))
            {
                _host.Player = _core.Fly;
                ApplySurfaceClip();
                // Allow FlyleafHost to finish SetupWinUI before applying the video layout.
                RequestSynchronize();
                return;
            }
            ApplySurfaceClip();
            var scpWidth = _host.SCP?.ActualWidth ?? width;
            var scpHeight = _host.SCP?.ActualHeight ?? height;
            var renderWidth = Math.Max(2, (int)Math.Round(scpWidth));
            var renderHeight = Math.Max(2, (int)Math.Round(scpHeight));
            var resized = renderWidth != _renderWidth || renderHeight != _renderHeight;
            var swapChain = _core.Fly.Renderer?.SwapChain;
            if (resized && swapChain != null)
            {
                swapChain.Resize(renderWidth, renderHeight);
                _renderWidth = renderWidth;
                _renderHeight = renderHeight;
            }
            _core.RefreshVideoLayout();
            if (resized && swapChain != null)
            {
                var renderer = _core.Fly.Renderer;
                Core.Logger.D(TAG, $"渲染尺寸 host={width:0}x{height:0}, scp={scpWidth:0}x{scpHeight:0}, renderer={renderer?.ControlWidth ?? 0}x{renderer?.ControlHeight ?? 0}");
            }
        }
        catch (Exception e) { Core.Logger.E(TAG, "同步视频布局失败: " + e.Message); }
    }

    void ApplySurfaceClip()
    {
        var surface = _host.SCP;
        if (surface == null) return;

        var visual = ElementCompositionPreview.GetElementVisual(surface);
        if (_surfaceCornerRadius <= 0)
        {
            visual.Clip = null;
            return;
        }

        var width = surface.ActualWidth;
        var height = surface.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (!ReferenceEquals(_surfaceVisual, visual))
        {
            _surfaceVisual = visual;
            _surfaceClipGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            _surfaceClip = visual.Compositor.CreateGeometricClip(_surfaceClipGeometry);
        }

        var offsetX = 0d;
        var offsetY = 0d;
        var clipWidth = width;
        var clipHeight = height;
        try
        {
            var scale = surface.XamlRoot?.RasterizationScale ?? 1d;
            var origin = surface.TransformToVisual(null)
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

        _surfaceClipGeometry.Offset = new Vector2((float)offsetX, (float)offsetY);
        _surfaceClipGeometry.Size = new Vector2((float)clipWidth, (float)clipHeight);
        _surfaceClipGeometry.CornerRadius = new Vector2((float)_surfaceCornerRadius);
        visual.Clip = _surfaceClip;
    }

    public void Detach()
    {
        if (_disposed) return;
        _generation++;
        _queued = false;
        _renderWidth = _renderHeight = 0;
        _core = null;
        if (_surfaceVisual != null) _surfaceVisual.Clip = null;
        try { _host.Player = null; } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _host.Loaded -= OnLoaded;
        _host.SizeChanged -= OnSizeChanged;
    }
}
