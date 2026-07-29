namespace TVBoxForWindows.Engine;

/// <summary>爬虫抽象基类（移植自 catvod Spider.java）：所有方法均有空实现，由 JsSpider 等子类覆写；方法可同步或异步完成。</summary>
public abstract class Spider
{
    /// <summary>所属站点（直播 spider 由 Live 合成临时 Site，Key=live.Name）。</summary>
    public Models.Site Site { get; set; }

    /// <summary>初始化（对应 init(context, extend)），ext 为站点扩展参数（字符串或 JSON）。</summary>
    public virtual Task InitAsync(string ext) => Task.CompletedTask;

    /// <summary>首页内容（分类 + 可选 filters），返回 Result JSON。</summary>
    public virtual Task<string> HomeContent(bool filter) => Task.FromResult("");

    /// <summary>首页推荐列表，返回 Result JSON（list）。</summary>
    public virtual Task<string> HomeVideoContent() => Task.FromResult("");

    /// <summary>分类内容，extend 为筛选键值。</summary>
    public virtual Task<string> CategoryContent(string tid, string pg, bool filter, Dictionary<string, string> extend) => Task.FromResult("");

    /// <summary>详情内容，ids 通常只有一个 vodId。</summary>
    public virtual Task<string> DetailContent(List<string> ids) => Task.FromResult("");

    /// <summary>搜索（首页，page=1 时调用两参版本）。</summary>
    public virtual Task<string> SearchContent(string key, bool quick) => Task.FromResult("");

    /// <summary>搜索（带页码，page!=1 时调用）。</summary>
    public virtual Task<string> SearchContent(string key, bool quick, string pg) => SearchContent(key, quick);

    /// <summary>播放地址，vipFlags 为配置全局 flags。</summary>
    public virtual Task<string> PlayerContent(string flag, string id, List<string> vipFlags) => Task.FromResult("");

    /// <summary>直播源内容，返回原始文本（TXT/M3U/JSON）。</summary>
    public virtual Task<string> LiveContent(string url) => Task.FromResult("");

    /// <summary>自定义动作，返回 Result JSON。</summary>
    public virtual Task<string> Action(string action) => Task.FromResult("");

    /// <summary>本地代理回调：返回 [int code, string contentType, byte[]|string body] 或 null。</summary>
    public virtual Task<object[]> ProxyLocal(Dictionary<string, string> query) => Task.FromResult<object[]>(null);

    /// <summary>嗅探是否需要人工判定（对应 manualVideoCheck）。</summary>
    public virtual bool ManualVideoCheck() => false;

    /// <summary>人工判定 url 是否为视频（对应 isVideoFormat）。</summary>
    public virtual Task<bool> IsVideoFormat(string url) => Task.FromResult(false);

    /// <summary>销毁（释放 JS 引擎等资源）。</summary>
    public virtual void Destroy() { }
}

/// <summary>空爬虫（移植自 SpiderNull.java）：JAR/Python 等 Windows 不支持的爬虫占位，所有方法返回空。</summary>
public class SpiderNull : Spider { }
