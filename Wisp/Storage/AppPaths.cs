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

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
