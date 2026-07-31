using System.Text.Json.Nodes;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;
using TVBoxForWindows.Net;
using Jint;

namespace TVBoxForWindows.Live;

/// <summary>Loads standalone playlists and extracts live entries from TVBox/CatPawOpen video configurations.</summary>
public class LiveConfigService
{
    const int MaxDepotDepth = 3;
    bool _syncedToVod;

    public static LiveConfigService Instance { get; } = new();

    public ConfigRecord Config { get; private set; } = new();
    public List<Models.Live> Lives { get; private set; } = new();
    public Models.Live Home { get; private set; } = new();

    public event Action Loaded;

    public void Clear()
    {
        _syncedToVod = false;
        Config = new();
        Lives = new();
        Home = new();
        App.Post(() => Loaded?.Invoke());
    }

    /// <summary>Clears stale lives that came from the previous video config, preserving a manually selected source.</summary>
    public void OnVodWithoutLives()
    {
        if (!_syncedToVod) return;
        var oldUrl = Config.Url;
        _syncedToVod = false;
        Config = new();
        Lives = new();
        Home = new();
        if (string.Equals(Setting.ConfigLive, oldUrl, StringComparison.OrdinalIgnoreCase)) Setting.ConfigLive = "";
        App.Post(() => Loaded?.Invoke());
    }

