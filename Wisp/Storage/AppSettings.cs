using System;
using System.IO;
using System.Text.Json;

namespace Wisp;

/// <summary>
/// User preferences, persisted as JSON in %APPDATA%\Wisp\settings.json.
/// Kept deliberately small — this is a barebones browser.
/// </summary>
/// <summary>An omnibox keyword shortcut (e.g. "yt" -> YouTube search). %s is replaced by the query.</summary>
public class SearchKeyword
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";

    public static System.Collections.Generic.List<SearchKeyword> Defaults() => new()
    {
        new() { Keyword = "yt",  Name = "YouTube",      Template = "https://www.youtube.com/results?search_query=%s" },
        new() { Keyword = "gh",  Name = "GitHub",       Template = "https://github.com/search?q=%s" },
        new() { Keyword = "w",   Name = "Wikipedia",    Template = "https://en.wikipedia.org/wiki/Special:Search?search=%s" },
        new() { Keyword = "a",   Name = "Amazon",       Template = "https://www.amazon.com/s?k=%s" },
        new() { Keyword = "r",   Name = "Reddit",       Template = "https://www.reddit.com/search/?q=%s" },
        new() { Keyword = "map", Name = "Google Maps",  Template = "https://www.google.com/maps/search/%s" },
    };
}

/// <summary>A new-tab shortcut tile.</summary>
public class Shortcut
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

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

    /// <summary>Show tabs in a left sidebar instead of the top strip.</summary>
    public bool VerticalTabs { get; set; }

    /// <summary>When true, opening a new tab puts the cursor in the address bar instead of the
    /// new-tab page's search box.</summary>
    public bool FocusAddressOnNewTab { get; set; }

    /// <summary>Middle-click / Ctrl+click on a link opens a background tab (stay on the current
    /// page). A plain left-click on a target=_blank link still switches to the new tab.</summary>
    public bool OpenLinksInBackground { get; set; } = true;

    /// <summary>Aggressive memory saver: coalesces cross-site renderers (fewer processes, less
    /// RAM) at the cost of weaker site isolation. Applies after restart.</summary>
    public bool MemorySaver { get; set; }

    /// <summary>Wisp's built-in network ad/tracker blocker.</summary>
    public bool AdBlockEnabled { get; set; } = true;

    /// <summary>Sites where the user turned the shield off (per-site allowlist).</summary>
    public System.Collections.Generic.List<string> AdBlockAllowedHosts { get; set; } = new();

    /// <summary>Whether we've already offered to import data from another browser (first-run prompt).</summary>
    public bool ImportOffered { get; set; }

    /// <summary>Check GitHub for a newer Wisp release on startup and offer a one-click update.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>The version that last ran. When it changes (i.e. after an update) we nudge the
    /// Windows shell to refresh icons, so an in-place .exe swap doesn't leave a blank taskbar icon.</summary>
    public string? LastRunVersion { get; set; }

    /// <summary>Omnibox keyword shortcuts: type "yt cats" to search YouTube. %s is the query.</summary>
    public System.Collections.Generic.List<SearchKeyword> SearchKeywords { get; set; } = SearchKeyword.Defaults();

    /// <summary>Custom new-tab shortcut tiles. Null until first seeded from history; then user-managed.</summary>
    public System.Collections.Generic.List<Shortcut>? NewTabShortcuts { get; set; }

    /// <summary>Remembered site permission decisions, keyed "origin|PermissionKind" -> allowed.
    /// Cleared alongside cookies/site data.</summary>
    public System.Collections.Generic.Dictionary<string, bool> SitePermissions { get; set; } = new();

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
            AppPaths.WriteAtomic(AppPaths.SettingsFile,
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
