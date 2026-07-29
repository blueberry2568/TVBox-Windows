using TVBoxForWindows.Core;

namespace TVBoxForWindows.Server.Process;

/// <summary>/cache 端点（移植自 server/process/Cache.java）：do=get/set/del，键 = cache_ + (rule_) + key，存 Setting。</summary>
public class CacheProcess : IProcess
{
    public bool IsRequest(ServerRequest req) => req.Path.StartsWith("/cache");

    public Task<ServerResponse> Handle(ServerRequest req)
    {
        var action = req.Params.GetValueOrDefault("do");
        var rule = req.Params.GetValueOrDefault("rule");
        var key = GetKey(rule, req.Params.GetValueOrDefault("key") ?? "");
        if (action == "get") return Task.FromResult(ServerResponse.Ok(Setting.GetString(key)));
        if (action == "set") Setting.Put(key, req.Params.GetValueOrDefault("value") ?? "");
        if (action == "del") Setting.Remove(key);
        return Task.FromResult(ServerResponse.Ok());
    }

    static string GetKey(string rule, string key) => "cache_" + (string.IsNullOrEmpty(rule) ? "" : rule + "_") + key;
}
