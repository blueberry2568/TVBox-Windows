using System.Text.Json.Serialization;
using System.Net;
using FongMi.TV.Core;

namespace FongMi.TV.Models;

public class Site
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("api")] public string Api { get; set; } = "";
    [JsonPropertyName("ext")] public string Ext { get; set; } = "";
    [JsonPropertyName("jar")] public string Jar { get; set; } = "";
    [JsonPropertyName("click")] public string Click { get; set; } = "";
    [JsonPropertyName("playUrl")] public string PlayUrl { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("hide")] public int Hide { get; set; }
    [JsonPropertyName("timeout")] public int Timeout { get; set; }
    [JsonPropertyName("searchable")] public int? Searchable { get; set; }
    [JsonPropertyName("changeable")] public int? Changeable { get; set; }
    [JsonPropertyName("quickSearch")] public int? QuickSearch { get; set; }
    [JsonPropertyName("searchByName")] public bool SearchByName { get; set; }
    [JsonPropertyName("indexs")] public int Indexs { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
    [JsonPropertyName("style")] public Style Style { get; set; }

    [JsonIgnore] public bool IsSearchable => (Searchable ?? 1) == 1;
    [JsonIgnore] public bool IsChangeable => (Changeable ?? 1) == 1;
    [JsonIgnore] public bool IsQuickSearch => (QuickSearch ?? 1) == 1 && IsSearchable;
    [JsonIgnore] public bool IsHidden => Hide == 1;
    [JsonIgnore] public bool IsEmpty => string.IsNullOrEmpty(Key);
    [JsonIgnore] public int RequestTimeout => Timeout > 0 ? Timeout * 1000 : Setting.SiteTimeout;

    /// <summary>type=4 时 ext 需先请求一次取回真实扩展。</summary>
    public async Task<Site> FetchExt()
    {
        if (Type != 4 || !Ext.StartsWith("http")) return this;
        try { Ext = await Net.HttpUtil.GetString(Ext); } catch { }
        return this;
    }

    public Style GetStyle() => Style ?? Style.Rect();
}

public class Style
{
    [JsonPropertyName("type")] public string Type { get; set; } = "rect";
    [JsonPropertyName("ratio")] public float? Ratio { get; set; }

    public static Style Rect(float ratio = 0.75f) => new() { Type = "rect", Ratio = ratio };
    [JsonIgnore] public bool IsOval => "oval".Equals(Type, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsList => "list".Equals(Type, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public float RatioOrDefault => Ratio is > 0 ? Ratio.Value : 0.75f;
}

public class Cate
{
    [JsonPropertyName("land")] public int Land { get; set; }
    [JsonPropertyName("circle")] public int Circle { get; set; }
    [JsonPropertyName("ratio")] public float? Ratio { get; set; }
}

public class VodClass
{
    [JsonPropertyName("type_id")] public string TypeId { get; set; }
    [JsonPropertyName("type_name")] public string TypeName { get; set; }
    [JsonPropertyName("type_flag")] public string TypeFlag { get; set; }
    [JsonPropertyName("id")] public string IdAlias { set { TypeId ??= value; } }
    [JsonPropertyName("name")] public string NameAlias { set { TypeName ??= value; } }
    [JsonPropertyName("land")] public int Land { get; set; }
    [JsonPropertyName("circle")] public int Circle { get; set; }
    [JsonPropertyName("ratio")] public float? Ratio { get; set; }
    [JsonIgnore] public List<Filter> Filters { get; set; } = new();
    [JsonIgnore] public Dictionary<string, string> Extend { get; set; } = new();

    public void Trans() { if (!Core.Trans.Pass()) TypeName = Core.Trans.S2T(TypeName); }
}

public class Filter
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("init")] public string Init { get; set; } = "";
    [JsonPropertyName("value")] public List<FilterValue> Value { get; set; } = new();
}

public class FilterValue
{
    [JsonPropertyName("n")] public string N { get; set; } = "";
    [JsonPropertyName("v")] public string V { get; set; } = "";
}

