using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FongMi.TV.Core;

namespace FongMi.TV.Server.Process;

/// <summary>/file /upload /newFolder /delFolder /delFile 端点（移植自 server/process/Local.java）：
/// 根目录 = AppPaths.Local；文件流带 ETag/Range（206/304/416）。</summary>
public class FileProcess : IProcess
{
    const string Tag = "FileProcess";

    public bool IsRequest(ServerRequest req) =>
        req.Path.StartsWith("/file") || req.Path.StartsWith("/upload") || req.Path.StartsWith("/newFolder") || req.Path.StartsWith("/delFolder") || req.Path.StartsWith("/delFile");

    public Task<ServerResponse> Handle(ServerRequest req)
    {
        if (req.Path.StartsWith("/file")) return Task.FromResult(GetFile(req));
        if (req.Path.StartsWith("/upload")) return Task.FromResult(Upload(req));
        if (req.Path.StartsWith("/newFolder")) return Task.FromResult(NewFolder(req));
        return Task.FromResult(Delete(req)); // /delFolder 与 /delFile 实现相同（等价 Path.clear）
    }

    // ---------- /file/{path} ----------

    ServerResponse GetFile(ServerRequest req)
    {
        try
        {
            var target = Resolve(req.Path[5..]); // 去掉前 5 字符 "/file"
            if (Directory.Exists(target)) return GetFolder(target);
            if (File.Exists(target)) return GetFile(req.Headers, target, LocalServer.GetMime(target));
            return ServerResponse.Error("File not found: " + target);
        }
        catch (Exception e) { return ServerResponse.Error(e.Message); }
    }

    /// <summary>Resolves a path inside AppPaths.Local and rejects rooted/traversal paths.</summary>
    static string Resolve(string rel)
    {
        rel = (rel ?? "").Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(rel) || (rel.Length >= 2 && rel[1] == ':'))
            throw new UnauthorizedAccessException("不允许访问应用数据目录之外的路径");

