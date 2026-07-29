using FlyleafLib.Controls.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FongMi.TV.Player;

/// <summary>在 FlyleafHost 完成有效布局后绑定播放器，并同步视频布局。</summary>
public sealed class FlyleafHostBinding : IDisposable
{
    const string TAG = "FlyleafHostBinding";

    readonly FlyleafHost _host;
    readonly Microsoft.UI.Xaml.Media.SolidColorBrush _blackBackground =
        new(Microsoft.UI.Colors.Black);
    PlayerCore _core;
    int _generation;
    int _renderWidth;
    int _renderHeight;
    bool _queued;
    bool _disposed;

    public FlyleafHostBinding(FlyleafHost host)
    {
        _host = host;
        _host.Background = _blackBackground;
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
                // Allow FlyleafHost to finish SetupWinUI before applying the video layout.
                RequestSynchronize();
                return;
            }
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

    public void Detach()
    {
        if (_disposed) return;
        _generation++;
        _queued = false;
        _renderWidth = _renderHeight = 0;
        _core = null;
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