    /// <summary>Loads TXT/M3U/JSON, a TVBox video config, or a CatPawOpen subscription.</summary>
    public async Task LoadAsync(ConfigRecord config)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.Url)) throw new Exception("请输入直播源地址");
        var lives = await LoadAddress(config.Url.Trim(), 0, new(StringComparer.OrdinalIgnoreCase));
        if (lives.Count == 0) throw new Exception("地址中没有可用的直播源");

        Apply(config, lives);
        _syncedToVod = false;
        Stores.SaveConfig(config);
        Setting.ConfigLive = config.Url;
        App.Post(() => Loaded?.Invoke());
    }

    /// <summary>Replaces the live list with entries embedded in the active video configuration.</summary>
    public Task ParseFromVod(ConfigRecord vodConfig, JsonNode node)
    {
        var temp = Stores.FindConfig(vodConfig.Url, 1);
        var lives = ExtractLives(node, vodConfig.Url);
        Stores.SaveConfig(temp);
        var selectedUrl = string.IsNullOrEmpty(Config.Url) ? Setting.ConfigLive : Config.Url;
        if (_syncedToVod || string.IsNullOrEmpty(selectedUrl) || selectedUrl == vodConfig.Url)
        {
            Apply(temp, lives);
            _syncedToVod = true;
            Setting.ConfigLive = vodConfig.Url;
            App.Post(() => Loaded?.Invoke());
        }
        return Task.CompletedTask;
    }

    async Task<List<Models.Live>> LoadAddress(string address, int depth, HashSet<string> visited)
    {
        if (depth > MaxDepotDepth) throw new Exception("配置仓库嵌套层级过多");
        var target = UrlUtil.Convert(address);
        if (!visited.Add(target)) throw new Exception("配置地址存在循环引用");

        if (NodeSource.MaybeNode(target))
        {
            var catPaw = await TryLoadCatPawLives(target);
            if (catPaw != null)
            {
                if (catPaw.Count == 0) throw new Exception("CatPawOpen 配置中没有可用的直播源");
                return catPaw;
            }
        }

        var text = await LoadDecoded(target);
        var node = JsonUtil.Parse(text);
        if (node != null)
        {
            if (node is JsonObject depot && depot["urls"] is JsonArray urls)
            {
                Exception failure = null;
                foreach (var item in urls)
                {
                    var entry = ModelJson.Parse<Depot>(item);
                    if (string.IsNullOrWhiteSpace(entry?.Url)) continue;
                    try { return await LoadAddress(Resolve(target, entry.Url), depth + 1, visited); }
                    catch (Exception e) { failure = e; }
                }
                throw failure ?? new Exception("配置仓库中没有可用地址");
            }

            var extracted = ExtractLives(node, target);
            if (extracted.Count > 0) return extracted;
            if (node is JsonArray groups && IsGroupArray(groups)) return FromGroups(groups, target);
            if (node is JsonObject obj && obj["groups"] is JsonArray objectGroups) return FromGroups(objectGroups, target);
            throw new Exception("JSON 配置中没有可用的直播源");
        }

        var live = new Models.Live { Name = UrlUtil.GetName(address), Url = target };
        LiveParser.Text(live, text);
        if (live.Groups.Count == 0) throw new Exception("直播列表格式无法识别");
        return new() { live };
    }

    static async Task<string> LoadDecoded(string target)
    {
        var scheme = UrlUtil.Scheme(target);
        if (scheme is "http" or "https") return await Core.Decoder.GetJson(target, allowPlainText: true);
        var text = await HttpUtil.Load(target);
        if (string.IsNullOrWhiteSpace(text)) throw new Exception("直播源内容为空或无法读取");
        return Core.Decoder.Verify(target, text);
    }

    /// <summary>Reads CatPawOpen's companion config without starting or replacing the active Node runtime.</summary>
    static async Task<List<Models.Live>> TryLoadCatPawLives(string sourceUrl)
    {
        var companion = await NodeSource.TryLoadCompanionAsync(sourceUrl);
        if (companion == null) return null;
        var node = ParseCompanionJs(companion.Value.Script);
        if (node == null) throw new Exception("CatPawOpen 伴随配置无法解析");
        var lives = ExtractLives(node, companion.Value.Url);
        return lives.Count > 0 ? lives : await TryLoadCatPawNodeLives(sourceUrl);
    }

    static JsonNode ParseCompanionJs(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        // Most CatPawOpen companions are esbuild CommonJS; the replacement also accepts simple ESM configs.
        script = script.Replace("export default", "module.exports.default =", StringComparison.Ordinal);
        var engine = new Jint.Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(3))
            .LimitMemory(32_000_000)
            .MaxStatements(250_000));
        engine.Execute("var module = { exports: {} }; var exports = module.exports;");
        engine.Execute(script);
        var json = engine.Evaluate("JSON.stringify(module.exports && (module.exports.default || module.exports))").ToString();
        return JsonUtil.Parse(json);
    }

    /// <summary>Imports live sources exposed by CatPaw's Node service when the companion has no live/lives data.</summary>
    static async Task<List<Models.Live>> TryLoadCatPawNodeLives(string sourceUrl)
    {
        var previousBase = NodeRuntime.BaseUrl;
        var previousVod = VodConfigService.Instance.Config?.Url ?? "";
        var sameAsVod = SameAddress(sourceUrl, previousVod);
        try
        {
            var flat = await NodeSource.TryLoadAsync(sourceUrl);
            if (string.IsNullOrWhiteSpace(flat)) return new();
            var baseUrl = NodeRuntime.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return new();

            var managed = await ReadManagedNodeLives(baseUrl);
            if (managed.Count > 0) return managed;
            return await ReadNodeLiveSites(flat);
        }
        finally
        {
            if (!sameAsVod && !string.IsNullOrWhiteSpace(previousBase) && NodeSource.MaybeNode(previousVod))
            {
                try { await NodeSource.TryLoadAsync(UrlUtil.Convert(previousVod)); }
                catch (Exception e) { Logger.E("LiveConfig", "恢复点播 Node 源失败: " + e.Message); }
            }
            else if (!sameAsVod && !string.IsNullOrWhiteSpace(NodeRuntime.BaseUrl)) NodeRuntime.Shutdown();
        }
    }

    static async Task<List<Models.Live>> ReadManagedNodeLives(string baseUrl)
    {
        foreach (var path in new[] { "/website/zhibo/sources", "/website/live/sources" })
        {
            try
            {
                var rsp = await HttpUtil.Get(baseUrl.TrimEnd('/') + path, timeoutMs: 10000);
                if (rsp.Code is < 200 or >= 300) continue;
                var node = JsonUtil.Parse(rsp.Text());
                var data = node is JsonObject root ? root["data"] ?? root["lives"] ?? root["live"] : node;
                var result = new List<Models.Live>();
                AddLiveNode(data, baseUrl, result);
                if (result.Count > 0) return result;
            }
            catch { }
        }
        return new();
    }

    static async Task<List<Models.Live>> ReadNodeLiveSites(string flat)
    {
        var result = new List<Models.Live>();
        if (JsonUtil.Parse(flat) is not JsonObject root || root["sites"] is not JsonArray sites) return result;
        foreach (var item in sites)
        {
            var site = ModelJson.Parse<Site>(item);
            if (site == null || !IsNodeLiveSite(site)) continue;
            try
            {
                var spider = new NodeSpider { Site = site, Api = site.Api };
                var home = Result.FromJson(await spider.HomeContent(true));
                var live = new Models.Live { Name = site.Name };
                foreach (var type in home.Types.Where(type => !string.IsNullOrWhiteSpace(type.TypeId)))
                {
                    var extend = new Dictionary<string, string>();
                    if (home.Filters.TryGetValue(type.TypeId, out var filters))
                        foreach (var filter in filters)
                            if (!string.IsNullOrWhiteSpace(filter.Key) && !string.IsNullOrWhiteSpace(filter.Init))
                                extend[filter.Key] = filter.Init;

                    Result category;
                    try { category = Result.FromJson(await spider.CategoryContent(type.TypeId, "1", true, extend)); }
                    catch { continue; }
                    var group = LiveGroup.Create(string.IsNullOrWhiteSpace(type.TypeName) ? type.TypeId : type.TypeName);
                    foreach (var vod in category.List.Take(500))
                    {
                        var channel = new LiveChannel { Name = vod.CleanName, Logo = vod.Pic };
                        if (IsMediaAddress(vod.Id)) AddNodeLine(channel, vod.Id, "");
                        else await AddNodeDetail(spider, vod, channel);
                        if (channel.Urls.Count > 0) group.Channel.Add(channel);
                    }
                    if (group.Channel.Count > 0) live.Groups.Add(group);
                }
                if (live.Groups.Count > 0) result.Add(live);
            }
            catch (Exception e) { Logger.D("LiveConfig", $"CatPaw 直播站 [{site.Name}]: {e.Message}"); }
        }
        return result;
    }

    static async Task AddNodeDetail(NodeSpider spider, Vod summary, LiveChannel channel)
    {
        try
        {
            var detail = Result.FromJson(await spider.DetailContent(new() { summary.Id }));
            foreach (var flag in detail.Vod.GetFlags())
            {
                foreach (var episode in flag.Episodes)
                {
                    var url = episode.Url;
                    if (!IsMediaAddress(url))
                    {
                        try
                        {
                            var play = Result.FromJson(await spider.PlayerContent(flag.Flag, url, new()));
                            url = play.RealUrl;
                            foreach (var header in play.Header) channel.Header[header.Key] = header.Value;
                        }
                        catch { continue; }
                    }
                    AddNodeLine(channel, url, flag.Flag);
                }
            }
        }
        catch { }
    }

    static void AddNodeLine(LiveChannel channel, string value, string name)
    {
        value = (value ?? "").Trim();
        if (!IsMediaAddress(value)) return;
        var marker = value.IndexOf(";User-Agent=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            channel.Ua = value[(marker + 12)..].Split(';', 2)[0];
            value = value[..marker];
        }
        if (channel.Urls.Any(url => url.Split('$')[0] == value)) return;
        channel.Urls.Add(string.IsNullOrWhiteSpace(name) ? value : value + "$" + name.Replace('$', ' '));
    }

    static bool IsMediaAddress(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("://", StringComparison.Ordinal);

    static bool IsNodeLiveSite(Site site)
    {
        var segments = (site.Api ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var spider = Array.FindIndex(segments, value => value.Equals("spider", StringComparison.OrdinalIgnoreCase));
        var slug = spider >= 0 && spider + 1 < segments.Length ? segments[spider + 1] : "";
        if (slug.Equals("live", StringComparison.OrdinalIgnoreCase) ||
            slug.Equals("zhibo", StringComparison.OrdinalIgnoreCase) ||
            slug.Contains("iptv", StringComparison.OrdinalIgnoreCase)) return true;

        var key = site.Key ?? "";
        if (key.StartsWith("nodejs_", StringComparison.OrdinalIgnoreCase)) key = key[7..];
        return key.Equals("live", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("zhibo", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("iptv", StringComparison.OrdinalIgnoreCase);
    }

    static bool SameAddress(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(UrlUtil.Convert(first.Trim()), UrlUtil.Convert(second.Trim()), StringComparison.OrdinalIgnoreCase);

    static List<Models.Live> ExtractLives(JsonNode node, string baseUrl)
    {
        var result = new List<Models.Live>();
        if (node is not JsonObject root) return result;

        AddLiveNode(root["lives"], baseUrl, result);
        AddLiveNode(root["live"], baseUrl, result);
        if (root["video"] is JsonObject video)
        {
            AddLiveNode(video["lives"], baseUrl, result);
            AddLiveNode(video["live"], baseUrl, result);
        }
        return result;
    }

    static void AddLiveNode(JsonNode node, string baseUrl, List<Models.Live> result)
    {
        if (node is JsonArray array)
        {
            AddLiveArray(array, baseUrl, result);
            return;
        }
        if (node is JsonValue value)
        {
            AddLive(new Models.Live { Url = Resolve(baseUrl, value.ToString()) }, result);
            return;
        }
        if (node is not JsonObject obj) return;

        if (obj["url"] is JsonArray urls) AddLiveArray(urls, baseUrl, result);
        if (obj["urls"] is JsonArray urlList) AddLiveArray(urlList, baseUrl, result);
        if (obj["lives"] is JsonArray lives) AddLiveArray(lives, baseUrl, result);
        if (obj["sites"] is JsonArray sites) AddLiveArray(sites, baseUrl, result);
        if (obj["url"] is JsonValue) AddLiveObject(obj, baseUrl, result);
    }

    static void AddLiveArray(JsonArray array, string baseUrl, List<Models.Live> result)
    {
        foreach (var item in array)
        {
            if (item is JsonValue value)
            {
                AddLive(new Models.Live { Url = Resolve(baseUrl, value.ToString()) }, result);
            }
            else if (item is JsonObject obj)
            {
                AddLiveObject(obj, baseUrl, result);
            }
        }
    }

    static void AddLiveObject(JsonObject source, string baseUrl, List<Models.Live> result)
    {
        if (False(source["enabled"]) || False(source["enable"])) return;
        var obj = (JsonObject)source.DeepClone();
        if (obj["logo"] == null && obj["logoUrl"] != null) obj["logo"] = obj["logoUrl"]!.DeepClone();
        var live = ModelJson.Parse<Models.Live>(obj);
        if (live == null) return;
        live.Url = Resolve(baseUrl, live.Url);
        live.Api = Resolve(baseUrl, live.Api);
        live.Epg = Resolve(baseUrl, live.Epg);
        live.Logo = Resolve(baseUrl, live.Logo);
        AddLive(live, result);
    }

    static void AddLive(Models.Live live, List<Models.Live> result)
    {
        if (live == null || (string.IsNullOrWhiteSpace(live.Url) &&
                            string.IsNullOrWhiteSpace(live.Api) && live.Groups.Count == 0)) return;
        if (string.IsNullOrWhiteSpace(live.Name)) live.Name = UrlUtil.GetName(live.Url);
        if (string.IsNullOrWhiteSpace(live.Name)) live.Name = "直播源 " + (result.Count + 1);
        if (result.Any(item => item.Name.Equals(live.Name, StringComparison.OrdinalIgnoreCase))) return;
        result.Add(live);
    }

    static List<Models.Live> FromGroups(JsonArray array, string sourceUrl)
    {
        var normalized = new JsonArray();
        foreach (var item in array)
        {
            if (item is not JsonObject source) continue;
            var group = (JsonObject)source.DeepClone();
            if (group["channel"] == null && group["channels"] != null)
                group["channel"] = group["channels"]!.DeepClone();
            normalized.Add(group);
        }
        var groups = ModelJson.Parse<List<LiveGroup>>(normalized) ?? new();
        groups.RemoveAll(group => group?.Channel == null || group.Channel.Count == 0);
        if (groups.Count == 0) throw new Exception("JSON 直播列表没有可用频道");
        return new() { new Models.Live { Name = UrlUtil.GetName(sourceUrl), Url = sourceUrl, Groups = groups } };
    }

    static bool IsGroupArray(JsonArray array) => array.Any(item =>
        item is JsonObject obj && (obj["channel"] is JsonArray || obj["channels"] is JsonArray));

    static bool False(JsonNode node)
    {
        if (node == null) return false;
        if (node is JsonValue value && value.TryGetValue<bool>(out var enabled)) return !enabled;
        return node.ToString() is "0" or "false" or "False";
    }

    static string Resolve(string baseUrl, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
        return UrlUtil.Resolve(baseUrl, value);
    }

    void Apply(ConfigRecord config, List<Models.Live> lives)
    {
        Config = config;
        Lives = lives ?? new();
        var boot = Lives.LastOrDefault(live => live.Boot);
        Home = Lives.FirstOrDefault(live => live.Name == config.Home) ?? boot ?? Lives.FirstOrDefault() ?? new Models.Live();
    }

    public void SetHome(Models.Live live)
    {
        Home = live;
        Config.Home = live.Name;
        Stores.SaveConfig(Config);
    }

    /// <summary>Loads the selected source's channels, unless they were embedded in JSON.</summary>
    public async Task<Models.Live> GetChannels(Models.Live live)
    {
        if (live.Groups.Count > 0)
        {
            foreach (var group in live.Groups)
                foreach (var channel in group.Channel) { channel.Group = group; channel.Live = live; }
            return live;
        }
        await LiveParser.Parse(live);
        return live;
    }
}
