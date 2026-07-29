using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace TVBoxForWindows.Net;

/// <summary>DNS over HTTPS（RFC 8484 wireformat，GET dns 参数，A 记录），带 Bootstrap IP 与缓存。</summary>
public static class DohResolver
{
    static readonly ConcurrentDictionary<string, (IPAddress[] ips, DateTime expire)> Cache = new();
    static readonly HttpClient Client = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                // Bootstrap：DoH 服务器域名用配置内附带的 IP 直连，避免鸡生蛋问题
                var doh = NetworkConfig.Doh;
                var host = ctx.DnsEndPoint.Host;
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (doh != null && doh.Ips is { Count: > 0 } && Core.UrlUtil.Host(doh.Url).Equals(host, StringComparison.OrdinalIgnoreCase))
                        await socket.ConnectAsync(doh.Ips.Select(IPAddress.Parse).ToArray(), ctx.DnsEndPoint.Port, ct);
                    else
                        await socket.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct);
                    return new System.Net.Sockets.NetworkStream(socket, true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        var doh = NetworkConfig.Doh;
        if (doh == null || string.IsNullOrEmpty(doh.Url)) return null;
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
        if (Cache.TryGetValue(host, out var hit) && hit.expire > DateTime.UtcNow) return hit.ips;
        try
        {
            var query = BuildQuery(host);
            var url = doh.Url + (doh.Url.Contains('?') ? "&" : "?") + "dns=" + Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/dns-message");
            using var res = await Client.SendAsync(req, ct);
            var ips = ParseAnswers(await res.Content.ReadAsByteArrayAsync(ct));
            if (ips.Length > 0) { Cache[host] = (ips, DateTime.UtcNow.AddMinutes(10)); return ips; }
        }
        catch (Exception e) { Core.Logger.E("Doh", host + " " + e.Message); }
        return null;
    }

    static byte[] BuildQuery(string host)
    {
        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(0); W16(0x0100); W16(1); W16(0); W16(0); W16(0);
        foreach (var label in host.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length); ms.Write(bytes);
        }
        ms.WriteByte(0); W16(1); W16(1);
        return ms.ToArray();
    }

    static IPAddress[] ParseAnswers(byte[] data)
    {
        var list = new List<IPAddress>();
        try
        {
            int ancount = (data[6] << 8) | data[7];
            int pos = 12;
            while (data[pos] != 0) pos += data[pos] >= 0xC0 ? 1 : data[pos] + 1; // question 名
            pos += 5;
            for (int i = 0; i < ancount && pos < data.Length; i++)
            {
                if (data[pos] >= 0xC0) pos += 2; else { while (data[pos] != 0) pos += data[pos] + 1; pos++; }
                int type = (data[pos] << 8) | data[pos + 1];
                pos += 8;
                int rdlen = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                if (type == 1 && rdlen == 4) list.Add(new IPAddress(data.AsSpan(pos, 4).ToArray()));
                pos += rdlen;
            }
        }
        catch { }
        return list.ToArray();
    }
}
