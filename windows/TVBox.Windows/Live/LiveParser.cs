using System.Text.RegularExpressions;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Live;

/// <summary>直播列表解析（TXT / M3U / JSON 自动识别，移植 LIVE.md 规格）。</summary>
public static class LiveParser
{
    static readonly Regex ExtInfAttr = new("([\\w-]+)=\"([^\"]*)\"");

    public static async Task Parse(Models.Live live)
    {
        string text;
        if (!string.IsNullOrEmpty(live.Api) && SpiderSupported(live))
        {
            var spider = await Engine.SpiderLoader.Instance.GetLiveSpider(live);
            text = await spider.LiveContent(live.Url);
        }
        else
        {
            text = await Net.HttpUtil.Load(live.Url, live.BuildHeaders());
        }
        Text(live, text);
    }

    static bool SpiderSupported(Models.Live live) => live.Api.EndsWith(".js") || live.Api.Contains(".js?");

    public static void Text(Models.Live live, string text)
    {
        live.Groups.Clear();
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.StartsWith('[')) Json(live, text);
        else if (text.Contains("#EXTM3U") && !text.Contains("#genre#")) M3u(live, text);
        else Txt(live, text);
        foreach (var group in live.Groups)
            foreach (var channel in group.Channel) { channel.Group = group; channel.Live = live; }
    }

    static void Json(Models.Live live, string text)
    {
        live.Groups.AddRange(ModelJson.Parse<List<LiveGroup>>(text) ?? new());
    }

    static LiveGroup GetGroup(Models.Live live, string name)
    {
        var temp = LiveGroup.Create(name);
        var exist = live.Groups.FirstOrDefault(g => g.Name == temp.Name);
        if (exist != null) return exist;
        live.Groups.Add(temp);
        return temp;
    }

    static LiveChannel GetChannel(LiveGroup group, string name)
    {
        var exist = group.Find(name);
        if (exist != null) return exist;
        var channel = new LiveChannel { Name = name };
        group.Channel.Add(channel);
        return channel;
    }

    // ---------------- TXT ----------------
    static void Txt(Models.Live live, string text)
    {
        var group = GetGroup(live, "默认");
        var setting = new LineSetting();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Contains("#genre#"))
            {
                group = GetGroup(live, line.Split(',')[0].Trim());
                setting = new LineSetting();
                continue;
            }
            var comma = line.IndexOf(',');
            if (comma < 0 || !line.Contains("://"))
            {
                setting.Apply(line); // 指令行，作用至下一个 #genre#
                continue;
            }
            var name = line[..comma].Trim();
            var body = line[(comma + 1)..].Trim();
            if (!body.Contains("://")) { setting.Apply(line); continue; }
            var channel = GetChannel(group, name);
            channel.Urls.AddRange(body.Split('#').Where(u => u.Contains("://")).Select(u => u.Trim()));
            setting.CopyTo(channel);
        }
        live.Groups.RemoveAll(g => g.Channel.Count == 0);
    }

    // ---------------- M3U ----------------
    static void M3u(Models.Live live, string text)
    {
        LiveChannel channel = null;
        var setting = new LineSetting();
        var globalCatchup = new Catchup();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTM3U"))
            {
                var attrs = ParseAttrs(line);
                if (string.IsNullOrEmpty(live.Epg) && attrs.TryGetValue("tvg-url", out var tvg)) live.Epg = tvg;
                if (string.IsNullOrEmpty(live.Epg) && attrs.TryGetValue("url-tvg", out var utv)) live.Epg = utv;
                attrs.TryGetValue("catchup", out var ct); attrs.TryGetValue("catchup-source", out var cs); attrs.TryGetValue("catchup-replace", out var cr);
                globalCatchup = new Catchup { Type = ct ?? "", Source = cs ?? "", Replace = cr ?? "" };
                continue;
            }
            if (line.StartsWith("#EXTINF:"))
            {
                var attrs = ParseAttrs(line);
                var name = line[(line.LastIndexOf(',') + 1)..].Trim();
                var group = GetGroup(live, attrs.GetValueOrDefault("group-title", "默认"));
                channel = GetChannel(group, string.IsNullOrEmpty(name) ? attrs.GetValueOrDefault("tvg-name", "") : name);
                if (attrs.TryGetValue("tvg-id", out var id)) channel.TvgId = id;
                if (attrs.TryGetValue("tvg-name", out var tn)) channel.TvgName = tn;
                if (attrs.TryGetValue("tvg-chno", out var no)) channel.Number = no;
                if (attrs.TryGetValue("tvg-logo", out var logo)) channel.Logo = logo;
                if (attrs.TryGetValue("http-user-agent", out var ua)) channel.Ua = ua;
                attrs.TryGetValue("catchup", out var ct); attrs.TryGetValue("catchup-source", out var cs); attrs.TryGetValue("catchup-replace", out var cr);
                var catchup = new Catchup
                {
                    Type = ct ?? globalCatchup.Type,
                    Source = cs ?? globalCatchup.Source,
                    Replace = cr ?? globalCatchup.Replace,
                };
                if (catchup.IsUsable) channel.Catchup = catchup;
                setting = new LineSetting();
                continue;
            }
            if (line.StartsWith("#KODIPROP:")) { setting.Kodi(line[10..]); continue; }
            if (line.StartsWith("#EXTHTTP:")) { setting.HeaderJson(line[9..]); continue; }
            if (line.StartsWith("#EXTVLCOPT:")) { setting.Vlc(line[11..]); continue; }
            if (line.StartsWith('#')) continue;
            if (!line.Contains("://")) { setting.Apply(line); continue; }
            if (channel == null) continue;
            channel.Urls.Add(line);
            setting.CopyTo(channel);
            setting = new LineSetting(); // M3U：指令仅作用于紧接的下一个 URL
        }
        live.Groups.RemoveAll(g => g.Channel.Count == 0);
    }

    static Dictionary<string, string> ParseAttrs(string line)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ExtInfAttr.Matches(line).ToArray()) map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    /// <summary>指令行状态（ua= referer= header= format= parse= click= forceKey= origin= 与 KODIPROP DRM）。</summary>
    class LineSetting
    {
        string ua, origin, referer, format, click;
        Dictionary<string, string> header;
        int? parse;
        bool? forceKey;
        string drmType, drmKey;

        public void Apply(string line)
        {
            if (line.StartsWith("ua=")) ua = line[3..].Trim();
            else if (line.StartsWith("origin=")) origin = line[7..].Trim();
            else if (line.StartsWith("referer=")) referer = line[8..].Trim();
            else if (line.StartsWith("referrer=")) referer = line[9..].Trim();
            else if (line.StartsWith("header=")) HeaderJson(line[7..]);
            else if (line.StartsWith("format=")) format = MapFormat(line[7..].Trim());
            else if (line.StartsWith("parse=")) parse = line[6..].Trim() == "1" ? 1 : 0;
            else if (line.StartsWith("click=")) click = line[6..].Trim();
            else if (line.StartsWith("forceKey=")) forceKey = line[9..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void HeaderJson(string json)
        {
            header ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in JsonUtil.ToMap(json.Trim())) header[kv.Key] = kv.Value;
        }

        public void Vlc(string line)
        {
            if (line.StartsWith("http-user-agent=")) ua = line[16..].Trim();
            else if (line.StartsWith("http-referrer=")) referer = line[14..].Trim();
            else if (line.StartsWith("http-origin=")) origin = line[12..].Trim();
        }

        public void Kodi(string line)
        {
            var kv = line.Split('=', 2);
            if (kv.Length != 2) return;
            var key = kv[0].Trim();
            var value = kv[1].Trim();
            switch (key)
            {
                case "inputstream.adaptive.license_type": drmType = value; break;
                case "inputstream.adaptive.license_key": drmKey = value; break;
                case "inputstream.adaptive.manifest_type": format = MapFormat(value); break;
                case "inputstream.adaptive.drm_legacy":
                    var parts = value.Split('|', 2);
                    drmType = parts[0].Trim();
                    if (parts.Length > 1) drmKey = parts[1].Trim();
                    break;
                case "inputstream.adaptive.stream_headers":
                case "inputstream.adaptive.common_headers":
                    header ??= new(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in value.Split('&'))
                    {
                        var p = pair.Split('=', 2);
                        if (p.Length != 2) continue;
                        if (p[0] == "drmScheme") drmType = p[1];
                        else if (p[0] == "drmLicense") drmKey = p[1];
                        else header[p[0]] = p[1];
                    }
                    break;
            }
        }

        static string MapFormat(string value) => value.ToLowerInvariant() switch
        {
            "hls" => "application/x-mpegURL",
            "mpd" or "dash" => "application/dash+xml",
            _ => value,
        };

        public void CopyTo(LiveChannel channel)
        {
            if (ua != null) channel.Ua = ua;
            if (origin != null) channel.Origin = origin;
            if (referer != null) channel.Referer = referer;
            if (format != null) channel.Format = format;
            if (click != null) channel.Click = click;
            if (parse != null) channel.Parse = parse.Value;
            if (header != null)
                foreach (var kvp in header) channel.Header[kvp.Key] = kvp.Value;
            if (drmType != null)
            {
                channel.Drm ??= new Drm();
                channel.Drm.Type = drmType.ToLowerInvariant();
                if (drmKey != null) channel.Drm.Key = drmKey;
                if (forceKey != null) channel.Drm.ForceKey = forceKey.Value;
            }
        }
    }
}
