using FongMi.TV.Core;
using FongMi.TV.Models;
using FongMi.TV.Net;

namespace FongMi.TV.Engine;

/// <summary>站点内容服务（移植自 SiteApi.java / SiteViewModel.java）：type 0/1/3/4 分派，所有异常内部捕获返回 Result.Error。</summary>
public static class SiteService
{
    const string TAG = "SiteService";
    const string PUSH = "push_agent";

    /// <summary>首页内容：分类 + filters + 推荐列表。</summary>
    public static async Task<Result> HomeContent(Site site)
    {
        try
        {
            if (site.Type == 3)
            {
                var spider = await SpiderLoader.Instance.GetSpider(site);
                if (NotSupported(site, spider, out var na)) return na;
                var home = await spider.HomeContent(true);
                var result = Result.FromJson(home);
                try
                {
                    var video = await spider.HomeVideoContent();
                    var list = Result.FromJson(video).List;
                    if (list.Count > 0) result.List = list;
                }
                catch (Exception e)
                {
                    // homeVideo is optional; a broken recommendation feed must not hide valid categories.
                    Logger.E(TAG, "homeVideo: " + e.Message);
                }
                SetTypes(site, result);
                return result.Trans();
            }
            if (site.Type == 4)
            {
                var ps = new Dictionary<string, string> { ["filter"] = "true" };
                var result = Result.FromJson(await Call(await site.FetchExt(), ps));
                SetTypes(site, result);
                return result.Trans();
            }
            // type 0/1/2：GET site.api（无参数，带 site.header）
            var body = (await HttpUtil.Get(site.Api, site.Header, null, site.RequestTimeout)).Text();
            var res = Result.FromType(site.Type, body);
            await FetchPic(site, res);
            SetTypes(site, res);
            return res.Trans();
        }
        catch (Exception e) { Logger.E(TAG, "home: " + e.Message); return Result.Error(e.Message); }
    }

    /// <summary>分类内容：type0 ac=videolist；type1 extend 非空加 f=JSON；type4 加 ext=Base64URLSafe(JSON)。</summary>
    public static async Task<Result> CategoryContent(Site site, string tid, string pg, bool filter, Dictionary<string, string> extend)
    {
        try
        {
            extend ??= new Dictionary<string, string>();
            if (site.Type == 3)
            {
                var spider = await SpiderLoader.Instance.GetSpider(site);
                if (NotSupported(site, spider, out var na)) return na;
                return Result.FromJson(await spider.CategoryContent(tid, pg, filter, extend)).Trans();
            }
            var ps = new Dictionary<string, string>();
            if (site.Type == 1 && extend.Count > 0) ps["f"] = JsonUtil.Serialize(extend);
            if (site.Type == 4) ps["ext"] = Base64UrlSafe(JsonUtil.Serialize(extend));
            ps["ac"] = Ac(site.Type);
            ps["t"] = tid;
            ps["pg"] = pg;
            return Result.FromType(site.Type, await Call(site, ps)).Trans();
        }
        catch (Exception e) { Logger.E(TAG, "category: " + e.Message); return Result.Error(e.Message); }
    }

    /// <summary>详情内容：结果所有 Vod.Site = site；push_agent 直接构造推送 Vod。</summary>
    public static async Task<Result> DetailContent(Site site, string vodId)
    {
        try
        {
            Result result;
            if (IsPush(site))
            {
                var vod = new Vod { Id = vodId, Name = vodId, PlayUrl = vodId, PlayFrom = "推送", Pic = "" };
                SourceParse(vod);
                result = Result.FromVod(vod);
            }
            else if (site.Type == 3)
            {
                var spider = await SpiderLoader.Instance.GetSpider(site);
                if (NotSupported(site, spider, out var na)) return na;
                result = Result.FromJson(await spider.DetailContent(new List<string> { vodId }));
                SourceParse(result.Vod);
            }
            else
            {
                var ps = new Dictionary<string, string> { ["ac"] = Ac(site.Type), ["ids"] = vodId };
                result = Result.FromType(site.Type, await Call(site, ps));
                SourceParse(result.Vod);
            }
            foreach (var vod in result.List) vod.Site = site;
            return result.Trans();
        }
        catch (Exception e) { Logger.E(TAG, "detail: " + e.Message); return Result.Error(e.Message); }
    }

