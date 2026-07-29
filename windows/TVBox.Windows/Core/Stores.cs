using TVBoxForWindows.Models;

namespace TVBoxForWindows.Core;

/// <summary>JSON 文件持久化仓储（等价 Room：Config/History/Keep 三表）。</summary>
public static class Stores
{
    public const long HistoryTime = 60L * 24 * 60 * 60 * 1000; // 保留 60 天

    static readonly object Lock = new();
    static List<ConfigRecord> _configs;
    static List<History> _histories;
    static List<Keep> _keeps;
    static long _historyRevision;
    static long _keepRevision;

    public static long HistoryRevision => Interlocked.Read(ref _historyRevision);
    public static long KeepRevision => Interlocked.Read(ref _keepRevision);

    static string ConfigFile => Path.Combine(AppPaths.Root, "configs.json");
    static string HistoryFile => Path.Combine(AppPaths.Root, "history.json");
    static string KeepFile => Path.Combine(AppPaths.Root, "keep.json");

    static List<T> Load<T>(string file) => DurableJsonFile.Read(
        file,
        json => JsonUtil.Deserialize<List<T>>(json),
        () => new List<T>());

    static void Save<T>(string file, List<T> list)
    {
        // Every caller holds Lock. Serialize a stable list snapshot before the
        // atomic replacement so an interrupted write cannot truncate live data.
        DurableJsonFile.Write(file, JsonUtil.Serialize(list.ToList()));
    }

    // ---------- Config ----------
    public static List<ConfigRecord> Configs { get { lock (Lock) return _configs ??= Load<ConfigRecord>(ConfigFile); } }

    public static ConfigRecord FindConfig(string url, int type)
    {
        lock (Lock)
        {
            var item = Configs.FirstOrDefault(c => c.Url == url && c.Type == type);
            if (item == null)
            {
                item = new ConfigRecord { Id = Configs.Count == 0 ? 1 : Configs.Max(c => c.Id) + 1, Url = url, Type = type, Time = Now() };
                Configs.Add(item);
            }
            return item;
        }
    }

    public static void SaveConfig(ConfigRecord item)
    {
        lock (Lock)
        {
            item.Time = Now();
            Save(ConfigFile, Configs.OrderByDescending(c => c.Time).ToList());
        }
    }

    public static void DeleteConfig(string url, int type)
    {
        lock (Lock) { Configs.RemoveAll(c => c.Url == url && c.Type == type); Save(ConfigFile, Configs); }
    }

    public static List<ConfigRecord> GetConfigs(int type)
    {
        lock (Lock) return Configs.Where(c => c.Type == type && !string.IsNullOrEmpty(c.Url)).OrderByDescending(c => c.Time).ToList();
    }

    // ---------- History ----------
    public static List<History> Histories { get { lock (Lock) return _histories ??= Load<History>(HistoryFile); } }

    public static List<History> GetHistories(int cid)
    {
        var deadline = Now() - HistoryTime;
        lock (Lock) return Histories.Where(h => h.Cid == cid && h.CreateTime >= deadline).OrderByDescending(h => h.CreateTime).ToList();
    }

    public static History FindHistory(int cid, string key)
    {
        lock (Lock) return Histories.FirstOrDefault(h => h.Cid == cid && h.Key == key);
    }

    public static void SaveHistory(History item)
    {
        if (Setting.Incognito) return;
        lock (Lock)
        {
            Histories.RemoveAll(h => h.Cid == item.Cid && h.Key == item.Key);
            item.CreateTime = Now();
            Histories.Add(item);
            Histories.RemoveAll(h => h.CreateTime < Now() - HistoryTime);
            Save(HistoryFile, Histories);
            Interlocked.Increment(ref _historyRevision);
        }
    }

    public static void DeleteHistory(int cid, string key)
    {
        lock (Lock)
        {
            if (Histories.RemoveAll(h => h.Cid == cid && h.Key == key) == 0) return;
            Save(HistoryFile, Histories);
            Interlocked.Increment(ref _historyRevision);
        }
    }

    public static void DeleteHistories(int cid)
    {
        lock (Lock)
        {
            if (Histories.RemoveAll(h => h.Cid == cid) == 0) return;
            Save(HistoryFile, Histories);
            Interlocked.Increment(ref _historyRevision);
        }
    }

    /// <summary>随设置迁移一次性修正被旧版初始化缺陷写入历史记录的 0.5x。</summary>
    public static void NormalizePlaybackSpeed()
    {
        lock (Lock)
        {
            var changed = false;
            foreach (var item in Histories)
                if (item.Speed < 1f || item.Speed > 4f) { item.Speed = 1f; changed = true; }
            if (changed)
            {
                Save(HistoryFile, Histories);
                Interlocked.Increment(ref _historyRevision);
            }
        }
    }

    // ---------- Keep ----------
    public static List<Keep> Keeps { get { lock (Lock) return _keeps ??= Load<Keep>(KeepFile); } }

    public static List<Keep> GetKeeps(int cid)
    {
        lock (Lock) return Keeps.Where(k => k.Type == 0 && k.Cid == cid).OrderByDescending(k => k.CreateTime).ToList();
    }

    public static Keep FindKeep(int cid, string key)
    {
        lock (Lock) return Keeps.FirstOrDefault(k => k.Type == 0 && k.Cid == cid && k.Key == key);
    }

    public static void SaveKeep(Keep item)
    {
        lock (Lock)
        {
            Keeps.RemoveAll(k => k.Cid == item.Cid && k.Key == item.Key);
            item.CreateTime = Now();
            Keeps.Add(item);
            Save(KeepFile, Keeps);
            Interlocked.Increment(ref _keepRevision);
        }
    }

    public static void DeleteKeep(int cid, string key)
    {
        lock (Lock)
        {
            if (Keeps.RemoveAll(k => k.Cid == cid && k.Key == key) == 0) return;
            Save(KeepFile, Keeps);
            Interlocked.Increment(ref _keepRevision);
        }
    }

    public static long Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds();
}
