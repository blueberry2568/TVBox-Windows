using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;

namespace TVBoxForWindows.Models;

/// <summary>通用回传对象（Spider 各方法与内置 API 共用），兼容 XML(type=0) 与 JSON。</summary>
public class Result
{
    [JsonPropertyName("class")] public List<VodClass> Types { get; set; } = new();
    [JsonPropertyName("list")] public List<Vod> List { get; set; } = new();
    [JsonPropertyName("filters")][JsonConverter(typeof(FiltersConverter))] public Dictionary<string, List<Filter>> Filters { get; set; } = new();
    [JsonPropertyName("url")][JsonConverter(typeof(UrlConverter))] public UrlBean Url { get; set; }
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
    [JsonPropertyName("msg")] public string Msg { get; set; } = "";
    [JsonPropertyName("danmaku")][JsonConverter(typeof(DanmakuListConverter))] public List<Danmaku> Danmaku { get; set; } = new();
    [JsonPropertyName("subs")] public List<Sub> Subs { get; set; } = new();
    [JsonPropertyName("playUrl")] public string PlayUrl { get; set; } = "";
    [JsonPropertyName("artwork")] public string Artwork { get; set; } = "";
    [JsonPropertyName("jxFrom")] public string JxFrom { get; set; } = "";
    [JsonPropertyName("flag")] public string Flag { get; set; } = "";
    [JsonPropertyName("desc")] public string Desc { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("click")] public string Click { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("position")] public long? Position { get; set; }
    [JsonPropertyName("pagecount")] public int? PageCount { get; set; }
    [JsonPropertyName("parse")] public int? Parse { get; set; }
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("jx")] public int? Jx { get; set; }
    [JsonPropertyName("drm")] public Drm Drm { get; set; }

    [JsonIgnore] public UrlBean UrlBean => Url ??= new UrlBean();
    [JsonIgnore] public string RealUrl => PlayUrl + UrlBean.V();
    [JsonIgnore] public Vod Vod => List.Count > 0 ? List[0] : new Vod();

    public bool NeedParse() => (Parse ?? 0) == 1 || (Jx ?? 0) == 1;
    public string GetMsg() => string.IsNullOrEmpty(Msg) || (Code ?? 0) != 0 ? "" : Msg;

    public static Result Empty() => new();
    public static Result Error(string msg) => new() { Parse = 0, Msg = msg };
    public static Result FromVod(Vod vod) => new() { List = new List<Vod> { vod } };

    public static Result FromJson(string json)
    {
        var result = ModelJson.Parse<Result>(json) ?? Empty();
        return result.Trans();
    }

    public static Result FromType(int type, string text) => type == 0 ? FromXml(text) : FromJson(text);

    public Result Trans()
    {
        if (Core.Trans.Pass()) return this;
        Types.ForEach(t => t.Trans());
        List.ForEach(v => v.Trans());
        return this;
    }

    public void SetUrl(string url) => Url = UrlBean.Replace(url);

    /// <summary>type=0 XML（MacCMS rss 格式）解析。</summary>
    public static Result FromXml(string text)
    {
        var result = Empty();
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(text.Trim());
            foreach (XmlNode ty in doc.SelectNodes("//class/ty"))
                result.Types.Add(new VodClass { TypeId = ty.Attributes?["id"]?.Value ?? "", TypeName = ty.InnerText?.Trim() ?? "" });
            foreach (XmlNode video in doc.SelectNodes("//list/video"))
            {
                var vod = new Vod
                {
                    Id = Sel(video, "id"),
                    Name = Sel(video, "name"),
                    Pic = Sel(video, "pic"),
                    Remarks = Sel(video, "note"),
                    Year = Sel(video, "year"),
                    Area = Sel(video, "area"),
                    Director = Sel(video, "director"),
                    Actor = Sel(video, "actor"),
                    Content = Sel(video, "des"),
                    TypeName = Sel(video, "type"),
                };
                var froms = new List<string>();
                var urls = new List<string>();
                foreach (XmlNode dd in video.SelectNodes("dl/dd"))
                {
                    froms.Add(dd.Attributes?["flag"]?.Value ?? "");
                    urls.Add(dd.InnerText?.Trim() ?? "");
                }
                vod.PlayFrom = string.Join("$$$", froms);
                vod.PlayUrl = string.Join("$$$", urls);
                result.List.Add(vod);
            }
            var page = doc.SelectSingleNode("//list");
            if (page?.Attributes?["pagecount"] != null && int.TryParse(page.Attributes["pagecount"].Value, out var pc)) result.PageCount = pc;
        }
        catch (Exception e) { Core.Logger.E("XmlResult", e.Message); }
        return result.Trans();
    }

    static string Sel(XmlNode node, string name)
    {
        var child = node.SelectSingleNode(name);
        return child == null ? "" : System.Net.WebUtility.HtmlDecode(child.InnerText ?? "").Trim();
    }
}

