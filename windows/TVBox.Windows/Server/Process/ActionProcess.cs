using System.Text;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Live;
using TVBoxForWindows.Models;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Server.Process;

/// <summary>/action 端点（移植自 server/process/Action.java）：全部 do= 子命令只触发事件后立即返回 OK。</summary>
public class ActionProcess : IProcess
{
    const string Tag = "ActionProcess";

    public bool IsRequest(ServerRequest req) => req.Path.StartsWith("/action");

    public Task<ServerResponse> Handle(ServerRequest req)
    {
        try
        {
            var action = req.Params.GetValueOrDefault("do");
            if (!string.IsNullOrEmpty(action)) DoJob(action, req.Params);
        }
        catch (Exception e) { Logger.E(Tag, e.Message); } // /action 永远回 200 OK
        return Task.FromResult(ServerResponse.Ok());
    }

    void DoJob(string action, Dictionary<string, string> p)
    {
        switch (action)
        {
            case "file": OnFile(p); break;
            case "push": OnPush(p); break;
            case "cast": OnCast(p); break;
            case "sync": OnSync(p); break;
            case "search": OnSearch(p); break;
            case "setting": OnSetting(p); break;
            case "refresh": OnRefresh(p); break;
            case "control": OnControl(p); break;
            case "danmaku": OnDanmaku(p); break;
        }
    }

    // ---------- file ----------