    /// <summary>搜索：关键词自动繁→简；quick 且站点不支持快搜时返回空；结果 Vod.Site = site。</summary>
    public static async Task<Result> SearchContent(Site site, string keyword, bool quick, string pg = "1")
    {
        try
        {
            keyword = Core.Trans.T2S(keyword);
            if (quick && !site.IsQuickSearch) return Result.Empty();
            bool hasPage = pg != "1";
            Result result;
            if (site.Type == 3)
            {
                var spider = await SpiderLoader.Instance.GetSpider(site);
                if (NotSupported(site, spider, out var na)) return na;
                var content = hasPage ? await spider.SearchContent(keyword, quick, pg) : await spider.SearchContent(keyword, quick);
                result = Result.FromJson(content);
            }
            else
            {
                var ps = new Dictionary<string, string> { ["wd"] = keyword, ["quick"] = quick ? "true" : "false", ["extend"] = "" };
                if (hasPage) ps["pg"] = pg;
                result = await FetchPic(site, Result.FromType(site.Type, await Call(site, ps)));
            }
            foreach (var vod in result.List) vod.Site = site;
            return result.Trans();
        }
        catch (Exception e) { Logger.E(TAG, "search: " + e.Message); return Result.Error(e.Message); }
    }

    /// <summary>播放内容：type3/4 走爬虫或 API；type0/1 构造直链/待解析 Result；push_agent 直接播。</summary>
    public static async Task<Result> PlayerContent(Site site, string flag, string id)
    {
        try
        {
            Result result;
            if (!IsPush(site) && site.Type == 3)
            {
                var spider = await SpiderLoader.Instance.GetSpider(site);
                if (NotSupported(site, spider, out var na)) return na;
                result = Result.FromJson(await spider.PlayerContent(flag, id, VodConfigService.Instance.Flags));
                if (string.IsNullOrEmpty(result.Flag)) result.Flag = flag;
                result.SetUrl(SourceFetch(result.UrlBean.V()));
                SetHeader(result, site.Header);
                result.Key = site.Key;
            }
            else if (!IsPush(site) && site.Type == 4)
            {
                var ps = new Dictionary<string, string> { ["play"] = id, ["flag"] = flag };
                result = Result.FromJson(await Call(site, ps));
                if (string.IsNullOrEmpty(result.Flag)) result.Flag = flag;
                result.SetUrl(SourceFetch(result.UrlBean.V()));
                SetHeader(result, site.Header);
            }
            else if (IsPush(site))
            {
                result = new Result();
                result.SetUrl(id);
                result.Parse = 0;
                result.Flag = flag;
                result.SetUrl(SourceFetch(result.UrlBean.V()));
            }
            else
            {
                // type 0/1（及其它）：url=id；裸视频直链（且无 playUrl）直接播，否则需解析
                result = new Result();
                result.SetUrl(id);
                result.Flag = flag;
                SetHeader(result, site.Header);
                result.PlayUrl = site.PlayUrl ?? "";
                result.Parse = Sniffer.IsVideoFormat(id) && string.IsNullOrEmpty(result.PlayUrl) ? 0 : 1;
                result.SetUrl(SourceFetch(result.UrlBean.V()));
            }
            return result.Trans();
        }
        catch (Exception e) { Logger.E(TAG, "player: " + e.Message); return Result.Error(e.Message); }
    }

    /// <summary>自定义动作：type3 调 spider.Action；type4 把 action 当 URL GET；其余返回空。</summary>
    public static async Task<Result> Action(Site site, string action)
    {
        try
        {
            if (site.Type == 3)
            {
                var spider = await SpiderLoader.Instance.GetSpider(site);
                if (NotSupported(site, spider, out var na)) return na;
                return Result.FromJson(await spider.Action(action)).Trans();
            }
            if (site.Type == 4) return Result.FromJson(await HttpUtil.GetString(action)).Trans();
            return Result.Empty();
        }
        catch (Exception e) { Logger.E(TAG, "action: " + e.Message); return Result.Error(e.Message); }
    }

    // ---- 私有辅助 ----

    /// <summary>ac 参数：type0 → videolist，其余 → detail。</summary>
    static string Ac(int type) => type == 0 ? "videolist" : "detail";

    /// <summary>推送站点判定（site 为空或 key=push_agent 且无 api）。</summary>
    static bool IsPush(Site site) => site == null || site.IsEmpty || (site.Key == PUSH && string.IsNullOrEmpty(site.Api));

