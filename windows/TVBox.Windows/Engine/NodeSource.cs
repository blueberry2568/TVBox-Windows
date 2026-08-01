using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TVBoxForWindows.Core;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Engine;

/// <summary>Downloads and starts CatPawOpen Node sources, then flattens their /config response.</summary>
public static class NodeSource
{
    const string Tag = "NodeSource";
    static readonly object SnapshotSync = new();
    static string _snapshotBaseUrl;
    static string _snapshotFingerprint;

    public static bool MaybeNode(string url) =>
        HasSuffix(url, ".js.md5") || HasSuffix(url, ".js");

    internal static Task<bool> IsCurrentRuntimeHealthyAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var paths = GetCachedPaths(url);
        return paths == null
            ? Task.FromResult(false)
            : NodeRuntime.IsCurrentSourceHealthyAsync(
                paths.Value.ScriptPath,
                paths.Value.ConfigPath,
                cancellationToken);
    }

    internal static bool IsCurrentRuntimeSource(string url)
    {
        var paths = GetCachedPaths(url);
        return paths != null &&
               NodeRuntime.MatchesCurrentSource(paths.Value.ScriptPath, paths.Value.ConfigPath);
    }

    // Bare .js URLs can also point to ordinary Jint spiders. Explicit .js.md5 subscriptions
    // are trusted as CatPawOpen only after their advertised checksum has been verified.
    static bool LooksLikeNode(string js) =>
        !string.IsNullOrEmpty(js) &&
        (js.Contains("catServerFactory") || js.Contains("fastify")) &&
        (js.Contains("require(\"node:") || js.Contains("require('node:") || js.Contains("node:crypto"));

    /// <summary>Returns a flattened TVBox config for a CatPawOpen source, or null for a non-Node .js URL.</summary>
    public static async Task<string> TryLoadAsync(string url)
    {
        if (!MaybeNode(url)) return null;
        try
        {
            var hasManifest = HasSuffix(url, ".js.md5");
            var jsUrl = hasManifest ? ReplaceSuffix(url, ".js.md5", ".js") : url;
            var sourceDir = Path.Combine(AppPaths.Node, "source-" + SourceKey(jsUrl));
            Directory.CreateDirectory(sourceDir);

            var scriptPath = Path.Combine(sourceDir, "index.js");
            var scriptMd5 = hasManifest
                ? await ReadMd5WithCacheFallbackAsync(url, scriptPath, "订阅校验文件")
                : null;
            var scriptBytes = await ReadFileAsync(jsUrl, scriptPath, scriptMd5, "订阅脚本");
            var scriptText = Encoding.UTF8.GetString(scriptBytes);
            if (!hasManifest && !LooksLikeNode(scriptText)) return null;

            var configPath = await LoadCompanionConfigAsync(jsUrl, sourceDir);
            var version = Hash(scriptBytes) + ":" + FileHash(configPath);
            var dataDir = Path.Combine(sourceDir, "data");
            Directory.CreateDirectory(dataDir);

            var baseUrl = await NodeRuntime.StartAsync(scriptPath, configPath, dataDir, version);
            if (baseUrl == null) throw new Exception(NodeRuntime.LastError ?? "Node 源服务不可用");

            var rsp = await HttpUtil.Get(baseUrl + "/config", timeoutMs: 10000);
            if (rsp.Code is < 200 or >= 300)
                throw new Exception("Node 源配置请求失败: HTTP " + rsp.Code);
            var flat = Flatten(rsp.Text(), baseUrl);
            if (flat == null) throw new Exception("Node 源返回的配置无法解析");
            RememberSnapshot(baseUrl, rsp.Body);
            Logger.D(Tag, "CatPawOpen 源加载成功: " + baseUrl);
            return flat;
        }
        catch (Exception e)
        {
            Logger.E(Tag, "CatPawOpen 源加载失败: " + e.Message);
            throw;
        }
    }

    /// <summary>Starts a previously verified CatPawOpen source without blocking on remote MD5 checks.</summary>
    internal static async Task<string> TryLoadCachedAsync(string url)
    {
        if (!MaybeNode(url)) return null;
        try
        {
            var hasManifest = HasSuffix(url, ".js.md5");
            var jsUrl = hasManifest ? ReplaceSuffix(url, ".js.md5", ".js") : url;
            var sourceDir = Path.Combine(AppPaths.Node, "source-" + SourceKey(jsUrl));
            var scriptPath = Path.Combine(sourceDir, "index.js");
            var configPath = Path.Combine(sourceDir, "index.config.js");
            var cachedFiles = await Task.Run(() =>
                (Script: ReadExisting(scriptPath), Config: ReadExisting(configPath)));
            var scriptBytes = cachedFiles.Script;
            var configBytes = cachedFiles.Config;
            if (scriptBytes == null || configBytes == null) return null;

            var scriptText = Encoding.UTF8.GetString(scriptBytes);
            if (!hasManifest && !LooksLikeNode(scriptText)) return null;

            var version = Hash(scriptBytes) + ":" + Hash(configBytes);
            var dataDir = Path.Combine(sourceDir, "data");
            Directory.CreateDirectory(dataDir);
            var baseUrl = await NodeRuntime.StartAsync(scriptPath, configPath, dataDir, version);
            if (baseUrl == null) return null;

            var rsp = await HttpUtil.Get(baseUrl + "/config", timeoutMs: 10000);
            if (rsp.Code is < 200 or >= 300) return null;
            var flat = Flatten(rsp.Text(), baseUrl);
            if (flat == null) return null;
            RememberSnapshot(baseUrl, rsp.Body);
            RefreshSourceCacheInBackground(url);
            Logger.D(Tag, "已从本地缓存恢复 CatPawOpen 源：" + baseUrl);
            return flat;
        }
        catch (Exception error)
        {
            Logger.E(Tag, "本地 CatPawOpen 源恢复失败，将使用在线加载：" + error.Message);
            return null;
        }
    }

    internal readonly record struct CompanionConfig(string Url, string Script);

    internal static Task<CompanionConfig?> TryLoadCachedCompanionAsync(string sourceUrl) =>
        Task.Run(() => TryLoadCachedCompanion(sourceUrl));

    static CompanionConfig? TryLoadCachedCompanion(string sourceUrl)
    {
        if (!MaybeNode(sourceUrl)) return null;
        try
        {
            var hasManifest = HasSuffix(sourceUrl, ".js.md5");
            var jsUrl = hasManifest ? ReplaceSuffix(sourceUrl, ".js.md5", ".js") : sourceUrl;
            var sourceDir = Path.Combine(AppPaths.Node, "source-" + SourceKey(jsUrl));
            var script = ReadExisting(Path.Combine(sourceDir, "index.js"));
            var config = ReadExisting(Path.Combine(sourceDir, "index.config.js"));
            if (config == null || (!hasManifest && (script == null || !LooksLikeNode(Encoding.UTF8.GetString(script)))))
                return null;
            return new CompanionConfig(ReplaceSuffix(jsUrl, ".js", ".config.js"), Encoding.UTF8.GetString(config));
        }
        catch { return null; }
    }

    internal static void RefreshSourceCacheInBackground(string sourceUrl)
    {
        if (!MaybeNode(sourceUrl)) return;
        _ = RefreshSourceCacheAsync(sourceUrl);
    }

    static async Task RefreshSourceCacheAsync(string sourceUrl)
    {
        try
        {
            var hasManifest = HasSuffix(sourceUrl, ".js.md5");
            var jsUrl = hasManifest ? ReplaceSuffix(sourceUrl, ".js.md5", ".js") : sourceUrl;
            var sourceDir = Path.Combine(AppPaths.Node, "source-" + SourceKey(jsUrl));
            Directory.CreateDirectory(sourceDir);
            var scriptPath = Path.Combine(sourceDir, "index.js");
            var scriptMd5 = hasManifest ? await ReadMd5Async(sourceUrl, "订阅校验文件") : null;
            await ReadFileAsync(jsUrl, scriptPath, scriptMd5, "订阅脚本");
            await LoadCompanionConfigAsync(jsUrl, sourceDir);
        }
        catch (Exception error)
        {
            Logger.E(Tag, "后台校验 CatPawOpen 源失败：" + error.Message);
        }
    }

    /// <summary>
    /// Loads a CatPawOpen companion config through the same verified on-disk cache
    /// used by the Node source loader. Bare .js URLs remain optional because they
    /// can also be ordinary spider scripts.
    /// </summary>
    internal static async Task<CompanionConfig?> TryLoadCompanionAsync(string sourceUrl)
    {
        if (!MaybeNode(sourceUrl)) return null;
        var hasManifest = HasSuffix(sourceUrl, ".js.md5");
        var jsUrl = hasManifest ? ReplaceSuffix(sourceUrl, ".js.md5", ".js") : sourceUrl;
        var sourceDir = Path.Combine(AppPaths.Node, "source-" + SourceKey(jsUrl));
        Directory.CreateDirectory(sourceDir);
        try
        {
            var path = await LoadCompanionConfigAsync(jsUrl, sourceDir);
            var bytes = ReadExisting(path);
            if (bytes == null) return null;
            return new CompanionConfig(ReplaceSuffix(jsUrl, ".js", ".config.js"), Encoding.UTF8.GetString(bytes));
        }
        catch when (!hasManifest)
        {
            return null;
        }
    }

    static async Task<string> LoadCompanionConfigAsync(string jsUrl, string sourceDir)
    {
        var configUrl = ReplaceSuffix(jsUrl, ".js", ".config.js");
        var md5Url = ReplaceSuffix(configUrl, ".js", ".js.md5");
        var path = Path.Combine(sourceDir, "index.config.js");
        var md5 = await ReadMd5WithCacheFallbackAsync(md5Url, path, "伴随配置校验文件");
        await ReadFileAsync(configUrl, path, md5, "伴随配置");
        return path;
    }

    static async Task<string> ReadMd5WithCacheFallbackAsync(string url, string cachedPath, string label)
    {
        try { return await ReadMd5Async(url, label); }
        catch (Exception error)
        {
            var cached = ReadExisting(cachedPath);
            if (cached == null) throw;
            Logger.E(Tag, $"{label}刷新失败，已使用本地缓存：{error.Message}");
            return Hash(cached);
        }
    }

    static async Task<string> ReadMd5Async(string url, string label)
    {
        var rsp = await GetAsync(url, 30000);
        if (rsp.Code is < 200 or >= 300)
            throw new Exception($"{label}下载失败: HTTP {rsp.Code}");

        var value = rsp.Text().Trim()
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (value?.Length != 32 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new Exception(label + "不是有效的 MD5");
        return value.ToLowerInvariant();
    }

    static async Task<byte[]> ReadFileAsync(string url, string path, string expectedMd5, string label)
    {
        var cached = ReadVerified(path, expectedMd5);
        if (cached != null) return cached;

        try
        {
            var rsp = await GetAsync(url, 60000);
            if (rsp.Code is < 200 or >= 300)
                throw new Exception($"{label}下载失败: HTTP {rsp.Code}");
            var bytes = rsp.Body ?? Array.Empty<byte>();
            if (bytes.Length == 0) throw new Exception(label + "内容为空");
            if (expectedMd5 != null && !string.Equals(Hash(bytes), expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new Exception(label + " MD5 校验失败");

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes);
                File.Move(temp, path, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            return bytes;
        }
        catch (Exception error)
        {
            cached = ReadExisting(path);
            if (cached == null) throw;
            Logger.E(Tag, $"{label}刷新失败，已使用本地缓存：{error.Message}");
            return cached;
        }
    }

    static byte[] ReadVerified(string path, string expectedMd5)
    {
        try
        {
            if (expectedMd5 == null || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return string.Equals(Hash(bytes), expectedMd5, StringComparison.OrdinalIgnoreCase) ? bytes : null;
        }
        catch { return null; }
    }

    static byte[] ReadExisting(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == 0 ? null : bytes;
        }
        catch { return null; }
    }

    static async Task<OkResponse> GetAsync(string url, int timeoutMs)
    {
        var target = RequestTarget.Create(url);
        var credentials = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (target.Headers != null) credentials[Origin(target.Url)] = target.Headers;

        for (var redirects = 0; redirects <= 5; redirects++)
        {
            var rsp = await HttpUtil.Execute(
                "GET", target.Url, target.Headers, null, null, null, redirect: false, timeoutMs: timeoutMs);
            if (!IsRedirect(rsp.Code) || !rsp.Headers.TryGetValue("Location", out var locations) || locations.Count == 0)
                return rsp;
            if (redirects == 5) throw new Exception("订阅下载重定向次数过多");

            var nextUri = new Uri(new Uri(target.Url), locations[0]);
            target = RequestTarget.Create(nextUri.AbsoluteUri);
            var origin = Origin(target.Url);
            if (target.Headers != null) credentials[origin] = target.Headers;
            else if (credentials.TryGetValue(origin, out var headers)) target = target with { Headers = headers };
        }
        throw new Exception("订阅下载重定向失败");
    }

    static bool IsRedirect(int code) => code is 301 or 302 or 303 or 307 or 308;

    static string Origin(string value)
    {
        var uri = new Uri(value);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    static string Hash(byte[] bytes) =>
        Convert.ToHexString(MD5.HashData(bytes ?? Array.Empty<byte>())).ToLowerInvariant();

    static void RememberSnapshot(string baseUrl, byte[] bytes)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes ?? Array.Empty<byte>()));
        lock (SnapshotSync)
        {
            _snapshotBaseUrl = baseUrl;
            _snapshotFingerprint = fingerprint;
        }
    }

    internal static string GetSnapshotFingerprint(string baseUrl)
    {
        lock (SnapshotSync)
            return string.Equals(_snapshotBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
                ? _snapshotFingerprint
                : null;
    }

    static string FileHash(string path)
    {
        try { return string.IsNullOrEmpty(path) || !File.Exists(path) ? "none" : Hash(File.ReadAllBytes(path)); }
        catch { return "none"; }
    }

    static string SourceKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    static (string ScriptPath, string ConfigPath)? GetCachedPaths(string url)
    {
        if (!MaybeNode(url)) return null;
        try
        {
            var jsUrl = HasSuffix(url, ".js.md5") ? ReplaceSuffix(url, ".js.md5", ".js") : url;
            var sourceDir = Path.Combine(AppPaths.Node, "source-" + SourceKey(jsUrl));
            return (Path.Combine(sourceDir, "index.js"), Path.Combine(sourceDir, "index.config.js"));
        }
        catch { return null; }
    }

    static bool HasSuffix(string url, string suffix)
    {
        var end = SuffixEnd(url);
        return end >= suffix.Length &&
               string.Equals(url.Substring(end - suffix.Length, suffix.Length), suffix, StringComparison.OrdinalIgnoreCase);
    }

    static string ReplaceSuffix(string url, string suffix, string replacement)
    {
        var end = SuffixEnd(url);
        if (end < suffix.Length ||
            !string.Equals(url.Substring(end - suffix.Length, suffix.Length), suffix, StringComparison.OrdinalIgnoreCase))
            throw new Exception("CatPawOpen 订阅地址格式无效");
        return url[..(end - suffix.Length)] + replacement + url[end..];
    }

    static int SuffixEnd(string value)
    {
        value ??= "";
        var query = value.IndexOf('?');
        var fragment = value.IndexOf('#');
        if (query < 0) return fragment < 0 ? value.Length : fragment;
        if (fragment < 0) return query;
        return Math.Min(query, fragment);
    }

    /// <summary>Flattens {video:{sites:[...]}} and rewrites relative spider APIs to localhost.</summary>
    static string Flatten(string cfgJson, string baseUrl)
    {
        if (JsonUtil.Parse(cfgJson) is not JsonObject root) return null;
        if (root["video"] is not JsonObject video || video["sites"] is not JsonArray sites) return null;

        var outSites = new JsonArray();
        foreach (var item in sites)
        {
            if (item is not JsonObject site) continue;
            if (site["enable"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && !on) continue;
            var api = site["api"]?.ToString() ?? "";
            if (api.StartsWith('/')) api = baseUrl + api;
            var copy = (JsonObject)site.DeepClone();
            copy["type"] = 3;
            copy["api"] = api;
            var searchByName = IsSearchCatalog(site, api);
            var searchable = site["searchable"] != null
                ? Int(site["searchable"], 1)
                : Bool(site["supportSearch"], true) ? 1 : 0;
            if (searchByName) searchable = 0;
            copy["searchByName"] = searchByName;
            copy["searchable"] = searchable;
            copy["quickSearch"] = searchable == 0 ? 0 : Int(site["quickSearch"], searchable);
            copy["filterable"] = Int(site["filterable"], 1);
            outSites.Add(copy);
        }
        var output = new JsonObject { ["sites"] = outSites };
        if (video["danmuSearchUrl"] != null) output["danmaku"] = video["danmuSearchUrl"]!.DeepClone();
        return output.ToJsonString();
    }

    static int Int(JsonNode node, int def)
    {
        if (node == null) return def;
        var text = node.ToString();
        if (int.TryParse(text, out var number)) return number;
        if (bool.TryParse(text, out var enabled)) return enabled ? 1 : 0;
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? (int)value : def;
    }

    static bool Bool(JsonNode node, bool def)
    {
        if (node == null) return def;
        if (bool.TryParse(node.ToString(), out var value)) return value;
        return int.TryParse(node.ToString(), out var number) ? number != 0 : def;
    }

    static bool IsSearchCatalog(JsonObject site, string api)
    {
        if (Bool(site["searchByName"], false)) return true;
        var segments = (api ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var route = Array.FindIndex(segments, s => s.Equals("spider", StringComparison.OrdinalIgnoreCase));
        if (route >= 0 && route + 1 < segments.Length &&
            segments[route + 1] is var slug &&
            (slug.Equals("douban", StringComparison.OrdinalIgnoreCase) ||
             slug.Equals("modou", StringComparison.OrdinalIgnoreCase) ||
             slug.Equals("newdb", StringComparison.OrdinalIgnoreCase))) return true;

        if (Int(site["indexs"], 0) != 1) return false;
        var name = site["name"]?.ToString() ?? "";
        return name.Contains("首页", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("推荐", StringComparison.OrdinalIgnoreCase);
    }

    readonly record struct RequestTarget(string Url, Dictionary<string, string> Headers)
    {
        public static RequestTarget Create(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
                return new(value, null);

            var parts = uri.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(parts[0]);
            var password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            return new(builder.Uri.AbsoluteUri, new(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Basic " + token,
            });
        }
    }
}
