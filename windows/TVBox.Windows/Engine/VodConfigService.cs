using System.Text.Json.Nodes;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;
using TVBoxForWindows.Net;
using TVBoxForWindows.Live;

namespace TVBoxForWindows.Engine;

/// <summary>点播配置（移植自 VodConfig.java）：加载→解码→解析 sites/parses/lives/doh/proxy/rules/headers/hosts/flags/ads。</summary>
public class VodConfigService
{
    const int MaxDepotDepth = 4;
    readonly SemaphoreSlim _loadLock = new(1, 1);

    public static VodConfigService Instance { get; } = new();

    public ConfigRecord Config { get; private set; } = new();
    public List<Site> Sites { get; private set; } = new();
    public List<Parse> Parses { get; private set; } = new();
    public List<string> Flags { get; private set; } = new();
    public List<Doh> Dohs { get; private set; } = new();
    public List<Rule> Rules { get; private set; } = new();
    public string Wall { get; private set; } = "";
    public string Spider { get; private set; } = "";
    public Site Home { get; private set; } = new();
    public Parse Parse { get; private set; } = new();

    public event Action Loaded;

    public static int Cid => Instance.Config?.Id ?? 0;
    public bool HasParse => Parses.Count > 0;

    public VodConfigService Clear()
    {
        Config = new();
        Sites = new(); Parses = new(); Flags = new(); Dohs = new(); Rules = new();
        Wall = ""; Spider = ""; Home = new Site(); Parse = new Parse();
        SpiderLoader.Instance.Clear();
        NodeRuntime.Shutdown();
        NetworkConfig.Clear();
        Core.Sniffer.SetRules(new());
        return this;
    }

