using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Wisp;

/// <summary>
/// Checks GitHub Releases for a newer Wisp and applies a one-click update by running the release's
/// silent installer (which closes Wisp, swaps the files, and relaunches). Pull-based — each copy
/// checks on its own; there's no server pushing to clients.
/// </summary>
public static class Updater
{
    private const string LatestApi = "https://api.github.com/repos/izoose/wisp-browser/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static Updater() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Wisp-Updater/1.0");

    public static Version Current =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build) : new Version(1, 0, 0);

    public record UpdateInfo(Version Version, string Tag, string? SetupUrl, string? ZipUrl, string? ZipSha256);

    /// <summary>Returns info about a newer release, or null if up to date / offline / no asset.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var v = ParseVersion(tag);
            if (v == null || v <= Current) return null;

            string? setup = null, zip = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (string.Equals(name, "WispSetup.exe", StringComparison.OrdinalIgnoreCase)) setup = url;
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && name.Contains("portable", StringComparison.OrdinalIgnoreCase)) zip = url;
                }

            // Optional integrity pin: a "zip-sha256: <hex>" line in the release notes lets us verify
            // the downloaded portable zip before applying it (guards against a corrupt/tampered asset).
            string? sha = null;
            if (root.TryGetProperty("body", out var body) && body.GetString() is { } notes)
            {
                var m = Regex.Match(notes, @"zip-sha256:\s*([a-fA-F0-9]{64})");
                if (m.Success) sha = m.Groups[1].Value.ToLowerInvariant();
            }
            return (setup == null && zip == null) ? null : new UpdateInfo(v, tag, setup, zip, sha);
        }
        catch { return null; }
    }

    /// <summary>Updates Wisp in place — downloads the portable build, then a helper waits for this
    /// process to exit, copies the new files over wherever Wisp is installed, and relaunches it.
    /// Works no matter where Wisp runs from (no fixed install location, no path mismatch).</summary>
    public static async Task<bool> ApplyInPlaceAsync(string zipUrl, string? expectedSha256 = null)
    {
        try
        {
            var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var exe = Path.Combine(appDir, "Wisp.exe");
            var tmpZip = Path.Combine(Path.GetTempPath(), "wisp_update.zip");
            var tmpDir = Path.Combine(Path.GetTempPath(), "wisp_update_extract");

            var bytes = await Http.GetByteArrayAsync(zipUrl);

            // Verify the download against the pinned hash (when the release published one).
            if (expectedSha256 != null)
            {
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (actual != expectedSha256) return false; // tampered/corrupt — refuse to apply
            }

            await File.WriteAllBytesAsync(tmpZip, bytes);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, tmpDir);

            // The zip may wrap everything in a top-level folder; copy from wherever Wisp.exe
            // actually lives, and abort if it isn't in the archive (don't relaunch the old binary).
            var matches = Directory.GetFiles(tmpDir, "Wisp.exe", SearchOption.AllDirectories);
            if (matches.Length == 0) return false;
            var srcDir = Path.GetDirectoryName(matches[0])!;

            int pid = Environment.ProcessId;
            var bat = Path.Combine(Path.GetTempPath(), "wisp_apply_update.cmd");
            var script =
                "@echo off\r\n" +
                ":waitloop\r\n" +
                $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto waitloop )\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                $"robocopy \"{srcDir}\" \"{appDir}\" /E /IS /IT /R:3 /W:1 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                $"start \"\" \"{exe}\"\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $"rmdir /s /q \"{tmpDir}\" >nul 2>&1\r\n" +
                $"del \"{tmpZip}\" >nul 2>&1\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n";
            await File.WriteAllTextAsync(bat, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch { return false; }
    }

    private static Version? ParseVersion(string tag)
        // Normalize to 3 components so a 4-part tag (v1.2.0.1) can't compare as newer than Current.
        => Version.TryParse(tag.TrimStart('v', 'V'), out var v)
            ? new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build)
            : null;

    /// <summary>Downloads the installer and runs it silently. Inno Setup closes this instance,
    /// updates in place, and relaunches. Returns false if the download/launch failed.</summary>
    public static async Task<bool> DownloadAndRunAsync(string setupUrl)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "WispSetup-update.exe");
            var bytes = await Http.GetByteArrayAsync(setupUrl);
            await File.WriteAllBytesAsync(tmp, bytes);
            // /CLOSEAPPLICATIONS lets the installer wait for our files to unlock; the app then
            // shuts itself down (see the caller) and the installer's post-install step relaunches
            // the new version. We don't use /RESTARTAPPLICATIONS because it relaunches whatever path
            // was closed — wrong when the running copy and the install target differ.
            Process.Start(new ProcessStartInfo
            {
                FileName = tmp,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /NOICONS /SP-",
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }
}