    /// <summary>通用请求（移植自 SiteApi.call）：ext 非空加 extend 参数；ext ≤1000 用 GET query，>1000 改 POST form。</summary>
    static async Task<string> Call(Site site, Dictionary<string, string> ps)
    {
        if (!string.IsNullOrEmpty(site.Ext)) ps["extend"] = site.Ext;
        if ((site.Ext ?? "").Length <= 1000)
            return (await HttpUtil.Get(site.Api, site.Header, ps, site.RequestTimeout)).Text();
        var body = string.Join("&", ps.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
        return (await HttpUtil.Execute("POST", site.Api, site.Header, null, System.Text.Encoding.UTF8.GetBytes(body), "application/x-www-form-urlencoded", true, site.RequestTimeout)).Text();
    }

    /// <summary>fetchPic：仅 type≤2、列表非空且首项无图时，二次请求 ac=&ids= 用详情结果替换列表（categories 白名单过滤）。</summary>
    static async Task<Result> FetchPic(Site site, Result result)
    {
        if (site.Type > 2 || result.List.Count == 0 || !string.IsNullOrEmpty(result.Vod.Pic)) return result;
        var ids = new List<string>();
        bool empty = site.Categories == null || site.Categories.Count == 0;
        foreach (var item in result.List) if (empty || site.Categories.Contains(item.TypeName)) ids.Add(item.Id);
        if (ids.Count == 0) { result.List = new List<Vod>(); return result; }
        var ps = new Dictionary<string, string> { ["ac"] = Ac(site.Type), ["ids"] = string.Join(",", ids) };
        var body = (await HttpUtil.Get(site.Api, site.Header, ps, site.RequestTimeout)).Text();
        result.List = Result.FromType(site.Type, body).List;
        return result;
    }

    /// <summary>setTypes：filters 挂到对应分类；site.categories 非空时按其顺序/白名单过滤重排分类。</summary>
    static void SetTypes(Site site, Result result)
    {
        foreach (var type in result.Types)
            if (type.TypeId != null && result.Filters.TryGetValue(type.TypeId, out var fs)) type.Filters = fs;
        if (site.Categories == null || site.Categories.Count == 0) return;
        var byName = new Dictionary<string, VodClass>();
        foreach (var t in result.Types) byName[t.TypeName ?? ""] = t;
        var types = site.Categories.Where(byName.ContainsKey).Select(c => byName[c]).ToList();
        if (types.Count > 0) result.Types = types;
    }

    /// <summary>Result.setHeader 语义：仅在现有 header 为空时写入（保 spider 返回的 header 优先级最高）。</summary>
    static void SetHeader(Result result, Dictionary<string, string> header)
    {
        if (result.Header != null && result.Header.Count > 0) return;
        result.Header = new Dictionary<string, string>(header ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>SpiderNull 且 api 为 csp_/py 时给出不支持提示（Windows 版已移除 JAR/Python 运行时）。</summary>
    static bool NotSupported(Site site, Spider spider, out Result result)
    {
        result = null;
        if (spider is not SpiderNull) return false;
        var api = site?.Api ?? "";
        if (api.StartsWith("csp_", StringComparison.Ordinal) ||
            api.Contains(".py", StringComparison.OrdinalIgnoreCase))
        {
            result = Result.Error("该类型的站点爬虫在 Windows 版下不支持");
            return true;
        }
        return false;
    }

    /// <summary>Base64 URL-safe（等价 Util.base64(s, URL_SAFE)：-_ 字符集、无换行、保留 = 填充）。</summary>
    static string Base64UrlSafe(string text) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text ?? "")).Replace('+', '-').Replace('/', '_');

    /// <summary>Source.fetch 占位：Android 端 extractor（thunder 磁力/jianpian/网盘 push 等）依赖原生库，Windows 暂不支持，原样返回。
    /// TODO: 后续如需支持磁力/网盘直链提取，在此接入对应实现。</summary>
    static string SourceFetch(string url) => url;

    /// <summary>Source.parse 占位：Android 端对 detail 的 flags 做 extractor 预处理（磁力分片等），Windows 暂不支持。
    /// TODO: 同 SourceFetch，保留调用点以便后续接入。</summary>
    static void SourceParse(Vod vod) { }
}
