using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>One installed extension, enriched with its icon + popup URL read from its manifest.</summary>
public class ExtensionEntry
{
    public CoreWebView2BrowserExtension Ext { get; init; } = null!;
    public string Id => Ext.Id;
    public string Name => Ext.Name;
    public bool IsEnabled => Ext.IsEnabled;
    public ImageSource? Icon { get; set; }
    public string? PopupUrl { get; set; }
    public string? SidePanelUrl { get; set; }
    public bool IsUserExtension { get; set; }
}

/// <summary>
/// Lists installed extensions and reads each one's manifest (from the folder we installed it
/// from) to find its toolbar icon and popup page — WebView2 doesn't surface these itself.
/// </summary>
public static class ExtensionsManager
{
    /// <summary>Edge/WebView2 built-in extensions we never want cluttering the toolbar.</summary>
    private static readonly HashSet<string> EdgeBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "mhjfbmdgcfjbbpaeojofohoefgiehjai", // Edge/Chrome PDF Viewer
        "dgiklkfkllikcanfonkcabmbdfmgleag", // Microsoft Clipboard Extension
        "jmjflgjpcpepeafmmgdpfkogkghcpiha", // Edge relevant text changes
        "ncbjelpjchkpbikbpkcchkhkblodoama", // WebView2 internal
    };

    public static async Task<List<ExtensionEntry>> ListAsync(CoreWebView2 cw, AppSettings settings)
    {
        var result = new List<ExtensionEntry>();
        try
        {
            var exts = await cw.Profile.GetBrowserExtensionsAsync();
            foreach (var e in exts)
            {
                if (EdgeBuiltIns.Contains(e.Id)) continue;
                var entry = new ExtensionEntry { Ext = e };
                var folder = FindFolder(e.Id, e.Name, settings);
                if (folder != null) { entry.IsUserExtension = true; Populate(entry, folder, e.Id); }
                result.Add(entry);
            }
        }
        catch { }
        return result;
    }

    private static string ExtensionsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "extensions");

    private static string? FindFolder(string id, string runtimeName, AppSettings settings)
    {
        // 1. Folder named by the runtime id (correct once we inject the signing key on install).
        var byId = Path.Combine(ExtensionsDir, id);
        if (File.Exists(Path.Combine(byId, "manifest.json"))) return byId;

        // 2. Fallback: unpacked installs get a path-derived id that differs from their store id
        //    (and hence the folder name). Match by the extension's resolved name instead.
        if (Directory.Exists(ExtensionsDir))
            foreach (var dir in Directory.GetDirectories(ExtensionsDir))
            {
                if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                var resolved = ResolveName(dir);
                if (!string.IsNullOrEmpty(resolved) && string.Equals(resolved, runtimeName, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        return null;
    }

    /// <summary>Reads a folder's extension name, resolving __MSG_ placeholders via _locales.</summary>
    private static string? ResolveName(string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) return null;
            if (!name.StartsWith("__MSG_") || !name.EndsWith("__")) return name;

            var msgKey = name.Substring(6, name.Length - 8);
            var locale = root.TryGetProperty("default_locale", out var dl) ? dl.GetString() : "en";
            var msgPath = Path.Combine(folder, "_locales", locale ?? "en", "messages.json");
            if (!File.Exists(msgPath)) return null;
            using var md = JsonDocument.Parse(File.ReadAllText(msgPath));
            foreach (var prop in md.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, msgKey, StringComparison.OrdinalIgnoreCase)
                    && prop.Value.TryGetProperty("message", out var msg))
                    return msg.GetString();
            return null;
        }
        catch { return null; }
    }

    private static void Populate(ExtensionEntry entry, string folder, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            var root = doc.RootElement;

            var popup = GetPopup(root);
            if (!string.IsNullOrEmpty(popup))
                entry.PopupUrl = $"chrome-extension://{id}/{popup.TrimStart('/')}";

            var panel = GetSidePanel(root, folder);
            if (!string.IsNullOrEmpty(panel))
                entry.SidePanelUrl = $"chrome-extension://{id}/{panel.TrimStart('/')}";

            var iconPath = GetActionIcon(root, "action") ?? GetActionIcon(root, "browser_action") ?? GetIcons(root);
            if (iconPath != null)
            {
                var full = Path.Combine(folder, iconPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) entry.Icon = LoadIcon(full);
            }
        }
        catch { }
    }

    private static string? GetPopup(JsonElement root)
    {
        if (root.TryGetProperty("action", out var a) && a.TryGetProperty("default_popup", out var p)) return p.GetString();
        if (root.TryGetProperty("browser_action", out var b) && b.TryGetProperty("default_popup", out var p2)) return p2.GetString();
        return null;
    }

    private static string? GetSidePanel(JsonElement root, string folder)
    {
        // Static path if the manifest declares one.
        if (root.TryGetProperty("side_panel", out var sp) && sp.TryGetProperty("default_path", out var dp))
        {
            var v = dp.GetString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        // Otherwise, if the extension uses the sidePanel API, look for a conventional panel file.
        if (HasPermission(root, "sidePanel"))
            foreach (var cand in new[] { "sidepanel.html", "side_panel.html", "sidePanel.html", "panel.html" })
                if (File.Exists(Path.Combine(folder, cand))) return cand;
        return null;
    }

    private static bool HasPermission(JsonElement root, string perm)
    {
        if (root.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var e in p.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() == perm) return true;
        return false;
    }

    private static string? GetActionIcon(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var a) && a.TryGetProperty("default_icon", out var di))
            return di.ValueKind == JsonValueKind.String ? di.GetString() : Largest(di);
        return null;
    }

    private static string? GetIcons(JsonElement root)
        => root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Object ? Largest(icons) : null;

    private static string? Largest(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        string? best = null; int bestSize = -1;
        foreach (var p in obj.EnumerateObject())
            if (int.TryParse(p.Name, out var sz) && sz > bestSize) { bestSize = sz; best = p.Value.GetString(); }
        return best;
    }

    private static ImageSource LoadIcon(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelWidth = 64; // decode large; the toolbar downscales it crisply (HighQuality)
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
