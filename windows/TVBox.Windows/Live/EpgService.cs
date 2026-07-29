using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Live;

/// <summary>EPG 节目表：XMLTV（支持 .gz）与 API 模板两种来源，XMLTV 每 6 小时刷新。</summary>
public class EpgService
{
    public static EpgService Instance { get; } = new();

    readonly ConcurrentDictionary<string, (Dictionary<string, Epg> map, DateTime time)> _xmltv = new();
    readonly ConcurrentDictionary<string, Epg> _api = new();

    public async Task<Epg> Get(LiveChannel channel)
    {
        var epgConf = !string.IsNullOrEmpty(channel.Epg) ? channel.Epg : channel.Live?.Epg ?? "";
        if (string.IsNullOrEmpty(epgConf)) return null;
        foreach (var source in epgConf.Split(','))
        {
            var trimmed = source.Trim();
            if (trimmed.Length == 0) continue;
            Epg result = null;
            if (trimmed.Contains(".xml") || trimmed.Contains(".gz") || trimmed.Contains("xmltv"))
                result = await FromXmlTv(trimmed, channel);
            else if (trimmed.Contains('{'))
                result = await FromApi(channel);
            if (result != null && result.List.Count > 0) return result;
        }
        return null;
    }

    async Task<Epg> FromApi(LiveChannel channel)
    {
        var url = channel.GetEpgUrl(DateTime.Today);
        if (string.IsNullOrEmpty(url)) return null;
        if (_api.TryGetValue(url, out var hit)) return hit;
        try
        {
            var json = await Net.HttpUtil.GetString(url);
            var node = JsonUtil.Parse(json);
            var epg = new Epg { Key = channel.Name };
            // 常见 API 结构：{"epg_data":[{title,start,end}]} 或 {"data":[...]} 或直接数组
            var arr = node?["epg_data"] ?? node?["data"] ?? (node is System.Text.Json.Nodes.JsonArray ? node : null);
            if (arr is System.Text.Json.Nodes.JsonArray list)
                foreach (var item in list)
                {
                    var data = new EpgData
                    {
                        Title = JsonUtil.SafeString(item, "title"),
                        Start = JsonUtil.SafeString(item, "start"),
                        End = JsonUtil.SafeString(item, "end"),
                    };
                    ParseTimes(data, DateTime.Today);
                    epg.List.Add(data);
                }
            _api[url] = epg;
            return epg;
        }
        catch { return null; }
    }

    static void ParseTimes(EpgData data, DateTime date)
    {
        if (TimeSpan.TryParse(data.Start, out var st)) data.StartTime = new DateTimeOffset(date + st).ToUnixTimeMilliseconds();
        if (TimeSpan.TryParse(data.End, out var et)) data.EndTime = new DateTimeOffset(date + et).ToUnixTimeMilliseconds();
        if (data.EndTime > 0 && data.EndTime < data.StartTime) data.EndTime += 24 * 3600 * 1000; // 跨日
    }

    async Task<Epg> FromXmlTv(string url, LiveChannel channel)
    {
        var map = await LoadXmlTv(url);
        if (map == null) return null;
        var keys = new[] { channel.TvgId, channel.TvgName, channel.Name }.Where(k => !string.IsNullOrEmpty(k));
        foreach (var key in keys)
            if (map.TryGetValue(key, out var epg)) return epg;
        return null;
    }

    async Task<Dictionary<string, Epg>> LoadXmlTv(string url)
    {
        if (_xmltv.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.time < TimeSpan.FromHours(6)) return hit.map;
        try
        {
            var res = await Net.HttpUtil.Get(url, timeoutMs: 60000);
            var bytes = res.Body;
            if (url.Contains(".gz") || (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B))
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                await gz.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            var map = ParseXmlTv(System.Text.Encoding.UTF8.GetString(bytes));
            _xmltv[url] = (map, DateTime.UtcNow);
            return map;
        }
        catch (Exception e) { Logger.E("Epg", e.Message); return null; }
    }

    static Dictionary<string, Epg> ParseXmlTv(string xml)
    {
        var map = new Dictionary<string, Epg>(StringComparer.OrdinalIgnoreCase);
        var idToNames = new Dictionary<string, List<string>>();
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        foreach (XmlNode ch in doc.SelectNodes("//channel"))
        {
            var id = ch.Attributes?["id"]?.Value ?? "";
            var names = new List<string>();
            foreach (XmlNode dn in ch.SelectNodes("display-name")) names.Add(dn.InnerText.Trim());
            if (id.Length > 0) idToNames[id] = names;
        }
        Epg GetEpg(string key)
        {
            if (!map.TryGetValue(key, out var epg)) map[key] = epg = new Epg { Key = key };
            return epg;
        }
        foreach (XmlNode prog in doc.SelectNodes("//programme"))
        {
            var chId = prog.Attributes?["channel"]?.Value ?? "";
            var start = ParseXmltvTime(prog.Attributes?["start"]?.Value);
            var stop = ParseXmltvTime(prog.Attributes?["stop"]?.Value);
            var title = prog.SelectSingleNode("title")?.InnerText?.Trim() ?? "";
            if (chId.Length == 0 || start == 0) continue;
            var data = new EpgData
            {
                Title = title,
                StartTime = start,
                EndTime = stop,
                Start = DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime().ToString("HH:mm"),
                End = stop > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(stop).ToLocalTime().ToString("HH:mm") : "",
            };
            GetEpg(chId).List.Add(data);
            if (idToNames.TryGetValue(chId, out var names))
                foreach (var name in names) GetEpg(name).List.Add(data);
        }
        return map;
    }

    static long ParseXmltvTime(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        try
        {
            var formats = new[] { "yyyyMMddHHmmss zzz", "yyyyMMddHHmmss zz", "yyyyMMddHHmmss" };
            foreach (var fmt in formats)
                if (DateTimeOffset.TryParseExact(value.Trim(), fmt, null, System.Globalization.DateTimeStyles.AssumeLocal, out var dto))
                    return dto.ToUnixTimeMilliseconds();
        }
        catch { }
        return 0;
    }
}
