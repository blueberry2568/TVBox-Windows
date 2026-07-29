using FongMi.TV.Core;

namespace FongMi.TV.Server.Process;

/// <summary>/parse 端点（移植自 server/process/Parse.java + assets/parse.html）：
/// 返回聚合嗅探页（多 iframe 同时加载 jxs 里各解析器），供 god 解析的 WebSniffer 加载。</summary>
public class ParseProcess : IProcess
{
    /// <summary>内置模板（等价 assets/parse.html，%s 两个占位：jxs 与 url）。</summary>
    const string Template = """
<!DOCTYPE html>
<html lang="zh-TW">

<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=yes">
    <title>解析</title>
</head>

<body>
    <div id="container"></div>
    <script>
        const jxs = "%s";
        const url = "%s";
        const list = jxs.split(";");
        const container = document.getElementById('container');
        list.forEach(item => {
            const iframe = document.createElement('iframe');
            iframe.src = item + url;
            iframe.sandbox = 'allow-scripts allow-same-origin allow-forms';
            container.appendChild(iframe);
        });
    </script>
</body>

</html>
""";

    public bool IsRequest(ServerRequest req) => req.Path.StartsWith("/parse");

    public Task<ServerResponse> Handle(ServerRequest req)
    {
        var jxs = req.Params.GetValueOrDefault("jxs") ?? "";
        var url = req.Params.GetValueOrDefault("url") ?? "";
        var html = AppPaths.ReadAsset("parse.html"); // 优先随包资源，缺失用内置模板
        if (string.IsNullOrEmpty(html)) html = Template;
        html = ReplaceFirst(html, "%s", jxs);
        html = ReplaceFirst(html, "%s", url);
        return Task.FromResult(ServerResponse.Ok(html, "text/html"));
    }

    static string ReplaceFirst(string text, string find, string value)
    {
        var i = text.IndexOf(find, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + value + text[(i + find.Length)..];
    }
}
