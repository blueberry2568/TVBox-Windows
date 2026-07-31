using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;
using TVBoxForWindows.Server.Process;

namespace TVBoxForWindows.Server;

/// <summary>服务器请求上下文（移植自 NanoHTTPD IHTTPSession 精简版）。</summary>
public class ServerRequest
{
    public string Method { get; set; } = "GET";
    public bool IsLoopback { get; set; }
    /// <summary>已 URL 解码的路径（以 / 开头）。</summary>
    public string Path { get; set; } = "/";
    /// <summary>query 参数 + 表单字段合并（后写覆盖，等价 session.getParms()）。</summary>
    public Dictionary<string, string> Params { get; } = new();
    /// <summary>请求头（键统一小写，等价 session.getHeaders()）。</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>multipart 上传：字段名 → 临时文件路径；非表单 POST：postData → 原始文本。</summary>
    public Dictionary<string, string> Files { get; } = new();
    /// <summary>请求结束后需要清理的临时文件。</summary>
    public List<string> TempFiles { get; } = new();
}

/// <summary>服务器响应载体（移植自 NanoHTTPD Response 精简版）。</summary>
public class ServerResponse
{
    public int Code { get; set; } = 200;
    public string Mime { get; set; } = "text/plain";
    public byte[] Body { get; set; }
    /// <summary>流式响应体（与 Body 二选一）；StreamLength &gt;= 0 定长，否则 chunked。</summary>
    public Stream Stream { get; set; }
    public long StreamLength { get; set; } = -1;
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ServerResponse Ok(string text = "OK", string mime = "text/plain") => new() { Code = 200, Mime = mime, Body = Encoding.UTF8.GetBytes(text ?? "") };
    public static ServerResponse Error(string msg, int code = 500) => new() { Code = code, Mime = "text/plain", Body = Encoding.UTF8.GetBytes(msg ?? "") };
}

/// <summary>请求处理器接口（移植自 server/impl/Process.java）。</summary>
public interface IProcess
{
    bool IsRequest(ServerRequest req);
    Task<ServerResponse> Handle(ServerRequest req);
}

/// <summary>本地 HTTP 服务器（移植自 Server.java + Nano.java）：HttpListener 监听 9978~9998，路由分发到 Server/Process 各处理器。</summary>
public class LocalServer
{
    const string Tag = "LocalServer";
    const long MaxRequestBodyBytes = 64L * 1024 * 1024;

    public static LocalServer Instance { get; } = new();

    HttpListener _listener;
    CancellationTokenSource _cts;
    List<IProcess> _process;

    /// <summary>生效端口（9978 起探测至 9998）。</summary>
    public int Port { get; private set; } = 9978;
    public bool IsLanAccessible { get; private set; }

    // ---- UI 订阅的事件（触发时已在 App.Post 内切 UI 线程）----
    public event Action<string> PushArrived;                 // do=push 的 url（点播推送/网址）
    public event Action<ConfigRecord> RefreshConfig;         // do=refresh / do=setting 配置刷新
    public event Action<string> DanmakuArrived;              // 弹幕推送 url/内容
    public event Action<Sub> SubtitleArrived;                // 字幕推送
    public event Action<string, string> CastArrived;         // do=cast: (configUrl, historyJson)

    /// <summary>/media 播放状态提供者（由播放器/UI 注入，需自行保证线程安全）；未注入时返回 {}。</summary>
    public Func<string> MediaStateProvider { get; set; }

    LocalServer() { }

    /// <summary>Starts on loopback by default; LAN binding must be explicitly enabled in settings.</summary>
    public void Start()
    {
        if (_listener != null) return;
        _process = new List<IProcess> { new DeviceProcess(), new ActionProcess(), new CacheProcess(), new FileProcess(), new ParseProcess(), new ProxyProcess() };
        var allowLan = Setting.LocalServerLan;
        for (int port = 9978; port < 9999 && _listener == null; port++)
        {
            if (allowLan && TryBind(port, true))
            {
                Port = port;
                IsLanAccessible = true;
            }
            else if (TryBind(port, false))
            {
                Port = port;
                IsLanAccessible = false;
            }
        }
        if (_listener == null) { Logger.E(Tag, "9978-9998 端口全部占用，本地服务器未启动"); return; }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        Logger.D(Tag, "本地服务器已启动：" + GetAddress("/"));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts = null;
        IsLanAccessible = false;
    }

