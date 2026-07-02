using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace Wisp;

/// <summary>
/// Installs extensions straight from the Chrome Web Store by fetching the extension's CRX
/// package from Google's update service, stripping the CRX header, and unpacking the ZIP into
/// a local folder that WebView2 can load. (WebView2 has no Web Store UI of its own.)
/// </summary>
public static class WebStore
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        // Google's update service returns an empty body without a browser User-Agent.
        c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return c;
    }

    /// <summary>Unpacked extensions are kept here so WebView2 can reload them each launch.</summary>
    public static string ExtensionsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "extensions");

    /// <summary>Extracts the 32-char extension id from a Chrome Web Store detail URL.</summary>
    public static string? ExtractId(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (!(uri.Host.Contains("chromewebstore.google.com") || uri.Host.Contains("chrome.google.com")))
                return null;
            foreach (var seg in uri.AbsolutePath.Trim('/').Split('/'))
                if (seg.Length == 32 && IsId(seg)) return seg;
        }
        catch { }
        return null;
    }

    private static bool IsId(string s)
    {
        foreach (var c in s) if (c < 'a' || c > 'p') return false;
        return true;
    }

    /// <summary>Downloads + unpacks the extension; returns the folder containing manifest.json.</summary>
    public static async Task<string> InstallAsync(string extensionId, string browserVersion)
    {
        if (!IsId(extensionId)) throw new ArgumentException("Not a valid extension id.");

        var url =
            "https://clients2.google.com/service/update2/crx?response=redirect" +
            "&acceptformat=crx2,crx3" +
            $"&prodversion={Uri.EscapeDataString(browserVersion)}" +
            $"&x=id%3D{extensionId}%26installsource%3Dondemand%26uc";

        var crx = await Http.GetByteArrayAsync(url);
        int zipStart = FindZipStart(crx);

        var dest = Path.Combine(ExtensionsDir, extensionId);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);

        using (var ms = new MemoryStream(crx, zipStart, crx.Length - zipStart))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            zip.ExtractToDirectory(dest, overwriteFiles: true);

        if (!File.Exists(Path.Combine(dest, "manifest.json")))
            throw new Exception("Downloaded package had no manifest.json.");

        return dest;
    }

    /// <summary>CRX = "Cr24" header + signatures, then a normal ZIP. Locate the ZIP start.</summary>
    private static int FindZipStart(byte[] b)
    {
        if (b.Length > 16 && b[0] == (byte)'C' && b[1] == (byte)'r' && b[2] == (byte)'2' && b[3] == (byte)'4')
        {
            int version = BitConverter.ToInt32(b, 4);
            if (version == 3) return 12 + BitConverter.ToInt32(b, 8);                    // header length
            if (version == 2) return 16 + BitConverter.ToInt32(b, 8) + BitConverter.ToInt32(b, 12); // key + sig
        }
        // Fallback: scan for the ZIP local-file-header signature "PK\x03\x04".
        for (int i = 0; i + 3 < b.Length; i++)
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 0x03 && b[i + 3] == 0x04) return i;
        return 0;
    }
}
