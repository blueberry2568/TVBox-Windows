namespace TVBoxForWindows.Core;

public static class AppPaths
{
    public static string Root { get; private set; }
    public static string Cache => Path.Combine(Root, "cache");
    public static string Js => Path.Combine(Root, "js");
    public static string Live => Path.Combine(Root, "live");
    public static string Wall => Path.Combine(Root, "wall");
    public static string Restore => Path.Combine(Root, "restore");
    public static string Local => Path.Combine(Root, "local");
    public static string Node => Path.Combine(Root, "node");

    public static void Init()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TVBox for Windows");
        foreach (var dir in new[] { Root, Cache, Js, Live, Wall, Restore, Local, Node })
            Directory.CreateDirectory(dir);
    }

    /// <summary>JAR/Python 运行时已移除；后台清理旧版本留下的下载与虚拟环境。</summary>
    public static void CleanupLegacyRuntimes()
    {
        foreach (var name in new[] { "runtime", "jar", "py", "pyenv" })
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(Root, name));
                if (path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }
    }

    public static string AssetDir => Path.Combine(AppContext.BaseDirectory, "Assets");
    public static string AssetNode => Path.Combine(AssetDir, "node");

    public static string ReadAsset(string relative)
    {
        var file = Path.Combine(AssetDir, relative.Replace("assets://", "").Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }
}
