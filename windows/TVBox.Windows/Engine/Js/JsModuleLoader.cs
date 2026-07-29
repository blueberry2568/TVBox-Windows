using Jint.Runtime.Modules;
using TVBoxForWindows.Core;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Engine.Js;

/// <summary>ES Module 加载器（移植自 quickjs/utils/Module.java + Spider.moduleNormalizeName）：
/// import 说明符按 URL 相对解析（相对路径相对当前模块，即 spider 的 api URL）；
/// 源码获取：http(s):// 下载（磁盘缓存 AppPaths.Js/md5(url)，下载失败读缓存兜底）、
/// assets:// 读内置资源、lib/ 前缀读内置 js/lib 目录；内存 LRU(50) 跨实例共享。</summary>
public class JsModuleLoader : IModuleLoader
{
    const string TAG = "JsModuleLoader";
    const int MaxCache = 50;

    // ---- 内存 LRU 缓存（等价 Module.java 单例 LruCache(50)，键=完整模块名）----
    static readonly object Lock = new();
    static readonly Dictionary<string, string> Cache = new();
    static readonly LinkedList<string> Order = new();

    /// <summary>说明符规范化（等价 UriUtil.resolve）：绝对（含 ://）原样；相对路径按 base URL 解析；其余（lib/ 等）原样。</summary>
    public ResolvedSpecifier Resolve(string referencingModuleLocation, ModuleRequest moduleRequest)
    {
        var name = UrlUtil.Resolve(referencingModuleLocation, moduleRequest.Specifier);
        return new ResolvedSpecifier(moduleRequest, name, Uri.TryCreate(name, UriKind.Absolute, out var uri) ? uri : null, SpecifierType.Bare);
    }

    public Jint.Runtime.Modules.Module LoadModule(Jint.Engine engine, ResolvedSpecifier resolved)
    {
        var code = Fetch(resolved.Key);
        if (string.IsNullOrEmpty(code)) throw new FileNotFoundException("JS 模块加载失败: " + resolved.Key);
        return ModuleFactory.BuildSourceTextModule(engine, resolved, code);
    }

    /// <summary>取模块源码（等价 Module.fetch）：内存缓存 → http 下载 / assets / lib；未知前缀返回 null。</summary>
    public static string Fetch(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Lock)
        {
            if (Cache.TryGetValue(name, out var hit))
            {
                Order.Remove(name);
                Order.AddFirst(name);
                return hit;
            }
        }
        string content = null;
        if (name.StartsWith("http", StringComparison.OrdinalIgnoreCase)) content = Download(name);
        else if (name.StartsWith("assets", StringComparison.OrdinalIgnoreCase)) content = AppPaths.ReadAsset(name);
        else if (name.StartsWith("lib/", StringComparison.Ordinal)) content = AppPaths.ReadAsset("js/" + name);
        if (!string.IsNullOrEmpty(content)) Put(name, content);
        return content;
    }

    /// <summary>清空内存缓存（配置切换时可选调用；磁盘缓存保留作离线兜底）。</summary>
    public static void ClearCache()
    {
        lock (Lock) { Cache.Clear(); Order.Clear(); }
    }

    // ---- 私有 ----

    /// <summary>下载脚本：成功写盘（文件名=md5(url)）；失败读磁盘缓存兜底。</summary>
    static string Download(string url)
    {
        var file = "";
        try { file = Path.Combine(AppPaths.Js, Md5Hex(url)); } catch { }
        try
        {
            var content = HttpUtil.GetString(url).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(content))
            {
                try { if (file.Length > 0) File.WriteAllText(file, content); } catch { }
                return content;
            }
        }
        catch (Exception e) { Logger.E(TAG, "下载模块失败: " + url + " → " + e.Message); }
        try { return file.Length > 0 && File.Exists(file) ? File.ReadAllText(file) : null; }
        catch { return null; }
    }

    static void Put(string name, string content)
    {
        lock (Lock)
        {
            if (!Cache.ContainsKey(name))
            {
                Order.AddFirst(name);
                if (Order.Count > MaxCache)
                {
                    Cache.Remove(Order.Last.Value);
                    Order.RemoveLast();
                }
            }
            Cache[name] = content;
        }
    }

    static string Md5Hex(string text)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
    }
}
