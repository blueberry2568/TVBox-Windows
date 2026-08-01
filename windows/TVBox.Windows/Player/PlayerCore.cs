using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Microsoft.UI.Dispatching;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Player;

/// <summary>播放核心（移植自 Players.java）：封装 FlyleafLib Player，统一 Open/事件/缩放/倍速；FFmpeg 缺失时降级报错。</summary>
public class PlayerCore : IDisposable
{
    const string TAG = "PlayerCore";
    const string MissingFFmpegMsg = "FFmpeg 解码库未就绪：安装内容可能不完整，请重新安装或完整解压 TVBox";
    const long TransferSampleIntervalMs = 500;
    static readonly string[] RequiredFFmpegLibraries =
    {
        "avcodec-62.dll",
        "avdevice-62.dll",
        "avfilter-11.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll",
        "swscale-9.dll",
    };

    /// <summary>供 FlyleafHost 绑定；引擎未就绪时为 null。</summary>
    public FlyleafLib.MediaPlayer.Player Fly { get; }

    /// <summary>FFmpeg dll 是否就绪（StartEngine 成功）。</summary>
    public static bool EngineReady { get; private set; }

    static bool _engineTried;

    DispatcherQueueTimer _timer;
    PlayItem _item;
    int _scale;
    int _openGeneration;
    int _openingGeneration;
    int _activePlaybackGeneration;
    bool _formatRecoveryTried;
    bool _videoRecoveryTried;
    bool _recovering;
    long _ignorePlaybackStopUntil;
    bool _disposed;
    bool _uiUpdatesEnabled = true;
    readonly object _subtitleLock = new();
    SubtitleOpenRequest _subtitleActive;
    SubtitleOpenRequest _subtitlePending;
    long _subtitleRequestId;
    long _transferSampleTick;
    long _transferSampleBytes;
    int _transferSampleGeneration = -1;

    public event Action Opened;
    public event Action<string> Errored;
    public event Action Ended;
    public event Action<long, bool, string> SubtitleOpened;
    /// <summary>当前进度（毫秒），约 250ms 一次（UI 线程）。</summary>
    public event Action<long> TimeChanged;
    /// <summary>当前媒体有效载荷吞吐量（字节/秒，约 500ms 一次，UI 线程）。</summary>
    public event Action<double> TransferRateChanged;
    /// <summary>播放中的缓冲状态（UI 线程；起播阶段由 Opened/Errored 管理）。</summary>
    public event Action<bool> BufferingChanged;

    /// <summary>App 启动时调用一次：探测 ffmpeg 目录并 Engine.Start。缺失时 EngineReady=false（不自动下载）。</summary>
    public static void StartEngine()
    {
        if (_engineTried) return;
        _engineTried = true;
        try
        {
            var dir = FindFFmpeg();
            if (dir == null)
            {
                Core.Logger.E(TAG, "未找到完整的 FFmpeg 8.1 动态库目录（程序内置 ffmpeg 或数据目录 ffmpeg）");
                return;
            }
            FlyleafLib.Engine.Start(new EngineConfig
            {
                FFmpegPath = dir,
                UIRefresh = false,
            });
            EngineReady = true;
            Core.Logger.D(TAG, "FFmpeg 引擎已启动: " + dir);
        }
        catch (Exception e) { EngineReady = false; Core.Logger.E(TAG, "FFmpeg 引擎启动失败: " + e.Message); }
    }

    /// <summary>探测 FFmpeg dll 目录：程序目录/ffmpeg 优先，其次数据目录/ffmpeg。</summary>
    static string FindFFmpeg()
    {
        foreach (var dir in new[] { AppPaths.FFmpegDir, Path.Combine(AppPaths.Root ?? "", "ffmpeg") })
        {
            try
            {
                if (Directory.Exists(dir) && RequiredFFmpegLibraries.All(name => File.Exists(Path.Combine(dir, name))))
                    return dir;
            }
            catch { }
        }
        return null;
    }

