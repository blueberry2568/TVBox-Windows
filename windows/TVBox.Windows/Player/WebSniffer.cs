using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Player;

/// <summary>隐藏 WebView2 网络嗅探（移植自 CustomWebView.java）：
/// 加载播放页并拦截全部资源请求，首个 Sniffer.IsVideoFormat 命中的 URL 即结果。
/// WebView2 必须在 UI 线程创建/销毁，故所有控件操作经 App.Post 投递。</summary>
public static class WebSniffer
{
    const string TAG = "WebSniffer";
    const int TimeoutMs = 15000;

    static Task<CoreWebView2Environment> _envTask;

    /// <summary>共享 CoreWebView2Environment（UserDataFolder=cache/webview）；失败可重建。</summary>
    static Task<CoreWebView2Environment> GetEnvironment()
    {
        var task = _envTask;
        if (task == null || task.IsFaulted || task.IsCanceled)
            _envTask = task = CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppPaths.Cache, "webview"), null).AsTask();
        return task;
    }

    /// <summary>隐藏 WebView2 加载 url，拦截首个 Sniffer.IsVideoFormat 命中的请求。click 为注入脚本。15 秒超时。</summary>
    public static async Task<ParseResult> Sniff(string url, Dictionary<string, string> headers, string click, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ParseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Session session = null;
        var stopRequested = 0;
        App.Post(async () =>
        {
            Session created = null;
            try
            {
                created = new Session(tcs);
                session = created;
                if (Volatile.Read(ref stopRequested) != 0)
                {
                    created.Destroy();
                    return;
                }
                await created.Start(url, headers ?? new Dictionary<string, string>(), click);
            }
            catch (Exception e) { tcs.TrySetException(e); }
            finally
            {
                if (Volatile.Read(ref stopRequested) != 0 && created != null)
                    App.Post(created.Destroy);
            }
        });
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeoutMs, CancellationToken.None));
            if (completed != tcs.Task) throw new TimeoutException("嗅探超时: " + url);
            return await tcs.Task;
        }
        finally
        {
            Interlocked.Exchange(ref stopRequested, 1);
            App.Post(() => session?.Destroy());
        }
    }

    /// <summary>单次嗅探会话：隐藏 Window + WebView2，全程只在 UI 线程访问。</summary>
    class Session
    {
        readonly TaskCompletionSource<ParseResult> _tcs;
        Window _window;
        WebView2 _webView;
        bool _destroyed;

        public Session(TaskCompletionSource<ParseResult> tcs) => _tcs = tcs;

        public async Task Start(string url, Dictionary<string, string> headers, string click)
        {
            _webView = new WebView2();
            _window = new Window { Content = _webView };
            try
            {
                _window.AppWindow.IsShownInSwitchers = false;
                _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-4000, -4000, 1280, 720));
            }
            catch { }
            _window.Activate();
            try { _window.AppWindow.Hide(); } catch { }

            var env = await GetEnvironment();
            if (_destroyed) return;
            await _webView.EnsureCoreWebView2Async(env);
            if (_destroyed) return;
            var core = _webView.CoreWebView2;

            var ua = headers.FirstOrDefault(kv => kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(ua)) try { core.Settings.UserAgent = ua; } catch { }

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationCompleted += (s, e) => InjectScripts(click, url);

            // 自定义 header（Referer/Cookie 等）需经 NavigateWithWebResourceRequest 携带
            var sb = new System.Text.StringBuilder();
            foreach (var kv in headers)
                if (!kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (sb.Length > 0) core.NavigateWithWebResourceRequest(env.CreateWebResourceRequest(url, "GET", null, sb.ToString()));
            else core.Navigate(url);
        }

        void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var uri = e.Request?.Uri;
                if (string.IsNullOrEmpty(uri) || !Sniffer.IsVideoFormat(uri)) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var kv in e.Request.Headers)
                        if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
                            kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
                            kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                            kv.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                            kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                            map[UrlUtil.FixHeader(kv.Key)] = kv.Value;
                }
                catch { }
                Logger.D(TAG, "嗅探命中: " + uri);
                _tcs.TrySetResult(new ParseResult { Url = uri, Headers = map });
            }
            catch (Exception ex) { Logger.E(TAG, "拦截异常: " + ex.Message); }
        }

        /// <summary>NavigationCompleted 后注入 click 脚本与站点规则脚本。</summary>
        async void InjectScripts(string click, string pageUrl)
        {
            try
            {
                var core = _webView?.CoreWebView2;
                if (core == null || _destroyed) return;
                if (!string.IsNullOrEmpty(click)) await core.ExecuteScriptAsync(click);
                foreach (var script in Sniffer.GetScript(pageUrl))
                    if (!string.IsNullOrEmpty(script)) await core.ExecuteScriptAsync(script);
            }
            catch { }
        }

        public void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            try { _webView?.Close(); } catch { }
            try { _window?.Close(); } catch { }
            _webView = null;
            _window = null;
        }
    }
}
