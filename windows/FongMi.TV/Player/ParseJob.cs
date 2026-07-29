using System.Text.Json.Nodes;
using FongMi.TV.Core;
using FongMi.TV.Engine;
using FongMi.TV.Net;

namespace FongMi.TV.Player;

/// <summary>解析结果：真实媒体 URL 与播放 headers。</summary>
public class ParseResult { public string Url; public Dictionary<string, string> Headers = new(); }

/// <summary>解析任务（移植自 ParseJob.java）：type 0=web 嗅探 1=json 2=json 扩展 3=聚合 4=God。
/// 与 Android 差异：type2/3 原走 JS 引擎 jsonExt/jsonExtMix 聚合（需 jar 内置 JS），Windows 版降级为
/// 「所有 type1 解析器并发 jsonParse + type0 解析器逐个 WebSniffer」的简化聚合，首个成功者胜。</summary>
public static class ParseJob
{
    const string TAG = "ParseJob";
    const int TimeoutMs = 30000; // 总超时（对应 Constant.TIMEOUT_PARSE_DEF）

    /// <summary>对 web 播放页执行解析（parse type 0-4），返回真实媒体 URL 与 headers。失败抛异常。</summary>
    public static async Task<ParseResult> Run(Models.Parse parse, string flag, string webUrl, CancellationToken ct)
    {
        if (parse == null) throw new Exception("无可用解析器");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);
        try
        {
            return parse.Type switch
            {
                1 => await JsonParse(parse, webUrl, cts.Token),
                2 => await MixParse(flag, webUrl, cts.Token),
                3 => await MixParse(flag, webUrl, cts.Token),
                4 => await GodParse(parse, flag, webUrl, cts.Token),
                _ => await WebParse(parse, webUrl, cts.Token),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("解析超时");
        }
    }

    /// <summary>type 1：GET parse.url + webUrl → JSON 取 url（顶层，空则 data.url）；url>40 判成功；
    /// headers 从响应顶层 ua/user-agent/referer/cookie 提取，一个都没有则用 parse.header。</summary>
    static async Task<ParseResult> JsonParse(Models.Parse parse, string webUrl, CancellationToken ct)
    {
        var res = await HttpUtil.Get(parse.Url + webUrl, parse.Headers(), null, Setting.SiteTimeout);
        ct.ThrowIfCancellationRequested();
        var node = JsonUtil.Parse(res.Text()) ?? throw new Exception("解析响应非 JSON: " + parse.Name);
        var url = JsonUtil.SafeString(node, "url");
        if (string.IsNullOrEmpty(url) && node["data"] is JsonNode data) url = JsonUtil.SafeString(data, "url");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject obj)
            foreach (var kv in obj)
            {
                var key = kv.Key.ToLowerInvariant();
                if (key is "ua" or "user-agent") headers["User-Agent"] = kv.Value?.ToString() ?? "";
                else if (key == "referer") headers["Referer"] = kv.Value?.ToString() ?? "";
                else if (key == "cookie") headers["Cookie"] = kv.Value?.ToString() ?? "";
            }
        if (headers.Count == 0) headers = parse.Headers();
        if ((url ?? "").Length <= 40) throw new Exception("解析失败: " + parse.Name);
        return new ParseResult { Url = url, Headers = headers };
    }

    /// <summary>type 0：WebView2 嗅探 parse.url + webUrl（带 parse.header 与 click 脚本）。</summary>
    static Task<ParseResult> WebParse(Models.Parse parse, string webUrl, CancellationToken ct)
        => WebSniffer.Sniff(parse.Url + webUrl, parse.Headers(), parse.Click, ct);

    /// <summary>type 2/3 简化聚合（与 Android 差异见类注释）：type1 并发 + type0 逐个嗅探，首个成功者胜。</summary>
    static async Task<ParseResult> MixParse(string flag, string webUrl, CancellationToken ct)
    {
        var jsons = GetParsesFallback(1, flag);
        var webs = GetParsesFallback(0, flag);
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task<ParseResult>>();
        foreach (var p in jsons) tasks.Add(JsonParse(p, webUrl, inner.Token));
        if (webs.Count > 0) tasks.Add(WebSniffSequential(webs, webUrl, inner.Token));
        if (tasks.Count == 0) throw new Exception("无可用解析器");
        var result = await FirstSuccess(tasks);
        inner.Cancel();
        return result;
    }

    /// <summary>type 4 God：所有 type1 json 并发 + type0 合并为 LocalServer /parse 聚合页做一次嗅探。</summary>
    static async Task<ParseResult> GodParse(Models.Parse parse, string flag, string webUrl, CancellationToken ct)
    {
        var jsons = GetParsesFallback(1, flag);
        var webs = GetParsesFallback(0, flag);
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task<ParseResult>>();
        foreach (var p in jsons) tasks.Add(JsonParse(p, webUrl, inner.Token));
        if (webs.Count > 0)
        {
            var jxs = string.Join(";", webs.Select(w => w.Url));
            var aggUrl = Server.LocalServer.Instance.GetAddress("/parse")
                + "?jxs=" + Uri.EscapeDataString(jxs)
                + "&url=" + Uri.EscapeDataString(webUrl ?? "");
            tasks.Add(WebSniffer.Sniff(aggUrl, new Dictionary<string, string>(), parse?.Click, inner.Token));
        }
        if (tasks.Count == 0) throw new Exception("无可用解析器");
        var result = await FirstSuccess(tasks);
        inner.Cancel();
        return result;
    }

    /// <summary>取 type+flag 匹配的解析器；按 Android 规则：过滤后为空则退回全量（不按 flag 过滤）。</summary>
    static List<Models.Parse> GetParsesFallback(int type, string flag)
    {
        var list = VodConfigService.Instance.GetParses(type, flag);
        if (list.Count == 0) list = VodConfigService.Instance.GetParses(type, "");
        return list;
    }

    /// <summary>type0 解析器逐个嗅探直到成功（作为一个整体任务参与并发）。</summary>
    static async Task<ParseResult> WebSniffSequential(List<Models.Parse> webs, string webUrl, CancellationToken ct)
    {
        foreach (var p in webs)
        {
            ct.ThrowIfCancellationRequested();
            try { return await WebSniffer.Sniff(p.Url + webUrl, p.Headers(), p.Click, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Logger.D(TAG, "嗅探失败 " + p.Name + ": " + e.Message); }
        }
        throw new Exception("嗅探失败");
    }

    /// <summary>等待首个成功任务（url 非空即成功），全部失败抛异常。</summary>
    static async Task<ParseResult> FirstSuccess(List<Task<ParseResult>> tasks)
    {
        var pending = new List<Task<ParseResult>>(tasks);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (done.Status == TaskStatus.RanToCompletion && !string.IsNullOrEmpty(done.Result?.Url)) return done.Result;
        }
        throw new Exception("解析失败");
    }
}
