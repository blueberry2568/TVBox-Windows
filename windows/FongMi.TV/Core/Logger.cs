using System.Diagnostics;

namespace FongMi.TV.Core;

public static class Logger
{
    static readonly object Lock = new();
    const long MaxBytes = 8L * 1024 * 1024;
    static string LogFile => Path.Combine(AppPaths.Root ?? Path.GetTempPath(), "app.log");
    static StreamWriter Writer;
    static string WriterPath;

    public static void D(string tag, string msg) => Write("D", tag, msg);
    public static void E(string tag, string msg) => Write("E", tag, msg);

    static void Write(string level, string tag, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level}/{tag}: {msg}";
        Debug.WriteLine(line);
        try
        {
            lock (Lock)
            {
                var path = LogFile;
                if (Writer == null || !string.Equals(WriterPath, path, StringComparison.OrdinalIgnoreCase) || Writer.BaseStream.Length >= MaxBytes)
                    OpenWriter(path);
                Writer.WriteLine(line);
            }
        }
        catch { }
    }

    static void OpenWriter(string path)
    {
        try { Writer?.Dispose(); } catch { }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
        {
            var previous = path + ".1";
            try { if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous); } catch { }
        }
        Writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        WriterPath = path;
    }

    public static void Shutdown()
    {
        lock (Lock)
        {
            try { Writer?.Dispose(); } catch { }
            Writer = null;
            WriterPath = null;
        }
    }
}
