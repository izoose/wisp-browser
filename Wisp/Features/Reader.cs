using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>
/// Reader mode: runs Mozilla's Readability in the page to extract the article, then renders
/// it as a clean dark page.
/// </summary>
public static class Reader
{
    private static string? _js;
    private static string ReadabilityJs =>
        _js ??= File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "reader", "Readability.js"));

    /// <summary>Extracts the article and returns a full dark HTML document, or null if the
    /// page isn't article-like.</summary>
    public static async Task<(string title, string html)?> BuildAsync(CoreWebView2 cw)
    {
        try
        {
            await cw.ExecuteScriptAsync(ReadabilityJs);
            var raw = await cw.ExecuteScriptAsync(
                "JSON.stringify((function(){try{var a=new Readability(document.cloneNode(true)).parse();" +
                "return a?{title:a.title,content:a.content,byline:a.byline}:null;}catch(e){return null;}})())");

            if (string.IsNullOrEmpty(raw) || raw == "null") return null;

            // ExecuteScriptAsync returns the JS string JSON-encoded; decode once to the inner JSON.
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return null;

            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Reader" : "Reader";
            var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var byline = root.TryGetProperty("byline", out var b) ? b.GetString() : null;
            if (string.IsNullOrWhiteSpace(content)) return null;

            return (title, Template(title, byline, content));
        }
        catch { return null; }
    }

    private static string Template(string title, string? byline, string content)
    {
        var head = WebUtility.HtmlEncode(title);
        var by = string.IsNullOrWhiteSpace(byline) ? "" : $"<p class='byline'>{WebUtility.HtmlEncode(byline)}</p>";
        return $@"<!doctype html><html><head><meta charset='utf-8'><title>{head}</title>
<style>
  :root {{ color-scheme: dark; }}
  html,body {{ margin:0; background:#1b1b1d; color:#e6e6e6; }}
  body {{ font-family:'Segoe UI',system-ui,sans-serif; line-height:1.7; }}
  .wrap {{ max-width:720px; margin:0 auto; padding:48px 24px 96px; }}
  h1 {{ font-size:32px; line-height:1.25; margin:0 0 8px; color:#f2f2f2; }}
  .byline {{ color:#9a9aa2; margin:0 0 28px; font-size:14px; }}
  .content {{ font-size:18px; }}
  .content a {{ color:#4c8dff; }}
  .content img {{ max-width:100%; height:auto; border-radius:8px; }}
  .content p {{ margin:0 0 18px; }}
  .content h2,.content h3 {{ color:#f2f2f2; margin-top:32px; }}
  .content pre,.content code {{ background:#26262b; border-radius:6px; }}
  .content pre {{ padding:12px; overflow:auto; }}
  .content blockquote {{ border-left:3px solid #4c8dff; margin:0 0 18px; padding:2px 16px; color:#c8c8ce; }}
</style></head>
<body><div class='wrap'><h1>{head}</h1>{by}<div class='content'>{content}</div></div></body></html>";
    }
}
