using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>
/// Owns the one <see cref="CoreWebView2Environment"/> shared by every tab. Sharing a single
/// environment + user-data folder means all tabs run under one browser process and keep
/// cookies/logins between runs. This is also where we strip telemetry and tune memory.
/// </summary>
public class BrowserEnvironment
{
    public CoreWebView2Environment Core { get; }

    private BrowserEnvironment(CoreWebView2Environment core) => Core = core;

    public static async Task<BrowserEnvironment> CreateAsync(AppSettings settings)
    {
        int rendererLimit = settings.MemorySaver ? 4 : 8;
        var args = new List<string>
        {
            // Shrink V8's new-space cap (officially-sanctioned per-renderer memory knob).
            "--js-flags=--scavenger_max_new_space_capacity_mb=8",
            // Tabs of the same site share one renderer process (fewer processes, less RAM).
            "--process-per-site",
            // Chromium's built-in low-memory profile: smaller caches, no prerender, aggressive purging.
            "--enable-low-end-device-mode",
            // Hard cap on renderer processes — the biggest lever against the process sprawl.
            "--renderer-process-limit=" + rendererLimit,
            // Cap the on-disk cache (indirectly bounds cache memory).
            "--disk-cache-size=52428800",
        };

        var disable = new List<string> { "SpareRendererForSitePerProcess" }; // no warm spare renderer
        var enable = new List<string>
        {
            "MemoryPurgeOnFreezeLimit",                        // purge memory when a tab is frozen
            "msWebView2SimulateMemoryPressureWhenInactive",    // inactive WebViews shed cache/memory
            "msWebView2TreatAppSuspendAsDeviceSuspend",        // pause timers when all views suspended
        };
        if (settings.MemorySaver)
        {
            // Coalesce cross-site renderers too — biggest memory win, weaker isolation.
            disable.Add("IsolateOrigins");
            disable.Add("site-per-process");
            args.Add("--disable-site-isolation-trials");
        }
        if (settings.ForceDark) enable.Add("WebContentsForceDark");

        args.Add("--disable-features=" + string.Join(",", disable));
        args.Add("--enable-features=" + string.Join(",", enable));

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', args),
            AreBrowserExtensionsEnabled = Environment.GetEnvironmentVariable("WISP_NO_EXT") != "1",
            IsCustomCrashReportingEnabled = true, // keep crash dumps local, don't phone home
        };

        var udf = Environment.GetEnvironmentVariable("WISP_UDF");
        if (string.IsNullOrEmpty(udf)) udf = AppPaths.UserDataFolder;
        Directory.CreateDirectory(udf);
        var core = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,          // use the installed Evergreen runtime
            userDataFolder: udf,
            options: options);

        return new BrowserEnvironment(core);
    }

    /// <summary>Creates a throwaway environment on a temp profile for a private window.
    /// Returns the temp folder so the caller can delete it on close.</summary>
    public static async Task<(BrowserEnvironment env, string udf)> CreatePrivateAsync(AppSettings settings)
    {
        var udf = Path.Combine(Path.GetTempPath(), "Wisp-Private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(udf);

        var args = new List<string> { "--js-flags=--scavenger_max_new_space_capacity_mb=8" };
        if (settings.ForceDark) args.Add("--enable-features=WebContentsForceDark");

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', args),
            AreBrowserExtensionsEnabled = false, // no extensions in private
            IsCustomCrashReportingEnabled = true,
        };

        var core = await CoreWebView2Environment.CreateAsync(null, udf, options);
        return (new BrowserEnvironment(core), udf);
    }
}
