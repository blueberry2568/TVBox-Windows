using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Engine;

/// <summary>Node.js 源爬虫（契约 RUNTIME-CONTRACT.md §8）：site.api 为 Node 服务下的站点前缀
/// （如 http://127.0.0.1:9989/spider/douban/3），各能力为其 POST 子路由，返回体已是标准 TVBox JSON。</summary>
public class NodeSpider : Spider
{
    readonly object _initSync = new();
    Task _initTask;
    bool? _homeVideoSupported;
    bool? _searchSupported;

    /// <summary>站点前缀（绝对 URL，由配置加载期重写）。</summary>
    public string Api { get; set; }

    public override Task<string> HomeContent(bool filter) => Post("home", new { filter });

    public override async Task<string> HomeVideoContent()
    {
        if (_homeVideoSupported == false) return "";
        var rsp = await Request("homeVideo", new { });
        if (rsp.Code == 404) { _homeVideoSupported = false; return ""; }
        _homeVideoSupported = true;
        return Read(rsp, "homeVideo");
    }

    // 服务端按 c.id||c.tid、c.page||c.pg、c.filters||c.extend、c.wd||c.key 取值，故同时给出两种拼写
    public override Task<string> CategoryContent(string tid, string pg, bool filter, Dictionary<string, string> extend) =>
        Post("category", new { id = tid ?? "", tid = tid ?? "", page = pg ?? "1", pg = pg ?? "1", filter, extend = extend ?? new(), filters = extend ?? new() });

    public override Task<string> DetailContent(List<string> ids)
    {
        ids ??= new();
        return Post("detail", new { id = ids.FirstOrDefault() ?? "", ids });
    }

    public override Task<string> SearchContent(string key, bool quick) => SearchContent(key, quick, "1");

    public override async Task<string> SearchContent(string key, bool quick, string pg)
    {
        if (_searchSupported == false) return "";
        var rsp = await Request("search", new { wd = key ?? "", key = key ?? "", page = pg ?? "1", pg = pg ?? "1", quick });
        if (rsp.Code == 404) { _searchSupported = false; return ""; }
        _searchSupported = true;
        return Read(rsp, "search");
    }

    public override Task<string> PlayerContent(string flag, string id, List<string> vipFlags) =>
        Post("play", new { flag = flag ?? "", id = id ?? "", vipFlags = vipFlags ?? new() });

    public override Task<string> Action(string action) => Post("action", new { action = action ?? "" });