        var root = Path.GetFullPath(AppPaths.Local).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("不允许访问应用数据目录之外的路径");
        return full;
    }

    /// <summary>目录 → JSON {"parent":..., "files":[{name,path,time,dir}]}；path 相对根（带前导 /），time 格式 yyyy/MM/dd HH:mm:ss。</summary>
    static ServerResponse GetFolder(string dir)
    {
        var root = Path.GetFullPath(AppPaths.Local);
        var full = Path.GetFullPath(dir);
        var files = new JsonArray();
        var entries = Directory.GetDirectories(full).Concat(Directory.GetFiles(full)).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var isDir = Directory.Exists(entry);
            files.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(entry),
                ["path"] = RelativeTo(entry, root),
                ["time"] = (isDir ? Directory.GetLastWriteTime(entry) : File.GetLastWriteTime(entry)).ToString("yyyy/MM/dd HH:mm:ss"),
                ["dir"] = isDir ? 1 : 0,
            });
        }
        var info = new JsonObject { ["parent"] = ParentOf(full, root), ["files"] = files };
        return ServerResponse.Ok(info.ToJsonString(JsonUtil.Options));
    }

    static string RelativeTo(string path, string root)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..].Replace('\\', '/') : full.Replace('\\', '/');
    }

    static string ParentOf(string dir, string root)
    {
        if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) return "."; // 已是根
        var parent = Path.GetDirectoryName(dir);
        if (parent == null || string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) return ""; // 上层即根
        return RelativeTo(parent, root);
    }

    /// <summary>文件流：ETag = CRC32(绝对路径+修改时间+长度) hex；支持 If-None-Match / If-Range / 单段 Range。</summary>
    static ServerResponse GetFile(Dictionary<string, string> headers, string file, string mime)
    {
        var info = new FileInfo(file);
        var length = info.Length;
        var etag = Etag(info.FullName, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), length);
        var ifNoneMatch = headers.GetValueOrDefault("if-none-match");
        if (ifNoneMatch != null && (ifNoneMatch == "*" || ifNoneMatch == etag))
            return new ServerResponse { Code = 304, Mime = mime, Body = Array.Empty<byte>() };
        var range = HttpRange.From(length, headers, etag);
        if (!range.Valid)
        {
            var invalid = new ServerResponse { Code = 416, Mime = "text/plain", Body = Array.Empty<byte>() };
            invalid.Headers["Content-Range"] = "bytes */" + length;
            return invalid;
        }
        var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (range.Start > 0) fs.Seek(range.Start, SeekOrigin.Begin);
        var res = new ServerResponse { Code = range.IsPartial(length) ? 206 : 200, Mime = mime, Stream = fs, StreamLength = range.Length };
        if (range.IsPartial(length)) res.Headers["Content-Range"] = $"bytes {range.Start}-{range.End}/{length}";
        res.Headers["Accept-Ranges"] = "bytes";
        res.Headers["ETag"] = etag;
        return res;
    }

    // ---------- /upload?path= ----------

    /// <summary>multipart 文件：.zip 解压到 root/path，其他复制为 root/path/文件名（等价 Local.upload）。</summary>
    ServerResponse Upload(ServerRequest req)
    {
        var dir = Resolve(req.Params.GetValueOrDefault("path") ?? "");
        Directory.CreateDirectory(dir);
        foreach (var kv in req.Files)
        {
            if (kv.Key == "postData" || !File.Exists(kv.Value)) continue;
            var name = req.Params.GetValueOrDefault(kv.Key) ?? kv.Key;
            try
            {
                if (name.ToLowerInvariant().EndsWith(".zip")) ZipFile.ExtractToDirectory(kv.Value, dir, Encoding.UTF8, true);
                else File.Copy(kv.Value, Path.Combine(dir, Path.GetFileName(name)), true);
            }
            catch (Exception e) { Logger.E(Tag, "上传处理失败：" + name + " " + e.Message); }
        }
        return ServerResponse.Ok();
    }

    // ---------- /newFolder /delFolder /delFile ----------

    ServerResponse NewFolder(ServerRequest req)
    {
        var path = req.Params.GetValueOrDefault("path") ?? "";
        var name = req.Params.GetValueOrDefault("name") ?? "";
        try { Directory.CreateDirectory(Resolve(Path.Combine(path, name))); } catch (Exception e) { Logger.E(Tag, e.Message); }
        return ServerResponse.Ok();
    }

    ServerResponse Delete(ServerRequest req)
    {
        var path = req.Params.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(path)) return ServerResponse.Ok(); // 防误删根目录
        try
        {
            var target = Resolve(path);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            else if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception e) { Logger.E(Tag, e.Message); }
        return ServerResponse.Ok();
    }

    // ---------- ETag（CRC32 hex，等价 Local.etag）----------

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    static string Etag(string absPath, long modified, long length)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(absPath + modified + length)) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return (crc ^ 0xFFFFFFFFu).ToString("x");
    }

    // ---------- Range（等价 Local.HttpRange）----------

    class HttpRange
    {
        public long Start;
        public long End;
        public long Length;
        public bool Valid;

        public bool IsPartial(long total) => Length < total;

        static HttpRange Invalid() => new() { Valid = false };

        public static HttpRange From(long fileLen, Dictionary<string, string> headers, string etag)
        {
            long start = 0, end = fileLen - 1;
            var rangeHeader = headers.GetValueOrDefault("range");
            var ifRange = headers.GetValueOrDefault("if-range");
            if (ifRange != null && ifRange != etag) rangeHeader = null; // If-Range 不匹配则忽略 Range
            if (rangeHeader != null && rangeHeader.StartsWith("bytes="))
            {
                try
                {
                    var parts = rangeHeader[6..].Split('-', 2);
                    if (parts[0].Length > 0) start = long.Parse(parts[0]);
                    if (parts.Length > 1 && parts[1].Length > 0) end = long.Parse(parts[1]);
                    if (start >= fileLen || start > end) return Invalid();
                }
                catch { return Invalid(); }
            }
            if (end >= fileLen) end = fileLen - 1;
            return new HttpRange { Start = start, End = end, Length = end - start + 1, Valid = true };
        }
    }
}
