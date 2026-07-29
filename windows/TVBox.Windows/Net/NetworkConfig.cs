using System.Text.RegularExpressions;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Net;

/// <summary>承载配置中的 hosts / proxy / ads / headers 规则（等价 OkHttp 拦截器所需状态）。</summary>
public static class NetworkConfig
{
    static readonly object Lock = new();
    static List<(string pattern, string target)> _hosts = new();
    static List<ProxyRule> _proxies = new();
    static List<string> _ads = new();
    static List<HeaderRule> _headers = new();
    public static Models.Doh Doh { get; set; }

    public static void Clear()
    {
        lock (Lock) { _hosts = new(); _proxies = new(); _ads = new(); _headers = new(); }
    }

    public static void SetHosts(List<string> hosts)
    {
        var list = new List<(string, string)>();
        foreach (var line in hosts ?? new())
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0) list.Add((parts[0].Trim(), parts[1].Trim()));
        }
        lock (Lock) _hosts = list;
    }

    public static void SetProxies(List<ProxyRule> proxies) { lock (Lock) _proxies = proxies ?? new(); }
    public static void SetAds(List<string> ads) { lock (Lock) _ads = ads ?? new(); }
    public static void SetHeaders(List<HeaderRule> headers) { lock (Lock) _headers = headers ?? new(); }

    /// <summary>hosts 覆写：支持 * 通配符，返回替换后的主机名（可能是域名或 IP），无匹配返回 null。</summary>
    public static string RewriteHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        lock (Lock)
        {
            foreach (var (pattern, target) in _hosts)
            {
                if (pattern.Contains('*'))
                {
                    var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                    if (Regex.IsMatch(host, regex, RegexOptions.IgnoreCase)) return target;
                }
                else if (pattern.Equals(host, StringComparison.OrdinalIgnoreCase)) return target;
            }
        }
        return null;
    }

    /// <summary>依 host 正则规则选择代理 URL（配置代理优先，其次全局手动代理设置）。</summary>
    public static string GetProxyFor(string host)
    {
        if (!string.IsNullOrEmpty(host))
            lock (Lock)
            {
                foreach (var rule in _proxies)
                    foreach (var pattern in rule.Hosts ?? new())
                        if (ContainOrMatch(host, pattern))
                            return rule.Urls?.FirstOrDefault();
            }
        var manual = Core.Setting.Proxy;
        return string.IsNullOrEmpty(manual) ? null : manual;
    }

    public static bool IsAd(string host, string url)
    {
        lock (Lock)
        {
            foreach (var ad in _ads)
                if (ContainOrMatch(host, ad) || (url != null && url.Contains(ad))) return true;
        }
        return false;
    }

    public static Dictionary<string, string> GetInjectHeaders(string host)
    {
        lock (Lock)
        {
            foreach (var rule in _headers)
                if (!string.IsNullOrEmpty(rule.Host) && ContainOrMatch(host, rule.Host)) return rule.Header ?? new();
        }
        return null;
    }

    public static bool ContainOrMatch(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return false;
        if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase); } catch { return false; }
    }
}
