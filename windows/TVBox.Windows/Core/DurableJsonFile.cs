using System.Text;

namespace TVBoxForWindows.Core;

/// <summary>Crash-safe JSON persistence with a last-known-good backup.</summary>
internal static class DurableJsonFile
{
    public static T Read<T>(string path, Func<string, T> deserialize, Func<T> createDefault)
        where T : class
    {
        foreach (var candidate in new[] { path, BackupPath(path) })
        {
            if (!File.Exists(candidate)) continue;

            try
            {
                var value = deserialize(File.ReadAllText(candidate, Encoding.UTF8));
                if (value != null)
                {
                    if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                        Logger.E("Persistence", $"Recovered {Path.GetFileName(path)} from backup.");
                    return value;
                }

                Logger.E("Persistence", $"Ignored invalid JSON in {Path.GetFileName(candidate)}.");
            }
            catch (Exception e)
            {
                Logger.E("Persistence", $"Failed to read {Path.GetFileName(candidate)}: {e.Message}");
            }
        }

        return createDefault();
    }

    public static bool Write(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return false;

        string temp = null;

        try
        {
            Directory.CreateDirectory(directory);
            temp = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            var bytes = new UTF8Encoding(false).GetBytes(json ?? "");
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, BackupPath(path), true);
            }
            else
            {
                File.Move(temp, path);
                TryCreateInitialBackup(path);
            }

            return true;
        }
        catch (Exception e)
        {
            Logger.E("Persistence", $"Failed to save {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
        finally
        {
            try { if (!string.IsNullOrEmpty(temp) && File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    static string BackupPath(string path) => path + ".bak";

    static void TryCreateInitialBackup(string path)
    {
        try { File.Copy(path, BackupPath(path), true); }
        catch (Exception e) { Logger.E("Persistence", $"Failed to back up {Path.GetFileName(path)}: {e.Message}"); }
    }
}
