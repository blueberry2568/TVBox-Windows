namespace TVBoxForWindows.Core;

public static class AppPaths
{
    static readonly Lazy<string> InstallDirectory = new(ResolveInstallRoot);

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

    /// <summary>The directory containing TVBox.exe.</summary>
    public static string InstallRoot => InstallDirectory.Value;

    public static string AssetDir => Path.Combine(InstallRoot, "assets");
    public static string IconDir => Path.Combine(AssetDir, "icons");
    public static string NodeRuntimeDir => Path.Combine(InstallRoot, "node");
    public static string FFmpegDir => Path.Combine(InstallRoot, "ffmpeg");

    public static string ReadAsset(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";

        var assetPath = relative.Trim();
        if (assetPath.StartsWith("assets://", StringComparison.OrdinalIgnoreCase))
            assetPath = assetPath["assets://".Length..];
        assetPath = assetPath.TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        try
        {
            var assetRoot = Path.GetFullPath(AssetDir);
            var file = Path.GetFullPath(Path.Combine(assetRoot, assetPath));
            var rootPrefix = assetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            if (!file.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return "";
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }
        catch
        {
            return "";
        }
    }

    static string ResolveInstallRoot()
    {
        var processPath = Environment.ProcessPath;
        var processDirectory = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);
#pragma warning disable IL3000
        var assemblyDirectory = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location);
#pragma warning restore IL3000

        foreach (var candidate in new[] { processDirectory, assemblyDirectory, AppContext.BaseDirectory }
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (IsInstallRoot(fullPath))
                    return fullPath;

                // Framework-dependent deployment loads TVBox.dll from libs while
                // the apphost and all runtime components remain one level above it.
                var parent = Directory.GetParent(fullPath)?.FullName;
                if (string.Equals(Path.GetFileName(fullPath), "libs", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(parent) && IsInstallRoot(parent))
                    return Path.GetFullPath(parent);
            }
            catch { }
        }

        // A normal launch always uses TVBox.exe, so its directory remains the most
        // useful failure location even when an installation is missing resources.
        if (!string.IsNullOrWhiteSpace(processDirectory) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "TVBox", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(processDirectory);

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    static bool IsInstallRoot(string path)
    {
        return Directory.Exists(Path.Combine(path, "assets")) ||
               Directory.Exists(Path.Combine(path, "node")) ||
               Directory.Exists(Path.Combine(path, "ffmpeg"));
    }
}
