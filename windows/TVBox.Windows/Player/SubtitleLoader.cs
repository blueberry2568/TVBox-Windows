using System.Security.Cryptography;
using System.Text;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Player;

/// <summary>外挂字幕加载：下载 SRT/ASS/VTT 到 cache/subs/（md5 文件名+原扩展名），返回本地路径供 Flyleaf 打开。</summary>
public static class SubtitleLoader
{
    const string TAG = "SubtitleLoader";

    /// <summary>下载字幕到本地缓存，返回本地路径；本地路径直接返回；失败返回空串。</summary>
    public static async Task<string> Fetch(Models.Sub sub)
    {
        try
        {
            var url = sub?.Url ?? "";
            if (url.Length == 0) return "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return File.Exists(url) ? url : "";
            var dir = Path.Combine(AppPaths.Cache, "subs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Md5(url) + Ext(sub));
            if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
            var res = await Net.HttpUtil.Get(url, null, null, Setting.SiteTimeout);
            if (res.Code != 200 || res.Body.Length == 0) return "";
            await File.WriteAllBytesAsync(file, res.Body);
            return file;
        }
        catch (Exception e) { Logger.E(TAG, "字幕下载失败: " + e.Message); return ""; }
    }

    /// <summary>扩展名：优先 format 字段，其次 URL 后缀，默认 .srt。</summary>
    static string Ext(Models.Sub sub)
    {
        var format = (sub.Format ?? "").ToLowerInvariant();
        if (format.Contains("ass")) return ".ass";
        if (format.Contains("ssa")) return ".ssa";
        if (format.Contains("vtt")) return ".vtt";
        if (format.Contains("ttml")) return ".xml";
        try
        {
            var ext = Path.GetExtension(new Uri(sub.Url).AbsolutePath).ToLowerInvariant();
            if (ext is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" or ".txt") return ext;
        }
        catch { }
        return ".srt";
    }

    static string Md5(string text)
    {
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
