using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TVBoxForWindows.Models;

public class Live
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("api")] public string Api { get; set; } = "";
    [JsonPropertyName("ext")] public string Ext { get; set; } = "";
    [JsonPropertyName("jar")] public string Jar { get; set; } = "";
    [JsonPropertyName("click")] public string Click { get; set; } = "";
    [JsonPropertyName("logo")] public string Logo { get; set; } = "";
    [JsonPropertyName("epg")] public string Epg { get; set; } = "";
    [JsonPropertyName("ua")] public string Ua { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("referer")] public string Referer { get; set; } = "";
    [JsonPropertyName("timeZone")] public string TimeZone { get; set; } = "";
    [JsonPropertyName("timeout")] public int Timeout { get; set; }
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
    [JsonPropertyName("catchup")] public Catchup Catchup { get; set; }
    [JsonPropertyName("groups")] public List<LiveGroup> Groups { get; set; } = new();
    [JsonPropertyName("boot")] public bool Boot { get; set; }
    [JsonPropertyName("pass")] public bool Pass { get; set; }

    [JsonIgnore] public bool IsEmpty => string.IsNullOrEmpty(Name);
    [JsonIgnore] public int Width { get; set; }

    public Dictionary<string, string> BuildHeaders()
    {
        var map = new Dictionary<string, string>(Header ?? new(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Ua)) map["User-Agent"] = Ua;
        if (!string.IsNullOrEmpty(Origin)) map["Origin"] = Origin;
        if (!string.IsNullOrEmpty(Referer)) map["Referer"] = Referer;
        return map;
    }
}

public class LiveGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("pass")] public string Pass { get; set; } = "";
    [JsonPropertyName("channel")] public List<LiveChannel> Channel { get; set; } = new();

    [JsonIgnore] public bool IsHidden => !string.IsNullOrEmpty(Pass);
    [JsonIgnore] public bool Unlocked { get; set; }

    public static LiveGroup Create(string name)
    {
        var parts = (name ?? "").Split('_', 2);
        return new LiveGroup { Name = parts[0], Pass = parts.Length > 1 ? parts[1] : "" };
    }

    public LiveChannel Find(string name) => Channel.FirstOrDefault(c => c.Name == name);
}

public class LiveChannel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("urls")] public List<string> Urls { get; set; } = new();
    [JsonPropertyName("number")] public string Number { get; set; } = "";
    [JsonPropertyName("logo")] public string Logo { get; set; } = "";
    [JsonPropertyName("epg")] public string Epg { get; set; } = "";
    [JsonPropertyName("ua")] public string Ua { get; set; } = "";
    [JsonPropertyName("click")] public string Click { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("referer")] public string Referer { get; set; } = "";
    [JsonPropertyName("tvgId")] public string TvgId { get; set; } = "";
    [JsonPropertyName("tvgName")] public string TvgName { get; set; } = "";
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
    [JsonPropertyName("parse")] public int Parse { get; set; }
    [JsonPropertyName("catchup")] public Catchup Catchup { get; set; }
    [JsonPropertyName("drm")] public Drm Drm { get; set; }

    [JsonIgnore] public int UrlIndex { get; set; }
    [JsonIgnore] public LiveGroup Group { get; set; }
    [JsonIgnore] public Live Live { get; set; }

    /// <summary>当前线路（去掉 $名称 与 |header 附加段）。</summary>
    public string CurrentUrl()
    {
        if (Urls.Count == 0) return "";
        var raw = Urls[Math.Clamp(UrlIndex, 0, Urls.Count - 1)];
        var url = raw.Split('$')[0];
        return url.Split('|')[0];
    }

    public string CurrentLineName(int index)
    {
        var raw = Urls[Math.Clamp(index, 0, Urls.Count - 1)];
        var parts = raw.Split('$');
        return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : $"线路{index + 1}";
    }

    /// <summary>行内标头（url|key=value&amp;key2=value2）。</summary>
    public Dictionary<string, string> InlineHeaders()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Urls.Count == 0) return map;
        var raw = Urls[Math.Clamp(UrlIndex, 0, Urls.Count - 1)].Split('$')[0];
        int i = raw.IndexOf('|');
        if (i < 0) return map;
        foreach (var pair in raw[(i + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2) map[Core.UrlUtil.FixHeader(kv[0].Trim())] = kv[1].Trim();
        }
        return map;
    }

    public Dictionary<string, string> BuildHeaders()
    {
        var map = Live?.BuildHeaders() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Header ?? new()) map[Core.UrlUtil.FixHeader(kv.Key)] = kv.Value;
        if (!string.IsNullOrEmpty(Ua)) map["User-Agent"] = Ua;
        if (!string.IsNullOrEmpty(Origin)) map["Origin"] = Origin;
        if (!string.IsNullOrEmpty(Referer)) map["Referer"] = Referer;
        foreach (var kv in InlineHeaders()) map[kv.Key] = kv.Value;
        return map;
    }

    public Catchup GetCatchup() => Catchup?.IsUsable == true ? Catchup : Live?.Catchup;

    public string GetLogo()
    {
        if (!string.IsNullOrEmpty(Logo)) return ReplaceTemplate(Logo);
        if (!string.IsNullOrEmpty(Live?.Logo)) return ReplaceTemplate(Live.Logo);
        return "";
    }

    string ReplaceTemplate(string template) => template
        .Replace("{name}", Name).Replace("{id}", string.IsNullOrEmpty(TvgId) ? Name : TvgId).Replace("{logo}", Logo ?? "");

    public string GetEpgUrl(DateTime date)
    {
        var epg = !string.IsNullOrEmpty(Epg) ? Epg : Live?.Epg ?? "";
        if (string.IsNullOrEmpty(epg) || !epg.Contains('{')) return "";
        foreach (var item in epg.Split(','))
            if (item.Contains('{'))
                return item.Replace("{name}", Uri.EscapeDataString(TvgName is { Length: > 0 } ? TvgName : Name))
                           .Replace("{id}", Uri.EscapeDataString(string.IsNullOrEmpty(TvgId) ? Name : TvgId))
                           .Replace("{epg}", Uri.EscapeDataString(Epg ?? ""))
                           .Replace("{date}", date.ToString("yyyy-MM-dd"));
        return "";
    }
}

