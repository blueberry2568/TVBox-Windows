using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;

namespace TVBoxForWindows.UI;

/// <summary>播放地址解析流水线（移植自 PlayerManager.parse + ParseJob.setParse 装配逻辑）：
/// SiteService.PlayerContent → NeedParse?→ParseJob → 产出可播 PlayItem（含 danmaku/subs，Header 删 Range）。</summary>
public static class PlayResolver
{
    /// <summary>点播：flag+集数 → 可播 PlayItem。失败抛异常（消息用于 UI 提示）。</summary>
    public static async Task<Player.PlayItem> Resolve(Models.Site site, string flag, Models.Episode ep, CancellationToken ct)
    {
        var result = await SiteService.PlayerContent(site, flag, ep?.Url ?? "");
        var url = result.UrlBean.V();
        if (string.IsNullOrEmpty(url)) throw new Exception(string.IsNullOrEmpty(result.Msg) ? "获取播放地址失败" : result.Msg);
        var headers = new Dictionary<string, string>(result.Header ?? new(), StringComparer.OrdinalIgnoreCase);
        var flg = string.IsNullOrEmpty(result.Flag) ? flag ?? "" : result.Flag;

        if (result.NeedParse())
        {
            var parse = PickParse(result);
            // parse.setHeader(result.header)：仅当解析器自身 ext.header 为空时生效
            if (parse.Ext?.Header == null || parse.Ext.Header.Count == 0)
                parse.Header = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            parse.Click = !string.IsNullOrEmpty(site?.Click) ? site.Click : result.Click;
            var parsed = await Player.ParseJob.Run(parse, flg, url, ct);
            url = parsed.Url;
            headers = new Dictionary<string, string>(parsed.Headers ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Android PlaySpec.from(Result) 使用 result.getRealUrl()，普通 API 的 playUrl 前缀不能丢。
            url = result.RealUrl;
        }

        headers.Remove("Range"); // PlayerManager：交给引擎前移除 Range
        url = PreparePlaybackRequest(url, headers);
        return new Player.PlayItem
        {
            Url = url,
            Headers = headers,
            Format = result.Format,
            Drm = result.Drm,
            Subs = result.Subs ?? new List<Models.Sub>(),
            Danmaku = result.Danmaku ?? new List<Models.Danmaku>(),
            StartPositionMs = result.Position is > 0 ? result.Position.Value : 0,
        };
    }

    /// <summary>直播频道 → PlayItem（catchup 不在此处理）。parse==1 时走配置默认解析器。</summary>
    public static async Task<Player.PlayItem> ResolveLive(Models.LiveChannel channel, CancellationToken ct)
    {
        var url = channel.CurrentUrl();
        if (string.IsNullOrEmpty(url)) throw new Exception("频道无可用线路");
        var headers = channel.BuildHeaders();
        if (channel.Parse == 1)
        {
            var parse = VodConfigService.Instance.Parse;
            if (parse != null && !parse.IsEmpty)
            {
                var parsed = await Player.ParseJob.Run(parse, "", url, ct);
                url = parsed.Url;
                if (parsed.Headers is { Count: > 0 }) headers = new Dictionary<string, string>(parsed.Headers, StringComparer.OrdinalIgnoreCase);
            }
        }
        headers.Remove("Range");
        url = PreparePlaybackRequest(url, headers);
        return new Player.PlayItem { Url = url, Headers = headers, Format = channel.Format, Drm = channel.Drm };
    }

    /// <summary>对齐 Android PlaySpec.checkUa + OkHttp ResponseInterceptor：转换内部协议、规范化 URI、
    /// 注入配置域名 Header，并在源未给 UA 时使用全局 UA。</summary>
    static string PreparePlaybackRequest(string url, Dictionary<string, string> headers)
    {
        url = UrlUtil.Convert((url ?? "").Trim().Replace("\\", ""));
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = uri.AbsoluteUri;
            var inject = Net.NetworkConfig.GetInjectHeaders(uri.Host);
            if (inject != null)
                foreach (var kv in inject) headers[kv.Key] = kv.Value;
        }
        if (!headers.Keys.Any(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
            headers["User-Agent"] = string.IsNullOrWhiteSpace(Setting.Ua)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/122.0.0.0 Safari/537.36"
                : Setting.Ua;
        return url;
    }

    /// <summary>选择解析器（移植自 ParseJob.setParse + Result.isUseParse）：
    /// isUseParse=配置有 parses 且（playUrl 为空且全局 flags 含 result.flag，或 jx==1）→ 配置默认解析器；
    /// 否则按 playUrl 前缀 json:/parse:/其他（作为 type0 前缀）。</summary>
    static Models.Parse PickParse(Models.Result result)
    {
        var cfg = VodConfigService.Instance;
        var playUrl = result.PlayUrl ?? "";
        bool useParse = cfg.HasParse && ((playUrl.Length == 0 && cfg.Flags.Contains(result.Flag ?? "")) || (result.Jx ?? 0) == 1);
        Models.Parse parse = null;
        if (useParse) parse = cfg.Parse;
        else if (playUrl.StartsWith("json:")) parse = Models.Parse.Get(1, playUrl[5..]);
        else if (playUrl.StartsWith("parse:")) parse = cfg.GetParse(playUrl[6..]);
        parse ??= Models.Parse.Get(0, playUrl);
        return parse;
    }
}
