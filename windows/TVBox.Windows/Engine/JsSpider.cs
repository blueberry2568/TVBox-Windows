using System.Text;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine.Js;
using JintEngine = Jint.Engine;

namespace TVBoxForWindows.Engine;

/// <summary>JS 爬虫（移植自 quickjs/crawler/Spider.java）：每实例独立 Jint 引擎，SemaphoreSlim(1,1) 串行所有 JS 调用；
/// 初始化：先 eval 内置 js/lib/http.js 与全局 API，再按 ES module 约定加载 spider 脚本
/// （cat 判定：源码含 __jsEvalReturn；__JS_SPIDER__ 字面量替换为 globalThis.__JS_SPIDER__，经 js/lib/spider.js 装配）；
/// 所有方法调用统一处理 Promise（then/catch 挂钩 + 定时器事件循环泵）。</summary>
public class JsSpider : Spider
{
    const string TAG = "JsSpider";
    const string SpiderKey = "__JS_SPIDER__";
    const string LoaderName = "__spider_loader__";
    /// <summary>Promise 泵总超时（安全阀，防止脚本内死循环定时器卡死引擎）。</summary>
    const int PumpTimeoutMs = 120_000;
    /// <summary>thenable 判定 + then/catch 挂钩辅助（等价 utils/Async.java）。</summary>
    const string SettleHelper = "globalThis.__settle__=function(v,ok,err){if(v&&typeof v.then==='function'){Promise.resolve(v).then(ok,err);return true}return false};";

    readonly SemaphoreSlim _sem = new(1, 1);

    JintEngine _engine;
    JsGlobals _globals;
    ObjectInstance _jsObject;
    bool _cat;

    /// <summary>初始化：建引擎 → 加载 spider 模块 → 调 init(ext)（cat 时 ext 包成 {stype:3, skey, ext}）。</summary>
    public override Task InitAsync(string ext) => Run<object>(() =>
    {
        CreateEngine();
        CreateObj();
        CallJs("init", GetExtArg(ext));
        return null;
    });

    public override Task<string> HomeContent(bool filter) => RunString("home", filter);

    public override Task<string> HomeVideoContent() => RunString("homeVod");

    public override Task<string> CategoryContent(string tid, string pg, bool filter, Dictionary<string, string> extend) =>
        Run(() => AsString(CallJs("category", tid ?? "", pg ?? "", filter, _globals.ParseJson(JsonUtil.Serialize(extend ?? new())))) ?? "");

    public override Task<string> DetailContent(List<string> ids) =>
        RunString("detail", ids is { Count: > 0 } ? ids[0] : "");

    public override Task<string> SearchContent(string key, bool quick) => RunString("search", key ?? "", quick);

    public override Task<string> SearchContent(string key, bool quick, string pg) => RunString("search", key ?? "", quick, pg ?? "");

    public override Task<string> PlayerContent(string flag, string id, List<string> vipFlags) =>
        Run(() => AsString(CallJs("play", flag ?? "", id ?? "", _globals.ParseJson(JsonUtil.Serialize(vipFlags ?? new())))) ?? "");

    public override Task<string> LiveContent(string url) => RunString("live", url ?? "");

    public override Task<string> Action(string action) => RunString("action", action ?? "");

    /// <summary>嗅探人工判定（同步契约）：调 JS sniffer()，异常按 false。</summary>
    public override bool ManualVideoCheck()
    {
        try { return Run(() => JsGlobals.Truthy(CallJs("sniffer"))).GetAwaiter().GetResult(); }
        catch { return false; }
    }

    public override async Task<bool> IsVideoFormat(string url)
    {
        try { return await Run(() => JsGlobals.Truthy(CallJs("isVideo", url ?? ""))); }
        catch { return false; }
    }

    /// <summary>本地代理回调：from=catvod 走 proxy2（url 分段数组 + header 对象，JS 返回 Res JSON），
    /// 否则 proxy1（参数对象，JS 返回 [code, mime, body, headers?, base64?] 数组）。</summary>
    public override Task<object[]> ProxyLocal(Dictionary<string, string> query) => Run(() =>
    {
        var pars = query ?? new Dictionary<string, string>();
        return "catvod".Equals(pars.GetValueOrDefault("from"), StringComparison.Ordinal) ? Proxy2(pars) : Proxy1(pars);
    });

