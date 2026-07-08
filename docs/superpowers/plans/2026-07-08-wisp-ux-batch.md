# Wisp UX Batch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the ad blocker breaking redirect links, add real multi-window support (New window + drag-out tear-off), make middle/Ctrl-click open background tabs, restyle the private window as recognizable Incognito, and stop the updater popup from appearing on the wrong monitor.

**Architecture:** WPF (.NET 8, `net8.0-windows10.0.19041.0`) + WebView2. One shared `BrowserEnvironment` (Chromium user-data folder) is used by every window and tab; each `MainWindow` owns a `TabManager` that hosts one `WebView2` per tab in a shared `Panel`. Tabs are made portable so one can be moved between windows' `TabManager`s live (reparenting the `WebView2`), which powers tear-off. Multi-window lifecycle is coordinated by a small window registry in `App`.

**Tech Stack:** C# / WPF / XAML, Microsoft.Web.WebView2, System.Text.Json settings, SQLite (unrelated to this batch).

## Global Constraints

- Target framework: `net8.0-windows10.0.19041.0`; single-instance app (mutex + named pipe in `App.xaml.cs`). New windows are created **in-process** (`new MainWindow(...)`), never as new processes.
- **No unit-test project exists.** This codebase verifies by building and running the app, using `WISP_*` environment hooks. Every task below verifies via **build + run + observe**, not xUnit. Do not add a test project.
- **Never force-close the user's running Wisp** to test a build (project memory). Verify with an **isolated** instance: set `WISP_NO_SINGLE_INSTANCE=1` and a throwaway `WISP_UDF` so it runs alongside the user's browser on its own profile.
- Build: `dotnet build Wisp/Wisp.csproj -c Debug` (run from repo root). Built exe: `Wisp/bin/Debug/net8.0-windows10.0.19041.0/Wisp.exe`.
- Private (incognito) windows are **excluded** from session save, history, and the window registry. Preserve every existing `if (_isPrivate)` guard.
- Commit after each task with a `feat:`/`fix:` message. Work on branch `wisp-ux-batch` (already created).

### Isolated-run helper (used in every verification step)

PowerShell, from repo root, after a successful build:

```powershell
$env:WISP_NO_SINGLE_INSTANCE = "1"
$env:WISP_UDF = "$env:TEMP\wisp-verify"
& "Wisp\bin\Debug\net8.0-windows10.0.19041.0\Wisp.exe"
```

Close that window when done. It never touches the user's real profile or running instance.

---

## File Structure

- `Browser/TabManager.cs` — ad-block navigation guard (T1); background-tab detection + injected script (T3); tab-portability routing via `tab.Owner` + `Raise*` methods + `OwnerWindow` (T6); `DetachTab`/`AdoptTab` (T7).
- `Browser/BrowserTab.cs` — `Owner` and `BgHintUtc` properties (T3/T6).
- `Storage/AppSettings.cs` — `OpenLinksInBackground` setting (T3).
- `App.xaml.cs` — window registry, `OpenNewWindow`/`OpenBlankWindow`, `SaveSession`, `FrontWindow`, `ShutdownMode` change, pipe routing (T5).
- `MainWindow.xaml.cs` — updater defer (T2); background-tab menu toggle (T3); incognito rename + badge wiring (T4); Ctrl+N + menu + registry hooks + session routing + `blankForAdopt` ctor (T5); tear-off drag gesture + `AdoptTabAsync` (T7).
- `MainWindow.xaml` — incognito badge element (T4); "New window" menu item + rename "New private window" → "New incognito window" (T4/T5); background-tab menu toggle button (T3).

---

## Task 1: Ad blocker — don't block top-level navigations (t.co fix)

**Files:**
- Modify: `Browser/TabManager.cs` (the `WebResourceRequested` handler, ~lines 474–488)

**Interfaces:**
- Consumes: `AdBlockEngine.Enabled`, `AdBlockEngine.ShouldBlock(reqHost, pageHost)` (existing).
- Produces: nothing new; behavior change only.

- [ ] **Step 1: Change the handler to allow main-frame navigations**

Replace the body of `cw.WebResourceRequested += (_, e) => { ... }` (currently lines ~474–488) with:

```csharp
cw.WebResourceRequested += (_, e) =>
{
    if (!AdBlockEngine.Enabled) return;
    try
    {
        var reqHost = new Uri(e.Request.Uri).Host;
        if (!AdBlockEngine.ShouldBlock(reqHost, pageHostCached)) return;

        // Never 204 the page the user is navigating to — many "trackers" (t.co,
        // redirectingat.com, affiliate/link shorteners) double as functional redirects,
        // so blocking the top-level navigation just breaks the click. We still block them
        // as sub-resources (pixels/scripts/iframes). Sec-Fetch-Dest == "document" marks the
        // main-frame navigation; iframes send "iframe", so ad iframes stay blocked.
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
        {
            var dest = e.Request.Headers.Contains("Sec-Fetch-Dest")
                ? e.Request.Headers.GetHeader("Sec-Fetch-Dest") : null;
            if (dest == null || dest == "document") return;
        }

        tab.BlockedCount++;
        AdBlockEngine.OnBlocked("");
        e.Response = _env.Core.CreateWebResourceResponse(null, 204, "No Content", "");
    }
    catch { }
};
```

- [ ] **Step 2: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Verify in an isolated instance**

