using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wisp;

public class SessionTab
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsPinned { get; set; }
}

public class SessionData
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int ActiveIndex { get; set; }
}

/// <summary>Persists the set of open tabs so a restart reopens them (cookies come back from
/// the shared user-data folder). Restored tabs load lazily — only the active one loads at
/// startup; the rest reload when first clicked.</summary>
public static class SessionStore
{
    public static SessionData? Load()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(AppPaths.SessionFile));
        }
        catch { /* corrupt/missing */ }
        return null;
    }

    public static void Save(SessionData data)
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.SessionFile,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
