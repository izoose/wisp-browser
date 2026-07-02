using System;
using System.Collections.Generic;
using System.IO;

namespace Wisp;

/// <summary>
/// Wisp's own network ad/tracker blocker: blocks requests to known ad/tracker domains at the
/// engine level (via WebView2's WebResourceRequested), so it works reliably regardless of what
/// extension DNR support WebView2 does or doesn't have.
/// </summary>
public static class AdBlockEngine
{
    public static bool Enabled { get; set; } = true;
    public static int Blocks;

    private static HashSet<string>? _blocked;
    private static HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static void OnBlocked(string host) => Blocks++;

    public static int Count => Domains.Count;

    // ---- per-site allowlist ("shield off for this site") -----------------------------

    private static string Norm(string host)
        => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;

    /// <summary>Replaces the allowlist (called on startup from saved settings).</summary>
    public static void SetAllowedHosts(IEnumerable<string> hosts)
        => _allowed = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);

    public static bool IsSiteAllowed(string pageHost)
        => !string.IsNullOrEmpty(pageHost) && _allowed.Contains(Norm(pageHost));

    public static void AllowSite(string pageHost) { if (!string.IsNullOrEmpty(pageHost)) _allowed.Add(Norm(pageHost)); }
    public static void BlockSite(string pageHost) { if (!string.IsNullOrEmpty(pageHost)) _allowed.Remove(Norm(pageHost)); }
    public static IEnumerable<string> AllowedHosts => _allowed;

    /// <summary>Whether a request should be blocked, given the page it's loading on.</summary>
    public static bool ShouldBlock(string requestHost, string pageHost)
        => Enabled && !IsSiteAllowed(pageHost) && IsBlocked(requestHost);

    private static HashSet<string> Domains
    {
        get
        {
            if (_blocked != null) return _blocked;
            lock (_lock)
            {
                if (_blocked != null) return _blocked;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "Resources", "adblock", "blocklist.txt");
                    if (File.Exists(path))
                        foreach (var line in File.ReadLines(path))
                        {
                            var d = line.Trim();
                            if (d.Length > 0 && d[0] != '#') set.Add(d);
                        }
                }
                catch { }
                _blocked = set;
                return _blocked;
            }
        }
    }

    /// <summary>True if the host (or a parent domain of it) is on the blocklist.</summary>
    public static bool IsBlocked(string host)
    {
        if (!Enabled || string.IsNullOrEmpty(host)) return false;
        var set = Domains;
        if (set.Count == 0) return false;

        var h = host;
        while (true)
        {
            if (set.Contains(h)) return true;
            int dot = h.IndexOf('.');
            if (dot < 0) return false;
            h = h.Substring(dot + 1);
            if (!h.Contains('.')) return false; // don't match a bare TLD
        }
    }
}
