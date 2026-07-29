using System.Collections.Concurrent;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Engine;

/// <summary>爬虫加载器（Windows 版仅支持 Node.js / JS(Jint) 爬虫；JAR/Python 已移除）。</summary>
public class SpiderLoader
{
    const string TAG = "SpiderLoader";

    public static SpiderLoader Instance { get; } = new();

    readonly ConcurrentDictionary<string, Lazy<Task<Spider>>> Spiders = new();

    SpiderLoader() { }

    /// <summary>Node 源站点：api 指向当前已启动的 Node 服务基址（配置加载期重写为绝对地址）。</summary>
    static bool IsNode(string api) =>
        NodeRuntime.BaseUrl != null && api.StartsWith(NodeRuntime.BaseUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>.js 判定：api 以 .js 结尾、或含 .js?（带查询串），或 ext 指向 .js 脚本。</summary>
    static bool IsJs(string api, string ext) =>
        api.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        api.Contains(".js?", StringComparison.OrdinalIgnoreCase) ||
        ext.EndsWith(".js", StringComparison.OrdinalIgnoreCase);

    /// <summary>取点播站点爬虫（type=3）：node → NodeSpider；.js → JsSpider；其余 → SpiderNull。</summary>
    public Task<Spider> GetSpider(Models.Site site)
    {
        var api = site?.Api ?? "";
        var ext = site?.Ext ?? "";
        if (IsNode(api)) return GetOrCreate(site.Key, () => CreateNode(site));
        if (IsJs(api, ext)) return GetOrCreate(site.Key, () => CreateJs(site, ext));
        return Task.FromResult<Spider>(new SpiderNull { Site = site });
    }

    /// <summary>取直播爬虫（live.api 为 .js 时）：以 live.Name 为缓存 key，合成临时 Site 传入。</summary>
    public Task<Spider> GetLiveSpider(Models.Live live)
    {
        var site = new Models.Site
        {
            Key = live?.Name ?? "",
            Name = live?.Name ?? "",
            Api = live?.Api ?? "",
            Ext = live?.Ext ?? "",
            Jar = live?.Jar ?? "",
            Click = live?.Click ?? "",
            Type = 3,
            Header = live?.Header ?? new(),
        };
        return GetSpider(site);
    }

    /// <summary>供 /proxy?do=js&siteKey= 回调：按 key 找已建成的 spider，找不到返回 null。</summary>
    public Spider FindByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (!Spiders.TryGetValue(key, out var lazy)) return null;
        if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) return null;
        return lazy.Value.Result;
    }

    /// <summary>销毁并清空所有实例（配置切换时由 VodConfigService.Clear 调用）。</summary>
    public void Clear()
    {
        foreach (var lazy in Spiders.Values)
        {
            try
            {
                if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result?.Destroy();
            }
            catch (Exception e) { Logger.E(TAG, "Destroy: " + e.Message); }
        }
        Spiders.Clear();
    }

    Task<Spider> GetOrCreate(string key, Func<Task<Spider>> factory) =>
        Spiders.GetOrAdd(key ?? "", _ => new Lazy<Task<Spider>>(factory)).Value;

    async Task<Spider> CreateJs(Models.Site site, string ext)
    {
        try
        {
            Spider spider = new JsSpider { Site = site };
            await spider.InitAsync(ext);
            return spider;
        }
        catch (Exception e)
        {
            Logger.E(TAG, $"加载 JS 爬虫失败 [{site?.Api}]: {e.Message}");
            return new SpiderNull { Site = site };
        }
    }

    Task<Spider> CreateNode(Models.Site site) =>
        Task.FromResult<Spider>(new NodeSpider { Site = site, Api = site?.Api ?? "" });
}
