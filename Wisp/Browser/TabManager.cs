using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Wisp;

/// <summary>
/// Owns the set of tabs and their WebView2 lifecycle. Tabs are shown by toggling the
/// visibility of one WebView2 per tab inside a shared host panel (never a WPF TabControl,
/// which unloads inactive tabs and breaks WebView2). Creation is lazy: a tab has no
/// WebView2 until it is first activated.
/// </summary>
public class TabManager
{
    /// <summary>Local dark new-tab page, served from the bundled Resources folder.</summary>
    public const string NewTabUrl = "https://wisp.newtab/newtab.html";
    private const string NewTabHost = "wisp.newtab";

    private readonly BrowserEnvironment _env;
    private readonly AppSettings _settings;
    private readonly Panel _host;

    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    public BrowserTab? Active { get; private set; }

    /// <summary>Raised when the active tab changes or navigates (refresh chrome/address bar).</summary>
    public event Action? ActiveTabUpdated;

    /// <summary>Raised (on the UI thread) when web content asks for a browser action via a
    /// keyboard shortcut. See <see cref="AcceleratorScript"/>.</summary>
    public event Action<string>? AcceleratorRequested;

    /// <summary>Raised (url, title) as pages commit/finish, for history recording.</summary>
    public event Action<string, string>? PageVisited;

    /// <summary>Raised when a download begins (default UI suppressed so we show our own).</summary>
    public event Action<CoreWebView2DownloadOperation>? DownloadStarted;

    /// <summary>Raised when the active tab's web content enters/exits element fullscreen (e.g. a video).</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>Raised when a page calls alert()/confirm()/prompt() so we can show a Wisp-styled dialog.</summary>
    public event Action<CoreWebView2ScriptDialogOpeningEventArgs>? ScriptDialogRequested;

    /// <summary>Recently-closed tabs, for Ctrl+Shift+T. Newest last.</summary>
    private readonly List<SessionTab> _closed = new();
    public bool HasClosedTabs => _closed.Count > 0;

    public TabManager(BrowserEnvironment env, AppSettings settings, Panel host)
    {
        _env = env;
        _settings = settings;
        _host = host;
    }

    public async Task<BrowserTab> NewTabAsync(string url, bool activate)
    {
        var tab = new BrowserTab { Url = url, Title = "New Tab", State = TabState.Discarded };
        Tabs.Add(tab);
        if (activate)
            await ActivateAsync(tab);
        return tab;
    }

    /// <summary>Adds a tab from restored session state without creating its WebView2 yet.</summary>
    public BrowserTab AddLazyTab(string url, string title, bool isPinned = false, bool neverSleep = false, string? groupColor = null)
    {
        var tab = new BrowserTab
        {
            Url = url,
            Title = string.IsNullOrEmpty(title) ? "New Tab" : title,
            State = TabState.Discarded,
            IsPinned = isPinned,
            NeverSleep = neverSleep,
            GroupColor = groupColor,
        };
        Tabs.Add(tab);
        return tab;
    }

    /// <summary>Captures the current tabs for session persistence.</summary>
    public SessionData Snapshot()
    {
        var data = new SessionData { ActiveIndex = Active != null ? Tabs.IndexOf(Active) : 0 };
        foreach (var t in Tabs)
            data.Tabs.Add(new SessionTab { Url = t.Url, Title = t.Title, IsPinned = t.IsPinned, NeverSleep = t.NeverSleep, GroupColor = t.GroupColor });
        return data;
    }

    public async Task ActivateAsync(BrowserTab tab)
    {
        if (Active == tab)
        {
            await EnsureViewAsync(tab);
            if (tab.View != null) tab.View.Visibility = Visibility.Visible;
            return;
        }

        if (Active != null)
        {
            Active.IsActive = false;
            Active.LastActiveUtc = DateTime.UtcNow; // idle clock starts when you leave a tab
            if (Active.View != null)
            {
                Active.View.Visibility = Visibility.Collapsed;
                Active.State = TabState.Background;
            }
        }

        Active = tab;
        tab.IsActive = true;
        tab.LastActiveUtc = DateTime.UtcNow;

        await EnsureViewAsync(tab);
        // Creating a cold tab's WebView2 can take a while; if the user switched to another tab
        // during that await, a newer ActivateAsync already won — don't resurrect this stale one.
        if (Active != tab)
        {
            if (tab.View != null) tab.View.Visibility = Visibility.Collapsed;
            return;
        }
        if (tab.View != null)
            tab.View.Visibility = Visibility.Visible; // becoming visible auto-resumes a suspended tab
        tab.State = TabState.Active;

        ActiveTabUpdated?.Invoke();
    }

