using System;
using System.IO;

namespace Wisp;

/// <summary>Central place for every on-disk location Wisp uses.</summary>
public static class AppPaths
{
    /// <summary>Per-user config/state (settings, session, bookmarks, history).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");

    /// <summary>Shared WebView2 user-data folder — cookies/cache/logins live here.</summary>
    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "WebView2");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string SessionFile => Path.Combine(DataDir, "session.json");
    public static string BookmarksFile => Path.Combine(DataDir, "bookmarks.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");

    /// <summary>Passwords staged during import, injected into Login Data on next startup.</summary>
    public static string PendingLoginsFile => Path.Combine(DataDir, "pending-logins.json");

    // WebView2 lays its Chromium profile under an "EBWebView" subfolder of the user-data folder.
    public static string WebViewProfileDir => Path.Combine(UserDataFolder, "EBWebView");
    public static string WebViewLocalState => Path.Combine(WebViewProfileDir, "Local State");
    public static string WebViewLoginData => Path.Combine(WebViewProfileDir, "Default", "Login Data");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);

    /// <summary>Writes a file crash-safely: write a temp file, then atomically replace the target.
    /// A crash/power-loss mid-write leaves the old file intact instead of a truncated/empty one.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, null); // atomic swap on NTFS
        else File.Move(tmp, path);
    }
}
