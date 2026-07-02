using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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

    public record UpdateInfo(Version Version, string Tag, string SetupUrl);

    /// <summary>Returns info about a newer release, or null if up to date / offline / no installer asset.</summary>
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

            string? setup = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), "WispSetup.exe", StringComparison.OrdinalIgnoreCase))
                        setup = a.GetProperty("browser_download_url").GetString();
            return setup == null ? null : new UpdateInfo(v, tag, setup);
        }
        catch { return null; }
    }

    private static Version? ParseVersion(string tag)
        => Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;

    /// <summary>Downloads the installer and runs it silently. Inno Setup closes this instance,
    /// updates in place, and relaunches. Returns false if the download/launch failed.</summary>
    public static async Task<bool> DownloadAndRunAsync(string setupUrl)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "WispSetup-update.exe");
            var bytes = await Http.GetByteArrayAsync(setupUrl);
            await File.WriteAllBytesAsync(tmp, bytes);
            Process.Start(new ProcessStartInfo
            {
                FileName = tmp,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NOICONS /SP-",
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }
}
