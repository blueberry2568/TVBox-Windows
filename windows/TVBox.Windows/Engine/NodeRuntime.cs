using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Engine;

/// <summary>Hosts one CatPawOpen source in the bundled Node.js process.</summary>
public static class NodeRuntime
{
    const string Tag = "NodeRuntime";

    static readonly SemaphoreSlim Lock = new(1, 1);
    static Process _proc;
    static Process _startingProc;
    static CancellationTokenSource _startupCts;
    static string _scriptPath;
    static string _configPath;
    static string _sourceVersion;
    static string _startupError;
    static int _shutdownRequested;

    static readonly Regex QueryPattern = new(
        @"\?(?=[A-Za-z0-9_.%~-]+=)[^'""\s<>{}\[\]]+",
        RegexOptions.Compiled);
    static readonly Regex SecretHeaderPattern = new(
        @"((?:authorization|proxy-authorization|cookie|set-cookie|x-api-key)\s*[:=]\s*)[^,'""}\r\n]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool NodeReady => NodePath != null;
    public static string NodePath { get; private set; }
    public static string LastError { get; private set; }
    public static string BaseUrl { get; private set; }

    public static bool EnsureNode()
    {
        if (NodeReady) return true;
        var found = FindNode();
        if (found == null)
        {
            LastError = "未找到 Node.js，请安装 Node.js 18+ 并加入 PATH（https://nodejs.org）";
            Logger.E(Tag, LastError);
            return false;
        }
        NodePath = found;
        return true;
    }

    static string FindNode()
    {
        try
        {
            var bundled = Path.Combine(AppPaths.NodeRuntimeDir, "node.exe");
            if (File.Exists(bundled)) return bundled;

            var home = Environment.GetEnvironmentVariable("NODE_HOME");
            if (!string.IsNullOrEmpty(home))
            {
                var exe = Path.Combine(home, "node.exe");
                if (File.Exists(exe)) return exe;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var exe = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var exe = Path.Combine(Environment.GetFolderPath(folder), "nodejs", "node.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Starts the CatPawOpen bootstrap and waits until its /config endpoint is ready.</summary>
    public static async Task<string> StartAsync(
        string scriptPath,
        string configPath,
        string dataDir,
        string sourceVersion,
        CancellationToken cancellationToken = default)
    {
        await Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource startupCts = null;
        Process candidate = null;
        try
        {
            if (Volatile.Read(ref _shutdownRequested) != 0)
            {
                LastError = "Node 源服务启动已取消";
                return null;
            }
            scriptPath = Path.GetFullPath(scriptPath);
            configPath = Path.GetFullPath(configPath);
            dataDir = Path.GetFullPath(dataDir);
            if (_proc is { HasExited: false } && BaseUrl != null &&
                string.Equals(_scriptPath, scriptPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_configPath, configPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_sourceVersion, sourceVersion, StringComparison.Ordinal))
                return BaseUrl;

            LastError = null;
            _startupError = null;
            if (!EnsureNode()) return null;

            var bootstrap = Path.Combine(AppPaths.NodeRuntimeDir, "catpaw-bootstrap.js");
            if (!File.Exists(bootstrap))
            {
                LastError = "缺少 CatPawOpen 启动组件 catpaw-bootstrap.js";
                return null;
            }
            if (!File.Exists(scriptPath) || !File.Exists(configPath))
            {
                LastError = "CatPawOpen 脚本或伴随配置不存在";
                return null;
            }
            Directory.CreateDirectory(dataDir);

            startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Volatile.Write(ref _startupCts, startupCts);
            if (Volatile.Read(ref _shutdownRequested) != 0) startupCts.Cancel();
            var startupToken = startupCts.Token;
            startupToken.ThrowIfCancellationRequested();

            var port = FreePort();
            var psi = new ProcessStartInfo
            {
                FileName = NodePath,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(bootstrap);
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(configPath);
            psi.Environment["CATPAW_PORT"] = port.ToString();
            psi.Environment["PORT"] = port.ToString();
            psi.Environment["CATPAW_DATA_DIR"] = dataDir;
            psi.Environment["HOST"] = "127.0.0.1";
            psi.Environment["DEV_HTTP_HOST"] = "127.0.0.1";
            var proxy = Setting.Proxy;
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                psi.Environment["HTTP_PROXY"] = proxy;
                psi.Environment["HTTPS_PROXY"] = proxy;
                psi.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            }

            var lifetimeJobReady = ProcessLifetimeJob.TryPrepare(out var lifetimeJobError);
            if (!lifetimeJobReady)
            {
                LastError = "无法建立 Node 进程生命周期保护：" + lifetimeJobError;
                Logger.E(Tag, LastError);
                return null;
            }

            candidate = Process.Start(psi);
            if (candidate == null)
            {
                LastError = "Node 进程启动失败";
                return null;
            }
            if (!ProcessLifetimeJob.TryAssign(candidate, out lifetimeJobError))
            {
                LastError = "无法绑定 Node 进程生命周期：" + lifetimeJobError;
                Logger.E(Tag, LastError);
                KillNow(candidate);
                candidate = null;
                return null;
            }
            _startingProc = candidate;
            Pipe(candidate, candidate.StandardOutput, false);
            Pipe(candidate, candidate.StandardError, true);

            var baseUrl = "http://127.0.0.1:" + port;
            if (!await WaitReady(baseUrl, candidate, startupToken).ConfigureAwait(false))
            {
                var detail = string.IsNullOrWhiteSpace(_startupError) ? "" : "：" + _startupError;
                LastError = (candidate.HasExited ? "Node 源服务异常退出" : "Node 源服务启动超时") + detail;
                Logger.E(Tag, LastError);
                await StopProcessAsync(candidate).ConfigureAwait(false);
                candidate = null;
                return null;
            }
            startupToken.ThrowIfCancellationRequested();

            // Keep the active service alive until its replacement is fully ready. This
            // makes a failed refresh non-destructive for pages using the current source.
            var previous = _proc;
            NodeConfigChangeMonitor.Stop();
            _proc = candidate;
            candidate = null;
            _scriptPath = scriptPath;
            _configPath = configPath;
            _sourceVersion = sourceVersion;
            BaseUrl = baseUrl;
            _startingProc = null;
            await StopProcessAsync(previous).ConfigureAwait(false);
            Logger.D(Tag, "CatPawOpen 服务就绪: " + baseUrl);
            return BaseUrl;
        }
        catch (OperationCanceledException)
        {
            LastError = "Node 源服务启动已取消";
            if (candidate != null) await StopProcessAsync(candidate).ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            LastError = "Node 源服务启动失败: " + e.Message;
            Logger.E(Tag, LastError);
            if (candidate != null) await StopProcessAsync(candidate).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _startingProc = null;
            if (ReferenceEquals(Volatile.Read(ref _startupCts), startupCts))
                Volatile.Write(ref _startupCts, null);
            startupCts?.Dispose();
            Lock.Release();
        }
    }

    static async Task<bool> WaitReady(string baseUrl, Process proc, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (proc.HasExited) return false;
            try
            {
                var rsp = await Net.HttpUtil.Get(baseUrl + "/config", timeoutMs: 1500);
                if (rsp.Code is >= 200 and < 300 && !string.IsNullOrWhiteSpace(rsp.Text())) return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    static void Pipe(Process owner, StreamReader reader, bool captureError)
    {
        _ = Task.Run(async () =>
        {
            var window = Stopwatch.StartNew();
            var emitted = 0;
            var omitted = 0;
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (window.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        if (omitted > 0) Logger.D(Tag, $"Node 日志过密，已省略 {omitted} 行");
                        window.Restart();
                        emitted = 0;
                        omitted = 0;
                    }
                    if (emitted >= 60)
                    {
                        omitted++;
                        continue;
                    }
                    var compact = CompactLog(line);
                    if (string.IsNullOrEmpty(compact)) continue;
                    if (captureError &&
                        (ReferenceEquals(_startingProc, owner) ||
                         (_startingProc == null && ReferenceEquals(_proc, owner))))
                        _startupError = compact;
                    Logger.D(Tag, compact);
                    emitted++;
                }
            }
            catch { }
            finally
            {
                if (omitted > 0) Logger.D(Tag, $"Node 日志过密，已省略 {omitted} 行");
            }
        });
    }

    static string CompactLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        var trimmed = line.Trim();
        if (trimmed.StartsWith("[ChunkProxy]", StringComparison.Ordinal) &&
            trimmed.Contains(" chunk=", StringComparison.Ordinal)) return "";
        try
        {
            if (JsonUtil.Parse(line) is JsonObject node)
            {
                var req = node["req"] as JsonObject;
                var res = node["res"] as JsonObject;
                var err = node["err"] as JsonObject;
                if (req != null)
                {
                    if (res == null && err == null) return "";
                    var method = JsonUtil.SafeString(req, "method");
                    var path = JsonUtil.SafeString(req, "url").Split('?', 2)[0];
                    if (path.Length > 240) path = path[..240] + "…";
                    var status = JsonUtil.SafeString(res, "statusCode");
                    var code = JsonUtil.SafeString(err, "code");
                    var message = JsonUtil.SafeString(err, "message");
                    var cost = JsonUtil.SafeString(node, "responseTime");
                    return Sanitize($"{method} {path} -> {(string.IsNullOrEmpty(status) ? "error" : status)}" +
                                    (string.IsNullOrEmpty(code) ? "" : " " + code) +
                                    (string.IsNullOrEmpty(message) ? "" : " " + message) +
                                    (string.IsNullOrEmpty(cost) ? "" : " " + cost + "ms"));
                }
                var msg = JsonUtil.SafeString(node, "msg");
                if (!string.IsNullOrEmpty(msg)) return Sanitize(msg);
            }
        }
        catch { }
        return Sanitize(trimmed);
    }

    static string Sanitize(string value)
    {
        value = QueryPattern.Replace(value ?? "", "?…");
        value = SecretHeaderPattern.Replace(value, "$1***");
        return value.Length <= 2048 ? value : value[..2048] + "…";
    }

    static int FreePort()
    {
        for (var port = 9989; port <= 10020; port++)
        {
            try
            {
                using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch { }
        }
        throw new IOException("没有可用的 CatPawOpen 本地端口");
    }

    public static void Shutdown()
    {
        NodeConfigChangeMonitor.Stop();
        Interlocked.Exchange(ref _shutdownRequested, 1);
        CancelStartup();
        _ = ShutdownAsync();
    }

    public static void TerminateForExit()
    {
        NodeConfigChangeMonitor.Stop();
        Interlocked.Exchange(ref _shutdownRequested, 1);
        CancelStartup();
        var starting = Interlocked.Exchange(ref _startingProc, null);
        var active = Interlocked.Exchange(ref _proc, null);
        KillNow(starting);
        if (!ReferenceEquals(active, starting)) KillNow(active);
        _scriptPath = null;
        _configPath = null;
        _sourceVersion = null;
        _startupError = null;
        BaseUrl = null;
    }

    public static async Task ShutdownAsync()
    {
        NodeConfigChangeMonitor.Stop();
        Interlocked.Exchange(ref _shutdownRequested, 1);
        CancelStartup();
        await Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var process = _proc;
            _proc = null;
            _scriptPath = null;
            _configPath = null;
            _sourceVersion = null;
            _startupError = null;
            BaseUrl = null;
            await StopProcessAsync(process).ConfigureAwait(false);
        }
        catch (Exception e) { Logger.E(Tag, "Node 源服务关闭失败: " + e.Message); }
        finally
        {
            Interlocked.Exchange(ref _shutdownRequested, 0);
            Lock.Release();
        }
    }

    static void CancelStartup()
    {
        try { Volatile.Read(ref _startupCts)?.Cancel(); } catch (ObjectDisposedException) { }
    }

    static async Task StopProcessAsync(Process process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch { }
        finally { try { process.Dispose(); } catch { } }
    }

    static void KillNow(Process process)
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try { process.Dispose(); } catch { }
    }
}
