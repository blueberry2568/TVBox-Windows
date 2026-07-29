using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FongMi.TV.Core;

namespace FongMi.TV.Engine.Js;

/// <summary>jsoup/drpy 风格选择器解析（AngleSharp 实现，对齐 drpy jsoup.js 约定）：
/// 规则用 &amp;&amp; 分层（等价 jQuery find 链），pdfh 末段为提取项（Text/Html/属性名）；
/// 每层默认取第一个（:eq(0)），显式 :eq(n)/:first/:last/:lt(n)/:gt(n) 可改；-- 后为排除子选择器。</summary>
public static class JsHtmlParser
{
    const string TAG = "JsHtmlParser";

    static readonly HtmlParser Parser = new();

    /// <summary>末层 token 含以下特征时不自动补 :eq(0)（等价 drpy parseHikerToJq 的 test 正则）。</summary>
    static readonly Regex SkipEq = new(":eq|:lt|:gt|:first|:last|^body$|^#", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>:eq(n)/:lt(n)/:gt(n) 提取。</summary>
    static readonly Regex EqRegex = new(@":(eq|lt|gt)\((-?\d+)\)", RegexOptions.Compiled);

    /// <summary>:first/:last 提取。</summary>
    static readonly Regex FirstLastRegex = new(@":(first|last)\b", RegexOptions.Compiled);

    /// <summary>style 属性内 url(...) 提取。</summary>
    static readonly Regex StyleUrl = new(@"url\((.*?)\)", RegexOptions.Compiled);

    /// <summary>单值提取：pdfh(html, 'a&amp;&amp;Text')；末段为 Text/Html/属性名，无 &amp;&amp; 时返回首个元素 outerHTML。
    /// baseUrl 非空且末段为属性时对取值做相对 URL 补全（pd 语义）。</summary>
    public static string Pdfh(string html, string rule, string baseUrl = "")
    {
        try
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(rule)) return "";
            var doc = Parser.ParseDocument(html);
            if (rule is "body&&Text" or "Text") return NormalizeText(doc.Body?.TextContent);
            if (rule is "body&&Html" or "Html") return doc.Body?.InnerHtml ?? "";
            string option = null;
            var parse = rule;
            if (parse.Contains("&&"))
            {
                var segs = parse.Split("&&");
                option = segs[^1];
                parse = string.Join("&&", segs[..^1]);
            }
            var el = SelectElements(doc, parse, true).FirstOrDefault();
            if (el == null) return "";
            if (option == null) return el.OuterHtml;
            switch (option)
            {
                case "Text": return NormalizeText(el.TextContent);
                case "Html": return el.InnerHtml;
                default:
                    var value = el.GetAttribute(option) ?? "";
                    if (option.Contains("style", StringComparison.OrdinalIgnoreCase) && value.Contains("url("))
                    {
                        var m = StyleUrl.Match(value);
                        if (m.Success) value = m.Groups[1].Value.Trim().Trim('\'', '"');
                    }
                    if (value.Length > 0 && !string.IsNullOrEmpty(baseUrl)) value = UrlUtil.Resolve(baseUrl, value);
                    return value;
            }
        }
        catch (Exception e) { Logger.E(TAG, "pdfh: " + e.Message); return ""; }
    }

    /// <summary>列表提取：pdfa(html, 'div.list&amp;&amp;a') 返回命中元素的 outerHTML 数组（末层不补 :eq(0)）。</summary>
    public static List<string> Pdfa(string html, string rule)
    {
        try
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(rule)) return new List<string>();
            var doc = Parser.ParseDocument(html);
            return SelectElements(doc, rule, false).Select(e => e.OuterHtml).ToList();
        }
        catch (Exception e) { Logger.E(TAG, "pdfa: " + e.Message); return new List<string>(); }
    }

    /// <summary>pdfh + 相对 URL 补全：pd(html, 'a&amp;&amp;href', baseUrl)。</summary>
    public static string Pd(string html, string rule, string baseUrl = "") => Pdfh(html, rule, baseUrl ?? "");

    /// <summary>列表快捷提取：rule 选出元素后，逐项取 texts/urls，返回 "文本$链接" 数组（drpy pdfl 约定；urlKey 为 drpy 遗留参数，此处不使用）。</summary>
    public static List<string> Pdfl(string html, string rule, string texts, string urls, string urlKey)
    {
        try
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(rule)) return list;
            var doc = Parser.ParseDocument(html);
            foreach (var el in SelectElements(doc, rule, false))
            {
                var outer = el.OuterHtml;
                list.Add(Pdfh(outer, texts) + "$" + Pdfh(outer, urls));
            }
            return list;
        }
        catch (Exception e) { Logger.E(TAG, "pdfl: " + e.Message); return new List<string>(); }
    }

    // ---- 内部：选择器链执行 ----

    /// <summary>规则展开 + 逐 token find 链（等价 drpy parseHikerToJq + parseOneRule）。</summary>
    static List<IElement> SelectElements(IDocument doc, string parse, bool first)
    {
        var tokens = ParseHikerToJq(parse, first).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<IElement> current = null;
        foreach (var token in tokens)
        {
            ParseInfo(token, out var sel, out var kind, out var num, out var excludes);
            List<IElement> matched;
            if (current == null) matched = Query(doc, sel);
            else
            {
                matched = new List<IElement>();
                var seen = new HashSet<IElement>();
                foreach (var el in current)
                    foreach (var m in Query(el, sel))
                        if (seen.Add(m)) matched.Add(m);
            }
            matched = Slice(matched, kind, num);
            if (excludes.Count > 0 && matched.Count > 0) matched = matched.Select(el => CloneWithout(el, excludes)).ToList();
            current = matched;
            if (current.Count == 0) return current;
        }
        return current ?? new List<IElement>();
    }

    /// <summary>&amp;&amp; → 空格连接；未显式索引的层补 :eq(0)（pdfa 的末层除外）。</summary>
    static string ParseHikerToJq(string parse, bool first)
    {
        if (parse.Contains("&&"))
        {
            var segs = parse.Split("&&");
            for (int i = 0; i < segs.Length; i++)
            {
                var tokens = segs[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var lastToken = tokens.Length > 0 ? tokens[^1] : segs[i];
                if (SkipEq.IsMatch(lastToken)) continue;
                if (i == segs.Length - 1 && !first) continue;
                segs[i] += ":eq(0)";
            }
            return string.Join(" ", segs);
        }
        return !SkipEq.IsMatch(parse) && first ? parse + ":eq(0)" : parse;
    }

    /// <summary>拆出 CSS 选择器 / 索引伪类（:eq 等 jQuery 扩展 AngleSharp 不支持，手动应用）/ -- 排除项。</summary>
    static void ParseInfo(string token, out string sel, out string kind, out int num, out List<string> excludes)
    {
        sel = token; kind = null; num = 0;
        var m = EqRegex.Match(token);
        if (m.Success)
        {
            kind = m.Groups[1].Value;
            num = int.Parse(m.Groups[2].Value);
            sel = token[..m.Index];
        }
        else
        {
            m = FirstLastRegex.Match(token);
            if (m.Success) { kind = m.Groups[1].Value; sel = token[..m.Index]; }
        }
        excludes = new List<string>();
        if (sel.Contains("--"))
        {
            var parts = sel.Split("--");
            sel = parts[0];
            excludes.AddRange(parts[1..].Where(p => p.Length > 0));
        }
    }

    /// <summary>应用索引伪类：eq(n 支持负数)/first/last/lt(n)/gt(n)。</summary>
    static List<IElement> Slice(List<IElement> list, string kind, int num)
    {
        if (kind == null || list.Count == 0) return list;
        switch (kind)
        {
            case "eq":
                var i = num < 0 ? list.Count + num : num;
                return i >= 0 && i < list.Count ? new List<IElement> { list[i] } : new List<IElement>();
            case "first": return new List<IElement> { list[0] };
            case "last": return new List<IElement> { list[^1] };
            case "lt": return list.Take(Math.Max(0, num)).ToList();
            case "gt": return list.Skip(num + 1).ToList();
            default: return list;
        }
    }

    static List<IElement> Query(IParentNode node, string sel)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sel)) return new List<IElement>();
            return node.QuerySelectorAll(sel).ToList();
        }
        catch { return new List<IElement>(); }
    }

    /// <summary>克隆并移除排除项（等价 drpy excludes：clone 后 find(exclude).remove()）。</summary>
    static IElement CloneWithout(IElement el, List<string> excludes)
    {
        try
        {
            if (el.Clone(true) is not IElement clone) return el;
            foreach (var exclude in excludes)
                foreach (var bad in clone.QuerySelectorAll(exclude).ToList())
                    bad.Remove();
            return clone;
        }
        catch { return el; }
    }

    /// <summary>jsoup text() 风格归一化：连续空白折叠为单空格并去首尾。</summary>
    static string NormalizeText(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