    public async Task LoadAsync(ConfigRecord config)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.Url)) throw new Exception("请输入点播配置地址");
        await LoadCoreAsync(config, null);
    }

    /// <summary>Reloads only while the expected source is still current.</summary>
    internal Task<bool> ReloadCurrentAsync(string expectedUrl) => LoadCoreAsync(null, expectedUrl);

    async Task<bool> LoadCoreAsync(ConfigRecord requested, string expectedUrl)
    {
        await _loadLock.WaitAsync();
        try
        {
            var config = requested;
            if (expectedUrl != null)
            {
                if (!string.Equals(Config?.Url, expectedUrl, StringComparison.OrdinalIgnoreCase)) return false;
                config = Config;
            }
            if (config == null || string.IsNullOrWhiteSpace(config.Url)) throw new Exception("请输入点播配置地址");

            var depots = new List<ConfigRecord>();
            var imports = new List<DepotImport>();
            var resolved = await ResolveConfigAsync(
                config,
                null,
                depots,
                imports,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                0);
            var pending = BuildPending(resolved.Config, resolved.Node, resolved.Name);
            await CommitAsync(pending);

            foreach (var import in imports)
                if (!string.IsNullOrWhiteSpace(import.Name)) import.Config.Name = import.Name;
            foreach (var depot in depots)
                if (!string.Equals(depot.Url, pending.Config.Url, StringComparison.OrdinalIgnoreCase))
                    Stores.DeleteConfig(depot.Url, 0);
            Stores.SaveConfig(pending.Config);
            Setting.ConfigVod = pending.Config.Url;
        }
        finally { _loadLock.Release(); }

        App.Post(() => Loaded?.Invoke());
        return true;
    }

    public async Task LoadLatestAsync()
    {
        var url = Setting.ConfigVod;
        if (string.IsNullOrEmpty(url)) return;
        await LoadAsync(Stores.FindConfig(url, 0));
    }

    async Task<ResolvedConfig> ResolveConfigAsync(
        ConfigRecord config,
        string name,
        List<ConfigRecord> depots,
        List<DepotImport> imports,
        HashSet<string> visited,
        int depth)
    {
        if (depth > MaxDepotDepth) throw new Exception("配置仓库嵌套层级过多");
        var url = UrlUtil.Convert(config.Url);
        if (!visited.Add(url)) throw new Exception("配置仓库存在循环引用");

        // Node.js 源（cat/T4 型 index.js）：正文是 6MB 服务端程序而非配置，需先起服务再取 /config
        var json = await NodeSource.TryLoadAsync(url) ?? await Decoder.GetJson(url);
        var node = JsonUtil.Parse(json) ?? throw new Exception("配置解析失败");
        if (node["msg"] != null) throw new Exception(node["msg"].ToString());
        if (node["urls"] == null) return new ResolvedConfig(config, node, name);

        var items = ModelJson.Parse<List<Depot>>(node["urls"]) ?? new();
        var candidates = items
            .Where(item => !string.IsNullOrWhiteSpace(item?.Url))
            .Select(item => new DepotImport(Stores.FindConfig(item.Url, 0), item.Name))
            .ToList();
        if (candidates.Count == 0) throw new Exception("Depot urls is empty");
        imports.AddRange(candidates);
        depots.Add(config);
        var first = candidates[0];
        return await ResolveConfigAsync(first.Config, first.Name, depots, imports, visited, depth + 1);
    }

    PendingConfig BuildPending(ConfigRecord config, JsonNode node, string name)
    {
        var spider = JsonUtil.SafeString(node, "spider");
        var sites = new List<Site>();
        if (node["sites"] is JsonArray sitesArr)
            foreach (var item in sitesArr)
            {
                var site = ModelJson.Parse<Site>(item);
                if (site == null || string.IsNullOrEmpty(site.Key) || sites.Any(s => s.Key == site.Key)) continue;
                if (string.IsNullOrEmpty(site.Jar)) site.Jar = spider;
                sites.Add(site);
            }

        var parses = new List<Parse>();
        if (node["parses"] is JsonArray parsesArr)
            foreach (var item in parsesArr)
            {
                var parse = ModelJson.Parse<Parse>(item);
                if (parse != null && !string.IsNullOrEmpty(parse.Name) && parses.All(p => p.Name != parse.Name)) parses.Add(parse);
            }
        if (parses.Count > 0 && parses[0].Type != 4) parses.Insert(0, Models.Parse.God());

        var dohs = ModelJson.Parse<List<Doh>>(node["doh"]) ?? new();
        return new PendingConfig
        {
            Config = config,
            Name = name,
            Node = node,
            Sites = sites,
            Parses = parses,
            Flags = JsonUtil.SafeListString(node, "flags"),
            Dohs = dohs,
            Rules = ModelJson.Parse<List<Rule>>(node["rules"]) ?? new(),
            Headers = ModelJson.Parse<List<HeaderRule>>(node["headers"]) ?? new(),
            Proxies = ModelJson.Parse<List<ProxyRule>>(node["proxy"]) ?? new(),
            Hosts = JsonUtil.SafeListString(node, "hosts"),
            Ads = JsonUtil.SafeListString(node, "ads"),
            Wall = JsonUtil.SafeString(node, "wallpaper"),
            Spider = spider,
            Home = sites.FirstOrDefault(site => site.Key == config.Home) ?? sites.FirstOrDefault() ?? new Site(),
            Parse = parses.FirstOrDefault(parse => parse.Name == config.Parse) ?? parses.FirstOrDefault() ?? new Parse(),
            Logo = JsonUtil.SafeString(node, "logo"),
            Notice = JsonUtil.SafeString(node, "notice"),
            Danmaku = JsonUtil.SafeString(node, "danmaku"),
            HasLives = node["lives"] is JsonArray lives && lives.Count > 0,
        };
    }

    async Task CommitAsync(PendingConfig pending)
    {
        var convertedUrl = UrlUtil.Convert(pending.Config.Url);
        if (!NodeSource.MaybeNode(convertedUrl)) await NodeRuntime.ShutdownAsync();

        SpiderLoader.Instance.Clear();
        NetworkConfig.SetHeaders(pending.Headers);
        NetworkConfig.SetProxies(pending.Proxies);
        NetworkConfig.SetHosts(pending.Hosts);
        NetworkConfig.SetAds(pending.Ads);
        NetworkConfig.Doh = pending.Dohs.FirstOrDefault(doh => doh.Name == Setting.Doh);
        Core.Sniffer.SetRules(pending.Rules);

        Config = pending.Config;
        Sites = pending.Sites;
        Parses = pending.Parses;
        Flags = pending.Flags;
        Dohs = pending.Dohs;
        Rules = pending.Rules;
        Wall = pending.Wall;
        Spider = pending.Spider;
        Home = pending.Home;
        Parse = pending.Parse;

        if (!string.IsNullOrWhiteSpace(pending.Name)) Config.Name = pending.Name;
        Config.Logo = pending.Logo;
        Config.Notice = pending.Notice;
        Config.Danmaku = pending.Danmaku;

        if (pending.HasLives) await LiveConfigService.Instance.ParseFromVod(Config, pending.Node);
        else LiveConfigService.Instance.OnVodWithoutLives();
    }

    sealed record ResolvedConfig(ConfigRecord Config, JsonNode Node, string Name);
    sealed record DepotImport(ConfigRecord Config, string Name);

    sealed class PendingConfig
    {
        public ConfigRecord Config { get; init; }
        public string Name { get; init; }
        public JsonNode Node { get; init; }
        public List<Site> Sites { get; init; }
        public List<Parse> Parses { get; init; }
        public List<string> Flags { get; init; }
        public List<Doh> Dohs { get; init; }
        public List<Rule> Rules { get; init; }
        public List<HeaderRule> Headers { get; init; }
        public List<ProxyRule> Proxies { get; init; }
        public List<string> Hosts { get; init; }
        public List<string> Ads { get; init; }
        public string Wall { get; init; }
        public string Spider { get; init; }
        public Site Home { get; init; }
        public Parse Parse { get; init; }
        public string Logo { get; init; }
        public string Notice { get; init; }
        public string Danmaku { get; init; }
        public bool HasLives { get; init; }
    }

    public Site GetSite(string key) => string.IsNullOrEmpty(key) ? new Site() : Sites.FirstOrDefault(s => s.Key == key) ?? new Site();

    public void SetHome(Site site)
    {
        Home = site;
        Config.Home = site.Key;
        Stores.SaveConfig(Config);
    }

    public void SetParse(Parse parse)
    {
        Parse = parse;
        Config.Parse = parse.Name;
        Stores.SaveConfig(Config);
    }

    public Parse GetParse(string name) => Parses.FirstOrDefault(p => p.Name == name);

    /// <summary>取 type 与 flag 匹配的解析器（超级解析用）。flag 为空时不过滤 flag。</summary>
    public List<Parse> GetParses(int type, string flag) =>
        Parses.Where(p => p.Type == type && (p.Ext?.Flag == null || p.Ext.Flag.Count == 0 || string.IsNullOrEmpty(flag) || p.Ext.Flag.Contains(flag))).ToList();
}
