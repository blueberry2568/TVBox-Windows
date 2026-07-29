using FongMi.TV.Models;

namespace FongMi.TV.Engine;

/// <summary>聚合搜索（移植自 SiteViewModel.searchContent(List&lt;Site&gt;)）：并行搜索全部可搜站点，逐站回调。</summary>
public class SearchService
{
    /// <summary>并行搜索所有可搜站点；每站完成即回调（UI 线程）。keyword 自动繁→简。
    /// 并发度 = Environment.ProcessorCount，单站超时用 site.RequestTimeout，空结果不回调。</summary>
    public static async Task SearchAll(string keyword, bool quick, Action<Models.Site, List<Models.Vod>> onSiteResult, CancellationToken ct)
    {
        keyword = Core.Trans.T2S(keyword);
        var sites = VodConfigService.Instance.Sites.Where(s => quick ? s.IsQuickSearch : s.IsSearchable).ToList();
        if (sites.Count == 0) return;
        using var gate = new SemaphoreSlim(Environment.ProcessorCount);
        var tasks = sites.Select(site => SearchOne(site, keyword, quick, onSiteResult, gate, ct)).ToList();
        try { await Task.WhenAll(tasks); } catch { }
    }

    /// <summary>单站搜索：限流 + 超时 + 取消，异常一律吞掉（等价 Android 每站独立任务失败不影响整体）。</summary>
    static async Task SearchOne(Site site, string keyword, bool quick, Action<Site, List<Vod>> onSiteResult, SemaphoreSlim gate, CancellationToken ct)
    {
        try { await gate.WaitAsync(ct); }
        catch { return; }
        try
        {
            if (ct.IsCancellationRequested) return;
            var result = await SiteService.SearchContent(site, keyword, quick).WaitAsync(TimeSpan.FromMilliseconds(site.RequestTimeout), ct);
            if (ct.IsCancellationRequested || result == null || result.List.Count == 0) return;
            App.Post(() => { if (!ct.IsCancellationRequested) onSiteResult?.Invoke(site, result.List); });
        }
        catch { }
        finally { gate.Release(); }
    }
}
