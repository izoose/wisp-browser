using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wisp;

/// <summary>
/// Fetches live search suggestions from Google's public suggest endpoint (the same one
/// Chrome/Firefox use). Returns a plain list of suggested query strings.
/// </summary>
public static class SearchSuggest
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    static SearchSuggest()
    {
        // A browser-ish UA keeps the endpoint happy.
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wisp/1.0");
    }

    /// <summary>Returns up to a handful of query suggestions for <paramref name="query"/>.
    /// Never throws — returns an empty list on any failure (offline, cancelled, malformed).</summary>
    public static async Task<List<string>> FetchAsync(string query, CancellationToken ct)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;
        try
        {
            // client=firefox returns simple JSON: ["query", ["s1","s2",...]]
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&hl=en&q="
                      + Uri.EscapeDataString(query);
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
            {
                var arr = root[1];
                if (arr.ValueKind == JsonValueKind.Array)
                    foreach (var s in arr.EnumerateArray())
                        if (s.ValueKind == JsonValueKind.String)
                        {
                            var v = s.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) result.Add(v!);
                        }
            }
        }
        catch { /* offline / cancelled / bad payload — no suggestions */ }
        return result;
    }
}
