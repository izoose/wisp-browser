using System;
using System.IO;
using System.Text.Json;

namespace Wisp;

/// <summary>
/// User preferences, persisted as JSON in %APPDATA%\Wisp\settings.json.
/// Kept deliberately small — this is a barebones browser.
/// </summary>
public class AppSettings
{
    public string SearchEngine { get; set; } = "Google";

    /// <summary>Algorithmic force-dark for light-only sites. Applied via a browser
    /// flag, so a change only takes effect on the next launch.</summary>
    public bool ForceDark { get; set; } = false;

    /// <summary>Minutes a background tab stays live before it is suspended.</summary>
    public int SuspendAfterMinutes { get; set; } = 5;

    /// <summary>Minutes a background tab stays suspended before it is fully discarded.</summary>
    public int DiscardAfterMinutes { get; set; } = 30;

    /// <summary>Id of our bundled ad-blocker once installed, so we don't reinstall or touch
    /// the user's other extensions.</summary>
    public string? AdBlockExtensionId { get; set; }

    public bool BookmarksBarVisible { get; set; }

    /// <summary>When true, opening a new tab puts the cursor in the address bar instead of the
    /// new-tab page's search box.</summary>
    public bool FocusAddressOnNewTab { get; set; }

    /// <summary>Aggressive memory saver: coalesces cross-site renderers (fewer processes, less
    /// RAM) at the cost of weaker site isolation. Applies after restart.</summary>
    public bool MemorySaver { get; set; }

    /// <summary>Wisp's built-in network ad/tracker blocker.</summary>
    public bool AdBlockEnabled { get; set; } = true;

    /// <summary>Sites where the user turned the shield off (per-site allowlist).</summary>
    public System.Collections.Generic.List<string> AdBlockAllowedHosts { get; set; } = new();

    /// <summary>Whether we've already offered to import data from another browser (first-run prompt).</summary>
    public bool ImportOffered { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile))
                       ?? new AppSettings();
        }
        catch { /* corrupt/missing -> defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public string BuildSearchUrl(string query)
    {
        var q = Uri.EscapeDataString(query);
        return SearchEngine switch
        {
            "DuckDuckGo"   => $"https://duckduckgo.com/?q={q}",
            "Brave Search" => $"https://search.brave.com/search?q={q}",
            "Bing"         => $"https://www.bing.com/search?q={q}",
            _              => $"https://www.google.com/search?q={q}",
        };
    }
}