/// <summary>url 字段兼容 string 与 {n:[],v:[]} 多码率两种写法。</summary>
public class UrlBean
{
    public List<string> Names { get; set; } = new();
    public List<string> Values { get; set; } = new();
    public int Position { get; set; }

    public string V() => Values.Count == 0 ? "" : Values[Math.Clamp(Position, 0, Values.Count - 1)];
    public bool IsEmpty => string.IsNullOrEmpty(V());
    public bool IsMulti => Values.Count > 1;

    public UrlBean Replace(string url)
    {
        if (Values.Count == 0) { Values.Add(url); if (Names.Count == 0) Names.Add(""); }
        else Values[Math.Clamp(Position, 0, Values.Count - 1)] = url;
        return this;
    }

    public static UrlBean Create(string url) => new UrlBean().Replace(url);
}

public class UrlConverter : JsonConverter<UrlBean>
{
    public override UrlBean Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var bean = new UrlBean();
        if (reader.TokenType == JsonTokenType.String) { bean.Replace(reader.GetString() ?? ""); return bean; }
        if (reader.TokenType == JsonTokenType.Number) { bean.Replace(reader.GetDouble().ToString()); return bean; }
        var node = JsonNode.Parse(ref reader);
        if (node is JsonArray arr)
        {
            // ["名称1","地址1","名称2","地址2"] 交错或 ["地址1","地址2"]
            var items = arr.Select(x => x?.ToString() ?? "").ToList();
            bool paired = items.Count % 2 == 0 && items.Where((s, i) => i % 2 == 1).All(s => s.Contains("://") || s.StartsWith("/"));
            if (paired && items.Count >= 2 && !items[0].Contains("://"))
                for (int i = 0; i + 1 < items.Count; i += 2) { bean.Names.Add(items[i]); bean.Values.Add(items[i + 1]); }
            else foreach (var s in items) { bean.Names.Add(""); bean.Values.Add(s); }
        }
        else if (node is JsonObject obj)
        {
            if (obj["n"] is JsonArray n) foreach (var x in n) bean.Names.Add(x?.ToString() ?? "");
            if (obj["v"] is JsonArray v) foreach (var x in v) bean.Values.Add(x?.ToString() ?? "");
            if (bean.Values.Count == 0 && obj["url"] != null) bean.Replace(obj["url"].ToString());
        }
        return bean;
    }

    public override void Write(Utf8JsonWriter writer, UrlBean value, JsonSerializerOptions o) => writer.WriteStringValue(value.V());
}

/// <summary>filters 兼容 {tid:[Filter]} 与空数组两种写法。</summary>
public class FiltersConverter : JsonConverter<Dictionary<string, List<Filter>>>
{
    public override Dictionary<string, List<Filter>> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var map = new Dictionary<string, List<Filter>>();
        var node = JsonNode.Parse(ref reader);
        if (node is JsonObject obj)
            foreach (var kv in obj)
            {
                var list = new List<Filter>();
                if (kv.Value is JsonArray arr)
                    foreach (var item in arr)
                    {
                        var f = ModelJson.Parse<Filter>(item);
                        if (f != null && f.Value.Count > 0) list.Add(f);
                    }
                map[kv.Key] = list;
            }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, List<Filter>> value, JsonSerializerOptions o)
        => JsonSerializer.Serialize(writer, value.ToDictionary(k => k.Key, v => v.Value), o);
}

public class DanmakuListConverter : JsonConverter<List<Danmaku>>
{
    public override List<Danmaku> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var list = new List<Danmaku>();
        if (reader.TokenType == JsonTokenType.String) { list.Add(new Danmaku { Url = reader.GetString() }); return list; }
        var node = JsonNode.Parse(ref reader);
        if (node is JsonArray arr)
            foreach (var item in arr)
            {
                if (item is JsonValue) list.Add(new Danmaku { Url = item.ToString() });
                else { var d = ModelJson.Parse<Danmaku>(item); if (d != null) list.Add(d); }
            }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<Danmaku> value, JsonSerializerOptions o)
        => JsonSerializer.Serialize(writer, value.ToArray(), o);
}

public class Danmaku
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonIgnore] public bool IsSelected { get; set; }
}

public class Sub
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("lang")] public string Lang { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("flag")] public int FlagValue { get; set; }
}

public class Drm
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
    [JsonPropertyName("forceKey")] public bool ForceKey { get; set; }
}