    public void CloseTab(BrowserTab tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;

        PushClosed(tab);
        DestroyView(tab);
        Tabs.Remove(tab);

        if (Active == tab)
        {
            Active = null;
            if (Tabs.Count > 0)
                _ = ActivateAsync(Tabs[Math.Min(idx, Tabs.Count - 1)]);
            else
                _ = NewTabAsync(NewTabUrl, true);
        }
    }

    public void NextTab()
    {
        if (Tabs.Count < 2 || Active == null) return;
        int i = (Tabs.IndexOf(Active) + 1) % Tabs.Count;
        _ = ActivateAsync(Tabs[i]);
    }

    public void PrevTab()
    {
        if (Tabs.Count < 2 || Active == null) return;
        int i = (Tabs.IndexOf(Active) - 1 + Tabs.Count) % Tabs.Count;
        _ = ActivateAsync(Tabs[i]);
    }

    /// <summary>Activates the tab at a 0-based index (Ctrl+1..8), clamped to the last tab.</summary>
    public void ActivateIndex(int index)
    {
        if (Tabs.Count == 0) return;
        _ = ActivateAsync(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void ActivateLast()
    {
        if (Tabs.Count == 0) return;
        _ = ActivateAsync(Tabs[Tabs.Count - 1]);
    }

    // ---- navigation on the active tab ------------------------------------------------

    public async Task NavigateActiveAsync(string text)
    {
        if (Active == null)
            await NewTabAsync(NewTabUrl, true);
        await EnsureViewAsync(Active!);
        var url = AddressBar.Resolve(text, _settings, out bool domainGuess);
        Active!.SearchFallback = domainGuess ? text : null; // fall back to search if the host won't resolve
        Active!.View!.CoreWebView2.Navigate(url);
    }

    public void GoBack()
    {
        var cw = Active?.View?.CoreWebView2;
        if (cw != null && cw.CanGoBack) cw.GoBack();
    }

    public void GoForward()
    {
        var cw = Active?.View?.CoreWebView2;
        if (cw != null && cw.CanGoForward) cw.GoForward();
    }

    public void Reload() => Active?.View?.CoreWebView2?.Reload();

    // ---- tab operations --------------------------------------------------------------

    private void PushClosed(BrowserTab tab)
    {
        if (string.IsNullOrEmpty(tab.Url) ||
            tab.Url.StartsWith("https://wisp.newtab", StringComparison.OrdinalIgnoreCase)) return;
        _closed.Add(new SessionTab { Url = tab.Url, Title = tab.Title });
        if (_closed.Count > 25) _closed.RemoveAt(0);
    }

    public async Task ReopenClosedAsync()
    {
        if (_closed.Count == 0) return;
        var st = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        await NewTabAsync(st.Url, true);
    }

    public void TogglePin(BrowserTab tab)
    {
        tab.IsPinned = !tab.IsPinned;
        ReorderPinned();
    }

    /// <summary>Keeps pinned tabs grouped at the left, preserving relative order.</summary>
    private void ReorderPinned()
    {
        var ordered = Tabs.OrderBy(t => t.IsPinned ? 0 : 1).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int cur = Tabs.IndexOf(ordered[i]);
            if (cur != i) Tabs.Move(cur, i);
        }
    }

    /// <summary>Freezes the foreground tab too (used when the window is minimized) so Wisp's
    /// memory drops while you're not looking at it.</summary>
    public async Task SuspendActiveAsync()
    {
        var t = Active;
        if (t?.View?.CoreWebView2 == null) return;
        t.View.Visibility = Visibility.Collapsed; // required before suspend
        try { await t.View.CoreWebView2.TrySuspendAsync(); } catch { }
    }

    public void ResumeActive()
    {
        if (Active?.View != null) Active.View.Visibility = Visibility.Visible; // auto-resumes
    }

    public void ToggleMute(BrowserTab tab)
    {
        var cw = tab.View?.CoreWebView2;
        if (cw == null) return;
        cw.IsMuted = !cw.IsMuted;
        tab.IsMuted = cw.IsMuted;
    }

    public async Task DuplicateAsync(BrowserTab tab) => await NewTabAsync(tab.Url, true);

    /// <summary>Opens a new tab showing raw HTML (used by reader mode).</summary>
    public async Task<BrowserTab> OpenHtmlTabAsync(string html, string title)
    {
        var tab = new BrowserTab { Url = NewTabUrl, Title = title, State = TabState.Discarded };
        Tabs.Add(tab);
        await ActivateAsync(tab);
        tab.View!.CoreWebView2.NavigateToString(html);
        tab.Title = title;
        return tab;
    }

    public void CloseOthers(BrowserTab keep)
    {
        foreach (var t in Tabs.ToArray())
            if (t != keep && !t.IsPinned) CloseTab(t);
    }

    public void CloseToRight(BrowserTab from)
    {
        int idx = Tabs.IndexOf(from);
        if (idx < 0) return;
        foreach (var t in Tabs.Skip(idx + 1).ToArray())
            if (!t.IsPinned) CloseTab(t);
    }

    /// <summary>Moves a tab to a new index (used by drag-to-reorder), staying within its
    /// pinned/unpinned group.</summary>
    public void MoveTab(BrowserTab tab, int newIndex)
    {
        int cur = Tabs.IndexOf(tab);
        if (cur < 0) return;
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (cur != newIndex) Tabs.Move(cur, newIndex);
        ReorderPinned();
    }

    // ---- zoom ------------------------------------------------------------------------

    public double ActiveZoom => Active?.View?.ZoomFactor ?? 1.0;

    public void SetZoom(double factor)
    {
        var v = Active?.View;
        if (v == null) return;
        v.ZoomFactor = Math.Clamp(factor, 0.25, 5.0);
        ActiveTabUpdated?.Invoke();
    }

    public void ZoomIn() => SetZoom(RoundStep(ActiveZoom + 0.1));
    public void ZoomOut() => SetZoom(RoundStep(ActiveZoom - 0.1));
    public void ZoomReset() => SetZoom(1.0);
    private static double RoundStep(double z) => Math.Round(z * 10) / 10.0;

    // ---- memory management (driven by SleepManager) ---------------------------------

    /// <summary>Freezes a hidden background tab's renderer to reclaim memory. The tab
    /// auto-resumes when it becomes visible again.</summary>
    public async Task<bool> TrySuspendTabAsync(BrowserTab tab)
    {
        if (tab == Active || tab.View?.CoreWebView2 == null || tab.State == TabState.Suspended)
            return false;
        if (tab.View.Visibility == Visibility.Visible) return false; // never sleep an on-screen tab
        if (tab.NeverSleep) return false; // user pinned this tab awake
        if (tab.IsPlayingAudio && !tab.IsMuted) return false; // never freeze audible playback
        try
        {
            bool ok = await tab.View.CoreWebView2.TrySuspendAsync();
            if (ok) tab.State = TabState.Suspended;
            return ok;
        }
        catch { return false; } // still visible / busy — retried next tick
    }

    /// <summary>Tears down a long-idle tab's renderer entirely. Its URL is kept so the
    /// page is recreated and reloaded the next time the tab is activated.</summary>
    public void DiscardTab(BrowserTab tab)
    {
        if (tab == Active || tab.View == null) return;
        if (tab.View.Visibility == Visibility.Visible) return; // never discard an on-screen tab
        if (tab.NeverSleep) return; // user pinned this tab awake
        if (tab.IsPlayingAudio && !tab.IsMuted) return; // never tear down audible playback
        DestroyView(tab);
        tab.State = TabState.Discarded;
    }

    /// <summary>Opens a tab whose page loads immediately but stays in the background.</summary>
    public async Task<BrowserTab> OpenBackgroundLoadedTabAsync(string url)
    {
        var tab = new BrowserTab { Url = url, Title = "New Tab", State = TabState.Background };
        Tabs.Add(tab);
        await EnsureViewAsync(tab);
        tab.LastActiveUtc = DateTime.UtcNow;
        return tab;
    }

    /// <summary>Opens a link (middle-click / target=_blank) as a background tab placed right after
    /// the tab it came from, like Chrome — not at the end of the strip.</summary>
    public async Task<BrowserTab> OpenChildTabAsync(string url, BrowserTab opener, bool background = false)
    {
        var tab = new BrowserTab { Url = url, Title = "New Tab", State = TabState.Background, Opener = opener };
        InsertAdjacent(tab, opener);
        await EnsureViewAsync(tab);
        tab.LastActiveUtc = DateTime.UtcNow;
        // A normal link/_blank click should switch to the new tab (otherwise it looks like the
        // click did nothing). Ctrl+click / middle-click keep it in the background.
        if (!background) await ActivateAsync(tab);
        return tab;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>True when Ctrl or the middle mouse button is down — i.e. "open in the background".</summary>
    private static bool OpenInBackground()
        => (GetKeyState(0x11) & 0x8000) != 0   // VK_CONTROL
        || (GetKeyState(0x04) & 0x8000) != 0;  // VK_MBUTTON

    /// <summary>Inserts a tab right after its opener (and after any siblings already opened from
    /// the same tab), skipping the pinned block so a normal tab never lands among pinned ones.</summary>
    private void InsertAdjacent(BrowserTab tab, BrowserTab? opener)
    {
        int i = opener != null ? Tabs.IndexOf(opener) : -1;
        if (i < 0) { Tabs.Add(tab); return; }
        int insert = i + 1;
        while (insert < Tabs.Count && Tabs[insert].Opener == opener) insert++;
        if (!tab.IsPinned)
            while (insert < Tabs.Count && Tabs[insert].IsPinned) insert++;
        Tabs.Insert(insert, tab);
    }

    // ---- WebView2 lifecycle ----------------------------------------------------------

    private async Task EnsureViewAsync(BrowserTab tab)
    {
        if (tab.View != null) return;

        var view = new WebView2 { Visibility = Visibility.Collapsed };
        _host.Children.Add(view);
        tab.View = view;

        await view.EnsureCoreWebView2Async(_env.Core);
        var cw = view.CoreWebView2;

        cw.Settings.IsReputationCheckingRequired = false; // no SmartScreen URL calls
        cw.Settings.AreDefaultScriptDialogsEnabled = false; // we render alert/confirm/prompt ourselves
        // Let our shortcuts (Ctrl+F, Ctrl+T, …) reach the page even when web content has focus —
        // otherwise Chromium swallows them as browser accelerators and, having no find UI, Ctrl+F
        // just does nothing.
        cw.Settings.AreBrowserAcceleratorKeysEnabled = false;
        cw.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        cw.ScriptDialogOpening += (_, e) => ScriptDialogRequested?.Invoke(e);

        // Serve the local new-tab page from the bundled Resources folder.
        try
        {
            cw.SetVirtualHostNameToFolderMapping(
                NewTabHost,
                Path.Combine(AppContext.BaseDirectory, "Resources"),
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { }

        cw.ProcessFailed += (_, e) =>
        {
            // If a tab's renderer dies or hangs, reload it rather than leaving a blank page.
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                try { cw.Reload(); } catch { }
            }
        };

        cw.FaviconChanged += async (_, _) => await LoadFaviconAsync(tab, cw);

        // Web-initiated fullscreen (e.g. a YouTube video's fullscreen button).
        cw.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab == Active) FullScreenChanged?.Invoke(cw.ContainsFullScreenElement);
        };

        // Add "Search <engine> for …" to the right-click menu (WebView2's default menu has no
        // search entry because it has no configured engine). Open-link-in-new-tab already works
        // through the default menu → NewWindowRequested.
        cw.ContextMenuRequested += (_, e) =>
        {
            try
            {
                var sel = e.ContextMenuTarget.SelectionText?.Trim();
                if (string.IsNullOrEmpty(sel)) return;
                var label = sel.Length > 32 ? sel.Substring(0, 32).TrimEnd() + "…" : sel;
                var search = _env.Core.CreateContextMenuItem(
                    $"Search {_settings.SearchEngine} for “{label}”", null, CoreWebView2ContextMenuItemKind.Command);
                search.CustomItemSelected += (_, _) => _ = NewTabAsync(_settings.BuildSearchUrl(sel), true);
                e.MenuItems.Insert(0, search);
                e.MenuItems.Insert(1, _env.Core.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            }
            catch { }
        };

        // Native ad/tracker blocking — block requests to known ad domains at the network level.
        // This runs on the UI thread for every request, so keep it cheap: bail before allocating a
        // Uri when blocking is off, and reuse the page host cached on each navigation (below)
        // instead of re-parsing cw.Source every call.
        string pageHostCached = "";
        cw.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        cw.WebResourceRequested += (_, e) =>
        {
            if (!AdBlockEngine.Enabled) return;
            try
            {
                var reqHost = new Uri(e.Request.Uri).Host;
                if (AdBlockEngine.ShouldBlock(reqHost, pageHostCached))
                {
                    tab.BlockedCount++;
                    AdBlockEngine.OnBlocked("");
                    e.Response = _env.Core.CreateWebResourceResponse(null, 204, "No Content", "");
                }
            }
            catch { }
        };

        // Site permission prompts (camera/mic/location/notifications): show a Wisp-styled prompt
        // and remember the choice per origin so we don't keep re-asking.
        cw.PermissionRequested += (_, e) =>
        {
            var what = PermissionLabel(e.PermissionKind);
            if (what == null) return; // unknown kind — let WebView2 use its own prompt

            string origin; try { origin = new Uri(e.Uri).GetLeftPart(UriPartial.Authority); } catch { origin = e.Uri; }
            var key = origin + "|" + e.PermissionKind;
            if (_settings.SitePermissions.TryGetValue(key, out var remembered))
            {
                e.State = remembered ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                e.Handled = true;
                return;
            }

            var deferral = e.GetDeferral();
            _host.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    string host; try { host = new Uri(e.Uri).Host; } catch { host = origin; }
                    var owner = Window.GetWindow(_host);
                    bool allow = owner != null && PromptDialog.AllowBlock(owner, host, $"{host} wants to {what}.");
                    _settings.SitePermissions[key] = allow;
                    _settings.Save();
                    e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                }
                catch { try { e.State = CoreWebView2PermissionState.Default; } catch { } }
                finally { try { deferral.Complete(); } catch { } } // args may be dead if the tab was discarded
            });
        };

