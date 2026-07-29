using System.Text;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;

namespace TVBoxForWindows.Server.Process;

/// <summary>/proxy 端点（移植自 server/process/Proxy.java + catvod Proxy.java）：
/// params = query ∪ 请求头(小写键) ∪ 上传文件；siteKey/do=js → 站点爬虫 ProxyLocal；do=local → 本地文件。</summary>
public class ProxyProcess : IProcess
{
    const string Tag = "ProxyProcess";
    const string InvalidResponse = "Invalid proxy response";

    public bool IsRequest(ServerRequest req) => req.Path.StartsWith("/proxy");

    public async Task<ServerResponse> Handle(ServerRequest req)
    {
        try
        {
            // 参数合并：query + 全部请求头（小写键）+ POST body files
            var query = new Dictionary<string, string>(req.Params);
            foreach (var kv in req.Headers) query[kv.Key] = kv.Value;
            foreach (var kv in req.Files) query[kv.Key] = kv.Value;
            var action = query.GetValueOrDefault("do");
            if (action == "local")
                return req.IsLoopback ? LocalFile(query) : ServerResponse.Error("Local files require loopback access", 403);
            var spider = FindSpider(query);
            if (spider == null) return ServerResponse.Error(InvalidResponse);
            return Convert(await spider.ProxyLocal(query));
        }
        catch (Exception e)
        {
            Logger.E(Tag, e.Message);
            return ServerResponse.Error(string.IsNullOrEmpty(e.Message) ? e.ToString() : e.Message);
        }
    }

    /// <summary>siteKey 定向到该站点爬虫；无 siteKey 时（do=js 等）Windows 版无最近使用记录，返回 null → 500。</summary>
    static Spider FindSpider(Dictionary<string, string> query)
    {
        var siteKey = query.GetValueOrDefault("siteKey");
        return string.IsNullOrEmpty(siteKey) ? null : SpiderLoader.Instance.FindByKey(siteKey);
    }

    /// <summary>rs 约定：[int code, string mime, byte[]|string|Stream body, 可选 IDictionary 附加响应头]。</summary>
    static ServerResponse Convert(object[] rs)
    {
        if (rs == null || rs.Length == 0) return ServerResponse.Error(InvalidResponse);
        if (rs[0] is ServerResponse ready) return ready;
        if (rs.Length < 3) return ServerResponse.Error(InvalidResponse);
        var res = new ServerResponse { Code = ToCode(rs[0]), Mime = rs[1]?.ToString() ?? "application/octet-stream" };
        switch (rs[2])
        {
            case byte[] bytes: res.Body = bytes; break;
            case Stream stream: res.Stream = stream; break; // chunked
            case string text: res.Body = Encoding.UTF8.GetBytes(text); break;
            case null: res.Body = Array.Empty<byte>(); break;
            default: res.Body = Encoding.UTF8.GetBytes(rs[2].ToString()); break;
        }
        if (rs.Length > 3 && rs[3] is System.Collections.IDictionary headers)
            foreach (System.Collections.DictionaryEntry entry in headers)
                if (entry.Key != null) res.Headers[entry.Key.ToString()] = entry.Value?.ToString() ?? "";
        return res;
    }

    /// <summary>状态码规整：非 100-599 一律 500（等价 Proxy.toStatus）。</summary>
    static int ToCode(object value)
    {
        try
        {
            var code = System.Convert.ToInt32(value);
            return code is >= 100 and <= 599 ? code : 500;
        }
        catch { return 500; }
    }

    /// <summary>do=local：直接回本地文件（等价 catvod Proxy 的 local 分支，Windows 无 jar 故在此内置）。</summary>
    static ServerResponse LocalFile(Dictionary<string, string> query)
    {
        var path = query.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(path)) path = query.GetValueOrDefault("url");
        if (string.IsNullOrEmpty(path)) return ServerResponse.Error(InvalidResponse);
        path = path.Replace("file://", "").Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppPaths.Local, path.TrimStart(Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return ServerResponse.Error("File not found: " + path);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new ServerResponse { Code = 200, Mime = LocalServer.GetMime(path), Stream = fs, StreamLength = fs.Length };
    }
}
