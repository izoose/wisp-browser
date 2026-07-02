using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Wisp;

/// <summary>
/// Registers Wisp with Windows as a browser + file handler so it can be picked as the default
/// app for http/https links, PDFs, and HTML files. Writes to HKCU (no admin needed). Windows
/// then requires the user to confirm the choice in Settings — you can't force it silently.
/// </summary>
public static class DefaultBrowser
{
    private const string ProgId = "WispHTML";

    public static void Register()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exe)) return;

        using (var cls = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            cls.SetValue("", "Wisp Document");
            cls.SetValue("FriendlyTypeName", "Wisp Document");
            using (var icon = cls.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = cls.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }

        using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Wisp\Capabilities"))
        {
            cap.SetValue("ApplicationName", "Wisp");
            cap.SetValue("ApplicationDescription", "A lightweight, dark browser.");
            using (var url = cap.CreateSubKey("URLAssociations"))
            {
                url.SetValue("http", ProgId);
                url.SetValue("https", ProgId);
            }
            using (var file = cap.CreateSubKey("FileAssociations"))
            {
                foreach (var ext in new[] { ".htm", ".html", ".pdf", ".svg", ".webp", ".shtml" })
                    file.SetValue(ext, ProgId);
            }
        }

        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            reg.SetValue("Wisp", @"Software\Wisp\Capabilities");
    }

    /// <summary>Opens the Windows "Default apps" screen so the user can pick Wisp.</summary>
    public static void OpenDefaultAppsSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
        catch { }
    }
}
