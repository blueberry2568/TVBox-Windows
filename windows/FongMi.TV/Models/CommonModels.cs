using System.Text.Json.Serialization;

namespace FongMi.TV.Models;

/// <summary>配置记录（对应 Android 的 Config 表）：type 0=vod 1=live 2=wall。</summary>
public class ConfigRecord
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("time")] public long Time { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("logo")] public string Logo { get; set; } = "";
    [JsonPropertyName("home")] public string Home { get; set; } = "";
    [JsonPropertyName("parse")] public string Parse { get; set; } = "";
    [JsonPropertyName("notice")] public string Notice { get; set; } = "";
    [JsonPropertyName("danmaku")] public string Danmaku { get; set; } = "";

    [JsonIgnore] public string Desc => string.IsNullOrEmpty(Name) ? Url : Name;
}

public class Depot
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class HeaderRule
{
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
}

public class ProxyRule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hosts")] public List<string> Hosts { get; set; } = new();
    [JsonPropertyName("urls")] public List<string> Urls { get; set; } = new();
}

public class Rule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hosts")] public List<string> Hosts { get; set; } = new();
    [JsonPropertyName("regex")] public List<string> Regex { get; set; } = new();
    [JsonPropertyName("script")] public List<string> Script { get; set; } = new();
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = new();

    public static Rule Empty() => new();
}

public class Doh
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("ips")] public List<string> Ips { get; set; } = new();
}

public class History
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("cid")] public int Cid { get; set; }
    [JsonPropertyName("vodPic")] public string VodPic { get; set; } = "";
    [JsonPropertyName("vodName")] public string VodName { get; set; } = "";
    [JsonPropertyName("vodFlag")] public string VodFlag { get; set; } = "";
    [JsonPropertyName("vodRemarks")] public string VodRemarks { get; set; } = "";
    [JsonPropertyName("episodeUrl")] public string EpisodeUrl { get; set; } = "";
    [JsonPropertyName("revSort")] public bool RevSort { get; set; }
    [JsonPropertyName("revPlay")] public bool RevPlay { get; set; }
    [JsonPropertyName("createTime")] public long CreateTime { get; set; }
    [JsonPropertyName("opening")] public long Opening { get; set; } = -1;
    [JsonPropertyName("ending")] public long Ending { get; set; } = -1;
    [JsonPropertyName("position")] public long Position { get; set; } = -1;
    [JsonPropertyName("duration")] public long Duration { get; set; } = -1;
    [JsonPropertyName("speed")] public float Speed { get; set; } = 1;
    [JsonPropertyName("scale")] public int Scale { get; set; } = -1;

    [JsonIgnore] public string SiteKey => Key.Split('@')[0];
    [JsonIgnore] public string VodId => Key.Split('@').Length > 1 ? Key.Split('@')[1] : "";
}

public class Keep
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("cid")] public int Cid { get; set; }
    [JsonPropertyName("siteName")] public string SiteName { get; set; } = "";
    [JsonPropertyName("vodName")] public string VodName { get; set; } = "";
    [JsonPropertyName("vodPic")] public string VodPic { get; set; } = "";
    [JsonPropertyName("createTime")] public long CreateTime { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }

    [JsonIgnore] public string SiteKey => Key.Split('@')[0];
    [JsonIgnore] public string VodId => Key.Split('@').Length > 1 ? Key.Split('@')[1] : "";
}

public class Device
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("serial")] public string Serial { get; set; } = "";
    [JsonPropertyName("eth")] public string Eth { get; set; } = "";
    [JsonPropertyName("wlan")] public string Wlan { get; set; } = "";
    [JsonPropertyName("time")] public long Time { get; set; }
}