    public PlayerCore()
    {
        if (!EngineReady) return;
        try
        {
            var config = new Config();
            config.Player.AutoPlay = true;
            // Keyframe seeking keeps repeated jumps responsive on remote HLS/HTTP media.
            config.Player.SeekAccurate = false;
            config.Demuxer.OpenTimeout = TimeSpan.FromMilliseconds(Math.Max(Setting.PlayTimeout, 15000)).Ticks;
            config.Demuxer.ReadTimeout = TimeSpan.FromMilliseconds(Math.Max(Setting.PlayTimeout, 15000)).Ticks;
            config.Demuxer.FormatOptToUnderlying = true;
            config.Demuxer.DefaultHTTPQueryToUnderlying = true;
            config.Demuxer.FormatOpt["reconnect"] = "1";
            config.Demuxer.FormatOpt["reconnect_streamed"] = "1";
            config.Demuxer.FormatOpt["reconnect_delay_max"] = "5";
            config.Video.AspectRatio = AspectRatio.Keep;
            config.Video.BackColor = System.Windows.Media.Colors.Black;
            config.Video.VideoAcceleration = Setting.Flag != 0;
            // Flyleaf's FFmpeg filter path provides higher-quality time stretching than
            // the basic resampler, avoiding metallic noise and gaps when speed changes.
            config.Audio.FiltersEnabled = true;
            config.Subtitles.Enabled = true;
            Fly = new FlyleafLib.MediaPlayer.Player(config);
            Fly.OpenCompleted += OnOpenCompleted;
            Fly.PlaybackStopped += OnPlaybackStopped;
            Fly.BufferingStarted += OnBufferingStarted;
            Fly.BufferingCompleted += OnBufferingCompleted;
        }
        catch (Exception e) { Core.Logger.E(TAG, "创建播放器失败: " + e.Message); }
        App.Post(() =>
        {
            if (App.Dispatcher == null) return;
            _timer = App.Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += OnUiTimerTick;
            if (_uiUpdatesEnabled) _timer.Start();
        });
    }

