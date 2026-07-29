using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using TVBoxForWindows.Core;
using TVBoxForWindows.Net;
using JintEngine = Jint.Engine;

namespace TVBoxForWindows.Engine.Js;

/// <summary>JS 全局 API 注入（移植自 quickjs/method/Global.java + Local.java + Console.java、
/// bean/Req.java + utils/Connect.java/Crypto.java，以及站点 jar 注入的常用函数 pdfa/pdfh/pd/pdfl/gzip 等）。
/// 所有回调均在 JsSpider 的串行线程内执行；req 为同步阻塞实现。</summary>
public class JsGlobals
{
    const string TAG = "JsGlobals";

    readonly JintEngine _engine;
    readonly string _siteKey;
    readonly Jint.Native.Json.JsonParser _parser;
    readonly Jint.Native.Json.JsonSerializer _serializer;

    // ---- 定时器（等价 Global.java 的 Timer + 单线程投递；由 JsSpider 的事件循环泵执行）----
    class TimerEntry { public int Id; public long Due; public JsValue Func; }
    readonly List<TimerEntry> _timers = new();
    int _timerId;
    volatile bool _destroyed;

    public JsGlobals(JintEngine engine, string siteKey)
    {
        _engine = engine;
        _siteKey = siteKey ?? "";
        _parser = new Jint.Native.Json.JsonParser(engine);
        _serializer = new Jint.Native.Json.JsonSerializer(engine);
        Register();
    }

    /// <summary>销毁：取消全部定时器（等价 Global.destroy）。</summary>
    public void Destroy()
    {
        _destroyed = true;
        lock (_timers) _timers.Clear();
    }

    // ---- 供 JsSpider 复用的辅助 ----

    /// <summary>JSON 文本 → JsValue（失败返回 Undefined）。</summary>
    public JsValue ParseJson(string json)
    {
        try { return _parser.Parse(json ?? "null"); }
        catch { return JsValue.Undefined; }
    }

    /// <summary>JsValue → JSON 文本（等价 JSObject.stringify；不可序列化返回空串）。</summary>
    public string Stringify(JsValue value)
    {
        try
        {
            var result = _serializer.Serialize(value);
            return result != null && result.IsString() ? result.AsString() : "";
        }
        catch { return ""; }
    }

    /// <summary>是否还有待执行的定时器。</summary>
    public bool HasTimer { get { lock (_timers) return _timers.Count > 0; } }

    /// <summary>执行最近到期的一个定时器回调（阻塞等待到期）；无定时器或已销毁返回 false。仅由 JsSpider 串行线程调用。</summary>
    public bool PumpNextTimer()
    {
        TimerEntry next;
        lock (_timers)
        {
            next = _timers.OrderBy(t => t.Due).FirstOrDefault();
            if (next != null) _timers.Remove(next);
        }
        if (next == null || _destroyed) return false;
        var wait = next.Due - Environment.TickCount64;
        if (wait > 0) Thread.Sleep((int)Math.Min(wait, 30_000));
        try { _engine.Invoke(next.Func); }
        catch (Exception e) { Logger.E(TAG, "timer: " + e.Message); }
        return true;
    }

    /// <summary>JS 值真值判定（宽容：undefined/null/false/0 为假，其余为真）。</summary>
    public static bool Truthy(JsValue value)
    {
        if (value == null || value.IsUndefined() || value.IsNull()) return false;
        if (value.IsBoolean()) return value.AsBoolean();
        if (value.IsNumber()) return value.AsNumber() != 0;
        if (value.IsString()) return value.AsString().Length > 0 && value.AsString() != "false";
        return true;
    }

    /// <summary>宽容 Base64 解码：兼容 URL-safe 字符集（-_）、缺省填充与空白。</summary>
    public static byte[] FromBase64Lenient(string text)
    {
        try
        {
            var clean = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s", "").Replace('_', '/').Replace('-', '+');
            if (clean.Length % 4 != 0) clean = clean.PadRight(clean.Length + (4 - clean.Length % 4), '=');
            return Convert.FromBase64String(clean);
        }
        catch { return Array.Empty<byte>(); }
    }

    // ---- 注册 ----

