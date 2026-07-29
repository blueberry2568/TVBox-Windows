using System.Text.Json;

namespace TVBoxForWindows.Core;

/// <summary>Thread-safe key/value settings persisted in prefs.json.</summary>
public static class Setting
{
    static readonly object Sync = new();
    static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    static string FilePath => Path.Combine(AppPaths.Root, "prefs.json");

    public static void Load()
    {
        lock (Sync)
        {
            var loaded = DurableJsonFile.Read(
                FilePath,
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json),
                () => new Dictionary<string, string>());
            _map = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
    }

    /// <summary>Repairs invalid playback defaults written by older builds.</summary>
    public static bool Migrate()
    {
        const string key = "migration_speed_init_20260727";
        lock (Sync)
        {
            if (GetBoolLocked(key)) return false;

            var speed = GetFloatLocked("speed", 1f);
            var fixedSpeed = speed < 1f || speed > 4f;
            if (fixedSpeed) _map["speed"] = "1";
            if (GetFloatLocked("danmaku_alpha", 0.9f) <= 0.1f) _map["danmaku_alpha"] = "0.9";
            if (GetIntLocked("danmaku_size", 24) <= 12) _map["danmaku_size"] = "24";
            _map[key] = "true";
            SaveLocked();
            return fixedSpeed;
        }
    }

    public static string GetString(string key, string def = "")
    {
        lock (Sync) return _map.TryGetValue(key, out var value) ? value : def;
    }

    public static int GetInt(string key, int def = 0)
    {
        lock (Sync) return GetIntLocked(key, def);
    }

    public static bool GetBool(string key, bool def = false)
    {
        lock (Sync) return GetBoolLocked(key, def);
    }

    public static float GetFloat(string key, float def = 0)
    {
        lock (Sync) return GetFloatLocked(key, def);
    }

    public static void Put(string key, object value)
    {
        var text = value is IFormattable formattable
            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            : value?.ToString() ?? "";

        lock (Sync)
        {
            if (_map.TryGetValue(key, out var current) && current == text) return;
            _map[key] = text;
            SaveLocked();
        }
    }

    public static void Remove(string key)
    {
        lock (Sync)
        {
            if (!_map.Remove(key)) return;
            SaveLocked();
        }
    }

    static void SaveLocked()
    {
        var snapshot = new Dictionary<string, string>(_map, StringComparer.Ordinal);
        DurableJsonFile.Write(FilePath, JsonSerializer.Serialize(snapshot, SaveOptions));
    }

    static string GetStringLocked(string key, string def = "") =>
        _map.TryGetValue(key, out var value) ? value : def;

    static int GetIntLocked(string key, int def = 0) =>
        int.TryParse(GetStringLocked(key, null), out var value) ? value : def;

    static bool GetBoolLocked(string key, bool def = false) =>
        bool.TryParse(GetStringLocked(key, null), out var value) ? value : def;

    static float GetFloatLocked(string key, float def = 0) =>
        float.TryParse(
            GetStringLocked(key, null),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : def;

    // Application settings. All user-facing switches default to off.
    public static string Doh { get => GetString("doh"); set => Put("doh", value); }
    public static string Ua { get => GetString("ua", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"); set => Put("ua", value); }
    public static string Proxy { get => GetString("proxy"); set => Put("proxy", value); }
    public static int Quality { get => GetInt("quality", 2); set => Put("quality", value); }
    public static int SiteTimeout { get => GetInt("site_timeout", 15000); set => Put("site_timeout", value); }
    public static int PlayTimeout { get => GetInt("play_timeout", 15000); set => Put("play_timeout", value); }
    public static bool LocalServerLan { get => GetBool("local_server_lan"); set => Put("local_server_lan", value); }
    public static bool Incognito { get => GetBool("incognito"); set => Put("incognito", value); }
    public static bool DanmakuLoad { get => GetBool("danmaku_load"); set => Put("danmaku_load", value); }
    public static bool DanmakuAuto { get => GetBool("danmaku_auto"); set => Put("danmaku_auto", value); }
    public static string DanmakuApi { get => GetString("danmaku_api"); set => Put("danmaku_api", value); }
    public static double Speed { get => GetFloat("speed", 1f); set => Put("speed", value); }
    public static int Scale { get => GetInt("scale"); set => Put("scale", value); }
    public static int SearchDisplay { get => GetInt("search_display"); set => Put("search_display", value); }
    public static int Flag { get => GetInt("flag", 2); set => Put("flag", value); }
    public static string Keep { get => GetString("keep"); set => Put("keep", value); }
    public static string HomeSite { get => GetString("home_site"); set => Put("home_site", value); }
    public static string Parse { get => GetString("parse"); set => Put("parse", value); }
    public static string ConfigVod { get => GetString("config_vod"); set => Put("config_vod", value); }
    public static string ConfigLive { get => GetString("config_live"); set => Put("config_live", value); }
    public static string ConfigWall { get => GetString("config_wall"); set => Put("config_wall", value); }
}