    /// <summary>销毁：调 JS destroy 后释放引擎（等价 Spider.destroy + releaseJS）。</summary>
    public override void Destroy()
    {
        try
        {
            if (_sem.Wait(3000))
            {
                try
                {
                    var fn = _jsObject?.Get("destroy");
                    if (fn != null && !fn.IsUndefined() && !fn.IsNull())
                    {
                        _engine.Invoke(fn, _jsObject, Array.Empty<object>());
                        _engine.Advanced.ProcessTasks();
                    }
                }
                catch (Exception e) { Logger.E(TAG, "destroy: " + e.Message); }
                finally { _sem.Release(); }
            }
        }
        catch { }
        try { _globals?.Destroy(); } catch { }
        try { _engine?.Dispose(); } catch { }
        _jsObject = null;
        _engine = null;
    }

    // ---- 引擎构建 ----

    /// <summary>建引擎（等价 createCtx + createFun）：启用 ES module（JsModuleLoader）、预载 http.js、注入全局 API 与 __settle__。</summary>
    void CreateEngine()
    {
        _engine = new JintEngine(options => options.EnableModules(new JsModuleLoader()));
        _engine.Execute(AppPaths.ReadAsset("js/lib/http.js"));
        _globals = new JsGlobals(_engine, Site?.Key ?? "");
        _engine.Execute(SettleHelper);
    }

    /// <summary>加载 spider 脚本（等价 createObj）：fetch api 源码 → cat 判定 → 注册 api 模块（__JS_SPIDER__ 替换）
    /// → 执行 js/lib/spider.js 装配器（%s 替换为 api）→ 取 globalThis.__JS_SPIDER__。</summary>
    void CreateObj()
    {
        var api = Site?.Api ?? "";
        var content = JsModuleLoader.Fetch(api);
        if (string.IsNullOrEmpty(content)) throw new FileNotFoundException("spider 脚本获取失败: " + api);
        _cat = content.Contains("__jsEvalReturn");
        _engine.Modules.Add(api, content.Replace(SpiderKey, "globalThis." + SpiderKey));
        var loader = AppPaths.ReadAsset("js/lib/spider.js");
        if (string.IsNullOrEmpty(loader)) throw new FileNotFoundException("内置 js/lib/spider.js 缺失");
        _engine.Modules.Add(LoaderName, loader.Replace("%s", api));
        _engine.Modules.Import(LoaderName);
        _jsObject = _engine.Evaluate("globalThis." + SpiderKey) as ObjectInstance;
        if (_jsObject == null) throw new InvalidOperationException("spider 未导出 " + SpiderKey + ": " + api);
    }

    /// <summary>init 的 ext 编组（等价 getExt）：cat → {stype:3, skey, ext:对象或字符串}；否则 JSON 对象或原字符串。</summary>
    JsValue GetExtArg(string ext)
    {
        ext ??= "";
        if (!_cat) return JsonUtil.IsObj(ext) ? _globals.ParseJson(ext) : (JsValue)ext;
        var payload = "{\"stype\":3,\"skey\":" + JsonUtil.Serialize(Site?.Key ?? "")
                    + ",\"ext\":" + (JsonUtil.IsObj(ext) ? ext : JsonUtil.Serialize(ext)) + "}";
        return _globals.ParseJson(payload);
    }

    // ---- 串行调度与 Promise 处理 ----

    /// <summary>串行执行（等价单线程 executor.submit）：SemaphoreSlim 保证 Jint 引擎无并发访问。</summary>
    async Task<T> Run<T>(Func<T> action)
    {
        await _sem.WaitAsync();
        try { return await Task.Run(action); }
        finally { _sem.Release(); }
    }

    Task<string> RunString(string func, params object[] args) => Run(() => AsString(CallJs(func, args)) ?? "");