        cw.IsDocumentPlayingAudioChanged += (_, _) => tab.IsPlayingAudio = cw.IsDocumentPlayingAudio;
        cw.IsMutedChanged += (_, _) => tab.IsMuted = cw.IsMuted;
        if (tab.IsMuted) cw.IsMuted = true; // restore mute after a discard/recreate

        cw.DownloadStarting += (_, e) =>
        {
            e.Handled = true; // suppress the default download bar; we show our own panel
            DownloadStarted?.Invoke(e.DownloadOperation);
        };

        await AdBlock.EnsureRemovedAsync(cw, _settings); // uninstall the old bundled uBO Lite, once

        cw.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(cw.DocumentTitle) ? HostOf(cw.Source) : cw.DocumentTitle;
            PageVisited?.Invoke(cw.Source, cw.DocumentTitle);
            if (tab == Active) ActiveTabUpdated?.Invoke();
        };
        cw.SourceChanged += (_, _) =>
        {
            tab.BlockedCount = 0; // fresh page — reset the per-site block tally
            tab.AddressDraft = null; // navigated — the typed draft is stale
            tab.Url = cw.Source;
            pageHostCached = HostOf(cw.Source); // refresh the ad-block allowlist host for this page
            PageVisited?.Invoke(cw.Source, tab.Title);
            if (tab == Active) ActiveTabUpdated?.Invoke();
        };
        cw.HistoryChanged += (_, _) =>
        {
            if (tab == Active) ActiveTabUpdated?.Invoke();
        };
        cw.NavigationCompleted += (_, e) =>
        {
            // If a guessed domain ("vs.code") didn't resolve, search for what the user typed instead
            // of leaving them on an error page — Chrome/Edge do the same.
            if (!e.IsSuccess && tab.SearchFallback is { } q
                && e.WebErrorStatus == CoreWebView2WebErrorStatus.HostNameNotResolved)
            {
                tab.SearchFallback = null;
                try { cw.Navigate(_settings.BuildSearchUrl(q)); } catch { }
            }
            else if (e.IsSuccess) tab.SearchFallback = null;
        };
        cw.NewWindowRequested += (_, e) =>
        {
            var f = e.WindowFeatures;
            bool isPopup = f != null && (f.HasSize || f.HasPosition);
            if (isPopup)
            {
                // Real popup (e.g. Google/Claude OAuth sign-in) — needs window.opener preserved,
                // so give it an actual popup window instead of a tab.
                var deferral = e.GetDeferral();
                _ = OpenPopupWindowAsync(e, deferral);
            }
            else
            {
                e.Handled = true; // a plain new window / target=_blank / middle-click — open as a tab
                // Foreground it on a normal click; keep it in the background for Ctrl/middle-click.
                _ = OpenChildTabAsync(e.Uri, tab, OpenInBackground());
            }
        };
        cw.WebMessageReceived += (_, e) =>
        {
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch { /* non-string message */ }
            if (msg is { } m && m.StartsWith("wisp:", StringComparison.Ordinal))
                AcceleratorRequested?.Invoke(m.Substring(5));
        };

        // Shortcuts must also work while web content has focus. The WPF wrapper doesn't
        // surface AcceleratorKeyPressed, so we capture the keys in-page and post them back.
        await cw.AddScriptToExecuteOnDocumentCreatedAsync(AcceleratorScript);
        // Adds an "Add to Wisp" button on Chrome Web Store detail pages.
        await cw.AddScriptToExecuteOnDocumentCreatedAsync(StoreButtonScript);
        // YouTube ads share the video's own domain, so the network blocker can't touch them —
        // skip them in the player instead (also dodges YouTube's anti-adblock nag).
        if (AdBlockEngine.Enabled)
            await cw.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAdSkipScript);

        cw.Navigate(string.IsNullOrEmpty(tab.Url) ? NewTabUrl : tab.Url);
    }

    private void DestroyView(BrowserTab tab)
    {
        if (tab.View == null) return;
        _host.Children.Remove(tab.View);
        tab.View.Dispose();
        tab.View = null;
    }

    /// <summary>Hosts a real browser popup (OAuth sign-in windows) so window.opener works.</summary>
    private async Task OpenPopupWindowAsync(CoreWebView2NewWindowRequestedEventArgs e, CoreWebView2Deferral deferral)
    {
        try
        {
            var f = e.WindowFeatures;
            double w = (f != null && f.HasSize && f.Width > 100) ? f.Width : 480;
            double h = (f != null && f.HasSize && f.Height > 100) ? f.Height : 640;

            var wv = new WebView2();
            var win = new Window
            {
                Title = "Wisp", Width = w, Height = h, Content = wv,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = System.Windows.Media.Brushes.Black,
            };
            win.Closed += (_, _) => { try { wv.Dispose(); } catch { } }; // don't leak the popup's renderer
            win.Show(); // show first so the WebView2 has a window to initialize into
            await wv.EnsureCoreWebView2Async(_env.Core);

            var pcw = wv.CoreWebView2;
            pcw.Settings.IsReputationCheckingRequired = false;
            pcw.WindowCloseRequested += (_, _) => { try { win.Close(); } catch { } };

            e.NewWindow = pcw;
            deferral.Complete();
        }
        catch
        {
            e.Handled = true;
            deferral.Complete();
            _ = NewTabAsync(e.Uri, true);
        }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host is { Length: > 0 } h ? h : url; }
        catch { return url; }
    }

    /// <summary>Friendly verb for a permission prompt, or null for kinds we let WebView2 handle.</summary>
    private static string? PermissionLabel(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "use your microphone",
        CoreWebView2PermissionKind.Camera => "use your camera",
        CoreWebView2PermissionKind.Geolocation => "know your location",
        CoreWebView2PermissionKind.Notifications => "show notifications",
        CoreWebView2PermissionKind.ClipboardRead => "read your clipboard",
        _ => null,
    };

    private static async Task LoadFaviconAsync(BrowserTab tab, CoreWebView2 cw)
    {
        try
        {
            if (string.IsNullOrEmpty(cw.FaviconUri)) { tab.Favicon = null; return; }

            using var src = await cw.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (src == null) return;

            using var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            tab.Favicon = bmp;
        }
        catch { /* favicon is best-effort */ }
    }

    /// <summary>Injected into every page: turns browser shortcuts into host messages.</summary>
    private const string AcceleratorScript = @"
