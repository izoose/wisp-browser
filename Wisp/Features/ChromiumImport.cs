using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>A detected Chromium-family browser profile we can import from.</summary>
public class BrowserProfile
{
    public string Name { get; init; } = "";        // "Brave", "Google Chrome", "Microsoft Edge"
    public string UserDataPath { get; init; } = ""; // ...\User Data
    public string ProfilePath { get; init; } = "";  // ...\User Data\Default (or best Profile N)
    public string LocalStatePath => Path.Combine(UserDataPath, "Local State");

    public bool HasCookies => CookiesDbPath != null;
    public string? CookiesDbPath
    {
        get
        {
            var net = Path.Combine(ProfilePath, "Network", "Cookies");
            if (File.Exists(net)) return net;
            var flat = Path.Combine(ProfilePath, "Cookies");
            return File.Exists(flat) ? flat : null;
        }
    }
    public string BookmarksPath => Path.Combine(ProfilePath, "Bookmarks");
    public string HistoryPath => Path.Combine(ProfilePath, "History");
}

public class ImportResult
{
    public int Cookies, Bookmarks, History;
    public string? Error;
    public bool Any => Cookies + Bookmarks + History > 0;
}

/// <summary>
/// Imports cookies (so you stay logged in), bookmarks (with folders) and history from an
/// installed Chromium-family browser (Brave/Chrome/Edge). Cookies are DPAPI+AES-GCM decrypted
/// with the source browser's key and re-added through WebView2's own cookie store.
/// </summary>
public static class ChromiumImport
{
    private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- detection -------------------------------------------------------------------

    public static List<BrowserProfile> DetectBrowsers()
    {
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var found = new List<BrowserProfile>();
        void Try(string name, string rel)
        {
            var userData = Path.Combine(lad, rel);
            if (!File.Exists(Path.Combine(userData, "Local State"))) return;
            var profile = BestProfile(userData);
            if (profile == null) return;
            found.Add(new BrowserProfile { Name = name, UserDataPath = userData, ProfilePath = profile });
        }
        Try("Brave", Path.Combine("BraveSoftware", "Brave-Browser", "User Data"));
        Try("Google Chrome", Path.Combine("Google", "Chrome", "User Data"));
        Try("Microsoft Edge", Path.Combine("Microsoft", "Edge", "User Data"));
        return found;
    }

    /// <summary>Picks the profile dir with the most data (largest cookies db, else largest history).</summary>
    private static string? BestProfile(string userData)
    {
        var candidates = new List<string>();
        var def = Path.Combine(userData, "Default");
        if (Directory.Exists(def)) candidates.Add(def);
        try
        {
            foreach (var d in Directory.GetDirectories(userData, "Profile *"))
                candidates.Add(d);
        }
        catch { }
        if (candidates.Count == 0) return null;

        long Weight(string p)
        {
            long w = 0;
            foreach (var f in new[] { Path.Combine(p, "Network", "Cookies"), Path.Combine(p, "Cookies"), Path.Combine(p, "History"), Path.Combine(p, "Bookmarks") })
                if (File.Exists(f)) { try { w += new FileInfo(f).Length; } catch { } }
            return w;
        }
        return candidates.OrderByDescending(Weight).First();
    }

    // ---- orchestration ---------------------------------------------------------------