    /// <summary>调用 jsObject 方法并等待 Promise 完成（等价 Async.run）：方法不存在返回 null；
    /// pending 时先清空微任务队列，再泵定时器（setTimeout 驱动的 sleep/轮询）直至完成。</summary>
    JsValue CallJs(string func, params object[] args)
    {
        if (_jsObject == null || _engine == null) return null;
        var fn = _jsObject.Get(func);
        if (fn == null || fn.IsUndefined() || fn.IsNull()) return null;
        var result = _engine.Invoke(fn, _jsObject, args ?? Array.Empty<object>());
        JsValue resolved = null;
        string error = null;
        var done = false;
        var thenable = _engine.Invoke("__settle__", result,
            (Action<JsValue>)(v => { resolved = v; done = true; }),
            (Action<JsValue>)(v => { error = v?.ToString() ?? "unknown"; done = true; }));
        if (!JsGlobals.Truthy(thenable)) return result;
        _engine.Advanced.ProcessTasks();
        var deadline = Environment.TickCount64 + PumpTimeoutMs;
        while (!done && Environment.TickCount64 < deadline)
        {
            if (!_globals.PumpNextTimer()) break;
            _engine.Advanced.ProcessTasks();
        }
        if (!done) throw new TimeoutException(func + ": Promise 未完成（无待执行任务或超时）");
        if (error != null) throw new Exception(func + ": " + error);
        return resolved;
    }

    static string AsString(JsValue value)
    {
        if (value == null || value.IsUndefined() || value.IsNull()) return null;
        return value.IsString() ? value.AsString() : value.ToString();
    }

    // ---- proxy（等价 Spider.proxy1/proxy2）----

    /// <summary>proxy1（普通）：proxy(paramsObj) → JSON 数组 [code, contentType, body, headers?:json, base64?:int]。</summary>
    object[] Proxy1(Dictionary<string, string> pars)
    {
        var value = CallJs("proxy", _globals.ParseJson(JsonUtil.Serialize(pars)));
        if (value == null) return null;
        if (JsonUtil.Parse(_globals.Stringify(value)) is not JsonArray array || array.Count < 3) return null;
        Dictionary<string, string> headers = null;
        if (array.Count > 3 && array[3] != null)
            headers = array[3] is JsonObject obj ? JsonUtil.ToMap(obj) : JsonUtil.ToMap(array[3].ToString());
        var base64 = array.Count > 4 && (array[4]?.ToString() ?? "") == "1";
        var content = array[2]?.ToString() ?? "";
        if (base64 && content.Contains("base64,")) content = content[(content.IndexOf("base64,", StringComparison.Ordinal) + 7)..];
        var body = base64 ? JsGlobals.FromBase64Lenient(content) : Encoding.UTF8.GetBytes(content);
        int.TryParse(array[0]?.ToString(), out var code);
        return new object[] { code, array[1]?.ToString() ?? "", body, headers };
    }

    /// <summary>proxy2（catvod）：proxy(urlSegments, headerObj) → Res JSON {code=200, buffer(2=base64), content, headers}。</summary>
    object[] Proxy2(Dictionary<string, string> pars)
    {
        var url = pars.GetValueOrDefault("url") ?? "";
        var header = pars.GetValueOrDefault("header") ?? "";
        var segments = _globals.ParseJson(JsonUtil.Serialize(url.Split('/')));
        var headerObj = _globals.ParseJson(JsonUtil.IsObj(header) ? header : "{}");
        var value = CallJs("proxy", segments, headerObj);
        var node = JsonUtil.Parse(AsString(value) ?? "");
        if (node == null) return null;
        if (!int.TryParse(JsonUtil.SafeString(node, "code"), out var code) || code == 0) code = 200;
        int.TryParse(JsonUtil.SafeString(node, "buffer"), out var buffer);
        var content = JsonUtil.SafeString(node, "content");
        var headers = JsonUtil.ToMap(node["headers"]);
        var contentType = headers.GetValueOrDefault("Content-Type");
        if (string.IsNullOrEmpty(contentType)) contentType = "application/octet-stream";
        var body = buffer == 2 ? JsGlobals.FromBase64Lenient(content) : Encoding.UTF8.GetBytes(content);
        return new object[] { code, contentType, body };
    }
}