(function () {
  window.addEventListener('keydown', function (e) {
    if (!e.isTrusted) return;
    var cmd = null;
    if (e.key === 'F11') cmd = 'fullscreen';
    else if (e.key === 'F12') cmd = 'devtools';
    else if (e.ctrlKey && e.shiftKey && (e.key || '').toLowerCase() === 'i') cmd = 'devtools';
    else if (e.ctrlKey && !e.altKey && !e.metaKey) {
      var k = (e.key || '').toLowerCase();
      if (e.code === 'Tab') cmd = e.shiftKey ? 'prevtab' : 'nexttab';
      else if (/^(Digit|Numpad)[1-9]$/.test(e.code)) {
        var d = parseInt(e.code.replace(/\D/g, ''), 10);
        cmd = d === 9 ? 'tabidx:last' : 'tabidx:' + (d - 1);
      }
      else if (k === 't') cmd = 'newtab';
      else if (k === 'w') cmd = 'closetab';
      else if (k === 'l') cmd = 'focusaddress';
      else if (k === 'r') cmd = 'reload';
      else if (k === 'f') cmd = 'find';
      else if (k === 'd') cmd = 'bookmark';
      else if (k === 'h') cmd = 'history';
    } else if (e.altKey && !e.ctrlKey) {
      if (e.code === 'ArrowLeft') cmd = 'back';
      else if (e.code === 'ArrowRight') cmd = 'forward';
    }
    if (cmd) { e.preventDefault(); e.stopPropagation(); window.chrome.webview.postMessage('wisp:' + cmd); }
  }, true);
})();";

    /// <summary>Injected on Chrome Web Store pages: replaces the store's "Add to Chrome"
    /// button with our own "Add to Wisp" (falls back to a floating button if not found).</summary>
    private const string StoreButtonScript = @"
