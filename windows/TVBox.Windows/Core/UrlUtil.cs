namespace TVBoxForWindows.Core;

public static class UrlUtil
{
    public static string Scheme(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        int i = url.IndexOf("://", StringComparison.Ordinal);
        return i < 0 ? "" : url[..i].ToLowerInvariant().Trim();
    }

    public static string Host(string url)
    {
        try { return string.IsNullOrEmpty(url) ? "" : new Uri(FixUri(url)).Host.ToLowerInvariant().Trim(); }
        catch { return ""; }
    }

    static string FixUri(string url) => url.Contains("://") ? url : "file:///" + url.TrimStart('/');

    /// <summary>URI 相对解析（等价 UriUtil.resolve），支持 ./ 与 ../。</summary>
    public static string Resolve(string baseUri, string reference)
    {
        if (string.IsNullOrEmpty(reference)) return baseUri ?? "";
        if (reference.Contains("://") || string.IsNullOrEmpty(baseUri) || !baseUri.Contains("://")) return reference;
        try { return new Uri(new Uri(baseUri), reference).AbsoluteUri; }
        catch { return reference; }
    }

    /// <summary>assets:// proxy:// file:// 协议转换为本地服务器地址。</summary>
    public static string Convert(string url)
    {
        var scheme = Scheme(url);
        var prefix = scheme + "://";
        return scheme switch
        {
            "assets" => url.Replace(prefix, TVBoxForWindows.Server.LocalServer.Instance.GetAddress("/")),
            "proxy" => url.Replace(prefix, TVBoxForWindows.Server.LocalServer.Instance.GetAddress("/proxy?")),
            "file" => TVBoxForWindows.Server.LocalServer.Instance.GetAddress("/file/") + Uri.EscapeDataString(url[prefix.Length..]).Replace("%2F", "/").Replace("%5C", "/"),
            _ => url,
        };
    }

    public static string GetName(string url)
    {
        try
        {
            var uri = new Uri(FixUri(url));
            var path = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : "";
            return !string.IsNullOrEmpty(path) ? path : !string.IsNullOrEmpty(uri.Host) ? uri.Host : url;
        }
        catch { return url; }
    }

    public static string FixHeader(string key)
    {
        if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) return "User-Agent";
        if (key.Equals("referer", StringComparison.OrdinalIgnoreCase)) return "Referer";
        if (key.Equals("cookie", StringComparison.OrdinalIgnoreCase)) return "Cookie";
        return key;
    }
}
