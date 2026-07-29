using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TVBoxForWindows.Core;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Engine;

/// <summary>Detects CatPawOpen configuration saves made in the external configuration website.</summary>
internal static class NodeConfigChangeMonitor
{
    const string Tag = "NodeConfigMonitor";
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan StableDelay = TimeSpan.FromMilliseconds(700);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    static readonly object Sync = new();

    static CancellationTokenSource _watchCts;
    static int _generation;

    public static bool Start(
        string baseUrl,
        string appliedFingerprint,
        Func<Task<NodeConfigReloadResult>> reload)
    {
        if (reload == null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp) return false;

        var cts = new CancellationTokenSource();
        CancellationTokenSource previous;
        int generation;
        lock (Sync)
        {
            generation = ++_generation;
            previous = _watchCts;
            _watchCts = cts;
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        _ = WatchAsync(baseUrl.TrimEnd('/'), appliedFingerprint, reload, generation, cts);
        return true;
    }

    public static void Stop()
    {
        CancellationTokenSource previous;
        lock (Sync)
        {
            _generation++;
            previous = _watchCts;
            _watchCts = null;
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }

    static async Task WatchAsync(
        string baseUrl,
        string baseline,
        Func<Task<NodeConfigReloadResult>> reload,
        int generation,
        CancellationTokenSource cts)
    {
        var token = cts.Token;
        string pending = null;
        var retryAfter = DateTimeOffset.MinValue;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                var current = await ReadFingerprintAsync(baseUrl).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation, cts)) return;
                if (current == null) continue;

                // The source loader normally supplies the last successfully applied
                // snapshot. If it is unavailable, the first valid read becomes the baseline.
                if (string.IsNullOrEmpty(baseline))
                {
                    baseline = current;
                    continue;
                }

                if (string.Equals(current, baseline, StringComparison.Ordinal))
                {
                    pending = null;
                    retryAfter = DateTimeOffset.MinValue;
                    continue;
                }
                if (!string.Equals(current, pending, StringComparison.Ordinal))
                {
                    pending = current;
                    retryAfter = DateTimeOffset.UtcNow + StableDelay;
                    continue;
                }
                if (DateTimeOffset.UtcNow < retryAfter) continue;

                Logger.D(Tag, "检测到配置中心已保存，正在自动重载点播配置");
                NodeConfigReloadResult result;
                try { result = await reload().ConfigureAwait(false); }
                catch (Exception e)
                {
                    Logger.E(Tag, "自动重载失败，将稍后重试: " + e.Message);
                    result = NodeConfigReloadResult.Retry();
                }

                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation, cts) || !result.ContinueWatching) return;
                if (result.Applied)
                {
                    baseline = string.IsNullOrEmpty(result.Fingerprint) ? current : result.Fingerprint;
                    pending = null;
                    retryAfter = DateTimeOffset.MinValue;
                }
                else retryAfter = DateTimeOffset.UtcNow + RetryDelay;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (Sync)
                if (generation == _generation && ReferenceEquals(_watchCts, cts))
                    _watchCts = null;
            cts.Dispose();
        }
    }

    static bool IsCurrent(int generation, CancellationTokenSource cts)
    {
        lock (Sync)
            return generation == _generation && ReferenceEquals(_watchCts, cts) && !cts.IsCancellationRequested;
    }

    static async Task<string> ReadFingerprintAsync(string baseUrl)
    {
        try
        {
            var response = await HttpUtil.Get(baseUrl.TrimEnd('/') + "/config", timeoutMs: 2500)
                .ConfigureAwait(false);
            if (response.Code is < 200 or >= 300 || response.Body is not { Length: > 0 }) return null;

            var root = JsonUtil.Parse(response.Text());
            if (root?["video"]?["sites"] is not JsonArray) return null;
            return Convert.ToHexString(SHA256.HashData(response.Body));
        }
        catch { return null; }
    }
}

internal readonly record struct NodeConfigReloadResult(
    bool ContinueWatching,
    bool Applied,
    string Fingerprint)
{
    public static NodeConfigReloadResult Success(string fingerprint) => new(true, true, fingerprint);
    public static NodeConfigReloadResult Retry() => new(true, false, null);
    public static NodeConfigReloadResult Stop() => new(false, false, null);
}