    void OnUiTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || Fly == null) return;
        SampleTransferRate();
        if (Fly.CanPlay) TimeChanged?.Invoke(PositionMs);
    }

    public void SetUiUpdatesEnabled(bool enabled)
    {
        _uiUpdatesEnabled = enabled;
        App.Post(() =>
        {
            if (_disposed || _timer == null) return;
            ResetTransferSampling(enabled);
            if (enabled) _timer.Start();
            else _timer.Stop();
        });
    }

    void SampleTransferRate()
    {
        var totalBytes = ReadTransferBytes();
        if (totalBytes < 0) return;

        var now = Environment.TickCount64;
        var generation = Volatile.Read(ref _openGeneration);
        if (_transferSampleGeneration != generation || _transferSampleTick == 0 ||
            totalBytes < _transferSampleBytes)
        {
            _transferSampleGeneration = generation;
            _transferSampleTick = now;
            _transferSampleBytes = totalBytes;
            return;
        }

        var elapsedMs = now - _transferSampleTick;
        if (elapsedMs < TransferSampleIntervalMs) return;

        var bytesPerSecond = Math.Max(0, totalBytes - _transferSampleBytes) * 1000d / elapsedMs;
        _transferSampleTick = now;
        _transferSampleBytes = totalBytes;
        TransferRateChanged?.Invoke(bytesPerSecond);
    }

    long ReadTransferBytes()
    {
        try
        {
            if (Fly == null) return -1;
            return Math.Max(0, Fly.VideoDemuxer?.TotalBytes ?? 0) +
                   Math.Max(0, Fly.AudioDemuxer?.TotalBytes ?? 0) +
                   Math.Max(0, Fly.SubtitlesDemuxer?.TotalBytes ?? 0);
        }
        catch { return -1; }
    }

    void ResetTransferSampling(bool notify, int generation = -1)
    {
        // Flyleaf resets demuxer counters asynchronously while replacing a source.
        // Let the first timer tick establish a baseline so bytes left by the previous
        // source cannot appear as a transient transfer-rate spike.
        _transferSampleTick = 0;
        _transferSampleBytes = 0;
        _transferSampleGeneration = generation;
        if (notify && !_disposed) TransferRateChanged?.Invoke(0);
    }

    public static string FormatTransferRate(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return "0 B/s";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0} KB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    public void Open(PlayItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Url))
        {
            var invalidGeneration = Interlocked.Increment(ref _openGeneration);
            ResetTransferSampling(true, invalidGeneration);
            Volatile.Write(ref _openingGeneration, 0);
            Volatile.Write(ref _activePlaybackGeneration, 0);
            _item = null;
            PostIfCurrent(invalidGeneration, () => Errored?.Invoke("播放地址为空"));
            return;
        }
        lock (_subtitleLock) _subtitlePending = null;
        var request = Snapshot(item);
        var generation = Interlocked.Increment(ref _openGeneration);
        ResetTransferSampling(true, generation);
        Volatile.Write(ref _activePlaybackGeneration, 0);
        _item = request;
        Volatile.Write(ref _openingGeneration, generation);
        _formatRecoveryTried = false;
        _videoRecoveryTried = false;
        _recovering = false;
        _ignorePlaybackStopUntil = Environment.TickCount64 + 2500;
        if (!EngineReady || Fly == null)
        {
            Volatile.Write(ref _openingGeneration, 0);
            PostIfCurrent(generation, () => Errored?.Invoke(MissingFFmpegMsg));
            return;
        }
        try
        {
            var options = BuildFormatOptions(request, generation);
            if (options == null)
            {
                Volatile.Write(ref _openingGeneration, 0);
                return;
            }
            Fly.Config.Demuxer.FormatOpt = options;
            ApplyFormat(request.Format);
            Fly.Config.Video.VideoAcceleration = Setting.Flag != 0;
            Core.Logger.D(TAG, $"打开媒体 #{generation} {DescribeUrl(request.Url)}, format={request.Format ?? "auto"}, headers=[{string.Join(',', request.Headers.Keys)}]");
            Fly.OpenAsync(request.Url);
        }
        catch (Exception e)
        {
            Volatile.Write(ref _openingGeneration, 0);
            PostIfCurrent(generation, () => Errored?.Invoke(e.Message));
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _openGeneration);
        Volatile.Write(ref _openingGeneration, 0);
        Volatile.Write(ref _activePlaybackGeneration, 0);
        _item = null;
        ResetTransferSampling(true);
        try { Fly?.Stop(); } catch { }
    }

    public void PlayPause() { try { Fly?.TogglePlayPause(); } catch { } }

    public void SeekMs(long ms)
    {
        try
        {
            if (Fly == null) return;
            var target = (int)Math.Min(int.MaxValue, Math.Max(0, ms));
            Fly.Seek(target, target > PositionMs);
        }
        catch { }
    }

    /// <summary>Queues an external subtitle and returns an id used to correlate its completion.</summary>
    public long OpenSubtitle(string path)
    {
        SubtitleOpenRequest request;
        bool start = false;
        string immediateError = null;
        lock (_subtitleLock)
        {
            request = new SubtitleOpenRequest(++_subtitleRequestId, path ?? "");
            if (_disposed || Fly == null) immediateError = "播放器尚未就绪";
            else if (string.IsNullOrWhiteSpace(request.Path)) immediateError = "字幕路径为空";
            else if (_subtitleActive == null)
            {
                _subtitleActive = request;
                start = true;
            }
            else
            {
                // Flyleaf's subtitle completion does not expose the input path. Keep one
                // in flight and retain only the newest queued request so results stay correlated.
                _subtitlePending = request;
            }
        }

        if (immediateError != null) RaiseSubtitleOpened(request, false, immediateError);
        else if (start) StartSubtitle(request);
        return request.Id;
    }

    void StartSubtitle(SubtitleOpenRequest request)
    {
        try
        {
            if (_disposed || Fly == null)
            {
                CompleteSubtitle(request.Id, false, "播放器已关闭");
                return;
            }
            Fly.OpenAsync(request.Path);
        }
        catch (Exception e)
        {
            CompleteSubtitle(request.Id, false, e.Message);
        }
    }

    void CompleteSubtitle(long expectedId, bool success, string error)
    {
        SubtitleOpenRequest completed;
        SubtitleOpenRequest next = null;
        lock (_subtitleLock)
        {
            if (_subtitleActive == null || (expectedId > 0 && _subtitleActive.Id != expectedId)) return;
            completed = _subtitleActive;
            _subtitleActive = null;
            if (!_disposed && _subtitlePending != null)
            {
                next = _subtitlePending;
                _subtitlePending = null;
                _subtitleActive = next;
            }
            else
            {
                _subtitlePending = null;
            }
        }

        RaiseSubtitleOpened(completed, success, error);
        if (next != null) StartSubtitle(next);
    }

    void RaiseSubtitleOpened(SubtitleOpenRequest request, bool success, string error)
    {
        void Notify()
        {
            if (!_disposed) SubtitleOpened?.Invoke(request.Id, success, error ?? "");
        }

        // Always enqueue, including on the UI thread, so OpenSubtitle returns its
        // request id before a synchronous Flyleaf rejection can notify the page.
        if (App.Dispatcher?.TryEnqueue(Notify) != true) Notify();
    }

    /// <summary>倍速 1~4（Flyleaf 3.10.4 的受支持范围）。</summary>
    public float Speed
    {
        get => Fly == null ? 1f : (float)Fly.Speed;
        set
        {
            try
            {
                if (Fly == null) return;
                var target = Math.Clamp(value, 1f, 4f);
                if (Math.Abs(Fly.Speed - target) < 0.001) return;
                Fly.Speed = target;
                Core.Logger.D(TAG, $"播放速度: {target:0.##}x (FFmpeg audio filters)");
            }
            catch (Exception e) { Core.Logger.E(TAG, "变速失败: " + e.Message); }
        }
    }

    /// <summary>画面缩放：0原始(Keep) 1拉伸(Fill) 2=16:9 3=4:3 4填充(Keep+Zoom裁切)。</summary>
    public int Scale
    {
        get => _scale;
        set { _scale = value; ApplyScale(); }
    }

    /// <summary>显示模式变化后重新应用用户选择的画面比例。</summary>
    public void SetViewportFill(bool fullscreen)
    {
        ApplyScale();
    }

    /// <summary>宿主尺寸变化后重新应用当前画面比例；填充模式会按新的渲染尺寸重算裁切。</summary>
    public void RefreshVideoLayout() => ApplyScale();

    void ApplyScale()
    {
        if (Fly == null) return;
        try
        {
            var video = Fly.Config.Video;
            // Flyleaf exposes Zoom as a percentage: 100 is the unscaled frame.
            video.Zoom = 100;
            switch (_scale)
            {
                case 1: video.AspectRatio = AspectRatio.Fill; break;
                case 2: video.AspectRatio = new AspectRatio(16, 9); break;
                case 3: video.AspectRatio = new AspectRatio(4, 3); break;
                case 4: video.AspectRatio = AspectRatio.Keep; video.Zoom = CoverZoom() * 100; break;
                default: video.AspectRatio = AspectRatio.Keep; break;
            }
        }
        catch (Exception e) { Core.Logger.E(TAG, "Scale 失败: " + e.Message); }
    }

    public long PositionMs { get { try { return Fly == null ? 0 : Fly.CurTime / 10000; } catch { return 0; } } }

    public long DurationMs { get { try { return Fly == null ? 0 : Fly.Duration / 10000; } catch { return 0; } } }

    public bool IsPlaying { get { try { return Fly != null && Fly.IsPlaying; } catch { return false; } } }

    /// <summary>填充模式所需 Zoom：按视频 DAR 与控件比例计算裁切放大倍数。</summary>
    double CoverZoom()
    {
        try
        {
            var renderer = Fly.Renderer;
            double dar = Fly.Video?.AspectRatio.Value ?? 0;
            if (renderer == null || renderer.ControlWidth <= 0 || renderer.ControlHeight <= 0 || dar <= 0) return 1;
            double control = (double)renderer.ControlWidth / renderer.ControlHeight;
            return Math.Max(control / dar, dar / control);
        }
        catch { return 1; }
    }

    Dictionary<string, string> BuildFormatOptions(PlayItem item, int generation)
    {
        var opt = new Dictionary<string, string>(
            Fly.Config.Demuxer.FormatOpt ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        opt.Remove("headers"); opt.Remove("user_agent"); opt.Remove("referer"); opt.Remove("http_proxy");
        opt.Remove("decryption_key");
        if (!ApplyDrm(item.Drm, opt, generation)) return null;
        ApplyHeaders(item.Headers, item.Url, opt);
        return opt;
    }

    /// <summary>播放 header 传给 ffmpeg：UA/Referer 独立选项，其余拼接为 \r\n 分隔的 headers。</summary>
    static void ApplyHeaders(Dictionary<string, string> headers, string mediaUrl, Dictionary<string, string> opt)
    {
        headers = new Dictionary<string, string>(headers ?? new(), StringComparer.OrdinalIgnoreCase);
        if (!headers.Keys.Any(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(Setting.Ua))
            headers["User-Agent"] = Setting.Ua;
        var proxy = Net.NetworkConfig.GetProxyFor(UrlUtil.Host(mediaUrl));
        if (!string.IsNullOrWhiteSpace(proxy)) opt["http_proxy"] = proxy;
        var sb = new System.Text.StringBuilder();
        foreach (var kv in headers)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            var value = (kv.Value ?? "").Replace("\r", "").Replace("\n", "");
            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) opt["user_agent"] = value;
            else if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) opt["referer"] = value;
            else sb.Append(kv.Key).Append(": ").Append(value).Append("\r\n");
        }
        if (sb.Length > 0) opt["headers"] = sb.ToString();
    }

    /// <summary>DRM：仅 ClearKey 尽力而为（ffmpeg decryption_key，只对 CENC mp4 有效）；其余提示不支持。</summary>
    bool ApplyDrm(Models.Drm drm, Dictionary<string, string> options, int generation)
    {
        if (drm == null || string.IsNullOrEmpty(drm.Type)) return true;
        if (drm.Type.Contains("clearkey", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var key = drm.Key ?? "";
                if (!JsonUtil.IsObj(key) && key.Contains(':')) key = key.Split(':')[^1].Trim();
                if (key.Length > 0) options["decryption_key"] = key;
            }
            catch { }
            return true;
        }
        PostIfCurrent(generation, () => Errored?.Invoke("Windows 版暂不支持 " + drm.Type + " DRM"));
        return false;
    }

    /// <summary>MIME 提示映射 ffmpeg 容器格式，跳过探测。
    /// 注意：ForceFormat 非 null 时 Flyleaf 必调 av_find_input_format，空串/未知名会直接报错——无匹配必须回 null 走自动探测。</summary>
    void ApplyFormat(string format)
    {
        try
        {
            var f = (format ?? "").ToLowerInvariant();
            string force = null;
            if (f.Contains("mpegurl")) force = "hls";
            else if (f.Contains("dash")) force = "dash";
            else if (f.Contains("flv")) force = "flv";
            else if (f.Contains("mp2t") || f.Contains("mpegts")) force = "mpegts";
            Fly.Config.Demuxer.ForceFormat = force;
        }
        catch { }
    }

    void OnBufferingStarted(object sender, EventArgs e) => RaiseBufferingChanged(true);

    void OnBufferingCompleted(object sender, BufferingCompletedArgs e) => RaiseBufferingChanged(false);

    void RaiseBufferingChanged(bool buffering)
    {
        var generation = Volatile.Read(ref _activePlaybackGeneration);
        if (generation <= 0 || generation != Volatile.Read(ref _openGeneration) ||
            Volatile.Read(ref _openingGeneration) != 0 || _item == null) return;
        PostIfCurrent(generation, () =>
        {
            if (generation == Volatile.Read(ref _activePlaybackGeneration) &&
                Volatile.Read(ref _openingGeneration) == 0 && _item != null)
                BufferingChanged?.Invoke(buffering);
        });
    }

    void OnOpenCompleted(object sender, OpenCompletedArgs e)
    {
        if (_disposed) return;
        if (e.IsSubtitles)
        {
            CompleteSubtitle(0, e.Success, e.Error);
            return;
        }
        var generation = Volatile.Read(ref _openGeneration);
        var item = _item;
        if (item == null || Volatile.Read(ref _openingGeneration) != generation) return;
        if (!string.IsNullOrWhiteSpace(e.Url) && !string.Equals(e.Url, item.Url, StringComparison.Ordinal))
        {
            Core.Logger.D(TAG, $"忽略旧媒体打开结果 #{generation}: {DescribeUrl(e.Url)}");
            return;
        }
        _recovering = false;
        if (e.Success)
        {
            Interlocked.CompareExchange(ref _openingGeneration, 0, generation);
            Volatile.Write(ref _activePlaybackGeneration, generation);
            // Flyleaf can deliver the stopped event from the replaced decoder after
            // the new decoder has opened. Do not surface that stale error.
            _ignorePlaybackStopUntil = Environment.TickCount64 + 2500;
            var start = item.StartPositionMs;
            if (start > 0) SeekMs(start);
            Core.Logger.D(TAG, $"媒体已打开 #{generation} video={Fly.Video?.IsOpened == true}, audio={Fly.Audio?.IsOpened == true}, hw={Fly.Config.Video.VideoAcceleration}");
            StartVideoWatchdog(generation, item);
            PostIfCurrent(generation, () => Opened?.Invoke());
        }
        else
        {
            var error = string.IsNullOrWhiteSpace(e.Error) ? "媒体打开失败" : e.Error;
            if (TryRecoverFormat(error, generation, item)) return;
            Interlocked.CompareExchange(ref _openingGeneration, 0, generation);
            PostIfCurrent(generation, () => Errored?.Invoke(error));
        }
    }

    void OnPlaybackStopped(object sender, PlaybackStoppedArgs e)
    {
        if (_disposed) return;
        var generation = Volatile.Read(ref _openGeneration);
        if (_item == null) return;
        try
        {
            if (Volatile.Read(ref _openingGeneration) == generation || _recovering)
            {
                Core.Logger.D(TAG, "已忽略换源期间旧解码器的停止事件: " + e.Error);
                return;
            }
            if (Fly.Status == Status.Ended)
            {
                PostIfCurrent(generation, () => Ended?.Invoke());
                return;
            }
            if (!string.IsNullOrEmpty(e.Error))
            {
                if (Environment.TickCount64 <= _ignorePlaybackStopUntil)
                {
                    Core.Logger.D(TAG, "已忽略新媒体打开后的旧解码器停止事件: " + e.Error);
                    return;
                }
                var error = e.Error;
                PostIfCurrent(generation, () => Errored?.Invoke(error));
            }
        }
        catch { }
    }

    bool TryRecoverFormat(string error, int generation, PlayItem item)
    {
        if (_formatRecoveryTried || item == null || generation != Volatile.Read(ref _openGeneration)) return false;
        if (!error.Contains("No audio / video stream", StringComparison.OrdinalIgnoreCase) &&
            !error.Contains("Invalid data", StringComparison.OrdinalIgnoreCase) &&
            !error.Contains("format", StringComparison.OrdinalIgnoreCase)) return false;
        var current = Fly.Config.Demuxer.ForceFormat;
        var next = string.IsNullOrEmpty(current) && item.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : null;
        if (string.Equals(current, next, StringComparison.OrdinalIgnoreCase)) return false;
        _formatRecoveryTried = true;
        _recovering = true;
        Volatile.Write(ref _openingGeneration, generation);
        _ignorePlaybackStopUntil = Environment.TickCount64 + 2500;
        Fly.Config.Demuxer.ForceFormat = next;
        Core.Logger.D(TAG, $"容器识别失败，改用{(next == null ? "自动探测" : next)}重试");
        try
        {
            Fly.OpenAsync(item.Url);
            return true;
        }
        catch (Exception ex)
        {
            _recovering = false;
            Core.Logger.E(TAG, "容器恢复重开失败: " + ex.Message);
            return false;
        }
    }

    void StartVideoWatchdog(int generation, PlayItem item)
    {
        if (_videoRecoveryTried || Fly?.Video?.IsOpened != true) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            App.Post(() =>
            {
                if (_disposed || generation != Volatile.Read(ref _openGeneration) || !ReferenceEquals(item, _item) ||
                    _videoRecoveryTried || Fly?.Video?.IsOpened != true) return;
                if (!Fly.IsPlaying || Fly.Video.FramesDisplayed > 0) return;
                _videoRecoveryTried = true;
                _recovering = true;
                var position = PositionMs;
                item.StartPositionMs = Math.Max(item.StartPositionMs, position);
                Volatile.Write(ref _openingGeneration, generation);
                _ignorePlaybackStopUntil = Environment.TickCount64 + 2500;
                Fly.Config.Video.VideoAcceleration = false;
                Core.Logger.D(TAG, "检测到视频流已打开但无画面，切换软件解码重试");
                try { Fly.OpenAsync(item.Url); }
                catch (Exception ex)
                {
                    _recovering = false;
                    Interlocked.CompareExchange(ref _openingGeneration, 0, generation);
                    PostIfCurrent(generation, () => Errored?.Invoke(ex.Message));
                }
            });
        });
    }

    void PostIfCurrent(int generation, Action action)
    {
        App.Post(() =>
        {
            if (!_disposed && generation == Volatile.Read(ref _openGeneration)) action?.Invoke();
        });
    }

    static PlayItem Snapshot(PlayItem item) => new()
    {
        Url = item.Url ?? "",
        Headers = new Dictionary<string, string>(item.Headers ?? new(), StringComparer.OrdinalIgnoreCase),
        Format = item.Format,
        Drm = item.Drm,
        StartPositionMs = item.StartPositionMs,
        Subs = item.Subs?.ToList() ?? new List<Models.Sub>(),
        Danmaku = item.Danmaku?.ToList() ?? new List<Models.Danmaku>(),
    };

    static string DescribeUrl(string value)
    {
        try
        {
            var uri = new Uri(value);
            var file = uri.Segments.LastOrDefault()?.Trim('/') ?? "";
            if (file.Length > 80) file = file[..80] + "…";
            return $"{uri.Scheme}://{uri.Host}/{file}";
        }
        catch { return UrlUtil.GetName(value ?? ""); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _openGeneration);
        Volatile.Write(ref _openingGeneration, 0);
        Volatile.Write(ref _activePlaybackGeneration, 0);
        _item = null;
        lock (_subtitleLock)
        {
            _subtitleActive = null;
            _subtitlePending = null;
        }
        App.Post(() => { try { _timer?.Stop(); } catch { } _timer = null; });
        try
        {
            if (Fly != null)
            {
                Fly.OpenCompleted -= OnOpenCompleted;
                Fly.PlaybackStopped -= OnPlaybackStopped;
                Fly.BufferingStarted -= OnBufferingStarted;
                Fly.BufferingCompleted -= OnBufferingCompleted;
                Fly.Dispose();
            }
        }
        catch { }
    }

    sealed record SubtitleOpenRequest(long Id, string Path);
}

/// <summary>可播条目（PlayResolver 产出）。</summary>
public class PlayItem
{
    public string Url; public Dictionary<string, string> Headers = new();
    public string Format;                                // MIME 提示，可空
    public List<Models.Sub> Subs = new(); public Models.Drm Drm;  // ClearKey 之外 Windows 不支持 → 提示
    public long StartPositionMs;
    public List<Models.Danmaku> Danmaku = new();         // 弹幕候选（契约 §5.3：Resolve 产出含 danmaku）
}