    /// <summary>Runs the full import. Cookie re-adding happens on the calling (UI) thread since it
    /// touches <paramref name="cw"/>; heavy parsing is done on a background thread.</summary>
    public static async Task<ImportResult> ImportAsync(
        BrowserProfile prof, CoreWebView2 cw, Bookmarks bm, History hist,
        bool cookies, bool bookmarks, bool history)
    {
        var result = new ImportResult();
        try
        {
            // 1. Cookies — decrypt off-thread, then add on the UI thread.
            if (cookies && prof.CookiesDbPath != null)
            {
                var plain = await Task.Run(() => ReadCookies(prof));
                foreach (var c in plain)
                {
                    try
                    {
                        var cookie = cw.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
                        cookie.IsSecure = c.Secure;
                        cookie.IsHttpOnly = c.HttpOnly;
                        cookie.SameSite = c.SameSite;
                        // Leaving Expires unset keeps it a session cookie (IsSession is read-only).
                        if (c.Expires is DateTime dt) cookie.Expires = dt;
                        cw.CookieManager.AddOrUpdateCookie(cookie);
                        result.Cookies++;
                    }
                    catch { /* a rejected cookie shouldn't sink the rest */ }
                }
            }

            // 2. Bookmarks + 3. History — pure data, safe off-thread; merge + save.
            await Task.Run(() =>
            {
                if (bookmarks && File.Exists(prof.BookmarksPath))
                    result.Bookmarks = MergeBookmarks(bm, prof.BookmarksPath, prof.Name);
                if (history && File.Exists(prof.HistoryPath))
                    result.History = MergeHistory(hist, prof.HistoryPath);
            });
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    // ---- cookies ---------------------------------------------------------------------

    private record PlainCookie(string Name, string Value, string Domain, string Path,
        bool Secure, bool HttpOnly, CoreWebView2CookieSameSiteKind SameSite, DateTime? Expires);

    private static List<PlainCookie> ReadCookies(BrowserProfile prof)
    {
        var outp = new List<PlainCookie>();
        byte[]? key;
        try { key = GetMasterKey(prof.LocalStatePath); }
        catch { return outp; }

        var tmp = CopyLocked(prof.CookiesDbPath!);
        try
        {
            using var conn = new SqliteConnection($"Data Source={tmp};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT host_key, name, encrypted_value, path, expires_utc, is_secure, is_httponly, samesite, is_persistent FROM cookies";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var host = r.GetString(0);
                var name = r.GetString(1);
                var enc = r.IsDBNull(2) ? Array.Empty<byte>() : r.GetFieldValue<byte[]>(2);
                var path = r.IsDBNull(3) ? "/" : r.GetString(3);
                var expires = r.IsDBNull(4) ? 0L : r.GetInt64(4);
                var secure = !r.IsDBNull(5) && r.GetInt64(5) != 0;
                var httpOnly = !r.IsDBNull(6) && r.GetInt64(6) != 0;
                var samesite = r.IsDBNull(7) ? -1 : r.GetInt64(7);
                var persistent = !r.IsDBNull(8) && r.GetInt64(8) != 0;

                if (string.IsNullOrEmpty(host)) continue;
                var value = DecryptValue(enc, key!, host);
                if (value == null) continue; // decryption failed
                outp.Add(new PlainCookie(name, value, host, path, secure, httpOnly,
                    MapSameSite(samesite), ExpiryOf(expires, persistent)));
            }
        }
        catch { }
        finally { SafeDelete(tmp); }
        return outp;
    }

    /// <summary>Reads and DPAPI-decrypts the AES master key from the browser's Local State.</summary>
    private static byte[] GetMasterKey(string localStatePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
        var b64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!;
        var raw = Convert.FromBase64String(b64);
        // Strip the 5-byte "DPAPI" prefix, then unprotect under the current Windows user.
        var protectedKey = raw[5..];
        return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
    }

    /// <summary>Decrypts a Chromium cookie value (v10/v11 AES-GCM, or legacy DPAPI). Returns null on failure.</summary>
    private static string? DecryptValue(byte[] enc, byte[] key, string host)
    {
        if (enc.Length == 0) return string.Empty;
        try
        {
            // v10 / v11 → AES-256-GCM: [3-byte prefix][12-byte nonce][ciphertext][16-byte tag]
            if (enc.Length > 3 + 12 + 16 && enc[0] == (byte)'v' && enc[1] == (byte)'1'
                && (enc[2] == (byte)'0' || enc[2] == (byte)'1'))
            {
                var nonce = enc[3..15];
                var tag = enc[^16..];
                var cipher = enc[15..^16];
                var plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(key, 16))
                    gcm.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(StripDomainHash(plain, host));
            }
            // Legacy: the value itself is DPAPI-encrypted.
            var dp = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dp);
        }
        catch { return null; }
    }

