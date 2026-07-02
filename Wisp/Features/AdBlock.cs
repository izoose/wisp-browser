using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>
/// Wisp used to bundle uBlock Origin Lite as an extension, but its popup can't work under
/// WebView2 (no Chrome tabs API) and it duplicated our native <see cref="AdBlockEngine"/>.
/// We now block natively and remove any previously-installed copy so it doesn't linger in the
/// toolbar or burn RAM on its service worker.
/// </summary>
public static class AdBlock
{
    private static bool _attempted;

    public static async Task EnsureRemovedAsync(CoreWebView2 cw, AppSettings settings)
    {
        if (_attempted) return; // once per app run
        _attempted = true;

        try
        {
            var existing = await cw.Profile.GetBrowserExtensionsAsync();
            foreach (var e in existing.Where(e =>
                         e.Id == settings.AdBlockExtensionId ||
                         e.Name.Contains("ublock", StringComparison.OrdinalIgnoreCase) ||
                         e.Name.Contains("uBO", StringComparison.OrdinalIgnoreCase)))
            {
                try { await e.RemoveAsync(); Log($"removed bundled ad-blocker '{e.Name}'"); }
                catch (Exception ex) { Log("remove failed: " + ex.Message); }
            }
            if (settings.AdBlockExtensionId != null) { settings.AdBlockExtensionId = null; settings.Save(); }
        }
        catch (Exception ex)
        {
            Log("enumerate failed: " + ex.Message);
        }
    }

    private static void Log(string msg)
    {
        var path = Environment.GetEnvironmentVariable("WISP_LOG");
        if (path == null) return;
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} [adblock] {msg}{Environment.NewLine}"); }
        catch { }
    }
}
