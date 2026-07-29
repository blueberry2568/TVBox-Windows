using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace FongMi.TV.Net;

public class OkResponse
{
    public int Code { get; set; }
    public string FinalUrl { get; set; } = "";
    public Dictionary<string, List<string>> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public string Text(string charset = "UTF-8")
    {
        try { return System.Text.Encoding.GetEncoding(charset).GetString(Body); }
        catch { return System.Text.Encoding.UTF8.GetString(Body); }
    }
}

/// <summary>OkHttp 等价物：hosts 覆写 + DoH + 按域名代理 + 广告拦截，客户端按 (proxy,redirect) 缓存。</summary>
public static class HttpUtil
{
    static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    static HttpUtil()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public static HttpClient Client(string proxyUrl = null, bool redirect = true)
    {
        var key = (proxyUrl ?? "") + "|" + redirect;
        return Clients.GetOrAdd(key, _ => Create(proxyUrl, redirect));
    }

    static HttpClient Create(string proxyUrl, bool redirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = redirect,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectCallback = ConnectAsync,
        };
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            try
            {
                var uri = new Uri(proxyUrl);
                var proxy = new WebProxy(uri);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(':', 2);
                    proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
                }
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            catch { }
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite) };
    }

    /// <summary>连接回调：应用 hosts 覆写与 DoH 解析。</summary>
    static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;
        var rewrite = NetworkConfig.RewriteHost(host);
        if (rewrite != null) host = rewrite;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                var ips = await DohResolver.ResolveAsync(host, ct);
                if (ips is { Length: > 0 }) { await socket.ConnectAsync(ips, port, ct); return new NetworkStream(socket, true); }
            }
            await socket.ConnectAsync(host, port, ct);
            return new NetworkStream(socket, true);
        }
        catch { socket.Dispose(); throw; }
    }

    public static Task<OkResponse> Get(string url, Dictionary<string, string> headers = null, Dictionary<string, string> query = null, int timeoutMs = 0)
        => Execute("GET", url, headers, query, null, null, true, timeoutMs);

    public static async Task<string> GetString(string url, Dictionary<string, string> headers = null, Dictionary<string, string> query = null)
    {
        try { return (await Get(url, headers, query)).Text(); }
        catch { return ""; }
    }

    public static async Task<OkResponse> Execute(string method, string url, Dictionary<string, string> headers, Dictionary<string, string> query, byte[] body, string contentType, bool redirect = true, int timeoutMs = 0)
    {
        url = AppendQuery(url, query);
        var host = Core.UrlUtil.Host(url);
        if (NetworkConfig.IsAd(host, url)) throw new IOException("Ad blocked: " + host);
        var client = Client(NetworkConfig.GetProxyFor(host), redirect);
        using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);
        var effectiveHeaders = new Dictionary<string, string>(headers ?? new(), StringComparer.OrdinalIgnoreCase);
        var inject = NetworkConfig.GetInjectHeaders(host);
        if (inject != null)
            foreach (var kv in inject) effectiveHeaders[kv.Key] = kv.Value;
        bool hasUa = false;
        foreach (var kv in effectiveHeaders)
        {
            if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUa = true;
            if (kv.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        if (!hasUa) req.Headers.TryAddWithoutValidation("User-Agent", "okhttp/5.1.0");
        if (body != null && method is not ("GET" or "HEAD"))
        {
            req.Content = new ByteArrayContent(body);
            var type = contentType ?? effectiveHeaders.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(type)) req.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        using var cts = new CancellationTokenSource(timeoutMs > 0 ? timeoutMs : Core.Setting.SiteTimeout);
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var ok = new OkResponse { Code = (int)res.StatusCode, FinalUrl = res.RequestMessage?.RequestUri?.AbsoluteUri ?? url };
        foreach (var h in res.Headers) ok.Headers[h.Key] = h.Value.ToList();
        foreach (var h in res.Content.Headers) ok.Headers[h.Key] = h.Value.ToList();
        ok.Body = await res.Content.ReadAsByteArrayAsync(cts.Token);
        return ok;
    }

    public static string AppendQuery(string url, Dictionary<string, string> query)
    {
        if (query == null || query.Count == 0) return url;
        var sb = new System.Text.StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        sb.AppendJoin('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
        return sb.ToString();
    }

    /// <summary>本地路径 / URL 通用读取：http(s) 走网络，file:// 与本地路径走磁盘，assets 走内置资源。</summary>
    public static async Task<string> Load(string urlOrPath, Dictionary<string, string> headers = null)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return "";
        var scheme = Core.UrlUtil.Scheme(urlOrPath);
        if (scheme is "http" or "https") return await GetString(urlOrPath, headers);
        if (scheme == "assets") return Core.AppPaths.ReadAsset(urlOrPath);
        if (scheme == "file") urlOrPath = new Uri(urlOrPath).LocalPath;
        try { return File.Exists(urlOrPath) ? await File.ReadAllTextAsync(urlOrPath) : ""; }
        catch { return ""; }
    }
}
