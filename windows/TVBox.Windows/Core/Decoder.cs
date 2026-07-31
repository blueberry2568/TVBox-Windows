using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TVBoxForWindows.Core;

/// <summary>配置解码（移植自 Decoder.java）：明文 JSON / "**" Base64 / "2423" 开头 hex AES-CBC，并修正相对路径。</summary>
public static class Decoder
{
    static readonly Regex JsUri = new("\"(\\.|\\.\\.)/(.?|.+?)\\.js\\?(.?|.+?)\"");
    static readonly Regex B64Marker = new("[A-Za-z0-9]{8}\\*\\*");

    public static async Task<string> GetJson(string url, bool allowPlainText = false)
    {
        var snapshot = SnapshotPath(url);
        try
        {
            var res = await Net.HttpUtil.Get(url, timeoutMs: 30000);
            if (res.Code is < 200 or >= 300)
                throw new Exception("配置下载失败：HTTP " + res.Code);
            var finalUrl = res.FinalUrl.Contains('?') == url.Contains('?') ? res.FinalUrl : url;
            var json = Verify(finalUrl, res.Text());
            if (!allowPlainText && !JsonUtil.IsObj(json)) throw new Exception("配置解析失败");
            if (IsCacheable(json, allowPlainText)) DurableJsonFile.Write(snapshot, json);
            return json;
        }
        catch (Exception networkError)
        {
            try
            {
                var cached = DurableJsonFile.Read(
                    snapshot,
                    content => IsCacheable(content, allowPlainText) ? content : null,
                    () => null);
                if (cached != null)
                {
                    Logger.E("ConfigCache", "配置刷新失败，已使用本地快照：" + networkError.Message);
                    return cached;
                }
            }
            catch (Exception cacheError)
            {
                Logger.E("ConfigCache", "读取配置快照失败：" + cacheError.Message);
            }

            throw;
        }
    }

    static string SnapshotPath(string url)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((url ?? "").Trim())))
            .ToLowerInvariant()[..24];
        return Path.Combine(AppPaths.Cache, "source-" + key + ".txt");
    }

    static bool IsCacheable(string content, bool allowPlainText)
    {
        if (JsonUtil.IsObj(content)) return true;
        if (!allowPlainText || string.IsNullOrWhiteSpace(content)) return false;

        // Only persist recognizable live playlists. This prevents a HTTP 200 HTML
        // error page from replacing a previously valid offline source.
        if (content.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("://", StringComparison.Ordinal)) return true;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            var comma = line.IndexOf(',');
            if (comma > 0 && line[(comma + 1)..].Contains("://", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static string Verify(string url, string data)
    {
        if (string.IsNullOrEmpty(data)) throw new Exception("配置内容为空");
        if (JsonUtil.IsObj(data)) return Fix(url, data);
        if (data.Contains("**")) data = Base64(data);
        if (data.StartsWith("2423")) data = Cbc(Regex.Replace(data, "\\s+", ""));
        return Fix(url, data);
    }

    static string Fix(string url, string data)
    {
        foreach (Match m in JsUri.Matches(data).ToArray()) data = Replace(url, data, m.Value);
        if (data.Contains("../")) data = data.Replace("../", UrlUtil.Resolve(url, "../"));
        if (data.Contains("./")) data = data.Replace("./", UrlUtil.Resolve(url, "./"));
        if (data.Contains("__JS1__")) data = data.Replace("__JS1__", "./");
        if (data.Contains("__JS2__")) data = data.Replace("__JS2__", "../");
        return data;
    }

    static string Replace(string url, string data, string ext)
    {
        var t = ext.Replace("\"./", "\"" + UrlUtil.Resolve(url, "./"));
        t = t.Replace("\"../", "\"" + UrlUtil.Resolve(url, "../"));
        t = t.Replace("./", "__JS1__").Replace("../", "__JS2__");
        return data.Replace(ext, t);
    }

    static string Cbc(string data)
    {
        var decode = Encoding.UTF8.GetString(Hex2Byte(data)).ToLowerInvariant();
        var key = PadEnd(decode[(decode.IndexOf("$#", StringComparison.Ordinal) + 2)..decode.IndexOf("#$", StringComparison.Ordinal)]);
        var iv = PadEnd(decode[^13..]);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Encoding.UTF8.GetBytes(iv);
        var payload = data[(data.IndexOf("2324", StringComparison.Ordinal) + 4)..^26];
        var decrypted = aes.CreateDecryptor().TransformFinalBlock(Hex2Byte(payload), 0, Hex2Byte(payload).Length);
        return Encoding.UTF8.GetString(decrypted);
    }

    static string Base64(string data)
    {
        var extract = Extract(data);
        if (extract.Length == 0) return data;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(extract)); } catch { return data; }
    }

    static string Extract(string data)
    {
        var m = B64Marker.Match(data);
        return m.Success ? data[(data.IndexOf(m.Value, StringComparison.Ordinal) + 10)..] : "";
    }

    static string PadEnd(string key) => key + "0000000000000000"[key.Length..];

    public static byte[] Hex2Byte(string hex)
    {
        hex = hex.Trim();
        if (hex.Length % 2 != 0) hex = hex[..^1];
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