(function () {
  if (!/chromewebstore\.google\.com/.test(location.host) && !/^\/webstore\//.test(location.pathname)) return;
  var state = 'idle'; // idle | installing | done
  function extId() {
    var m = location.pathname.match(/\/detail\/(?:[^\/]+\/)?([a-p]{32})/);
    return m ? m[1] : null;
  }
  function label() {
    return state === 'installing' ? 'Installing...' : state === 'done' ? 'Added to Wisp' : '+  Add to Wisp';
  }
  function apply() {
    var bs = document.querySelectorAll('.wisp-add-btn');
    for (var i = 0; i < bs.length; i++) { bs[i].textContent = label(); bs[i].disabled = (state === 'installing'); }
  }
  function onClick(id) {
    return function (e) {
      if (e) { e.preventDefault(); e.stopPropagation(); }
      state = 'installing'; apply();
      window.chrome.webview.postMessage('wisp:install:' + id);
    };
  }
  function storeButtons() {
    var out = [], els = document.querySelectorAll('button, a[role=button]');
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      if (el.classList.contains('wisp-add-btn')) continue;
      if (/^Add to Chrome/i.test((el.textContent || '').trim()) && el.offsetParent !== null) out.push(el);
    }
    return out;
  }
  function sync() {
    if (!document.body) return;
    var id = extId();
    if (!id) {
      var o = document.querySelectorAll('.wisp-add-btn'); for (var i = 0; i < o.length; i++) o[i].remove();
      var h = document.querySelectorAll('[data-wisp-hidden]'); for (var j = 0; j < h.length; j++) { h[j].style.display = ''; h[j].removeAttribute('data-wisp-hidden'); }
      state = 'idle'; return;
    }
    var sbs = storeButtons();
    for (var k = 0; k < sbs.length; k++) {
      var sb = sbs[k];
      sb.setAttribute('data-wisp-hidden', '1');
      sb.style.display = 'none';
      var clone = sb.cloneNode(true);
      clone.classList.add('wisp-add-btn');
      clone.removeAttribute('data-wisp-hidden');
      clone.style.display = '';
      clone.onclick = onClick(id);
      sb.parentNode.insertBefore(clone, sb);
    }
    if (sbs.length > 0) { var fl = document.getElementById('wisp-float'); if (fl) fl.remove(); }
    if (!document.querySelector('.wisp-add-btn')) {
      var f = document.createElement('button');
      f.id = 'wisp-float';
      f.className = 'wisp-add-btn';
      f.style.cssText = 'position:fixed;top:14px;right:16px;z-index:2147483647;background:#4c8dff;color:#fff;border:none;border-radius:10px;padding:10px 16px;font:600 14px sans-serif;box-shadow:0 6px 18px rgba(0,0,0,.45);cursor:pointer';
      f.onclick = onClick(id);
      document.body.appendChild(f);
    }
    apply();
  }
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (ev) {
      if (typeof ev.data !== 'string' || ev.data.indexOf('wisp:installed:') !== 0) return;
      state = (ev.data.substring(15) === 'ok') ? 'done' : 'idle';
      apply();
    });
  }
  sync();
  setInterval(sync, 1000);
})();";

    /// <summary>YouTube serves ads from the video's own domain, so we can't block them at the
    /// network layer without breaking playback. Instead: hide static ad slots, click skip when
    /// offered, and seek unskippable ads to their end. Skipping in-player also avoids YouTube's
    /// anti-adblock detection (which watches for blocked ad requests).</summary>
    private const string YouTubeAdSkipScript = @"
