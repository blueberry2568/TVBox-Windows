using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TVBoxForWindows.Core;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsObj(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (!text.StartsWith('{') && !text.StartsWith('[')) return false;
        try { JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); return true; }
        catch { return false; }
    }

    public static JsonNode Parse(string text)
    {
        try { return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch { return null; }
    }

    public static T Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch { return default; }
    }

    public static string Serialize(object obj) => JsonSerializer.Serialize(obj, Options);

    public static string SafeString(JsonNode node, string key)
    {
        var v = node?[key];
        if (v == null) return "";
        return v is JsonValue ? v.ToString() : v.ToJsonString(Options);
    }

    public static List<string> SafeListString(JsonNode node, string key)
    {
        var list = new List<string>();
        if (node?[key] is JsonArray arr)
        {
            foreach (var item in arr) if (item != null) list.Add(item.ToString());
        }
        else if (node?[key] is JsonValue val)
        {
            list.Add(val.ToString());
        }
        return list;
    }

    /// <summary>把 JSON 对象转成 string→string 字典（值为对象时序列化）。</summary>
    public static Dictionary<string, string> ToMap(JsonNode node)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject obj)
            foreach (var kv in obj)
                map[kv.Key] = kv.Value is JsonValue ? kv.Value.ToString() : kv.Value?.ToJsonString(Options) ?? "";
        return map;
    }

    public static Dictionary<string, string> ToMap(string json) => string.IsNullOrEmpty(json) ? new() : ToMap(Parse(json));
}