    public void Restart(bool allowLan)
    {
        Setting.LocalServerLan = allowLan;
        Stop();
        Start();
    }

    /// <summary>本机回环地址（path 以 / 开头）。</summary>
    public string GetAddress(string path) => $"http://127.0.0.1:{Port}" + path;

    /// <summary>局域网地址（/device 的 ip 字段、投屏回发用）。</summary>
    internal string GetAddressLan(string path) => $"http://{GetLanIp()}:{Port}" + path;

    // ---- 事件转发（供 Process 处理器调用）----
    internal void RaisePush(string url) => App.Post(() => PushArrived?.Invoke(url));
    internal void RaiseRefreshConfig(ConfigRecord config) => App.Post(() => RefreshConfig?.Invoke(config));
    internal void RaiseDanmaku(string text) => App.Post(() => DanmakuArrived?.Invoke(text));
    internal void RaiseSubtitle(Sub sub) => App.Post(() => SubtitleArrived?.Invoke(sub));
    internal void RaiseCast(string configUrl, string historyJson) => App.Post(() => CastArrived?.Invoke(configUrl, historyJson));

    bool TryBind(int port, bool anyHost)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(anyHost ? $"http://+:{port}/" : $"http://127.0.0.1:{port}/");
        try { listener.Start(); _listener = listener; return true; }
        catch { try { listener.Close(); } catch { } return false; }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener != null && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { if (ct.IsCancellationRequested || !listener.IsListening) break; continue; }
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        ServerRequest req = null;
        ServerResponse res;
        try
        {
            if (!IsOriginAllowed(ctx.Request)) res = ServerResponse.Error("Forbidden origin", 403);
            else
            {
                req = await ReadRequest(ctx);
                res = req.Method == "OPTIONS" ? ServerResponse.Ok("") : await Route(req);
            }
        }
        catch (RequestTooLargeException e)
        {
            res = ServerResponse.Error(e.Message, 413);
        }
        catch (Exception e)
        {
            Logger.E(Tag, ctx.Request?.RawUrl + " → " + e.Message);
            res = ServerResponse.Error(string.IsNullOrEmpty(e.Message) ? e.ToString() : e.Message);
        }
        await WriteResponse(ctx, res ?? ServerResponse.Error("Empty response"), req != null && req.Method == "HEAD");
        Cleanup(req);
    }

    /// <summary>路由：/tvbus 与 /media 内联，其余按注册顺序 startsWith 匹配（Device→Action→Cache→Local→Parse→Proxy），未命中回退静态资源。</summary>
    async Task<ServerResponse> Route(ServerRequest req)
    {
        if (req.Path.StartsWith("/tvbus")) return ServerResponse.Ok(""); // TVBus 原生库仅 Android，返回空配置
        if (req.Path.StartsWith("/media")) return GetMedia();
        foreach (var process in _process) if (process.IsRequest(req)) return await process.Handle(req);
        return GetStatic(req.Path);
    }

    ServerResponse GetMedia()
    {
        try
        {
            var text = MediaStateProvider?.Invoke();
            return ServerResponse.Ok(string.IsNullOrEmpty(text) ? "{}" : text);
        }
        catch { return ServerResponse.Ok("{}"); }
    }

    /// <summary>静态资源：/ 返回状态页，其余映射 Assets 目录，未找到 404 空 HTML。</summary>
    ServerResponse GetStatic(string path)
    {
        if (path == "/" || path.Length == 0) return ServerResponse.Ok(StatusPage(), "text/html");
        try
        {
            var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppPaths.AssetDir, path.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (file.StartsWith(AppPaths.AssetDir, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                return new ServerResponse { Code = 200, Mime = GetMime(file), Body = File.ReadAllBytes(file) };
        }
        catch { }
        return new ServerResponse { Code = 404, Mime = "text/html", Body = Array.Empty<byte>() };
    }

    string StatusPage()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.6";
        return "<!DOCTYPE html><html lang=\"zh\"><head><meta charset=\"utf-8\"><title>TVBox</title></head><body style=\"font-family:'Segoe UI',sans-serif;margin:40px\">"
             + "<h1>TVBox for Windows</h1>"
             + $"<p>端口：{Port}</p><p>版本：{version}</p>"
             + "<p>端点：/action /cache /file /upload /parse /proxy /device /media</p>"
             + "</body></html>";
    }

    // ---------- 请求解析 ----------

    async Task<ServerRequest> ReadRequest(HttpListenerContext ctx)
    {
        var remote = ctx.Request.RemoteEndPoint?.Address;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        var req = new ServerRequest
        {
            Method = ctx.Request.HttpMethod?.ToUpperInvariant() ?? "GET",
            IsLoopback = remote == null || IPAddress.IsLoopback(remote),
        };
        try { req.Path = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/"); }
        catch { req.Path = ctx.Request.Url?.AbsolutePath ?? "/"; }
        foreach (var key in ctx.Request.Headers.AllKeys)
            if (key != null) req.Headers[key.ToLowerInvariant()] = ctx.Request.Headers[key] ?? "";
        ParseQuery(ctx.Request.Url?.Query, req.Params);
        if (req.Method is "POST" or "PUT")
        {
            var body = await ReadBody(ctx.Request);
            var contentType = ctx.Request.ContentType ?? "";
            if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase)) ParseMultipart(body, contentType, req);
            else if (contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) ParseQuery(Encoding.UTF8.GetString(body), req.Params);
            else if (body.Length > 0) req.Files["postData"] = Encoding.UTF8.GetString(body); // 等价 NanoHTTPD 的 postData
        }
        return req;
    }

    static bool IsOriginAllowed(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Authority, request.Url?.Authority, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<byte[]> ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxRequestBodyBytes)
            throw new RequestTooLargeException();

        var capacity = request.ContentLength64 is > 0 and <= int.MaxValue
            ? (int)request.ContentLength64
            : 0;
        using var output = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer);
            if (read <= 0) break;
            total += read;
            if (total > MaxRequestBodyBytes) throw new RequestTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return output.ToArray();
    }

    /// <summary>解析 query / form-urlencoded 字符串到 params（键值均 URL 解码，重复键后者覆盖）。</summary>
    internal static void ParseQuery(string query, Dictionary<string, string> map)
    {
        if (string.IsNullOrEmpty(query)) return;
        if (query.StartsWith('?')) query = query[1..];
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0) continue;
            var i = pair.IndexOf('=');
            var key = Decode(i < 0 ? pair : pair[..i]);
            if (key.Length == 0) continue;
            map[key] = i < 0 ? "" : Decode(pair[(i + 1)..]);
        }
    }

    static string Decode(string text)
    {
        try { return Uri.UnescapeDataString(text.Replace('+', ' ')); }
        catch { return text; }
    }

    // ---------- multipart 手写解析（不引包，boundary split）----------

    static readonly byte[] HeaderSep = { 13, 10, 13, 10 }; // \r\n\r\n

    static void ParseMultipart(byte[] body, string contentType, ServerRequest req)
    {
        var idx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        var boundary = contentType[(idx + 9)..].Split(';')[0].Trim().Trim('"');
        if (boundary.Length == 0) return;
        var delimiter = Encoding.UTF8.GetBytes("--" + boundary);
        var pos = IndexOf(body, delimiter, 0);
        while (pos >= 0)
        {
            var start = pos + delimiter.Length;
            if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-') break; // 结束标记 --boundary--
            while (start < body.Length && (body[start] == 13 || body[start] == 10)) start++;   // 跳过 \r\n
            var next = IndexOf(body, delimiter, start);
            if (next < 0) break;
            var end = next;
            while (end > start && (body[end - 1] == 13 || body[end - 1] == 10)) end--;         // 去掉部件尾部 \r\n
            if (end > start) ParsePart(body, start, end, req);
            pos = next;
        }
    }

    static void ParsePart(byte[] body, int start, int end, ServerRequest req)
    {
        try
        {
            var headerEnd = IndexOf(body, HeaderSep, start);
            if (headerEnd < 0 || headerEnd >= end) return;
            var headerText = Encoding.UTF8.GetString(body, start, headerEnd - start);
            var name = MatchQuoted(headerText, "(?<![a-zA-Z])name=\"([^\"]*)\"");
            var fileName = MatchQuoted(headerText, "filename=\"([^\"]*)\"");
            if (string.IsNullOrEmpty(name)) return;
            var contentStart = headerEnd + 4;
            var length = end - contentStart;
            if (length < 0) return;
            if (!string.IsNullOrEmpty(fileName))
            {
                // 文件部件：临时落盘，files[name]=临时路径，params[name]=原始文件名（等价 NanoHTTPD）
                var temp = System.IO.Path.Combine(AppPaths.Cache, "upload_" + Guid.NewGuid().ToString("N"));
                using (var fs = File.Create(temp)) fs.Write(body, contentStart, length);
                req.Files[name] = temp;
                req.Params[name] = fileName;
                req.TempFiles.Add(temp);
            }
            else req.Params[name] = Encoding.UTF8.GetString(body, contentStart, length);
        }
        catch (Exception e) { Logger.E(Tag, "multipart 解析失败：" + e.Message); }
    }

    static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        if (from < 0) from = 0;
        for (int i = from; i <= data.Length - pattern.Length; i++)
        {
            var match = true;
            for (int j = 0; j < pattern.Length; j++) if (data[i + j] != pattern[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    static string MatchQuoted(string text, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    // ---------- 响应写出 ----------

    async Task WriteResponse(HttpListenerContext ctx, ServerResponse res, bool head)
    {
        try
        {
            var r = ctx.Response;
            r.StatusCode = res.Code is >= 100 and <= 599 ? res.Code : 500;
            r.ContentType = res.Mime ?? "text/plain";
            var origin = ctx.Request.Headers["Origin"];
            if (!string.IsNullOrWhiteSpace(origin) && IsOriginAllowed(ctx.Request))
            {
                r.Headers["Access-Control-Allow-Origin"] = origin;
                r.Headers["Access-Control-Allow-Methods"] = "GET, POST, HEAD, OPTIONS";
                r.Headers["Access-Control-Allow-Headers"] = "Content-Type, Range";
                r.Headers["Vary"] = "Origin";
            }
            foreach (var kv in res.Headers)
            {
                if (kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { r.ContentType = kv.Value; continue; }
                try { r.Headers[kv.Key] = kv.Value; } catch { }
            }
            if (res.Stream != null)
            {
                if (res.StreamLength >= 0)
                {
                    r.ContentLength64 = res.StreamLength;
                    if (!head) await CopyExact(res.Stream, r.OutputStream, res.StreamLength);
                }
                else
                {
                    r.SendChunked = true;
                    if (!head) await res.Stream.CopyToAsync(r.OutputStream);
                }
            }
            else
            {
                var body = res.Body ?? Array.Empty<byte>();
                var noBody = res.Code is 204 or 304 || res.Code < 200; // 无正文状态码
                if (!noBody) r.ContentLength64 = body.Length;
                if (!head && !noBody && body.Length > 0) await r.OutputStream.WriteAsync(body);
            }
            r.OutputStream.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
        finally { try { res.Stream?.Dispose(); } catch { } }
    }

    static async Task CopyExact(Stream from, Stream to, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)));
            if (read <= 0) break;
            await to.WriteAsync(buffer.AsMemory(0, read));
            count -= read;
        }
    }

    static void Cleanup(ServerRequest req)
    {
        if (req == null) return;
        foreach (var file in req.TempFiles)
            try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    // ---------- 通用工具（供各 Process 处理器复用）----------

    /// <summary>按扩展名取 MIME（等价 NanoHTTPD.getMimeTypeForFile 常用子集）。</summary>
    internal static string GetMime(string path)
    {
        var ext = System.IO.Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "html" or "htm" => "text/html",
            "css" => "text/css",
            "js" or "mjs" => "application/javascript",
            "json" => "application/json",
            "xml" => "text/xml",
            "txt" or "log" or "srt" or "ass" or "ssa" => "text/plain",
            "vtt" => "text/vtt",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            "ico" => "image/x-icon",
            "mp4" or "m4v" => "video/mp4",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "mov" => "video/quicktime",
            "flv" => "video/x-flv",
            "ts" => "video/mp2t",
            "m3u8" => "application/vnd.apple.mpegurl",
            "m3u" => "audio/mpegurl",
            "mpd" => "application/dash+xml",
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "ogg" => "audio/ogg",
            "wav" => "audio/wav",
            "zip" => "application/zip",
            "apk" => "application/vnd.android.package-archive",
            "pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }

    /// <summary>取局域网 IPv4（无可用网卡时回退 127.0.0.1）。</summary>
    internal static string GetLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        return addr.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    sealed class RequestTooLargeException : Exception
    {
        public RequestTooLargeException() : base("请求体超过 64 MB 限制") { }
    }
}
