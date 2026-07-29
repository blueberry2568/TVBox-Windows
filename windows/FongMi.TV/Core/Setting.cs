using System.Collections.Concurrent;
using System.Text.Json;

namespace FongMi.TV.Core;

/// <summary>SharedPreferences 等价物：键值存储，落盘为 prefs.json。也承载 JS local / /cache 端点。</summary>
public static class Setting
{
    static ConcurrentDictionary<string, string> Map = new();
    static string FilePath => Path.Combine(AppPaths.Root, "prefs.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Map = new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch { Map = new(); }
    }

    /// <summary>修复旧版设置页初始化时把默认倍速误写为 0.5x 的一次性数据迁移。</summary>
    public static bool Migrate()
    {
        const string key = "migration_speed_init_20260727";
        if (GetBool(key)) return false;
        var speed = GetFloat("speed", 1f);
        var fixedSpeed = speed < 1f || speed > 4f;
        if (fixedSpeed) Map["speed"] = "1";
        if (GetFloat("danmaku_alpha", 0.9f) <= 0.1f) Map["danmaku_alpha"] = "0.9";
        if (GetInt("danmaku_size", 24) <= 12) Map["danmaku_size"] = "24";
        Map[key] = "true";
        Save();
        return fixedSpeed;
    }

    static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Map, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public static string GetString(string key, string def = "") => Map.TryGetValue(key, out var v) ? v : def;
    public static int GetInt(string key, int def = 0) => int.TryParse(GetString(key, null), out var v) ? v : def;
    public static bool GetBool(string key, bool def = false) => bool.TryParse(GetString(key, null), out var v) ? v : def;
    public static float GetFloat(string key, float def = 0) => float.TryParse(GetString(key, null), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

    /// <summary>写入统一用 InvariantCulture，避免逗号小数区域下浮点设置回读失败。</summary>
    public static void Put(string key, object value) { Map[key] = value is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : value?.ToString() ?? ""; Save(); }
    public static void Remove(string key) { Map.TryRemove(key, out _); Save(); }

    // ---- 应用设置（与 Android 端 Setting 对应）----
    public static string Doh { get => GetString("doh"); set => Put("doh", value); }
    public static string Ua { get => GetString("ua", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"); set => Put("ua", value); }
    public static string Proxy { get => GetString("proxy"); set => Put("proxy", value); }
    public static int Quality { get => GetInt("quality", 2); set => Put("quality", value); }
    public static int SiteTimeout { get => GetInt("site_timeout", 15000); set => Put("site_timeout", value); }
    public static int PlayTimeout { get => GetInt("play_timeout", 15000); set => Put("play_timeout", value); }
    public static bool LocalServerLan { get => GetBool("local_server_lan"); set => Put("local_server_lan", value); }
    public static bool Incognito { get => GetBool("incognito"); set => Put("incognito", value); }
    public static bool DanmakuLoad { get => GetBool("danmaku_load", true); set => Put("danmaku_load", value); }
    public static bool DanmakuAuto { get => GetBool("danmaku_auto"); set => Put("danmaku_auto", value); }
    public static string DanmakuApi { get => GetString("danmaku_api"); set => Put("danmaku_api", value); }
    public static double Speed { get => GetFloat("speed", 1f); set => Put("speed", value); }
    public static int Scale { get => GetInt("scale"); set => Put("scale", value); }
    public static int SearchDisplay { get => GetInt("search_display"); set => Put("search_display", value); }
    public static int Flag { get => GetInt("flag", 2); set => Put("flag", value); } // 播放器解码偏好
    public static string Keep { get => GetString("keep"); set => Put("keep", value); }
    public static string HomeSite { get => GetString("home_site"); set => Put("home_site", value); }
    public static string Parse { get => GetString("parse"); set => Put("parse", value); }
    public static string ConfigVod { get => GetString("config_vod"); set => Put("config_vod", value); }
    public static string ConfigLive { get => GetString("config_live"); set => Put("config_live", value); }
    public static string ConfigWall { get => GetString("config_wall"); set => Put("config_wall", value); }
}
