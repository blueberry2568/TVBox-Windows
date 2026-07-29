using System.Text.Json.Nodes;
using System.Xml;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Player;

/// <summary>单条弹幕：Mode 1=滚动 4=底部 5=顶部；Color 为 24 位 RGB。</summary>
public class DanmakuItem { public long TimeMs; public string Text; public int Mode; public uint Color; }

/// <summary>弹幕解析引擎（替代 DanmakuFlameMaster 的解析层）：
/// 支持 B 站 XML（&lt;d p="time,mode,size,color,..."&gt;text&lt;/d&gt;）与 JSON
/// （dplayer 数组 [time,type,color,author,text] / 对象数组 / 每行一条 JSON）。</summary>
public class DanmakuEngine
{
    const string TAG = "DanmakuEngine";

    public List<DanmakuItem> Items { get; } = new();

    /// <summary>加载弹幕：入参可为 URL、本地文件路径或原始文本。解析后按时间排序。</summary>
    public async Task LoadAsync(string urlOrText)
    {
        Clear();
        try
        {
            var text = urlOrText ?? "";
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) text = await Net.HttpUtil.GetString(text);
            else if (text.Length < 260 && File.Exists(text)) text = await File.ReadAllTextAsync(text);
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.StartsWith('<')) ParseXml(text);
            else ParseJson(text);
            Items.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            Logger.D(TAG, "弹幕加载完成: " + Items.Count);
        }
        catch (Exception e) { Logger.E(TAG, "弹幕加载失败: " + e.Message); }
    }

    public void Clear() => Items.Clear();

    /// <summary>B 站 XML：p="出现秒,模式,字号,颜色,..."。</summary>
    void ParseXml(string text)
    {
        var doc = new XmlDocument();
        doc.LoadXml(text);
        foreach (XmlNode node in doc.SelectNodes("//d"))
        {
            try
            {
                var p = node.Attributes?["p"]?.Value ?? "";
                var parts = p.Split(',');
                if (parts.Length == 0) continue;
                double time = ToDouble(parts[0]);
                int mode = parts.Length > 1 ? (int)ToDouble(parts[1]) : 1;
                uint color = parts.Length > 3 ? (uint)(long)ToDouble(parts[3]) & 0xFFFFFF : 0xFFFFFF;
                Add(time, NormalizeBiliMode(mode), color, node.InnerText);
            }
            catch { }
        }
    }

    void ParseJson(string text)
    {
        var node = JsonUtil.Parse(text);
        if (node == null)
        {
            // 每行一条 JSON
            foreach (var line in text.Split('\n'))
            {
                var item = JsonUtil.Parse(line.Trim());
                if (item != null) AddNode(item);
            }
            return;
        }
        var arr = node as JsonArray ?? node["data"] as JsonArray ?? node["danmaku"] as JsonArray ?? node["danmuku"] as JsonArray;
        if (arr == null && node["data"] is JsonObject dataObj) arr = dataObj["danmaku"] as JsonArray ?? dataObj["list"] as JsonArray;
        if (arr == null) return;
        foreach (var item in arr) AddNode(item);
    }

    void AddNode(JsonNode item)
    {
        try
        {
            if (item is JsonArray arr)
            {
                // dplayer: [time, type, color, author, text]（type: 0滚动 1顶部 2底部）
                if (arr.Count < 2) return;
                var text = arr[^1]?.ToString() ?? "";
                double time = ToDouble(arr[0]?.ToString());
                int mode = arr.Count > 2 ? NormalizeDplayerMode((int)ToDouble(arr[1]?.ToString())) : 1;
                uint color = arr.Count > 3 ? ParseColor(arr[2]) : 0xFFFFFF;
                Add(time, mode, color, text);
            }
            else if (item is JsonObject obj)
            {
                var text = Str(obj, "text") ?? Str(obj, "m") ?? Str(obj, "content") ?? Str(obj, "danmaku");
                if (string.IsNullOrEmpty(text)) return;
                double time = ToDouble(Str(obj, "time") ?? Str(obj, "t") ?? Str(obj, "stime") ?? "0");
                int mode = obj["mode"] != null ? NormalizeBiliMode((int)ToDouble(Str(obj, "mode")))
                    : obj["type"] != null ? NormalizeDplayerMode((int)ToDouble(Str(obj, "type"))) : 1;
                uint color = ParseColor(obj["color"] ?? obj["c"]);
                Add(time, mode, color, text);
            }
        }
        catch { }
    }

    void Add(double timeSec, int mode, uint color, string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || timeSec < 0) return;
        Items.Add(new DanmakuItem { TimeMs = (long)(timeSec * 1000), Mode = mode, Color = color == 0 ? 0xFFFFFF : color, Text = text });
    }

    /// <summary>B 站模式：1-3 滚动，4 底部，5 顶部，其余归为滚动。</summary>
    static int NormalizeBiliMode(int mode) => mode is 4 or 5 ? mode : 1;

    /// <summary>dplayer 模式：0 滚动，1 顶部，2 底部。</summary>
    static int NormalizeDplayerMode(int type) => type switch { 1 => 5, 2 => 4, _ => 1 };

    static uint ParseColor(JsonNode node)
    {
        try
        {
            if (node == null) return 0xFFFFFF;
            var s = node.ToString().Trim();
            if (s.StartsWith('#')) return (uint)Convert.ToInt32(s[1..], 16) & 0xFFFFFF;
            return (uint)(long)ToDouble(s) & 0xFFFFFF;
        }
        catch { return 0xFFFFFF; }
    }

    static string Str(JsonObject obj, string key) => obj[key] is JsonValue v ? v.ToString() : null;

    static double ToDouble(object value)
    {
        try { return double.Parse(value?.ToString() ?? "0", System.Globalization.CultureInfo.InvariantCulture); }
        catch { return 0; }
    }
}
