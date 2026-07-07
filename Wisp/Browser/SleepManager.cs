using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Wisp;

/// <summary>
/// The memory lever. On a timer it suspends background tabs idle past a threshold (freeing
/// their renderer memory) and fully discards tabs idle even longer (tearing down the
/// renderer; the tab reloads when next activated). Timings come from <see cref="AppSettings"/>
/// but can be overridden with env vars (seconds) for testing/tuning:
/// WISP_SLEEP_TICK_SECONDS, WISP_SUSPEND_SECONDS, WISP_DISCARD_SECONDS, WISP_LOG.
/// </summary>
public class SleepManager
{
    private readonly TabManager _tabs;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly string? _logPath;

    public SleepManager(TabManager tabs, AppSettings settings)
    {
        _tabs = tabs;
        _settings = settings;
        _logPath = Environment.GetEnvironmentVariable("WISP_LOG");

        var tick = SecondsFromEnv("WISP_SLEEP_TICK_SECONDS", 30);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tick) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private TimeSpan SuspendAfter =>
        TimeSpan.FromSeconds(SecondsFromEnv("WISP_SUSPEND_SECONDS", Math.Max(1, _settings.SuspendAfterMinutes) * 60));

    private TimeSpan DiscardAfter =>
        TimeSpan.FromSeconds(SecondsFromEnv("WISP_DISCARD_SECONDS", Math.Max(2, _settings.DiscardAfterMinutes) * 60));

    private async void Tick()
    {
        var now = DateTime.UtcNow;
        Log($"tick active='{Trim(_tabs.Active?.Title ?? "<null>")}' tabs={_tabs.Tabs.Count}");
        foreach (var tab in _tabs.Tabs.ToArray())
        {
            if (tab == _tabs.Active || tab.View == null) continue;
            // Never freeze/discard a tab that's audibly playing (music/video) — like Chrome/Edge.
            if (tab.IsPlayingAudio && !tab.IsMuted) continue;

            var idle = now - tab.LastActiveUtc;
            if (idle >= DiscardAfter)
            {
                _tabs.DiscardTab(tab);
                Log($"discard '{Trim(tab.Title)}' idle={idle.TotalSeconds:F0}s webviewProcs={WebViewProcCount()}");
            }
            else if (idle >= SuspendAfter && tab.State != TabState.Suspended)
            {
                bool ok = await _tabs.TrySuspendTabAsync(tab);
                bool isSuspended = false;
                try { isSuspended = tab.View?.CoreWebView2?.IsSuspended ?? false; } catch { }
                Log($"suspend '{Trim(tab.Title)}' ok={ok} IsSuspended={isSuspended} idle={idle.TotalSeconds:F0}s webviewProcs={WebViewProcCount()}");
            }
        }
    }

    private static int SecondsFromEnv(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) && n > 0 ? n : fallback;
    }

    private static int WebViewProcCount()
    {
        try { return Process.GetProcessesByName("msedgewebview2").Length; }
        catch { return -1; }
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s.Substring(0, 40);

    private void Log(string msg)
    {
        Debug.WriteLine("[Wisp/Sleep] " + msg);
        if (_logPath == null) return;
        try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"); }
        catch { /* best effort */ }
    }
}
