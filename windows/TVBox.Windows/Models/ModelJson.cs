using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TVBoxForWindows.Models;

/// <summary>模型反序列化选项：宽容处理脏配置（数字/字符串互转、字符串或对象二象性字段）。</summary>
public static class ModelJson
{
    public static readonly JsonSerializerOptions Options = Build();

    static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new FlexStringConverter());
        o.Converters.Add(new FlexStringListConverter());
        o.Converters.Add(new HeaderMapConverter());
        return o;
    }

    public static T Parse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch { return default; }
    }

    public static T Parse<T>(JsonNode node)
    {
        try { return node == null ? default : node.Deserialize<T>(Options); }
        catch { return default; }
    }

    public static string Stringify(object obj) => JsonSerializer.Serialize(obj, Options);
}

/// <summary>string 字段兼容数字/布尔/对象（对象序列化为原始 JSON 字符串，等价 Site.ext 的处理）。</summary>
public class FlexStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => null,
        _ => JsonNode.Parse(ref reader)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions o) => writer.WriteStringValue(value);
}

/// <summary>List&lt;string&gt; 兼容单个字符串写法。</summary>
public class FlexStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var list = new List<string>();
        if (reader.TokenType == JsonTokenType.String) { list.Add(reader.GetString()); return list; }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString());
                else if (reader.TokenType == JsonTokenType.Number) list.Add(reader.GetDouble().ToString());
                else JsonNode.Parse(ref reader);
            }
        }
        else JsonNode.Parse(ref reader);
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions o)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>header 字典兼容 JSON 字符串与对象两种写法（等价 HeaderAdapter），value 为数组时取首个。</summary>
public class HeaderMapConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonNode node;
        if (reader.TokenType == JsonTokenType.String)
        {
            try { node = JsonNode.Parse(reader.GetString() ?? ""); } catch { return map; }
        }
        else node = JsonNode.Parse(ref reader);
        if (node is JsonObject obj)
            foreach (var kv in obj)
                map[kv.Key] = kv.Value is JsonArray arr ? arr.FirstOrDefault()?.ToString() ?? "" : kv.Value?.ToString() ?? "";
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions o)
    {
        writer.WriteStartObject();
        foreach (var kv in value) writer.WriteString(kv.Key, kv.Value);
        writer.WriteEndObject();
    }
}