Launch via the isolated-run helper. Then:
1. Go to `https://x.com` (or any tweet with a link) and click an outbound link → it must resolve to the destination site (previously blank/no-op). If you don't have an X login handy, verify the general rule instead: navigate directly to a known blocklisted redirect host that is also a shortener and confirm the page loads rather than returning "No Content".
2. Confirm sub-resource blocking still works: open a mainstream news site, click the shield — the blocked count should still increment (trackers loaded as sub-resources are still 204'd).

- [ ] **Step 4: Commit**

```bash
git add Wisp/Browser/TabManager.cs
git commit -m "fix: don't ad-block top-level navigations so t.co/redirect links work"
```

---

## Task 2: Updater popup — defer until the window is active

**Files:**
- Modify: `MainWindow.xaml.cs` (`CheckForUpdatesAsync` ~1467–1480; add an `Activated` handler; add one field)

**Interfaces:**
- Consumes: `Updater.CheckAsync()`, `_pendingUpdate`, `UpdatePopup`, `UpdateMsg` (existing).
- Produces: `_showUpdateWhenActive` field; deferred-open behavior.

- [ ] **Step 1: Add a deferral flag field**

Near the other update fields (`private Updater.UpdateInfo? _pendingUpdate;`, ~line 1463) add:

```csharp
private bool _showUpdateWhenActive;
```

- [ ] **Step 2: Defer the popup when the window isn't active**

In `CheckForUpdatesAsync(bool manual)`, replace the block that sets the popup open (currently ~1477–1479):

```csharp
_pendingUpdate = info;
UpdateMsg.Text = $"Wisp {info.Version.ToString(3)} is ready to install. You have {Updater.Current.ToString(3)}.";
UpdatePopup.IsOpen = true;
```

with:

```csharp
_pendingUpdate = info;
UpdateMsg.Text = $"Wisp {info.Version.ToString(3)} is ready to install. You have {Updater.Current.ToString(3)}.";
// A detached WPF Popup opened while the window is inactive lands on the wrong monitor.
// For an automatic check, wait until the window is focused; a manual check opens now.
if (manual || IsActive) UpdatePopup.IsOpen = true;
else _showUpdateWhenActive = true;
```

- [ ] **Step 3: Open the deferred popup on activation**

In the constructor, near the other event hookups (e.g. after the `StateChanged += ...` block, ~line 94), add:

```csharp
Activated += (_, _) =>
{
    if (_showUpdateWhenActive && _pendingUpdate != null && !UpdatePopup.IsOpen)
    {
        _showUpdateWhenActive = false;
        UpdatePopup.IsOpen = true;
    }
};
```

- [ ] **Step 4: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 5: Verify the deferral**

Because a real newer release may not exist, force one: temporarily lower the running version's comparison by editing `Updater.CheckAsync` is overkill — instead verify the **gating logic** directly:
1. Launch the isolated instance, immediately click another app so Wisp is **not** focused, and from the menu choose nothing. (Auto-check fires ~4s after launch.)
2. If GitHub has a newer release, confirm **no popup** appears on any monitor while Wisp is unfocused, then click Wisp → the "ready to install" popup appears under the menu button on Wisp's monitor.
3. If GitHub has no newer release (common), instead confirm the manual path still opens immediately: menu → "Check for updates" shows either the up-to-date toast or the popup right away. This exercises the `manual || IsActive` branch.

- [ ] **Step 6: Commit**

```bash
git add Wisp/MainWindow.xaml.cs
git commit -m "fix: defer update popup until window is active (stops wrong-monitor popup)"
```

---

## Task 3: Middle/Ctrl-click → background tab, with a setting

**Files:**
- Modify: `Storage/AppSettings.cs` (add setting)
- Modify: `Browser/BrowserTab.cs` (add `BgHintUtc`)
- Modify: `Browser/TabManager.cs` (injected script, message handling, `NewWindowRequested`, remove `GetKeyState` reliance)
- Modify: `MainWindow.xaml` (menu toggle button)
- Modify: `MainWindow.xaml.cs` (`Menu_Action` case, `RefreshMenuLabels`)

**Interfaces:**
- Consumes: existing `AddScriptToExecuteOnDocumentCreatedAsync`, `WebMessageReceived`, `OpenChildTabAsync`.
- Produces: `AppSettings.OpenLinksInBackground` (bool, default true); `BrowserTab.BgHintUtc` (DateTime); menu tag `bgtabs`.

- [ ] **Step 1: Add the setting (default on)**

In `Storage/AppSettings.cs`, after `FocusAddressOnNewTab` (~line 61) add:

```csharp
/// <summary>Middle-click / Ctrl+click on a link opens a background tab (stay on the current
/// page). A plain left-click on a target=_blank link still switches to the new tab.</summary>
public bool OpenLinksInBackground { get; set; } = true;
```

- [ ] **Step 2: Add the per-tab hint timestamp**

In `Browser/BrowserTab.cs`, with the other simple auto-properties, add:

```csharp
/// <summary>Set by the injected link-click script when the last click on a link was a
/// middle-click or Ctrl+click, so the resulting NewWindowRequested opens in the background.</summary>
public DateTime BgHintUtc { get; set; }
```

- [ ] **Step 3: Add the injected link-click detection script**

In `Browser/TabManager.cs`, next to `AcceleratorScript` (~line 691) add a new constant:

```csharp
/// <summary>Marks the next new-window request as a background open when the user middle-clicked
/// or Ctrl/Cmd-clicked a link. NewWindowRequested itself can't tell us the mouse button, and
/// GetKeyState is unreliable (the button is already released by the time it fires), so we detect
/// it in the page at mousedown and post a hint back.</summary>
private const string BgClickScript = @"
(function () {
  window.addEventListener('mousedown', function (e) {
    if (!e.isTrusted) return;
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) return;
    if (e.button === 1 || (e.button === 0 && (e.ctrlKey || e.metaKey)))
      window.chrome.webview.postMessage('wisp:bgnext');
  }, true);
})();";
```

- [ ] **Step 4: Register the script**

In `EnsureViewAsync`, after the existing `await cw.AddScriptToExecuteOnDocumentCreatedAsync(AcceleratorScript);` (~line 594) add:

```csharp
await cw.AddScriptToExecuteOnDocumentCreatedAsync(BgClickScript);
```

- [ ] **Step 5: Record the hint in WebMessageReceived**

In `EnsureViewAsync`, change the `WebMessageReceived` handler (~584–590) to intercept `bgnext` before forwarding accelerators:

```csharp
cw.WebMessageReceived += (_, e) =>
{
    string? msg = null;
    try { msg = e.TryGetWebMessageAsString(); } catch { /* non-string message */ }
    if (msg == "wisp:bgnext") { tab.BgHintUtc = DateTime.UtcNow; return; }
    if (msg is { } m && m.StartsWith("wisp:", StringComparison.Ordinal))
        AcceleratorRequested?.Invoke(m.Substring(5));
};
```

- [ ] **Step 6: Use the hint + setting in NewWindowRequested; drop GetKeyState**

In `EnsureViewAsync`, in the `else` branch of `NewWindowRequested` (~577–582), replace:

```csharp
e.Handled = true; // a plain new window / target=_blank / middle-click — open as a tab
// Foreground it on a normal click; keep it in the background for Ctrl/middle-click.
_ = OpenChildTabAsync(e.Uri, tab, OpenInBackground());
```

with:

```csharp
e.Handled = true; // a plain new window / target=_blank / middle-click — open as a tab
// Background it when the setting is on AND this was a middle/Ctrl click (hint fired within 1s);
// a plain left-click on a target=_blank link (no hint) still foregrounds.
bool bg = _settings.OpenLinksInBackground
          && (DateTime.UtcNow - tab.BgHintUtc) < TimeSpan.FromSeconds(1);
tab.BgHintUtc = DateTime.MinValue; // consume the hint
_ = OpenChildTabAsync(e.Uri, tab, bg);
```

Then delete the now-unused `OpenInBackground()` method and the `GetKeyState` P/Invoke (~lines 378–384) **only if** nothing else references them. Verify with a search first:

Run: `grep -rn "OpenInBackground\|GetKeyState" Wisp/Browser/TabManager.cs`
Expected after deletion: no matches. (If `GetKeyState` is used elsewhere, keep the DllImport and delete only `OpenInBackground`.)

- [ ] **Step 7: Add the menu toggle button (XAML)**

In `MainWindow.xaml`, in the overflow menu `StackPanel`, after the `VerticalTabsBtn` line (~525) add:

```xml
<Button x:Name="OpenBgBtn" Content="Open links in background" Tag="bgtabs" Click="Menu_Action" Style="{StaticResource MenuButtonStyle}"/>
```

- [ ] **Step 8: Wire the toggle (code-behind)**

In `MainWindow.xaml.cs`, add a case to the `Menu_Action` switch (~after the `verticaltabs` case, line 2249):

```csharp
case "bgtabs": ToggleOpenLinksInBackground(); break;
```

Add the toggle method next to `ToggleVerticalTabs` (~line 2277):

```csharp
private void ToggleOpenLinksInBackground()
{
    _settings.OpenLinksInBackground = !_settings.OpenLinksInBackground;
    _settings.Save();
    RefreshMenuLabels();
}
```

Add its label to `RefreshMenuLabels` (~line 2264, next to the VerticalTabs line):

```csharp
OpenBgBtn.Content = "Open links in background: " + (_settings.OpenLinksInBackground ? "On" : "Off");
```

- [ ] **Step 9: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 10: Verify**

Launch the isolated instance. On any page with links (e.g. a Wikipedia article):
1. **Middle-click** a link → a new tab appears but you **stay** on the current page (background). Repeat quickly on several links → several background tabs, no focus change.
2. **Ctrl+click** a link → same background behavior.
3. A page with a `target=_blank` link, **plain left-click** → switches to the new tab (foreground).
4. Menu → toggle "Open links in background: Off" → now middle-click foregrounds like a normal new tab. Toggle back On.

- [ ] **Step 11: Commit**

```bash
git add Wisp/Storage/AppSettings.cs Wisp/Browser/BrowserTab.cs Wisp/Browser/TabManager.cs Wisp/MainWindow.xaml Wisp/MainWindow.xaml.cs
git commit -m "feat: middle/Ctrl-click opens background tab, gated by OpenLinksInBackground setting"
```

---

## Task 4: Incognito look & feel

**Files:**
- Modify: `MainWindow.xaml` (rename menu item; add incognito badge element)
- Modify: `MainWindow.xaml.cs` (title text; show badge when `_isPrivate`)

**Interfaces:**
- Consumes: existing `_isPrivate`, existing purple `Background` set at ~line 101.
- Produces: `IncognitoBadge` named element.

- [ ] **Step 1: Rename the menu item label**

In `MainWindow.xaml` (~line 523) change:

```xml
<Button Content="New private window" Tag="private" Click="Menu_Action" Style="{StaticResource MenuButtonStyle}"/>
```

to:

```xml
<Button Content="New incognito window" Tag="private" Click="Menu_Action" Style="{StaticResource MenuButtonStyle}"/>
```

(Keep `Tag="private"` — only the user-facing label changes.)

- [ ] **Step 2: Add the incognito badge to the toolbar**

In `MainWindow.xaml`, inside the left toolbar `StackPanel` (`Grid.Column="0" Orientation="Horizontal"`, ~line 247), add as the first child (before `BackBtn`):

```xml
<Border x:Name="IncognitoBadge" Visibility="Collapsed" VerticalAlignment="Center"
        Background="#3A2B57" CornerRadius="10" Padding="8,2" Margin="4,0,6,0">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#x1F576;" FontFamily="Segoe UI Emoji" FontSize="12" VerticalAlignment="Center"/>
        <TextBlock Text="Incognito" FontSize="11.5" Margin="5,0,0,0" VerticalAlignment="Center"
                   Foreground="{StaticResource TextBrush}"/>
    </StackPanel>
</Border>
```

(`&#x1F576;` is the 🕶 sunglasses glyph — the closest built-in Segoe UI Emoji stand-in for the incognito/"spy" look.)

- [ ] **Step 3: Show the badge + update the title for incognito windows**

In `MainWindow.xaml.cs`, in the `if (_isPrivate)` block (~lines 98–102), change:

```csharp
if (_isPrivate)
{
    Title = "Private — Wisp";
    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1B, 0x33)); // subtle purple tint
}
```

to:

```csharp
if (_isPrivate)
{
    Title = "Incognito — Wisp";
    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1B, 0x33)); // subtle purple tint
    IncognitoBadge.Visibility = Visibility.Visible;
}
```

- [ ] **Step 4: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 5: Verify**

Launch the isolated instance. Menu → "New incognito window" (or Ctrl+Shift+N):
1. A new window opens titled "Incognito — Wisp" with the purple tint **and** an "🕶 Incognito" pill at the top-left of the toolbar.
2. The normal window shows **no** badge.
3. Browse a site in the incognito window, close it → confirm it still records no history (open History in the normal window; the incognito visit is absent).

- [ ] **Step 6: Commit**

```bash
git add Wisp/MainWindow.xaml Wisp/MainWindow.xaml.cs
git commit -m "feat: restyle private window as recognizable Incognito (badge + title + label)"
```

---

## Task 5: New window (Ctrl+N) + multi-window lifecycle

**Files:**
- Modify: `App.xaml.cs` (registry, `OpenNewWindow`, `OpenBlankWindow`, `SaveSession`, `FrontWindow`, `HasOpenWindows`, `ShutdownMode`, pipe routing)
- Modify: `MainWindow.xaml.cs` (register/unregister, route session save through `App`, `Activated` → `FrontWindow`, `blankForAdopt` ctor flag, `OpenNewWindow` handler + Ctrl+N + menu case, `SnapshotSession`)
- Modify: `MainWindow.xaml` (add "New window" menu item)

**Interfaces:**
- Consumes: `MainWindow(BrowserEnvironment, AppSettings, bool isPrivate, string? privateUdf, string? startupUrl)`; `_tabs.Snapshot()`; `SessionStore.Save(SessionData)`.
- Produces:
  - `App.OpenNewWindow()` → `MainWindow` (normal, auto home/restore).
  - `App.OpenBlankWindow()` → `MainWindow` (normal, **no** initial tab; for adopt).
  - `App.SaveSession()`; `App.FrontWindow` (MainWindow?); `App.HasOpenWindows` (bool); `App.RegisterWindow(MainWindow)`, `App.UnregisterWindow(MainWindow)`.
  - `MainWindow` ctor gains `bool blankForAdopt = false`; `internal SessionData SnapshotSession()`.

- [ ] **Step 1: Add the window registry + helpers to App**

In `App.xaml.cs`, add fields and methods (place near `_main`, ~line 26):

```csharp
private readonly List<MainWindow> _windows = new();      // open NORMAL windows (not incognito)
public MainWindow? FrontWindow { get; private set; }      // most-recently-activated normal window
public bool HasOpenWindows => _windows.Count > 0;

public void RegisterWindow(MainWindow w)
{
    if (!_windows.Contains(w)) _windows.Add(w);
    FrontWindow = w;
}

public void UnregisterWindow(MainWindow w)
{
    _windows.Remove(w);
    if (FrontWindow == w) FrontWindow = _windows.Count > 0 ? _windows[^1] : null;
}

public void SetFront(MainWindow w) => FrontWindow = w;

public MainWindow OpenNewWindow()
{
    var w = new MainWindow(Browser, Settings);
    w.Show();
    return w;
}

public MainWindow OpenBlankWindow()
{
    var w = new MainWindow(Browser, Settings, blankForAdopt: true);
    w.Show();
    return w;
}

/// <summary>Persists the union of all open normal windows' tabs, so a second window never
/// clobbers the first's session. Restore still loads into a single window.</summary>
public void SaveSession()
{
    if (_windows.Count == 0) return;
    var combined = new SessionData { ActiveIndex = 0 };
    foreach (var win in _windows)
        combined.Tabs.AddRange(win.SnapshotSession().Tabs);
    SessionStore.Save(combined);
}
```

Add `using System.Collections.Generic;` if not already present (it is used elsewhere; confirm the file compiles).

- [ ] **Step 2: Switch shutdown mode to last-window-close**

In `App.OnStartup`, change (~line 64):

```csharp
ShutdownMode = ShutdownMode.OnMainWindowClose;
```

to:

```csharp
ShutdownMode = ShutdownMode.OnLastWindowClose;
```

- [ ] **Step 3: Route the single-instance pipe to the front window**

In `StartPipeServer`, change the dispatch (~163–166):

```csharp
if (!string.IsNullOrWhiteSpace(url)) _main?.OpenUrlExternally(url);
else _main?.BringToForeground();
```

to:

```csharp
var target = FrontWindow ?? _main;
if (!string.IsNullOrWhiteSpace(url)) target?.OpenUrlExternally(url);
else target?.BringToForeground();
```

- [ ] **Step 4: Add the `blankForAdopt` ctor flag + `SnapshotSession`**

In `MainWindow.xaml.cs`, change the constructor signature (~line 44):

```csharp
public MainWindow(BrowserEnvironment env, AppSettings settings, bool isPrivate = false, string? privateUdf = null, string? startupUrl = null)
```

to add the flag:

```csharp
public MainWindow(BrowserEnvironment env, AppSettings settings, bool isPrivate = false, string? privateUdf = null, string? startupUrl = null, bool blankForAdopt = false)
```

Add a field near the other readonly fields (~line 36):

```csharp
private readonly bool _blankForAdopt;
```

Assign it in the constructor body next to `_isPrivate = isPrivate;` (~line 49):

```csharp
_blankForAdopt = blankForAdopt;
```

Add the snapshot accessor (place near the `Closed` handler region, anywhere in the class):

```csharp
internal SessionData SnapshotSession() => _tabs.Snapshot();
```

- [ ] **Step 5: Skip home/restore for blank adopt windows; register on load**

In the `Loaded += async (_, _) => { ... }` handler (~line 104), wrap the restore/startup so a blank adopt window opens empty, and register the window. Change the start of the handler:

```csharp
Loaded += async (_, _) =>
{
    await RestoreOrOpenHomeAsync();
    if (!string.IsNullOrWhiteSpace(_startupUrl))
        await _tabs.NewTabAsync(_startupUrl, true); // opened from a link/file (default browser)
    _tabs.Active?.View?.Focus();
```

to:

```csharp
Loaded += async (_, _) =>
{
    if (!_isPrivate) App.Current.RegisterWindow(this);
    if (!_blankForAdopt)
    {
        await RestoreOrOpenHomeAsync();
        if (!string.IsNullOrWhiteSpace(_startupUrl))
            await _tabs.NewTabAsync(_startupUrl, true); // opened from a link/file (default browser)
    }
    _tabs.Active?.View?.Focus();
```

(Leave the rest of the handler — extension refresh, `WISP_TEST_*` hooks, update check — unchanged. Note: the update-check block is already guarded by `if (!_isPrivate ...)`; it will now also run for secondary normal windows, which is fine because the popup only opens when that window is active.)

- [ ] **Step 6: Track the front window on activation; unregister + route session save on close**

Extend the `Activated` handler added in Task 2 (or add one) so a normal window becomes the front window when focused. Change:

```csharp
Activated += (_, _) =>
{
    if (_showUpdateWhenActive && _pendingUpdate != null && !UpdatePopup.IsOpen)
    {
        _showUpdateWhenActive = false;
        UpdatePopup.IsOpen = true;
    }
};
```

to:

```csharp
Activated += (_, _) =>
{
    if (!_isPrivate) App.Current.SetFront(this);
    if (_showUpdateWhenActive && _pendingUpdate != null && !UpdatePopup.IsOpen)
    {
        _showUpdateWhenActive = false;
        UpdatePopup.IsOpen = true;
    }
};
```

In the `Closed += (_, _) => { ... }` handler (~170–181), replace the non-private branch so session save aggregates remaining windows (and the last window still saves its own tabs):

```csharp
Closed += (_, _) =>
{
    _sleep.Stop();
    if (_isPrivate)
    {
        try { if (_privateUdf != null && System.IO.Directory.Exists(_privateUdf)) System.IO.Directory.Delete(_privateUdf, true); } catch { }
        return;
    }
    App.Current.UnregisterWindow(this);
    if (App.Current.HasOpenWindows) App.Current.SaveSession(); // union of remaining windows
    else SessionStore.Save(_tabs.Snapshot());                  // last window: preserve its tabs
    _settings.Save();
    _history.Save();
};
```

- [ ] **Step 7: Route the debounce session timer through App**

In the constructor, change the session timer tick (~line 71):

```csharp
_sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); if (!_isPrivate) SessionStore.Save(_tabs.Snapshot()); };
```

to:

```csharp
_sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); if (!_isPrivate) App.Current.SaveSession(); };
```

- [ ] **Step 8: Add `OpenNewWindow` handler + Ctrl+N + menu case**

In `MainWindow.xaml.cs`, add a handler method (near `OpenPrivate`, ~line 1283):

```csharp
private void OpenNewWindow() => App.Current.OpenNewWindow();
```

Add the shortcut in `RegisterShortcuts` (next to the private one, ~line 2424):

```csharp
Add(Key.N, ModifierKeys.Control, OpenNewWindow);
```

Add the `Menu_Action` case (next to `private`, ~line 2246):

```csharp
case "newwindow": MenuPopup.IsOpen = false; OpenNewWindow(); break;
```

- [ ] **Step 9: Add the "New window" menu item (XAML)**

In `MainWindow.xaml`, immediately **before** the "New incognito window" button (~line 523) add:

```xml
<Button Content="New window" Tag="newwindow" Click="Menu_Action" Style="{StaticResource MenuButtonStyle}"/>
```

- [ ] **Step 10: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 11: Verify multi-window lifecycle**

Launch the isolated instance:
1. **Ctrl+N** → a second normal window opens. Log into a site in window A, open the same site in window B → you're already logged in (shared profile).
2. Open distinct tabs in A and B. Close **A** (not the last) → B stays open, app keeps running.
3. Close **B** (last window) → app exits.
4. Relaunch the isolated instance → the tabs from the last-open state are restored (into one window; no tab loss). Open A and B again, add tabs to both, then close both without quitting between — relaunch shows the union, nothing lost.
5. Default-browser routing still works: with the instance running, from another app open a link that Wisp handles → it opens in the front Wisp window (test only if Wisp is registered as default; otherwise skip).

- [ ] **Step 12: Commit**

```bash
git add Wisp/App.xaml.cs Wisp/MainWindow.xaml.cs Wisp/MainWindow.xaml
git commit -m "feat: add New window (Ctrl+N) + multi-window lifecycle (registry, aggregate session, last-window shutdown)"
```

---

## Task 6: Tab portability — route per-tab handlers through the tab's current owner

This is the prerequisite refactor for tear-off. **No user-visible behavior changes** — the goal is that a tab still behaves identically while now being drivable by whichever `TabManager` currently owns it. Verify nothing regresses.

**Files:**
- Modify: `Browser/BrowserTab.cs` (add `Owner`)
- Modify: `Browser/TabManager.cs` (add `OwnerWindow` + `Raise*` methods; set `Owner`; reroute handler bodies from `this`/`_host` to `tab.Owner`)

**Interfaces:**
- Produces on `TabManager`:
  - `public Window? OwnerWindow => Window.GetWindow(_host);`
  - `public void RaiseActiveTabUpdated() => ActiveTabUpdated?.Invoke();`
  - `public void RaisePageVisited(string url, string title) => PageVisited?.Invoke(url, title);`
  - `public void RaiseDownloadStarted(CoreWebView2DownloadOperation op) => DownloadStarted?.Invoke(op);`
  - `public void RaiseFullScreenChanged(bool on) => FullScreenChanged?.Invoke(on);`
  - `public void RaiseAccelerator(string cmd) => AcceleratorRequested?.Invoke(cmd);`
- Produces on `BrowserTab`: `public TabManager? Owner { get; set; }`

- [ ] **Step 1: Add `Owner` to BrowserTab**

In `Browser/BrowserTab.cs`, with the other auto-properties, add:

```csharp
/// <summary>The TabManager/window currently hosting this tab. Set when the tab's view is created
/// and updated when the tab is adopted by another window. Per-tab WebView2 handlers route through
/// this so a moved tab drives its new window, not the one that created it.</summary>
public TabManager? Owner { get; set; }
```

Add `using System;` etc. as needed (`DateTime` from Task 3 already required it).

- [ ] **Step 2: Add `OwnerWindow` + `Raise*` methods to TabManager**

In `Browser/TabManager.cs`, add near the top of the class (after the events, ~line 55):

```csharp
/// <summary>The window currently hosting this manager's tabs (resolved from the host panel).</summary>
public Window? OwnerWindow => Window.GetWindow(_host);

// Event re-raisers so a tab's handlers can fire events on its CURRENT owner after being moved.
public void RaiseActiveTabUpdated() => ActiveTabUpdated?.Invoke();
public void RaisePageVisited(string url, string title) => PageVisited?.Invoke(url, title);
public void RaiseDownloadStarted(CoreWebView2DownloadOperation op) => DownloadStarted?.Invoke(op);
public void RaiseFullScreenChanged(bool on) => FullScreenChanged?.Invoke(on);
public void RaiseAccelerator(string cmd) => AcceleratorRequested?.Invoke(cmd);
```

- [ ] **Step 3: Set `Owner` when the view is created and when tabs are created**

At the very top of `EnsureViewAsync` (before/after `if (tab.View != null) return;`, ~line 403) add:

```csharp
tab.Owner = this;
```

Also set it where tabs are first created so it's never null before a view exists — in `NewTabAsync` (after `var tab = new BrowserTab {...};`, ~line 66) and in `AddLazyTab` (~line 76) and `OpenChildTabAsync` (~line 368), add `tab.Owner = this;` right after construction. (Harmless if reset again in `EnsureViewAsync`.)

- [ ] **Step 4: Reroute the fullscreen handler**

Change (~444–447):

```csharp
cw.ContainsFullScreenElementChanged += (_, _) =>
{
    if (tab == Active) FullScreenChanged?.Invoke(cw.ContainsFullScreenElement);
};
```

to:

```csharp
cw.ContainsFullScreenElementChanged += (_, _) =>
{
    if (tab == tab.Owner?.Active) tab.Owner?.RaiseFullScreenChanged(cw.ContainsFullScreenElement);
};
```

- [ ] **Step 5: Reroute the context-menu "Search for" action**

Change (~461):

```csharp
search.CustomItemSelected += (_, _) => _ = NewTabAsync(_settings.BuildSearchUrl(sel), true);
```

to:

```csharp
search.CustomItemSelected += (_, _) => _ = (tab.Owner ?? this).NewTabAsync(_settings.BuildSearchUrl(sel), true);
```

- [ ] **Step 6: Reroute the permission prompt owner window**

In the `PermissionRequested` handler, change (~512):

```csharp
var owner = Window.GetWindow(_host);
```

to:

```csharp
var owner = tab.Owner?.OwnerWindow ?? Window.GetWindow(_host);
```

(`_host.Dispatcher` on the line above is fine — all windows share the one UI-thread dispatcher.)

- [ ] **Step 7: Reroute downloads**

Change (~530):

```csharp
DownloadStarted?.Invoke(e.DownloadOperation);
```

to:

```csharp
(tab.Owner ?? this).RaiseDownloadStarted(e.DownloadOperation);
```

- [ ] **Step 8: Reroute title/source/history events**

Change the three navigation handlers (~535–553) so each `PageVisited?.Invoke(...)` becomes `tab.Owner?.RaisePageVisited(...)` and each `if (tab == Active) ActiveTabUpdated?.Invoke();` becomes `if (tab == tab.Owner?.Active) tab.Owner?.RaiseActiveTabUpdated();`:

```csharp
cw.DocumentTitleChanged += (_, _) =>
{
    tab.Title = string.IsNullOrWhiteSpace(cw.DocumentTitle) ? HostOf(cw.Source) : cw.DocumentTitle;
    tab.Owner?.RaisePageVisited(cw.Source, cw.DocumentTitle);
    if (tab == tab.Owner?.Active) tab.Owner?.RaiseActiveTabUpdated();
};
cw.SourceChanged += (_, _) =>
{
    tab.BlockedCount = 0;
    tab.AddressDraft = null;
    tab.Url = cw.Source;
    pageHostCached = HostOf(cw.Source);
    tab.Owner?.RaisePageVisited(cw.Source, tab.Title);
    if (tab == tab.Owner?.Active) tab.Owner?.RaiseActiveTabUpdated();
};
cw.HistoryChanged += (_, _) =>
{
    if (tab == tab.Owner?.Active) tab.Owner?.RaiseActiveTabUpdated();
};
```

- [ ] **Step 9: Reroute NewWindowRequested to the owner**

In the `NewWindowRequested` handler, change the tab-open branch (from Task 3) to use the owner, and the popup fallback path. The else branch becomes:

```csharp
e.Handled = true;
bool bg = _settings.OpenLinksInBackground
          && (DateTime.UtcNow - tab.BgHintUtc) < TimeSpan.FromSeconds(1);
tab.BgHintUtc = DateTime.MinValue;
_ = (tab.Owner ?? this).OpenChildTabAsync(e.Uri, tab, bg);
```

(`OpenPopupWindowAsync` uses only the shared `_env`, so it does not need rerouting.)

- [ ] **Step 10: Reroute the accelerator message**

In `WebMessageReceived` (from Task 3), change the forward line:

```csharp
if (msg is { } m && m.StartsWith("wisp:", StringComparison.Ordinal))
    AcceleratorRequested?.Invoke(m.Substring(5));
```

to:

```csharp
if (msg is { } m && m.StartsWith("wisp:", StringComparison.Ordinal))
    (tab.Owner ?? this).RaiseAccelerator(m.Substring(5));
```

- [ ] **Step 11: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 12: Verify no regression (single window)**

Launch the isolated instance and confirm everything still works exactly as before (this refactor should be invisible):
1. Navigate; the address bar and tab title update (SourceChanged/DocumentTitleChanged → ActiveTabUpdated).
2. Back/forward buttons enable/disable correctly (HistoryChanged).
3. Right-click selected text → "Search … for …" opens a new tab in the same window.
4. Trigger a permission prompt (a site asking for location/notifications) → the Wisp prompt appears over the correct window.
5. Start a download → the downloads panel shows it.
6. A YouTube video's fullscreen button works (fullscreen enter/exit).
7. Middle-click still backgrounds (Task 3 behavior intact).

- [ ] **Step 13: Commit**

```bash
git add Wisp/Browser/BrowserTab.cs Wisp/Browser/TabManager.cs
git commit -m "refactor: route per-tab WebView2 handlers through tab.Owner so tabs are portable"
```

---

## Task 7: Tab tear-off — drag a tab out into a new window (live)

**Files:**
- Modify: `Browser/TabManager.cs` (`DetachTab`, `AdoptTab`)
- Modify: `MainWindow.xaml.cs` (`AdoptTabAsync`; tear-off detection in `TabStrip_MouseMove`; `GetCursorPos` P/Invoke)

**Interfaces:**
- Consumes: `App.OpenBlankWindow()` (Task 5), `tab.Owner` + `ActivateAsync` (Task 6), existing `_dragTab`/`_dragging`/`TabStrip`.
- Produces:
  - `TabManager.DetachTab(BrowserTab tab)` → `BrowserTab` (removes from this manager without disposing the view).
  - `TabManager.AdoptTab(BrowserTab tab)` → `Task` (adds to this manager + activates).
  - `MainWindow.AdoptTabAsync(BrowserTab tab)` → `Task`.

- [ ] **Step 1: Add DetachTab / AdoptTab to TabManager**

In `Browser/TabManager.cs`, add (near `OpenChildTabAsync`, after ~line 376):

```csharp
/// <summary>Removes a tab from this window WITHOUT disposing its WebView2, so it can be adopted
/// by another window with its live page intact. Activates a neighbor if the active tab left.</summary>
public BrowserTab DetachTab(BrowserTab tab)
{
    int idx = Tabs.IndexOf(tab);
    BrowserTab? neighbor = null;
    if (idx >= 0)
    {
        if (idx + 1 < Tabs.Count) neighbor = Tabs[idx + 1];
        else if (idx - 1 >= 0) neighbor = Tabs[idx - 1];
    }

    if (tab.View != null) _host.Children.Remove(tab.View); // detach the control; do NOT dispose
    Tabs.Remove(tab);

    if (Active == tab)
    {
        Active = null;
        if (neighbor != null) _ = ActivateAsync(neighbor);
    }
    return tab;
}

/// <summary>Takes ownership of a detached tab: hosts its existing WebView2 in this window and
/// activates it. The page keeps its live state (scroll/media/forms).</summary>
public async Task AdoptTab(BrowserTab tab)
{
    tab.Owner = this;
    if (tab.View != null)
    {
        tab.View.Visibility = Visibility.Collapsed;
        if (!_host.Children.Contains(tab.View)) _host.Children.Add(tab.View);
    }
    Tabs.Add(tab);
    await ActivateAsync(tab);
}
```

- [ ] **Step 2: Add AdoptTabAsync + GetCursorPos to MainWindow**

In `MainWindow.xaml.cs`, add the P/Invoke near the other DllImports (e.g. by the foreground-window imports, ~line 1351 region):

```csharp
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
private struct POINT { public int X; public int Y; }

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern bool GetCursorPos(out POINT p);
```

Add the adopt helper (near `OpenNewWindow`, ~line 1283):

```csharp
internal Task AdoptTabAsync(BrowserTab tab) => _tabs.AdoptTab(tab);
```

- [ ] **Step 3: Detect tear-off in the drag handler**

In `MainWindow.xaml.cs`, replace `TabStrip_MouseMove` (~397–410) with a version that tears the tab off when dragged clear of the strip:

```csharp
private async void TabStrip_MouseMove(object sender, MouseEventArgs e)
{
    if (_dragTab == null || e.LeftButton != MouseButtonState.Pressed) return;
    var pos = e.GetPosition(TabStrip);
    if (!_dragging)
    {
        if (Math.Abs(pos.X - _dragStart.X) < 8) return;
        _dragging = true;
        TabStrip.CaptureMouse();
    }

    // Dragged well below the strip → tear the tab off into its own new window (like Chrome/Edge).
    if (pos.Y > TabStrip.ActualHeight + 40)
    {
        var torn = _dragTab;
        _dragTab = null;
        _dragging = false;
        TabStrip.ReleaseMouseCapture();
        await TearOffAsync(torn);
        return;
    }

    var target = FindTabAt(pos);
    if (target != null && target != _dragTab)
        _tabs.MoveTab(_dragTab, _tabs.Tabs.IndexOf(target));
}

/// <summary>Moves a tab (live) into a new window positioned at the cursor. Closes this window
/// if the tab was its last one.</summary>
private async Task TearOffAsync(BrowserTab tab)
{
    // Don't tear off the only tab of the only window — nothing would be left.
    if (_tabs.Tabs.Count <= 1 && !App.Current.HasOpenWindows) { return; }

    var w = App.Current.OpenBlankWindow();

    // Position the new window at the cursor (convert physical pixels → DIPs for Left/Top).
    try
    {
        GetCursorPos(out var pt);
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            var dip = src.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y));
            w.Left = dip.X - 80;
            w.Top = dip.Y - 12;
        }
    }
    catch { }

    var detached = _tabs.DetachTab(tab);
    await w.AdoptTabAsync(detached);

    if (_tabs.Tabs.Count == 0) Close();
}
```

- [ ] **Step 4: Build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 5: Verify live tear-off (the critical validation)**

Launch the isolated instance:
1. Open 2–3 tabs. In one, start a YouTube video **playing**, or scroll a long article partway, or type text into a form field (don't submit).
2. Drag that tab down out of the strip (past ~40px below it) and release → a **new window** appears at the cursor hosting that tab, and the page keeps its live state: the video is still playing at the same spot / the scroll position is preserved / the typed text is still there.
3. The source window loses the tab and its remaining tabs still work (title/address update, new tabs open in the right window).
4. Drag the **only** tab out → the source window closes and the tab lives on in the new window.
5. In the torn-off window, navigate, open a link in a new tab, start a download → all route to that window (confirms Task 6 owner-routing after a real move).

**If live reparenting misbehaves** (blank page, crashed renderer, or the WebView2 doesn't re-render in the new window): fall back to reopen-by-URL. Replace the two calls in `TearOffAsync`:

```csharp
var detached = _tabs.DetachTab(tab);
await w.AdoptTabAsync(detached);
```

with:

```csharp
await w.OpenAdoptedUrlAsync(tab.Url);
_tabs.CloseTab(tab);
```

and add to `MainWindow`:

```csharp
internal async Task OpenAdoptedUrlAsync(string url) => await _tabs.NewTabAsync(url, true);
```

Then re-run this step; the page reloads fresh in the new window (state not preserved, but robust). Note in the commit message which path shipped.

- [ ] **Step 6: Commit**

```bash
git add Wisp/Browser/TabManager.cs Wisp/MainWindow.xaml.cs
git commit -m "feat: drag a tab out of the strip to tear it off into a new window (live reparent)"
```

---

## Final verification (whole batch)

- [ ] **Step 1: Full build**

Run: `dotnet build Wisp/Wisp.csproj -c Debug`
Expected: `Build succeeded.`, 0 errors.

- [ ] **Step 2: End-to-end smoke in one isolated session**

Launch the isolated instance and walk all six:
1. t.co / redirect link resolves (T1).
2. Menu → Check for updates opens immediately; auto-check while unfocused does not pop on another monitor (T2).
3. Middle/Ctrl-click backgrounds; setting toggles it (T3).
4. Incognito window looks incognito (badge + title) and records no history (T4).
5. Ctrl+N opens a second window; closing non-last keeps the app alive; session restores without loss (T5).
6. Dragging a tab out tears it off into a new window with live state (T6/T7).

- [ ] **Step 3: Invoke the `verify` skill** to drive the affected flows in the real app if desired, then hand back for review.

---

## Self-review notes (author)

- **Spec coverage:** §1→T1, §2→T5, §3→T6+T7, §4→T3, §5→T4, §6→T2. All six covered.
- **Optional items deliberately deferred:** the "You're incognito" new-tab hint (§5 optional) is **not** in this plan to keep `newtab.html` untouched; add later if wanted. Cursor-follow window drag during tear-off (§3 note) is out of scope; drop-at-cursor is implemented.
- **Type consistency:** `DetachTab`/`AdoptTab`/`AdoptTabAsync`, `OpenNewWindow`/`OpenBlankWindow`/`SaveSession`/`FrontWindow`/`HasOpenWindows`/`RegisterWindow`/`UnregisterWindow`/`SetFront`, `SnapshotSession`, `Raise*`, `OwnerWindow`, `Owner`, `BgHintUtc`, `OpenLinksInBackground` are used identically wherever referenced.
- **Known simplification:** session restore reopens into a single window (documented in spec §2); the union-save prevents tab loss.