(function () {
  if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;
  var css = ['.ytp-ad-module','.ytp-ad-overlay-container','.ytp-ad-image-overlay','.ytp-ad-overlay-slot',
    '.ytp-ad-player-overlay','.ytp-ad-player-overlay-instream-info','.ytp-suggested-action',
    'ytd-ad-slot-renderer','ytd-in-feed-ad-layout-renderer','ytd-banner-promo-renderer',
    'ytd-statement-banner-renderer','#masthead-ad','ytd-promoted-sparse-video-renderer',
    'ytd-companion-slot-renderer','ytd-promoted-video-renderer','ytd-display-ad-renderer',
    '#player-ads','.ytd-ad-slot-renderer','ytmusic-mealbar-promo-renderer',
    'ytd-popup-container:has(ytd-enforcement-message-view-model)','tp-yt-iron-overlay-backdrop'
    ].join(',') + '{display:none!important}';
  function addStyle(){ if (document.getElementById('wisp-yt')) return; var s=document.createElement('style'); s.id='wisp-yt'; s.textContent=css; (document.head||document.documentElement).appendChild(s); }
  addStyle();
  var weMuted = false;
  // YouTube's ""ad blockers violate our terms"" popup pauses the video — remove it and resume.
  function killNag(){
    var em = document.querySelector('ytd-enforcement-message-view-model, .ytd-enforcement-message-view-model');
    if (!em) return;
    var pop = em.closest('ytd-popup-container') || em.closest('tp-yt-paper-dialog') || em;
    try { pop.remove(); } catch (e) {}
    document.querySelectorAll('tp-yt-iron-overlay-backdrop').forEach(function(b){ try { b.remove(); } catch (e) {} });
    document.documentElement.style.overflow=''; if (document.body) document.body.style.overflow='';
    var v = document.querySelector('video'); if (v && v.paused) { try { v.play(); } catch (e) {} }
  }
  function tick(){
    try {
      addStyle();
      killNag();
      var player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
      var video = document.querySelector('video.html5-main-video') || document.querySelector('video');
      if (!video) return;
      document.querySelectorAll('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, .ytp-ad-skip-button-container button')
        .forEach(function(b){ try { b.click(); } catch (e) {} });
      var ad = player && (player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting'));
      if (ad) {
        // Seek to the end of the ad — works for skippable and unskippable pre/mid-rolls.
        if (isFinite(video.duration) && video.duration > 0) video.currentTime = video.duration;
        if (!video.muted) { video.muted = true; weMuted = true; }
        if (video.paused) { try { video.play(); } catch (e) {} }
      } else if (weMuted) {
        video.muted = false; weMuted = false;
      }
    } catch (e) {}
  }
  setInterval(tick, 200);
})();";
}
