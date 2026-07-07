using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wisp;

/// <summary>A node in the bookmarks tree — either a saved page ("url") or a folder ("folder").</summary>
public class BookmarkNode
{
    public string Type { get; set; } = "url";        // "url" | "folder"
    public string Title { get; set; } = "";
    public string? Url { get; set; }                  // set when Type == "url"
    public bool IconOnly { get; set; }                // show just the favicon on the bar (Brave-style)
    public List<BookmarkNode> Children { get; set; } = new(); // set when Type == "folder"

    [JsonIgnore] public bool IsFolder => Type == "folder";

    public static BookmarkNode Link(string url, string title) => new() { Type = "url", Url = url, Title = title };
    public static BookmarkNode Folder(string title) => new() { Type = "folder", Title = title };
}

/// <summary>Legacy flat bookmark (pre-folders). Kept only so old files still deserialize.</summary>
public class Bookmark
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// Saved sites, as a tree with folders, persisted as JSON in %APPDATA%\Wisp\bookmarks.json.
/// <see cref="Roots"/> are the items shown directly on the bookmarks bar.
/// </summary>
public class Bookmarks
{
    /// <summary>Top-level bar items (links and folders).</summary>
    public List<BookmarkNode> Roots { get; set; } = new();

    /// <summary>Legacy flat list — only present in old files; migrated into <see cref="Roots"/> on load.</summary>
    public List<Bookmark>? Items { get; set; }

    public static Bookmarks Load()
    {
        try
        {
            if (File.Exists(AppPaths.BookmarksFile))
            {
                var b = JsonSerializer.Deserialize<Bookmarks>(File.ReadAllText(AppPaths.BookmarksFile)) ?? new Bookmarks();
                b.MigrateLegacy();
                return b;
            }
        }
        catch { /* corrupt/missing */ }
        return new Bookmarks();
    }

    /// <summary>Folds an old flat <see cref="Items"/> list into the tree, once.</summary>
    private void MigrateLegacy()
    {
        if (Items == null || Items.Count == 0) return;
        if (Roots.Count == 0)
            foreach (var it in Items)
                Roots.Add(BookmarkNode.Link(it.Url, string.IsNullOrWhiteSpace(it.Title) ? it.Url : it.Title));
        Items = null;
        Save();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            AppPaths.WriteAtomic(AppPaths.BookmarksFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }));
        }
        catch { /* best effort */ }
    }

    // ---- queries ---------------------------------------------------------------------

    private static IEnumerable<BookmarkNode> Walk(IEnumerable<BookmarkNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n.IsFolder)
                foreach (var c in Walk(n.Children))
                    yield return c;
        }
    }

    public bool Contains(string url) => Walk(Roots).Any(n => !n.IsFolder && n.Url == url);

    /// <summary>Every saved link anywhere in the tree, as (url, title).</summary>
    public IEnumerable<(string url, string title)> AllUrls()
        => Walk(Roots).Where(n => !n.IsFolder && n.Url != null).Select(n => (n.Url!, n.Title));

    /// <summary>All folders in the tree, as (folder, displayPath) — used for "add to folder" menus.</summary>
    public IEnumerable<(BookmarkNode folder, string path)> AllFolders()
    {
        var acc = new List<(BookmarkNode, string)>();
        void Recurse(IEnumerable<BookmarkNode> nodes, string prefix)
        {
            foreach (var n in nodes.Where(n => n.IsFolder))
            {
                var path = string.IsNullOrEmpty(prefix) ? n.Title : prefix + " / " + n.Title;
                acc.Add((n, path));
                Recurse(n.Children, path);
            }
        }
        Recurse(Roots, "");
        return acc;
    }

    // ---- mutations -------------------------------------------------------------------

    private static bool RemoveRec(List<BookmarkNode> nodes, string url)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!nodes[i].IsFolder && nodes[i].Url == url) { nodes.RemoveAt(i); return true; }
            if (nodes[i].IsFolder && RemoveRec(nodes[i].Children, url)) return true;
        }
        return false;
    }

    public void Remove(string url)
    {
        if (RemoveRec(Roots, url)) Save();
    }

    public void RemoveNode(BookmarkNode node)
    {
        bool RemoveObj(List<BookmarkNode> nodes)
        {
            if (nodes.Remove(node)) return true;
            foreach (var n in nodes.Where(n => n.IsFolder))
                if (RemoveObj(n.Children)) return true;
            return false;
        }
        if (RemoveObj(Roots)) Save();
    }

    /// <summary>Adds the page if not bookmarked, removes it if it is. Returns true if now bookmarked.</summary>
    public bool Toggle(string url, string title)
    {
        if (Contains(url)) { Remove(url); return false; }
        Roots.Insert(0, BookmarkNode.Link(url, string.IsNullOrWhiteSpace(title) ? url : title));
        Save();
        return true;
    }

    public void AddLink(string url, string title, BookmarkNode? folder = null)
    {
        var node = BookmarkNode.Link(url, string.IsNullOrWhiteSpace(title) ? url : title);
        (folder?.Children ?? Roots).Add(node);
        Save();
    }

    public BookmarkNode AddFolder(string name, BookmarkNode? parent = null)
    {
        var f = BookmarkNode.Folder(string.IsNullOrWhiteSpace(name) ? "New folder" : name);
        (parent?.Children ?? Roots).Add(f);
        Save();
        return f;
    }

    // ---- import / export (Netscape bookmark HTML, the format every browser reads) -----

    public string ExportHtml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        WriteNodes(sb, Roots, 1);
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    private static void WriteNodes(StringBuilder sb, List<BookmarkNode> nodes, int indent)
    {
        var pad = new string(' ', indent * 4);
        foreach (var n in nodes)
        {
            if (n.IsFolder)
            {
                sb.AppendLine($"{pad}<DT><H3>{Esc(n.Title)}</H3>");
                sb.AppendLine($"{pad}<DL><p>");
                WriteNodes(sb, n.Children, indent + 1);
                sb.AppendLine($"{pad}</DL><p>");
            }
            else
            {
                sb.AppendLine($"{pad}<DT><A HREF=\"{Esc(n.Url ?? "")}\">{Esc(n.Title)}</A>");
            }
        }
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Imports links from a Netscape bookmark HTML file (flat), deduped. Returns count added.</summary>
    public int ImportHtml(string html)
    {
        var existing = new HashSet<string>(AllUrls().Select(u => u.url), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (Match m in Regex.Matches(html, "<A[^>]*HREF=\"([^\"]*)\"[^>]*>(.*?)</A>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var url = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (!existing.Add(url)) continue;
            var title = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")).Trim();
            Roots.Add(BookmarkNode.Link(url, string.IsNullOrEmpty(title) ? url : title));
            added++;
        }
        if (added > 0) Save();
        return added;
    }
}
