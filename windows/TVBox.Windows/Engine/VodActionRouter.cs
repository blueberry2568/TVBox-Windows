using Windows.System;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Engine;

/// <summary>点播动作卡路由：配置中心打开原始配置地址，其余动作保留站点语义。</summary>
public static class VodActionRouter
{
    /// <summary>CatPawOpen 豆瓣卡仅提供检索线索：按片名聚合搜索，不请求空详情。</summary>
    public static bool ShouldSearch(string siteKey, string vodId)
    {
        if ((vodId ?? "").StartsWith("msearch:", StringComparison.OrdinalIgnoreCase)) return true;

        var site = VodConfigService.Instance.GetSite(siteKey);
        if (site.SearchByName) return true;
        var source = VodConfigService.Instance.Config?.Url;
        if (string.IsNullOrWhiteSpace(source)) source = Setting.ConfigVod;
        if (!NodeSource.MaybeNode(UrlUtil.Convert(source ?? "")) ||
            !Uri.TryCreate(site?.Api, UriKind.Absolute, out var siteUri) || !siteUri.IsLoopback) return false;

        var segments = siteUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("spider", StringComparison.OrdinalIgnoreCase)) return false;
        var slug = segments[1];
        return slug.Equals("douban", StringComparison.OrdinalIgnoreCase) ||
               slug.Equals("modou", StringComparison.OrdinalIgnoreCase) ||
               slug.Equals("newdb", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<ActionRouteResult> RouteAsync(string siteKey, string vodId, string title, string remark, string action)
    {
        var site = VodConfigService.Instance.GetSite(siteKey);
        if (IsNodeConfigCenter(site, vodId))
        {
            // CatPaw advertises a LAN address, but the Windows bootstrap intentionally binds
            // to loopback. Always open the reachable local website for an in-app click.
            string baseUrl;
            try
            {
                baseUrl = await VodConfigService.Instance.RestoreCurrentNodeAsync();
            }
            catch (Exception e)
            {
                Logger.E("VodActionRouter", "恢复配置中心服务失败: " + e.Message);
                return ActionRouteResult.Handled("配置中心服务启动失败: " + e.Message);
            }
            if (string.IsNullOrWhiteSpace(baseUrl) || !await NodeRuntime.IsCurrentHealthyAsync())
                return ActionRouteResult.Handled("配置中心服务暂时不可用，请稍后重试");

            var configUrl = VodConfigService.Instance.Config?.Url;
            var website = baseUrl?.TrimEnd('/') + "/website";
            var watching = NodeConfigChangeMonitor.Start(
                baseUrl,
                NodeSource.GetSnapshotFingerprint(baseUrl),
                () => ReloadNodeConfigAsync(baseUrl, configUrl));
            var launchResult = await OpenAsync(website, "配置中心地址无效或系统无法打开默认浏览器");
            if (watching && !string.IsNullOrEmpty(launchResult.Message)) NodeConfigChangeMonitor.Stop();
            return launchResult;
        }
        if (IsLegacyConfigCenter(title, remark, action))
        {
            var configUrl = VodConfigService.Instance.Config?.Url;
            if (string.IsNullOrWhiteSpace(configUrl)) configUrl = Setting.ConfigVod;
            return await OpenAsync(configUrl, "配置地址无效或系统无法打开默认浏览器");
        }
        if (string.IsNullOrWhiteSpace(action)) return ActionRouteResult.NotHandled();

        var result = await SiteService.Action(site, action);
        var url = result?.UrlBean?.V();
        if (IsHttp(url)) return await OpenAsync(url, "动作地址无效或系统无法打开默认浏览器");
        var message = result?.GetMsg();
        if (string.IsNullOrWhiteSpace(message)) message = result?.Msg;
        return ActionRouteResult.Handled(message);
    }

    static bool IsNodeConfigCenter(Site site, string vodId)
    {
        var source = VodConfigService.Instance.Config?.Url;
        if (string.IsNullOrWhiteSpace(source)) source = Setting.ConfigVod;
        if (!NodeSource.MaybeNode(UrlUtil.Convert(source ?? "")) || !IsNodeConfigSite(site)) return false;
        if (IsHttp(vodId)) return Uri.TryCreate(vodId.Trim(), UriKind.Absolute, out var uri) &&
                                      uri.AbsolutePath.TrimEnd('/').EndsWith("/website", StringComparison.OrdinalIgnoreCase);
        return string.Equals(vodId, "config-center", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(vodId, "openInternalWebsite", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsNodeConfigSite(Site site)
    {
        var key = site?.Key ?? "";
        var name = site?.Name ?? "";
        var api = site?.Api ?? "";
        return key.Contains("baseset", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("配置", StringComparison.OrdinalIgnoreCase) ||
               api.Contains("/spider/baseset/", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsLegacyConfigCenter(string title, string remark, string action)
    {
        var text = string.Join('\n', title ?? "", remark ?? "", action ?? "");
        return text.Contains("配置中心", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("扫码配置", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("点击配置", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("openInternalWebsite", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsHttp(string value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    static async Task<NodeConfigReloadResult> ReloadNodeConfigAsync(string baseUrl, string configUrl)
    {
        if (string.IsNullOrWhiteSpace(configUrl) ||
            !string.Equals(NodeRuntime.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(VodConfigService.Instance.Config?.Url, configUrl, StringComparison.OrdinalIgnoreCase))
            return NodeConfigReloadResult.Stop();

        try
        {
            if (!await VodConfigService.Instance.ReloadCurrentAsync(configUrl).ConfigureAwait(false))
                return NodeConfigReloadResult.Stop();
            return NodeConfigReloadResult.Success(NodeSource.GetSnapshotFingerprint(baseUrl));
        }
        catch (Exception e)
        {
            Logger.E("NodeConfigMonitor", "自动重载点播配置失败: " + e.Message);
            return NodeConfigReloadResult.Retry();
        }
    }

    static async Task<ActionRouteResult> OpenAsync(string value, string error)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ActionRouteResult.Handled(error);
        try
        {
            return await Launcher.LaunchUriAsync(uri)
                ? ActionRouteResult.Handled()
                : ActionRouteResult.Handled(error);
        }
        catch (Exception e) { return ActionRouteResult.Handled(error + "：" + e.Message); }
    }
}

public readonly record struct ActionRouteResult(bool Consumed, string Message)
{
    public static ActionRouteResult NotHandled() => new(false, "");
    public static ActionRouteResult Handled(string message = "") => new(true, message ?? "");
}
