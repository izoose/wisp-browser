using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Wisp;

public class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime VisitedUtc { get; set; }
}

/// <summary>Local browsing history, persisted as JSON in %APPDATA%\Wisp\history.json.</summary>
public class History
{
    private const int MaxEntries = 2000;

    public List<HistoryEntry> Items { get; set; } = new();

    public static History Load()
    {
        try
        {
            if (File.Exists(AppPaths.HistoryFile))
                return JsonSerializer.Deserialize<History>(File.ReadAllText(AppPaths.HistoryFile)) ?? new History();
        }
        catch { /* corrupt/missing */ }
        return new History();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            AppPaths.WriteAtomic(AppPaths.HistoryFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public void Record(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !(url.StartsWith("http://") || url.StartsWith("https://")) ||
            url.StartsWith("https://wisp.newtab", System.StringComparison.OrdinalIgnoreCase))
            return;

        if (Items.Count > 0 && Items[0].Url == url)
        {
            if (!string.IsNullOrWhiteSpace(title)) Items[0].Title = title; // same page, better title
            return;
        }

        Items.Insert(0, new HistoryEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? url : title,
            VisitedUtc = DateTime.UtcNow,
        });
        if (Items.Count > MaxEntries)
            Items.RemoveRange(MaxEntries, Items.Count - MaxEntries);
    }

    public IEnumerable<HistoryEntry> Recent(int count) => Items.Take(count);

    /// <summary>Wipes all recorded visits (used by Clear browsing data).</summary>
    public void Clear() { Items.Clear(); Save(); }
}