    /// <summary>Newer Chromium prefixes the plaintext with a 32-byte SHA-256 of the cookie's domain
    /// (domain-bound cookies). Strip it if present so we recover the real value.</summary>
    private static byte[] StripDomainHash(byte[] plain, string host)
    {
        if (plain.Length < 32) return plain;
        using var sha = SHA256.Create();
        foreach (var candidate in new[] { host, host.TrimStart('.') })
        {
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(candidate));
            if (plain.AsSpan(0, 32).SequenceEqual(h)) return plain[32..];
        }
        return plain;
    }

    private static CoreWebView2CookieSameSiteKind MapSameSite(long s) => s switch
    {
        0 => CoreWebView2CookieSameSiteKind.None,
        2 => CoreWebView2CookieSameSiteKind.Strict,
        _ => CoreWebView2CookieSameSiteKind.Lax,
    };

    /// <summary>Converts a persistent cookie's Chromium expiry (micros since 1601) to a UTC DateTime.
    /// Returns null for session cookies or out-of-range values.</summary>
    private static DateTime? ExpiryOf(long micros, bool persistent)
    {
        if (!persistent || micros <= 0) return null;
        try { return ChromeEpoch.AddTicks(micros * 10); }
        catch { return null; }
    }

    // ---- bookmarks -------------------------------------------------------------------

    private static int MergeBookmarks(Bookmarks bm, string bookmarksPath, string browserName)
    {
        int added = 0;
        var existing = new HashSet<string>(bm.AllUrls().Select(u => u.url), StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(File.ReadAllText(bookmarksPath));
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return 0;

        // Bookmarks-bar items go straight onto our bar.
        if (roots.TryGetProperty("bookmark_bar", out var bar) && bar.TryGetProperty("children", out var barKids))
            foreach (var child in barKids.EnumerateArray())
            {
                var node = MapNode(child, existing, ref added);
                if (node != null) bm.Roots.Add(node);
            }

        // "Other bookmarks" become a folder so nothing is lost.
        if (roots.TryGetProperty("other", out var other) && other.TryGetProperty("children", out var otherKids)
            && otherKids.GetArrayLength() > 0)
        {
            var folder = BookmarkNode.Folder($"Other bookmarks ({browserName})");
            foreach (var child in otherKids.EnumerateArray())
            {
                var node = MapNode(child, existing, ref added);
                if (node != null) folder.Children.Add(node);
            }
            if (folder.Children.Count > 0) bm.Roots.Add(folder);
        }

        if (added > 0) bm.Save();
        return added;
    }

    private static BookmarkNode? MapNode(JsonElement e, HashSet<string> existing, ref int added)
    {
        var type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
        var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        if (type == "url")
        {
            var url = e.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url) || url.StartsWith("chrome://") || url.StartsWith("edge://")) return null;
            if (!existing.Add(url)) return null; // dedupe against what we already have
            added++;
            return BookmarkNode.Link(url, string.IsNullOrWhiteSpace(name) ? url : name);
        }
        if (type == "folder")
        {
            var folder = BookmarkNode.Folder(string.IsNullOrWhiteSpace(name) ? "Folder" : name);
            if (e.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var c in kids.EnumerateArray())
                {
                    var child = MapNode(c, existing, ref added);
                    if (child != null) folder.Children.Add(child);
                }
            return folder.Children.Count > 0 ? folder : null;
        }
        return null;
    }

    // ---- history ---------------------------------------------------------------------

    private static int MergeHistory(History hist, string historyPath)
    {
        var tmp = CopyLocked(historyPath);
        int added = 0;
        try
        {
            var existing = new HashSet<string>(hist.Items.Select(i => i.Url), StringComparer.OrdinalIgnoreCase);
            using var conn = new SqliteConnection($"Data Source={tmp};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT url, title, last_visit_time FROM urls ORDER BY last_visit_time DESC LIMIT 3000";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var url = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://"))) continue;
                if (!existing.Add(url)) continue;
                var title = r.IsDBNull(1) ? "" : r.GetString(1);
                var when = r.IsDBNull(2) ? 0L : r.GetInt64(2);
                hist.Items.Add(new HistoryEntry
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? url : title,
                    VisitedUtc = when > 0 ? ChromeEpoch.AddTicks(when * 10) : DateTime.UtcNow,
                });
                added++;
            }
            hist.Items.Sort((a, b) => b.VisitedUtc.CompareTo(a.VisitedUtc));
            if (hist.Items.Count > 2000) hist.Items.RemoveRange(2000, hist.Items.Count - 2000);
            if (added > 0) hist.Save();
        }
        catch { }
        finally { SafeDelete(tmp); }
        return added;
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Copies a possibly-locked SQLite DB (browser may be running) to a temp file we own.</summary>
    private static string CopyLocked(string src)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wisp_imp_" + Guid.NewGuid().ToString("N") + ".db");
        using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var outp = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            fs.CopyTo(outp);
        // Bring along the WAL so the snapshot is consistent.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var s = src + suffix;
            if (!File.Exists(s)) continue;
            try
            {
                using var fs = new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var outp = new FileStream(tmp + suffix, FileMode.Create, FileAccess.Write);
                fs.CopyTo(outp);
            }
            catch { }
        }
        return tmp;
    }

    private static void SafeDelete(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