    void OnFile(Dictionary<string, string> p)
    {
        var path = p.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(path)) return;
        var local = ToLocal(path);
        if (path.EndsWith(".apk")) Logger.E(Tag, "Windows 不支持安装 APK：" + path);
        else if (path.EndsWith(".srt") || path.EndsWith(".ssa") || path.EndsWith(".ass")) LocalServer.Instance.RaiseSubtitle(new Sub { Url = local, Name = UrlUtil.GetName(local) });
        else OnSetting(new Dictionary<string, string> { ["text"] = local }); // 其他文件当配置载入（等价 ServerEvent.setting）
    }

    /// <summary>等价 Path.local()：绝对路径原样，相对路径挂到 AppPaths.Local。</summary>
    static string ToLocal(string path)
    {
        path = (path ?? "").Replace('\\', '/');
        if (path.Length >= 2 && path[1] == ':') return path;               // C:/xxx 磁盘绝对路径
        if (path.StartsWith("file://")) return path["file://".Length..];
        return Path.Combine(AppPaths.Local, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    // ---------- push / search / danmaku ----------

    void OnPush(Dictionary<string, string> p)
    {
        var url = p.GetValueOrDefault("url");
        if (!string.IsNullOrEmpty(url)) LocalServer.Instance.RaisePush(url);
    }

    void OnSearch(Dictionary<string, string> p)
    {
        // 契约未定义搜索事件：记录日志（后续 UI 模块可扩展）
        var word = p.GetValueOrDefault("word");
        if (!string.IsNullOrEmpty(word)) Logger.D(Tag, "收到搜索指令（暂未接线）：" + word);
    }

    void OnDanmaku(Dictionary<string, string> p)
    {
        var text = p.GetValueOrDefault("text");
        if (!string.IsNullOrEmpty(text)) LocalServer.Instance.RaiseDanmaku(text);
    }

    void OnControl(Dictionary<string, string> p)
    {
        // Windows 版无 PlaybackService 契约：记录日志（后续播放器模块可经 MediaStateProvider 同级机制扩展）
        Logger.D(Tag, "收到播放控制指令（暂未接线）：" + p.GetValueOrDefault("type"));
    }

    // ---------- setting ----------

    void OnSetting(Dictionary<string, string> p)
    {
        var text = p.GetValueOrDefault("text");
        var name = p.GetValueOrDefault("name");
        if (string.IsNullOrEmpty(text)) return;
        // 纯 JSON 内容先落盘到 Local 目录再作为地址载入
        if (JsonUtil.IsObj(text))
        {
            var file = Path.Combine(AppPaths.Local, "setting_config.json");
            try { File.WriteAllText(file, text); } catch { }
            text = file;
        }
        var config = Stores.FindConfig(text, 0);
        if (!string.IsNullOrEmpty(name)) config.Name = name;
        LocalServer.Instance.RaiseRefreshConfig(config);
    }

    // ---------- refresh ----------

    void OnRefresh(Dictionary<string, string> p)
    {
        var type = p.GetValueOrDefault("type");
        var path = p.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(type)) return;
        switch (type)
        {
            case "live":
                var live = LiveConfigService.Instance.Config;
                if (string.IsNullOrEmpty(live?.Url) && !string.IsNullOrEmpty(Setting.ConfigLive))
                    live = Stores.FindConfig(Setting.ConfigLive, 1);
                if (!string.IsNullOrEmpty(live?.Url)) LocalServer.Instance.RaiseRefreshConfig(live);
                break;
            case "vod":
            case "detail":
            case "player":
            case "category": LocalServer.Instance.RaiseRefreshConfig(VodConfigService.Instance.Config); break;
            case "danmaku": if (!string.IsNullOrEmpty(path)) LocalServer.Instance.RaiseDanmaku(path); break;
            case "subtitle": if (!string.IsNullOrEmpty(path)) LocalServer.Instance.RaiseSubtitle(new Sub { Url = path, Name = UrlUtil.GetName(path) }); break;
        }
    }

    // ---------- cast ----------

    void OnCast(Dictionary<string, string> p)
    {
        var config = ModelJson.Parse<ConfigRecord>(p.GetValueOrDefault("config") ?? "");
        var history = p.GetValueOrDefault("history") ?? "";
        if (config == null || string.IsNullOrEmpty(config.Url)) return;
        Stores.FindConfig(config.Url, config.Type); // 等价 Config.find：登记到本地配置表
        LocalServer.Instance.RaiseCast(config.Url, history);
    }

    // ---------- sync（规格 §5：mode 0=双向 1=仅接收 2=仅发送）----------

    void OnSync(Dictionary<string, string> p)
    {
        var type = p.GetValueOrDefault("type");
        var force = p.GetValueOrDefault("force") == "true";
        var mode = p.GetValueOrDefault("mode");
        if (string.IsNullOrEmpty(mode)) mode = "0";
        if (p.ContainsKey("device") && mode is "0" or "2")
        {
            var device = ModelJson.Parse<Device>(p["device"]);
            if (device != null && !string.IsNullOrEmpty(device.Ip))
            {
                if (type == "history") SendHistory(device, p);
                else if (type == "keep") SendKeep(device);
            }
        }
        if (mode is "0" or "1")
        {
            if (type == "history") SyncHistory(p, force);
            else if (type == "keep") SyncKeep(p, force);
        }
    }

    /// <summary>向对端回发（mode=0，对端不带 device 参数故只走接收分支，不会回弹）。</summary>
    static void PostForm(Device device, string type, Dictionary<string, string> form)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(string.Join('&', form.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? ""))));
                await HttpUtil.Execute("POST", device.Ip + "/action?do=sync&mode=0&type=" + type, null, null, body, "application/x-www-form-urlencoded", true, 5000);
            }
            catch (Exception e) { Logger.E(Tag, "sync 发送失败：" + e.Message); }
        });
    }

    void SendHistory(Device device, Dictionary<string, string> p)
    {
        try
        {
            var config = FindConfig(p.GetValueOrDefault("config"));
            if (config == null || string.IsNullOrEmpty(config.Url)) config = VodConfigService.Instance.Config;
            if (config == null || string.IsNullOrEmpty(config.Url)) return;
            PostForm(device, "history", new Dictionary<string, string>
            {
                ["config"] = JsonUtil.Serialize(config),
                ["targets"] = JsonUtil.Serialize(Stores.GetHistories(config.Id)),
            });
        }
        catch (Exception e) { Logger.E(Tag, e.Message); }
    }

    void SendKeep(Device device)
    {
        try
        {
            PostForm(device, "keep", new Dictionary<string, string>
            {
                ["targets"] = JsonUtil.Serialize(Stores.Keeps.Where(k => k.Type == 0).ToList()),
                ["configs"] = JsonUtil.Serialize(Stores.GetConfigs(0)),
            });
        }
        catch (Exception e) { Logger.E(Tag, e.Message); }
    }

    /// <summary>把请求里的 Config JSON 映射到本地配置表（等价 Config.find(Config.objectFrom(...))）。</summary>
    static ConfigRecord FindConfig(string json)
    {
        var item = ModelJson.Parse<ConfigRecord>(json ?? "");
        if (item == null || string.IsNullOrEmpty(item.Url)) return null;
        var config = Stores.FindConfig(item.Url, 0);
        if (string.IsNullOrEmpty(config.Name)) config.Name = item.Name;
        return config;
    }

    void SyncHistory(Dictionary<string, string> p, bool force)
    {
        var config = FindConfig(p.GetValueOrDefault("config"));
        if (config == null) return; // url 为空则丢弃不处理
        var targets = ModelJson.Parse<List<History>>(p.GetValueOrDefault("targets") ?? "") ?? new();
        if (config.Url == VodConfigService.Instance.Config?.Url)
        {
            MergeHistory(config.Id, targets, force);
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try { await VodConfigService.Instance.LoadAsync(config); MergeHistory(config.Id, targets, force); }
                catch (Exception e) { Logger.E(Tag, "sync history 载入配置失败：" + e.Message); }
            });
        }
    }

    static void MergeHistory(int cid, List<History> targets, bool force)
    {
        if (force) Stores.DeleteHistories(cid);
        foreach (var target in targets)
        {
            if (string.IsNullOrEmpty(target.Key)) continue;
            var mine = Stores.FindHistory(cid, target.Key);
            if (mine == null || target.CreateTime > mine.CreateTime) { target.Cid = cid; Stores.SaveHistory(target); }
        }
    }

    void SyncKeep(Dictionary<string, string> p, bool force)
    {
        var targets = ModelJson.Parse<List<Keep>>(p.GetValueOrDefault("targets") ?? "") ?? new();
        var configs = ModelJson.Parse<List<ConfigRecord>>(p.GetValueOrDefault("configs") ?? "") ?? new();
        if (string.IsNullOrEmpty(VodConfigService.Instance.Config?.Url) && configs.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await VodConfigService.Instance.LoadAsync(Stores.FindConfig(configs[0].Url, 0)); MergeKeep(configs, targets, force); }
                catch (Exception e) { Logger.E(Tag, "sync keep 载入配置失败：" + e.Message); }
            });
        }
        else
        {
            MergeKeep(configs, targets, force);
        }
    }

    /// <summary>合并收藏：按发送方 cid → 发送方配置 url → 本地配置 cid 重新映射（等价 Keep.sync）。</summary>
    static void MergeKeep(List<ConfigRecord> configs, List<Keep> targets, bool force)
    {
        if (force) foreach (var keep in Stores.Keeps.ToList()) Stores.DeleteKeep(keep.Cid, keep.Key);
        foreach (var target in targets)
        {
            if (string.IsNullOrEmpty(target.Key)) continue;
            var sender = configs.FirstOrDefault(c => c.Id == target.Cid && !string.IsNullOrEmpty(c.Url));
            var cid = sender != null ? Stores.FindConfig(sender.Url, 0).Id : VodConfigService.Instance.Config?.Id ?? 0;
            if (Stores.FindKeep(cid, target.Key) == null) { target.Cid = cid; Stores.SaveKeep(target); }
        }
    }
}