    public override async Task<object[]> ProxyLocal(Dictionary<string, string> query)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await EnsureInitialized();
                var url = Net.HttpUtil.AppendQuery(Url("proxy"), query ?? new());
                var rsp = await Net.HttpUtil.Get(url, timeoutMs: 30000);
                var mime = rsp.Headers.TryGetValue("Content-Type", out var ct) && ct.Count > 0
                    ? ct[0]
                    : "application/octet-stream";
                return new object[] { rsp.Code == 0 ? 200 : rsp.Code, mime, rsp.Body };
            }
            catch (Exception error) when (attempt == 0 && IsLoopbackTransportFailure(error))
            {
                if (!await TryRecoverNodeAsync()) throw;
            }
        }
        throw new Exception("Node 源代理请求失败");
    }

    string Url(string route) => (Api ?? "").TrimEnd('/') + "/" + route;

    async Task<string> Post(string route, object body)
        => Read(await Request(route, body), route);

    async Task<Net.OkResponse> Request(string route, object body)
    {
        if (string.IsNullOrEmpty(Api)) throw new Exception("NodeSpider 未设置 Api");
        Exception failure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await EnsureInitialized();
                var rsp = await Send(route, body);
                if (!Transient(rsp) || attempt > 0) return rsp;
            }
            catch (Exception e)
            {
                failure = e;
                if (attempt > 0) throw;
                if (IsLoopbackTransportFailure(e) && !await TryRecoverNodeAsync()) throw;
            }
            await Task.Delay(300);
        }
        throw failure ?? new Exception("Node 源请求失败: " + route);
    }

    Task EnsureInitialized()
    {
        lock (_initSync) return _initTask ??= Initialize();
    }

    async Task Initialize()
    {
        if (string.IsNullOrEmpty(Api)) throw new Exception("NodeSpider 未设置 Api");
        var ext = ParseExt(Site?.Ext);
        var body = new JsonObject
        {
            ["ext"] = ext?.DeepClone(),
            ["extend"] = ext?.DeepClone(),
        };
        var rsp = await Send("init", body);
        if (rsp?.Code == 404) return; // Older CatPawOpen services do not expose init.
        Read(rsp, "init");
    }

    static JsonNode ParseExt(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return JsonValue.Create("");
        var parsed = JsonUtil.Parse(ext);
        return parsed ?? (ext.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)
            ? null
            : JsonValue.Create(ext));
    }

    Task<Net.OkResponse> Send(string route, object body)
    {
        var payload = Encoding.UTF8.GetBytes(JsonUtil.Serialize(body));
        var timeout = Math.Max(5000, Site?.RequestTimeout ?? 15000);
        return Net.HttpUtil.Execute("POST", Url(route), null, null, payload, "application/json", timeoutMs: timeout);
    }

    async Task<bool> TryRecoverNodeAsync()
    {
        string oldApi;
        lock (_initSync) oldApi = Api;
        if (!Uri.TryCreate(oldApi, UriKind.Absolute, out var oldUri) || !oldUri.IsLoopback) return false;

        string baseUrl;
        try
        {
            baseUrl = await VodConfigService.Instance.RestoreCurrentNodeAsync();
        }
        catch (Exception error)
        {
            Logger.E("NodeSpider", "恢复点播 Node 服务失败: " + error.Message);
            return false;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return false;

        var rebased = new UriBuilder(baseUri)
        {
            Path = oldUri.AbsolutePath,
            Query = oldUri.Query.TrimStart('?'),
        }.Uri.AbsoluteUri.TrimEnd('/');
        lock (_initSync)
        {
            Api = rebased;
            _initTask = null;
        }
        Logger.D("NodeSpider", $"Node 服务已恢复，站点请求重定向到 {baseUri.Authority}");
        return true;
    }

    bool IsLoopbackTransportFailure(Exception error)
    {
        if (error == null || !Uri.TryCreate(Api, UriKind.Absolute, out var api) || !api.IsLoopback)
            return false;
        var pending = new Stack<Exception>();
        pending.Push(error);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is SocketException socket && socket.SocketErrorCode is
                SocketError.ConnectionRefused or
                SocketError.ConnectionReset or
                SocketError.ConnectionAborted or
                SocketError.NetworkReset)
                return true;
            if (current is HttpRequestException http &&
                http.HttpRequestError == HttpRequestError.ConnectionError)
                return true;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException != null)
                pending.Push(current.InnerException);
        }
        return false;
    }

    static string Read(Net.OkResponse rsp, string route)
    {
        if (rsp == null) throw new Exception("Node 源请求失败: " + route);
        var text = rsp.Text();
        if (rsp.Code is >= 200 and < 300) return text;
        var node = JsonUtil.Parse(text);
        var message = JsonUtil.SafeString(node, "message");
        if (string.IsNullOrWhiteSpace(message)) message = JsonUtil.SafeString(node, "msg");
        if (string.IsNullOrWhiteSpace(message)) message = $"Node 源返回 HTTP {rsp.Code}";
        throw new Exception(message);
    }

    static bool Transient(Net.OkResponse rsp)
    {
        if (rsp == null || rsp.Code is 408 or 429 or 502 or 503 or 504) return true;
        if (rsp.Code != 500) return false;
        var text = rsp.Text();
        return text.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("socket hang up", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }
}
