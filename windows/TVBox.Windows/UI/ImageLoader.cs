using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.UI;

/// <summary>海报加载器（契约 §5.4）：解析 @Referer=/@User-Agent= 后缀 → HttpUtil 下载 → 内存 LRU(256) + 磁盘缓存（AppPaths.Cache/img/md5）。失败返回 null，由控件显示占位。</summary>
public static class ImageLoader
{
    const int Capacity = 256;

    static readonly object Gate = new();
    static readonly ConcurrentDictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> Cache = new();
    static readonly LinkedList<(string Key, BitmapImage Image)> Order = new();

    /// <summary>加载海报（必须在 UI 线程调用；BitmapImage 是 UI 对象）。失败返回 null。</summary>
    public static async Task<BitmapImage> Load(string pic)
    {
        if (string.IsNullOrWhiteSpace(pic)) return null;
        pic = NormalizeLocalNodeUrl(pic.Trim());
        if (Cache.TryGetValue(pic, out var node)) { Touch(node); return node.Value.Image; }
        var bytes = await Fetch(pic);
        if (bytes == null || bytes.Length == 0) return null;
        var image = await Decode(bytes);
        if (image != null) Put(pic, image);
        return image;
    }

    // ---------- 下载与磁盘缓存 ----------

    static async Task<byte[]> Fetch(string pic)
    {
        var file = CacheFile(pic);
        try { if (File.Exists(file)) return await File.ReadAllBytesAsync(file); } catch { }
        var (url, headers) = ParsePic(pic);
        try
        {
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var res = await HttpUtil.Get(url, headers);
                if (ShouldRetryWithReferer(res) && !headers.ContainsKey("Referer") &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                    {
                        ["Referer"] = uri.GetLeftPart(UriPartial.Authority) + "/",
                    };
                    res = await HttpUtil.Get(url, headers);
                }
                if (res.Code >= 400 || res.Body == null || res.Body.Length == 0) return null;
                try { await File.WriteAllBytesAsync(file, res.Body); } catch { }
                return res.Body;
            }
            if (File.Exists(url)) return await File.ReadAllBytesAsync(url);
        }
        catch (Exception e) { Logger.E("ImageLoader", url + " " + e.Message); }
        return null;
    }

    static bool ShouldRetryWithReferer(OkResponse response)
    {
        if (response == null || response.Code >= 400 || response.Body == null || response.Body.Length == 0) return true;
        if (!response.Headers.TryGetValue("Content-Type", out var values)) return false;
        var type = values.FirstOrDefault() ?? "";
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeLocalNodeUrl(string value)
    {
        var referer = value.IndexOf("@Referer=", StringComparison.OrdinalIgnoreCase);
        var userAgent = value.IndexOf("@User-Agent=", StringComparison.OrdinalIgnoreCase);
        var marker = referer < 0 ? userAgent : userAgent < 0 ? referer : Math.Min(referer, userAgent);
        var raw = marker < 0 ? value : value[..marker];
        var suffix = marker < 0 ? "" : value[marker..];
        if (!Uri.TryCreate(NodeRuntime.BaseUrl, UriKind.Absolute, out var local) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var source) ||
            !source.IsLoopback ||
            (!source.AbsolutePath.StartsWith("/spider/", StringComparison.OrdinalIgnoreCase) &&
             !source.AbsolutePath.StartsWith("/website", StringComparison.OrdinalIgnoreCase))) return value;
        return NodeRuntime.BaseUrl.TrimEnd('/') + source.PathAndQuery + suffix;
    }

    /// <summary>解析 pic@Referer=xxx@User-Agent=yyy 后缀（顺序任意、大小写不敏感）。</summary>
    public static (string Url, Dictionary<string, string> Headers) ParsePic(string pic)
    {
        var headers = new Dictionary<string, string>();
        var markers = new[] { "@Referer=", "@User-Agent=" };
        int first = int.MaxValue;
        foreach (var m in markers)
        {
            var i = pic.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < first) first = i;
        }
        if (first == int.MaxValue) return (pic, headers);
        var url = pic[..first];
        var rest = pic[first..];
        while (rest.Length > 0)
        {
            string marker = null;
            foreach (var m in markers)
                if (rest.StartsWith(m, StringComparison.OrdinalIgnoreCase)) { marker = m; break; }
            if (marker == null) break;
            var value = rest[marker.Length..];
            int next = int.MaxValue;
            foreach (var m in markers)
            {
                var i = value.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && i < next) next = i;
            }
            var v = next == int.MaxValue ? value : value[..next];
            if (v.Length > 0) headers[marker == "@Referer=" ? "Referer" : "User-Agent"] = v;
            rest = next == int.MaxValue ? "" : value[next..];
        }
        return (url, headers);
    }

    static string CacheFile(string pic)
    {
        var dir = Path.Combine(AppPaths.Cache, "img");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, Md5(pic));
    }

    static string Md5(string text)
        => Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    // ---------- 解码 ----------

    static async Task<BitmapImage> Decode(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch { return null; }
    }

    // ---------- 内存 LRU ----------

    static void Touch(LinkedListNode<(string Key, BitmapImage Image)> node)
    {
        lock (Gate)
        {
            if (node.List != Order) return;
            Order.Remove(node);
            Order.AddFirst(node);
        }
    }

    static void Put(string key, BitmapImage image)
    {
        lock (Gate)
        {
            if (Cache.TryRemove(key, out var exist) && exist.List == Order) Order.Remove(exist);
            var node = new LinkedListNode<(string Key, BitmapImage Image)>((key, image));
            Order.AddFirst(node);
            Cache[key] = node;
            while (Order.Count > Capacity)
            {
                var last = Order.Last;
                Order.RemoveLast();
                Cache.TryRemove(last.Value.Key, out _);
            }
        }
    }
}