    void Register()
    {
        _engine.SetValue("console", new JsConsole());
        _engine.SetValue("local", new JsLocal());
        _engine.SetValue("s2t", new Func<string, string>(text => { try { return Trans.S2T(text ?? ""); } catch { return text ?? ""; } }));
        _engine.SetValue("t2s", new Func<string, string>(text => { try { return Trans.T2S(text ?? ""); } catch { return text ?? ""; } }));
        _engine.SetValue("getPort", new Func<int>(() => Server.LocalServer.Instance.Port));
        _engine.SetValue("getProxy", new Func<JsValue, string>(GetProxy));
        _engine.SetValue("js2Proxy", new Func<JsValue, JsValue, JsValue, JsValue, JsValue, string>(Js2Proxy));
        _engine.SetValue("setTimeout", new Func<JsValue, JsValue, int>(SetTimeout));
        _engine.SetValue("clearTimeout", new Action<JsValue>(ClearTimeout));
        _engine.SetValue("_http", new Func<string, JsValue, JsValue>(HttpAsync));
        _engine.SetValue("req", new Func<string, JsValue, JsValue>(Req));
        _engine.SetValue("reqs", new Func<string, JsValue, JsValue>(Req));
        _engine.SetValue("joinUrl", new Func<string, string, string>((parent, child) => UrlUtil.Resolve(parent, child)));
        _engine.SetValue("md5X", new Func<string, string>(Md5));
        _engine.SetValue("md5", new Func<string, string>(Md5));
        _engine.SetValue("aesX", new Func<string, JsValue, string, JsValue, string, JsValue, JsValue, string>(AesX));
        _engine.SetValue("rsaX", new Func<string, JsValue, JsValue, string, JsValue, string, JsValue, string>(RsaX));
        _engine.SetValue("pdfh", new Func<string, string, string>((html, rule) => JsHtmlParser.Pdfh(html, rule)));
        _engine.SetValue("pdfa", new Func<string, string, JsValue>((html, rule) => ToJsArray(JsHtmlParser.Pdfa(html, rule))));
        _engine.SetValue("pd", new Func<string, string, JsValue, string>((html, rule, baseUrl) => JsHtmlParser.Pd(html, rule, Str(baseUrl))));
        _engine.SetValue("pdfl", new Func<string, string, string, string, JsValue, JsValue>((html, rule, texts, urls, urlKey) => ToJsArray(JsHtmlParser.Pdfl(html, rule, texts, urls, Str(urlKey)))));
        _engine.SetValue("gzip", new Func<string, string>(Gzip));
        _engine.SetValue("ungzip", new Func<string, string>(Ungzip));
        _engine.SetValue("base64Encode", new Func<string, string>(text => { try { return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? "")); } catch { return ""; } }));
        _engine.SetValue("base64Decode", new Func<string, string>(text => { try { return Encoding.UTF8.GetString(FromBase64Lenient(text)); } catch { return ""; } }));
        _engine.SetValue("btoa", new Func<string, string>(text => { try { return Convert.ToBase64String(Encoding.Latin1.GetBytes(text ?? "")); } catch { return ""; } }));
        _engine.SetValue("atob", new Func<string, string>(text => { try { return Encoding.Latin1.GetString(FromBase64Lenient(text)); } catch { return ""; } }));
    }

    // ---- 代理地址 ----

    /// <summary>getProxy(local)：返回定向到当前站点的 JS 代理地址（缺省按本机）。</summary>
    string GetProxy(JsValue local)
    {
        return ProxyBase(local) + "?do=js&siteKey=" + Uri.EscapeDataString(_siteKey ?? "");
    }

    string ProxyBase(JsValue local)
    {
        var isLocal = local == null || !local.IsBoolean() || local.AsBoolean();
        var server = Server.LocalServer.Instance;
        return isLocal ? server.GetAddress("/proxy") : server.GetAddressLan("/proxy");
    }

    /// <summary>js2Proxy(dynamic, siteType, siteKey, url, headers)：拼 catvod 回调地址（等价 Global.js2Proxy）。</summary>
    string Js2Proxy(JsValue dynamic, JsValue siteType, JsValue siteKey, JsValue url, JsValue headers)
    {
        var headerJson = headers is ObjectInstance ? Stringify(headers) : "{}";
        if (string.IsNullOrEmpty(headerJson)) headerJson = "{}";
        var key = Str(siteKey);
        if (string.IsNullOrEmpty(key)) key = _siteKey;
        return ProxyBase(!Truthy(dynamic))
            + $"?do=js&from=catvod&siteType={StrNum(siteType)}&siteKey={Uri.EscapeDataString(key)}&header={Uri.EscapeDataString(headerJson)}&url={Uri.EscapeDataString(Str(url))}";
    }

    // ---- 定时器 ----

    int SetTimeout(JsValue func, JsValue delay)
    {
        if (_destroyed || func == null || func.IsUndefined() || func.IsNull()) return 0;
        var id = Interlocked.Increment(ref _timerId);
        var ms = delay != null && delay.IsNumber() ? Math.Max(0, (int)delay.AsNumber()) : 0;
        lock (_timers) _timers.Add(new TimerEntry { Id = id, Due = Environment.TickCount64 + ms, Func = func });
        return id;
    }

    void ClearTimeout(JsValue id)
    {
        if (id == null || !id.IsNumber()) return;
        var key = (int)id.AsNumber();
        lock (_timers) _timers.RemoveAll(t => t.Id == key);
    }

    // ---- HTTP ----

    /// <summary>_http(url, options)：options 含 complete 回调则请求后立即回调（Windows 版同步完成，语义等价）；否则同 req。</summary>
    JsValue HttpAsync(string url, JsValue options)
    {
        var complete = options is ObjectInstance obj ? obj.Get("complete") : JsValue.Undefined;
        if (complete == null || complete.IsUndefined() || complete.IsNull()) return Req(url, options);
        var res = Req(url, options);
        try { _engine.Invoke(complete, res); }
        catch (Exception e) { Logger.E(TAG, "_http complete: " + e.Message); }
        return JsValue.Undefined;
    }

    /// <summary>req(url, options)：同步 HTTP（等价 Global.req + Connect），返回 {code, headers, content}；失败返回 {code:"", headers:{}, content:""}。</summary>
    JsValue Req(string url, JsValue options)
    {
        try
        {
            var req = ReqOptions.From(options is ObjectInstance ? Stringify(options) : "{}");
            var body = req.BuildBody(out var contentType);
            var res = HttpUtil.Execute(req.Method, url, req.Headers, null, body, contentType, req.Redirect, req.Timeout).GetAwaiter().GetResult();
            return Success(req, res);
        }
        catch (Exception e)
        {
            Logger.E(TAG, "req: " + url + " → " + e.Message);
            return ErrorRes();
        }
    }

    /// <summary>响应 → JS 对象（等价 Connect.success）：content 按 buffer 0文本/1整型数组/2base64/3原始字节。</summary>
    JsValue Success(ReqOptions req, OkResponse res)
    {
        try
        {
            var obj = new JsObject(_engine);
            var headers = new JsObject(_engine);
            foreach (var kv in res.Headers)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                if (kv.Value.Count == 1) headers.Set(kv.Key, kv.Value[0]);
                else headers.Set(kv.Key, new JsArray(_engine, kv.Value.Select(v => (JsValue)v).ToArray()));
            }
            obj.Set("code", res.Code);
            obj.Set("headers", headers);
            switch (req.Buffer)
            {
                case 1: obj.Set("content", new JsArray(_engine, res.Body.Select(b => (JsValue)(int)b).ToArray())); break;
                case 2: obj.Set("content", Convert.ToBase64String(res.Body)); break;
                case 3: obj.Set("content", JsValue.FromObject(_engine, res.Body)); break;
                default: obj.Set("content", res.Text(req.Charset)); break;
            }
            return obj;
        }
        catch { return ErrorRes(); }
    }

    /// <summary>失败响应（等价 Connect.error）：code 为空串。</summary>
    JsValue ErrorRes()
    {
        var obj = new JsObject(_engine);
        obj.Set("code", "");
        obj.Set("headers", new JsObject(_engine));
        obj.Set("content", "");
        return obj;
    }

    // ---- 加密（等价 utils/Crypto.java）----

    static string Md5(string text)
    {
        try
        {
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>aesX(mode, encrypt, input, inBase64, key, iv, outBase64)：mode 如 "AES/CBC/PKCS7"；key/iv 不足 16 字节补零；iv 为 null 时不用 IV。</summary>
    static string AesX(string mode, JsValue encrypt, string input, JsValue inBase64, string key, JsValue iv, JsValue outBase64)
    {
        try
        {
            var parts = (mode ?? "").Split('/');
            string ivText = iv != null && iv.IsString() ? iv.AsString() : null;
            using var aes = Aes.Create();
            aes.Mode = parts.Length > 1 ? parts[1].ToUpperInvariant() switch
            {
                "CBC" => CipherMode.CBC,
                "ECB" => CipherMode.ECB,
                "CFB" => CipherMode.CFB,
                _ => throw new NotSupportedException("AES 模式不支持: " + parts[1]),
            } : CipherMode.ECB;
            aes.Padding = parts.Length > 2 ? parts[2].ToUpperInvariant() switch
            {
                "PKCS7" or "PKCS5" => PaddingMode.PKCS7,
                "NOPADDING" => PaddingMode.None,
                "ZEROPADDING" => PaddingMode.Zeros,
                _ => PaddingMode.PKCS7,
            } : PaddingMode.PKCS7;
            if (aes.Mode == CipherMode.CFB) aes.FeedbackSize = 128; // Java CFB 默认 128 位反馈
            aes.Key = PadTo16(Encoding.UTF8.GetBytes(key ?? ""));
            if (ivText != null) aes.IV = PadTo16(Encoding.UTF8.GetBytes(ivText));
            else if (aes.Mode != CipherMode.ECB) aes.IV = new byte[16];
            var inBuf = Truthy(inBase64) ? FromBase64Lenient(input) : Encoding.UTF8.GetBytes(input ?? "");
            using var transform = Truthy(encrypt) ? aes.CreateEncryptor() : aes.CreateDecryptor();
            var outBuf = transform.TransformFinalBlock(inBuf, 0, inBuf.Length);
            return Truthy(outBase64) ? Convert.ToBase64String(outBuf) : Encoding.UTF8.GetString(outBuf);
        }
        catch (Exception e) { Logger.E(TAG, "aesX: " + e.Message); return ""; }
    }

    /// <summary>rsaX(mode, pub, encrypt, input, inBase64, key, outBase64)：PKCS1/OAEP；pub=X509(SPKI) 公钥、否则 PKCS8 私钥（PEM 自动剥壳）。
    /// "RSA/None/NoPadding" 与公钥解密/私钥原文加密在 .NET 无原生支持，返回空串。</summary>
    static string RsaX(string mode, JsValue pub, JsValue encrypt, string input, JsValue inBase64, string key, JsValue outBase64)
    {
        try
        {
            mode ??= "";
            if (mode.Contains("NoPadding", StringComparison.OrdinalIgnoreCase))
            {
                Logger.E(TAG, "rsaX: RSA/None/NoPadding 暂不支持");
                return "";
            }
            var padding = RSAEncryptionPadding.Pkcs1;
            if (mode.Contains("OAEP", StringComparison.OrdinalIgnoreCase))
                padding = mode.Contains("256") ? RSAEncryptionPadding.OaepSHA256 : RSAEncryptionPadding.OaepSHA1;
            using var rsa = RSA.Create();
            var der = FromBase64Lenient(StripPem(key));
            if (Truthy(pub)) rsa.ImportSubjectPublicKeyInfo(der, out _);
            else rsa.ImportPkcs8PrivateKey(der, out _);
            var inBuf = Truthy(inBase64) ? FromBase64Lenient(input) : Encoding.UTF8.GetBytes(input ?? "");
            var outBuf = Truthy(encrypt) ? rsa.Encrypt(inBuf, padding) : rsa.Decrypt(inBuf, padding);
            return Truthy(outBase64) ? Convert.ToBase64String(outBuf) : Encoding.UTF8.GetString(outBuf);
        }
        catch (Exception e) { Logger.E(TAG, "rsaX: " + e.Message); return ""; }
    }

    static byte[] PadTo16(byte[] buf) => buf.Length < 16 ? buf.Concat(new byte[16 - buf.Length]).ToArray() : buf;

    static string StripPem(string key) =>
        System.Text.RegularExpressions.Regex.Replace(key ?? "", @"-----[^-]+-----|[\r\n\s]", "");

    // ---- gzip / ungzip（drpy 约定：gzip(text)→base64，ungzip(base64)→text）----

    static string Gzip(string text)
    {
        try
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress))
            {
                var buf = Encoding.UTF8.GetBytes(text ?? "");
                gz.Write(buf, 0, buf.Length);
            }
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }

    static string Ungzip(string base64)
    {
        try
        {
            using var input = new MemoryStream(FromBase64Lenient(base64));
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch { return ""; }
    }

    // ---- 私有辅助 ----

    JsValue ToJsArray(List<string> items) => new JsArray(_engine, (items ?? new()).Select(s => (JsValue)(s ?? "")).ToArray());

    static string Str(JsValue value) =>
        value == null || value.IsUndefined() || value.IsNull() ? "" : value.IsString() ? value.AsString() : value.ToString();

    static string StrNum(JsValue value) =>
        value != null && value.IsNumber() ? ((int)value.AsNumber()).ToString() : Str(value);

    // ---- req 选项（等价 bean/Req.java）----

    class ReqOptions
    {
        public string Method = "GET";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public int Timeout = 10000;
        public bool Redirect = true;
        public int Buffer;
        public string PostType = "json";
        public string BodyText;
        public JsonNode Data;

        public static ReqOptions From(string json)
        {
            var req = new ReqOptions();
            var node = JsonUtil.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            if (node == null) return req;
            var method = JsonUtil.SafeString(node, "method").ToLowerInvariant();
            req.Method = method switch { "post" => "POST", "header" => "HEAD", _ => "GET" };
            req.Headers = JsonUtil.ToMap(node["headers"]);
            if (int.TryParse(JsonUtil.SafeString(node, "timeout"), out var timeout) && timeout > 0) req.Timeout = timeout;
            if (int.TryParse(JsonUtil.SafeString(node, "redirect"), out var redirect)) req.Redirect = redirect == 1;
            if (int.TryParse(JsonUtil.SafeString(node, "buffer"), out var buffer)) req.Buffer = buffer;
            var postType = JsonUtil.SafeString(node, "postType");
            if (!string.IsNullOrEmpty(postType)) req.PostType = postType;
            req.BodyText = node["body"] != null ? JsonUtil.SafeString(node, "body") : null;
            req.Data = node["data"];
            return req;
        }

        /// <summary>响应解码字符集：取请求头 Content-Type 的 charset（等价 Req.getCharset），默认 UTF-8。</summary>
        public string Charset
        {
            get
            {
                var type = Headers.GetValueOrDefault("Content-Type") ?? "";
                foreach (var part in type.Split(';'))
                    if (part.Contains("charset=", StringComparison.OrdinalIgnoreCase))
                        return part.Split('=')[^1].Trim();
                return "UTF-8";
            }
        }

        /// <summary>POST 体构造（等价 Connect.getPostBody）：postType json/form/form-data 取 data；否则 body + 头部 Content-Type。</summary>
        public byte[] BuildBody(out string contentType)
        {
            contentType = null;
            if (Method != "POST") return null;
            if (Data != null && PostType == "json")
            {
                contentType = "application/json; charset=utf-8";
                return Encoding.UTF8.GetBytes(Data.ToJsonString(JsonUtil.Options));
            }
            if (Data != null && PostType == "form")
            {
                contentType = "application/x-www-form-urlencoded";
                var map = JsonUtil.ToMap(Data);
                return Encoding.UTF8.GetBytes(string.Join("&", map.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? ""))));
            }
            if (Data != null && PostType == "form-data")
            {
                var boundary = "--dio-boundary-" + Random.Shared.Next(10000, 99999) + Random.Shared.Next(10000, 99999);
                contentType = "multipart/form-data; boundary=" + boundary;
                var sb = new StringBuilder();
                foreach (var kv in JsonUtil.ToMap(Data))
                {
                    sb.Append("--").Append(boundary).Append("\r\n");
                    sb.Append("Content-Disposition: form-data; name=\"").Append(kv.Key).Append("\"\r\n\r\n");
                    sb.Append(kv.Value ?? "").Append("\r\n");
                }
                sb.Append("--").Append(boundary).Append("--\r\n");
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            var headerType = Headers.GetValueOrDefault("Content-Type");
            if (BodyText != null && headerType != null)
            {
                contentType = headerType;
                return Encoding.UTF8.GetBytes(BodyText);
            }
            return Array.Empty<byte>();
        }
    }

    // ---- local 对象（等价 method/Local.java，键与 /cache 端点一致）----

    class JsLocal
    {
        static string GetKey(string rule, string key) => "cache_" + (string.IsNullOrEmpty(rule) ? "" : rule + "_") + key;

        public string get(string rule, string key) => Setting.GetString(GetKey(rule, key));

        public void set(string rule, string key, string value) => Setting.Put(GetKey(rule, key), value ?? "");

        public void delete(string rule, string key) => Setting.Remove(GetKey(rule, key));
    }

    // ---- console 对象（等价 method/Console.java）----

    class JsConsole
    {
        static string Join(object[] args) => string.Join(" ", (args ?? Array.Empty<object>()).Select(a => a?.ToString() ?? "null"));

        public void log(params object[] args) => Logger.D("JsConsole", Join(args));

        public void info(params object[] args) => Logger.D("JsConsole", Join(args));

        public void debug(params object[] args) => Logger.D("JsConsole", Join(args));

        public void warn(params object[] args) => Logger.D("JsConsole", Join(args));

        public void error(params object[] args) => Logger.E("JsConsole", Join(args));
    }
}