public class Vod
{
    [JsonPropertyName("vod_id")] public string Id { get; set; } = "";
    [JsonPropertyName("vod_name")] public string Name { get; set; } = "";
    [JsonPropertyName("vod_pic")] public string Pic { get; set; } = "";
    [JsonPropertyName("vod_remarks")] public string Remarks { get; set; } = "";
    [JsonPropertyName("vod_year")] public string Year { get; set; } = "";
    [JsonPropertyName("vod_area")] public string Area { get; set; } = "";
    [JsonPropertyName("vod_director")] public string Director { get; set; } = "";
    [JsonPropertyName("vod_actor")] public string Actor { get; set; } = "";
    [JsonPropertyName("vod_content")] public string Content { get; set; } = "";
    [JsonPropertyName("vod_play_from")] public string PlayFrom { get; set; } = "";
    [JsonPropertyName("vod_play_url")] public string PlayUrl { get; set; } = "";
    [JsonPropertyName("vod_tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("type_name")] public string TypeName { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("cate")] public Cate Cate { get; set; }
    [JsonPropertyName("style")] public Style Style { get; set; }
    [JsonPropertyName("land")] public int Land { get; set; }
    [JsonPropertyName("circle")] public int Circle { get; set; }
    [JsonPropertyName("ratio")] public float? Ratio { get; set; }

    [JsonIgnore] public Site Site { get; set; }
    [JsonIgnore] public bool IsFolder => "folder".Equals(Tag, StringComparison.OrdinalIgnoreCase) || Cate != null;
    [JsonIgnore] public bool HasAction => !string.IsNullOrEmpty(Action);

    public string CleanName => WebUtility.HtmlDecode(Name ?? "").Trim();

    public void Trans()
    {
        if (Core.Trans.Pass()) return;
        Name = Core.Trans.S2T(Name); Remarks = Core.Trans.S2T(Remarks); TypeName = Core.Trans.S2T(TypeName);
        Area = Core.Trans.S2T(Area); Director = Core.Trans.S2T(Director); Actor = Core.Trans.S2T(Actor); Content = Core.Trans.S2T(Content);
    }

    /// <summary>解析 vod_play_from / vod_play_url 为线路+集数结构（$$$ 分线路，# 分集，$ 分名称与地址）。</summary>
    public List<VodFlag> GetFlags()
    {
        var flags = new List<VodFlag>();
        var froms = (PlayFrom ?? "").Split("$$$", StringSplitOptions.None);
        var urls = (PlayUrl ?? "").Split("$$$", StringSplitOptions.None);
        for (int i = 0; i < urls.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(urls[i])) continue;
            var flag = new VodFlag { Flag = i < froms.Length && !string.IsNullOrWhiteSpace(froms[i]) ? froms[i].Trim() : $"线路{i + 1}" };
            int index = 1;
            foreach (var part in urls[i].Split('#'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var split = part.Split('$', 2);
                var ep = split.Length == 2
                    ? new Episode { Name = string.IsNullOrWhiteSpace(split[0]) ? $"第{index:00}集" : split[0].Trim(), Url = split[1].Trim() }
                    : new Episode { Name = $"第{index:00}集", Url = part.Trim() };
                if (ep.Url.Length > 0) { ep.Index = index++; flag.Episodes.Add(ep); }
            }
            if (flag.Episodes.Count > 0) flags.Add(flag);
        }
        return flags;
    }
}

public class VodFlag
{
    public string Flag { get; set; } = "";
    public List<Episode> Episodes { get; set; } = new();

    public Episode Find(string name, bool strict)
    {
        if (Episodes.Count == 0) return null;
        var match = Episodes.FirstOrDefault(e => e.Name == name);
        if (match != null) return match;
        var number = Episode.Digit(name);
        if (number >= 0) match = Episodes.FirstOrDefault(e => Episode.Digit(e.Name) == number);
        if (match != null) return match;
        return strict ? null : Episodes[0];
    }
}

public class Episode
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int Index { get; set; }

    public static int Digit(string text)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", "\\d+");
            return m.Success ? int.Parse(m.Value) : -1;
        }
        catch { return -1; }
    }
}

public class Parse
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("ext")] public ParseExt Ext { get; set; } = new();

    [JsonIgnore] public Dictionary<string, string> Header { get; set; } = new();
    [JsonIgnore] public string Click { get; set; } = "";
    [JsonIgnore] public bool IsEmpty => Type == 0 && string.IsNullOrEmpty(Url);

    public static Parse Get(int type, string url) => new() { Type = type, Url = url, Name = "" };
    public static Parse God() => new() { Type = 4, Name = "超级解析" };

    public Dictionary<string, string> Headers()
    {
        var map = new Dictionary<string, string>(Ext?.Header ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Header) map[kv.Key] = kv.Value;
        return map;
    }
}

public class ParseExt
{
    [JsonPropertyName("flag")] public List<string> Flag { get; set; } = new();
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
}
