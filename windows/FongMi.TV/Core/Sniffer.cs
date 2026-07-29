using System.Text.RegularExpressions;
using FongMi.TV.Models;

namespace FongMi.TV.Core;

/// <summary>媒体 URL 嗅探判定（移植自 Sniffer.java），rules 由配置注入。</summary>
public static class Sniffer
{
    public static readonly Regex AiPush = new(@"(https?|thunder|magnet|ed2k|video):\S+");
    public static readonly Regex Media = new(@"https?://[^\s]{12,}\.(?:m3u8|mp4|mkv|flv|mp3|m4a|aac|mpd)(?:\?.*)?|https?://.*?video/tos[^\s]*|rtmp:[^\s]+");

    static List<Rule> _rules = new();
    public static void SetRules(List<Rule> rules) => _rules = rules ?? new();
    public static List<Rule> Rules => _rules;

    public static string GetUrl(string text)
    {
        if (JsonUtil.IsObj(text) || text.Contains('$')) return text;
        var m = AiPush.Match(text);
        return m.Success ? m.Value : text;
    }

    public static bool IsVideoFormat(string url)
    {
        var rule = GetRule(url);
        foreach (var exclude in rule.Exclude) if (url.Contains(exclude)) return false;
        foreach (var exclude in rule.Exclude) if (SafeMatch(url, exclude)) return false;
        foreach (var regex in rule.Regex) if (url.Contains(regex)) return true;
        foreach (var regex in rule.Regex) if (SafeMatch(url, regex)) return true;
        if (url.Contains("url=http") || url.Contains("v=http") || url.Contains(".html")) return false;
        return Media.IsMatch(url);
    }

    public static List<string> GetScript(string url) => GetRule(url).Script;

    public static Rule GetRule(string url)
    {
        var host = UrlUtil.Host(url);
        if (string.IsNullOrEmpty(host)) return Rule.Empty();
        string embedded = "";
        try
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["url"];
            embedded = UrlUtil.Host(q ?? "");
        }
        catch { }
        var hosts = host + "," + embedded;
        foreach (var rule in _rules)
            foreach (var h in rule.Hosts)
                if (Net.NetworkConfig.ContainOrMatch(hosts, h)) return rule;
        return Rule.Empty();
    }

    static bool SafeMatch(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern); } catch { return false; }
    }
}