/// <summary>追看/时移（移植自 Catchup.java）。</summary>
public class Catchup
{
    static readonly Regex TokenPattern = new("(\\$?\\{[^}]*\\})");
    static readonly Regex TagPattern = new("\\{([^}]+)\\}");

    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("days")] public string Days { get; set; } = "";
    [JsonPropertyName("regex")] public string Regex { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("replace")] public string Replace { get; set; } = "";

    [JsonIgnore] public bool IsUsable => !string.IsNullOrEmpty(Source);

    public static Catchup PLTV() => new()
    {
        Days = "7",
        Type = "append",
        Regex = "/PLTV/",
        Replace = "/PLTV/,/TVOD/",
        Source = "?playseek=${(b)yyyyMMddHHmmss}-${(e)yyyyMMddHHmmss}"
    };

    public bool Match(string url)
    {
        if (string.IsNullOrEmpty(Regex)) return IsUsable;
        if (url.Contains(Regex)) return true;
        try { return new Regex(Regex).IsMatch(url); } catch { return false; }
    }

    public string Format(string url, long startMs, long endMs)
    {
        var result = Source;
        foreach (Match m in TokenPattern.Matches(result).ToArray())
            result = result.Replace(m.Groups[1].Value, FormatToken(m.Groups[1].Value, startMs, endMs));
        if ("default".Equals(Type, StringComparison.OrdinalIgnoreCase)) return result;
        var target = url;
        var splits = (Replace ?? "").Split(',', 2);
        if (splits.Length == 2) target = System.Text.RegularExpressions.Regex.Replace(target, splits[0], splits[1]);
        try { if (!string.IsNullOrEmpty(new Uri(target).Query)) result = result.Replace("?", "&"); } catch { }
        return target + result;
    }

    string FormatToken(string group, long start, long end)
    {
        var m = TagPattern.Match(group);
        if (!m.Success) return "";
        var tag = m.Groups[1].Value;
        int paren = tag.IndexOf(')');
        if (tag.StartsWith("(b") && paren >= 0) return FormatTime(start, tag[(paren + 1)..]);
        if (tag.StartsWith("(e") && paren >= 0) return FormatTime(end, tag[(paren + 1)..]);
        if (tag.StartsWith("utcend:")) return (end / 1000).ToString();
        if (tag.StartsWith("utc:")) return (start / 1000).ToString();
        return "";
    }

    static string FormatTime(long millis, string fmt)
    {
        if (fmt == "timestamp") return (millis / 1000).ToString();
        try { return DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime().ToString(fmt); }
        catch { return ""; }
    }
}

public class Epg
{
    public string Key { get; set; } = "";
    public List<EpgData> List { get; set; } = new();

    public EpgData Now()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return List.FirstOrDefault(d => d.StartTime <= now && now < d.EndTime);
    }
}

public class EpgData
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("start")] public string Start { get; set; } = "";
    [JsonPropertyName("end")] public string End { get; set; } = "";
    [JsonIgnore] public long StartTime { get; set; }
    [JsonIgnore] public long EndTime { get; set; }
    [JsonIgnore] public bool IsSelected { get; set; }

    public bool IsInRange => StartTime > 0 && EndTime > 0;
    public string TimeRange => IsInRange ? $"{DateTimeOffset.FromUnixTimeMilliseconds(StartTime).ToLocalTime():HH:mm} ~ {DateTimeOffset.FromUnixTimeMilliseconds(EndTime).ToLocalTime():HH:mm}" : "";
    public bool IsFuture => StartTime > DateTimeOffset.Now.ToUnixTimeMilliseconds();
}
