# Wisp UX batch — design

Date: 2026-07-08

A batch of six related fixes/features for the Wisp browser (WPF + WebView2). Delivered on
one feature branch, verified in the running app, then reviewed.

## Scope

1. Ad blocker no longer breaks link clicks that route through a blocklisted redirect host (`t.co`).
2. A `New window` command (Ctrl+N) that opens a second normal window, plus the multi-window
   lifecycle work it requires.
3. Dragging a tab out of the strip tears it off into its own new window, **preserving the live page**.
4. Middle-click / Ctrl+click opens a link in a background tab (reliably), gated by a new setting
   that defaults on.
5. The existing private window is rebranded and restyled as a recognizable **Incognito** window.
6. The updater popup no longer appears on a second monitor when Wisp isn't focused.

Non-goals: restoring multiple windows after a restart (session restore stays single-window for
now); syncing tabs across windows; any change to the ad-blocklist contents beyond the redirect fix.

---

## 1. Ad blocker: don't block top-level navigations

**Problem.** `AdBlockEngine` blocks by host: `blocklist.txt` contains `t.co` (Twitter's link
redirect). The `WebResourceRequested` handler (`Browser/TabManager.cs`) returns `204 No Content`
for every request to a blocklisted host — including the *page navigation itself*. Clicking a link
on Twitter/X navigates to `https://t.co/…`, which 204s, so the click does nothing. The same breaks
other functional redirect/affiliate services on the list (`redirectingat.com`, `georiot.com`,
`t.podcast.co`, …).

**Fix.** Block these hosts only as **sub-resources**, never as the top-level document navigation.
In the `WebResourceRequested` handler, after `AdBlockEngine.ShouldBlock(...)` says block, bail out
(allow) when the request is the main-frame document:

- `e.ResourceContext == CoreWebView2WebResourceContext.Document`, **and**
- request header `Sec-Fetch-Dest == "document"` (main-frame navigation; iframes send `iframe`, so
  ad iframes are still blocked). If the header is absent, fall back to allowing `Document` context.

The header is only read for requests already flagged to block (rare), so the hot path is unchanged.

**Rationale.** This is standard blocker behavior — you don't 204 the page the user is navigating
to. It fixes the whole class, not just `t.co`, and still blocks `t.co` when used as a beacon/pixel.

**Verification.** On x.com, click a `t.co` link → it resolves to the destination. Confirm a known
tracker sub-resource is still blocked (shield count increments on a page that loads one).

---

## 2. New window (Ctrl+N) + multi-window lifecycle

**Problem.** No command opens a second *normal* window; only the private window and OAuth popups
create windows. `App` also assumes a single window in two places.

**Fix.**
- `TabManager`/`MainWindow`: a `New window` menu item (in the overflow menu next to
  "New private window") and a `Ctrl+N` shortcut → `App.Current.OpenNewWindow()`, which does
  `new MainWindow(App.Browser, App.Settings).Show()`.
- **Window registry.** `App` keeps a list of open normal (non-private) windows. Windows register
  on load and unregister on close.
- **Shutdown.** Change `ShutdownMode` from `OnMainWindowClose` to `OnLastWindowClose` so closing
  the original window doesn't quit the app while others are open.
- **Session save aggregation.** Today each window calls `SessionStore.Save(_tabs.Snapshot())` on a
  debounce and on close, so a second window clobbers the first's tabs → data loss. Change saving so
  the persisted session is the **union of all open normal windows'** snapshots. Implementation:
  `App.SaveSession()` iterates the registry, concatenates each window's `Snapshot()` tabs into one
  `SessionData`, and writes it; windows call `App.SaveSession()` instead of saving their own
  snapshot directly. Restore still loads into a single window (documented limitation).
- **Pipe routing.** `App`'s single-instance pipe currently targets `_main`. Route
  `OpenUrlExternally` / `BringToForeground` to the most-recently-activated normal window from the
  registry; drop the stale `_main` field (or keep it pointing at the current front window).

**Private windows** are excluded from the registry and from session save (unchanged behavior).

**Verification.** Ctrl+N opens a second window sharing logins. Open tabs in both, close one, quit,
relaunch → no tabs lost. Being the default browser still opens links in the running instance.

---

## 3. Tab tear-off (drag a tab into a new window), live-preserving

**Decision:** preserve the live page (reparent the actual WebView2), not reopen-by-URL. Validate
reparenting works early; fall back to reopen-by-URL only if WebView2 reparenting proves glitchy.

**Problem.** Dragging a tab only reorders it within the strip (`TabStrip_MouseMove` → `MoveTab`).
Per-tab `CoreWebView2` event handlers wired in `TabManager.EnsureViewAsync` close over the creating
`TabManager` (`this`) and its window's host panel, so a tab cannot currently be driven by a
different window.

**Fix — make tabs portable, then detach/adopt on drag-out.**

*3a. Portability refactor.* Give `BrowserTab` a reference to its current owner (`TabManager`/window),
and route the per-tab handlers through that current owner rather than the captured `this`:
- Method calls that must target the current window (`OpenChildTabAsync`, `NewTabAsync` from the
  context menu, `Window.GetWindow(host)` for permission prompts) resolve via the tab's current
  owner at event time.
- Manager-level events (`ActiveTabUpdated`, `PageVisited`, `DownloadStarted`, `FullScreenChanged`,
  `ScriptDialogRequested`, `AcceleratorRequested`, `FaviconChanged`) are raised on the tab's current
  owner so the *hosting* window's chrome updates.
This is the moderate-risk part; it is done first and verified (open link-in-tab, context-menu
search, permission prompt, downloads, favicon/title all still work) before wiring drag-out.

*3b. Detach / adopt.*
- `TabManager.DetachTab(BrowserTab tab)`: remove `tab` from `Tabs` and its `WebView2` from
  `_host.Children` **without disposing**; reassign `Active` to a neighbor; return the tab. If it was
  the window's last tab, the source window closes after the adopt completes.
- `TabManager.AdoptTab(BrowserTab tab)`: set the tab's owner to this manager, add its `WebView2` to
  this `_host`, add to `Tabs`, and activate it.

*3c. Drag-out gesture.* Extend `TabStrip_MouseMove`: once dragging, if the cursor moves outside the
tab strip bounds by a threshold (e.g. > ~40px below the strip), end the reorder, create a new normal
`MainWindow`, position it at the cursor, `DetachTab` from this window and `AdoptTab` into the new
one. Keep the current in-strip reorder behavior otherwise. (Polish — window following the cursor
until mouse-up — is optional and out of scope for v1; drop-at-cursor is enough.)

**Fallback.** If live reparenting is unstable, `DetachTab`/`AdoptTab` degrade to: new window
navigates to `tab.Url`, source tab closes. Same call sites, no gesture changes.

**Verification.** Drag a tab with a playing video / scrolled position / half-typed text out → new
window opens with that exact live state; source strip loses the tab; both windows drive their own
tabs correctly afterward (title/address update, new-tab, downloads). Dragging out the only tab
closes the source window.

---

## 4. Middle/Ctrl+click → background tab, with a setting

**Problem.** `OpenInBackground()` reads `GetKeyState(VK_MBUTTON)` when `NewWindowRequested` fires,
but the middle button is already up by then, so middle-clicked links foreground instead of
backgrounding.

**Fix.**
- New setting `AppSettings.OpenLinksInBackground` (bool, default **true**), surfaced as a menu
  toggle (and/or settings.html entry) labeled e.g. "Open middle/Ctrl-clicked links in the
  background".
- Reliable detection via an injected document script (same mechanism as the existing accelerator
  script and its `wisp:` `WebMessageReceived` channel): on `mousedown` (capture phase), if the
  target is inside an `<a href>`/link and the button is middle (`button === 1`) or it's a
  left-click with Ctrl/Meta held, post `wisp:bgnext`. `TabManager` records a timestamped hint.
- In `NewWindowRequested` (the tab branch), open in the background when
  `OpenLinksInBackground` is on **and** the hint fired within ~1s. A plain left-click on a
  `target=_blank` link (no hint) still foregrounds. The old `GetKeyState` check is removed.

**Verification.** With the setting on: middle-click and Ctrl+click keep you on the current page and
add a background tab; a plain click on a `target=_blank` link switches to the new tab. With the
setting off: middle/Ctrl+click foreground like a normal new tab.

---

## 5. Incognito look & feel

**Decision:** make the existing private window recognizably "Incognito"; engine unchanged.

**Fix.**
- Rename user-facing "private" → **Incognito**: menu item "New incognito window"; window title
  "Incognito — Wisp". `Ctrl+Shift+N` unchanged. Internal `isPrivate` naming can stay.
- Add an in-window **incognito badge**: a pill in the toolbar (incognito glyph + "Incognito")
  shown only when `_isPrivate`, on top of the existing purple window tint (`#221B33`).
- Optional (include if cheap): a one-line "You're browsing incognito — history, cookies, and site
  data are cleared when you close these windows" hint on the new-tab page for incognito windows,
  toggled via a flag/class passed to `newtab.html`.

**Verification.** Ctrl+Shift+N / menu opens a window that is visually, unmistakably incognito
(badge + purple), titled "Incognito — Wisp", still records no history/session and cleans its temp
profile on close.

---

## 6. Updater popup on the wrong monitor

**Problem.** The 6-hour auto-check timer (and the 4s-after-launch check) opens the detached WPF
`UpdatePopup` regardless of window focus. A detached Popup opened while the window is inactive is
placed on the wrong monitor.

**Fix.** Defer showing until the window is active. In `CheckForUpdatesAsync(manual: false)`, if
`!IsActive`, stash `_pendingUpdate` and set a "show when activated" flag instead of opening the
popup. Add an `Activated` handler that opens the popup if a pending update is waiting and the popup
isn't already open. Manual "Check for updates" opens immediately as today. (Considered and rejected
for v1: converting the popup to an in-window docked banner — more robust but a larger UI change.)

**Verification.** Simulate an available update (point the check at a higher version) while Wisp is
backgrounded on a multi-monitor setup → no popup appears until Wisp is focused, and then it appears
anchored under the menu button on Wisp's own monitor.

---

## Cross-cutting notes

- **Files touched (expected):** `Features/AdBlockEngine.cs` is unchanged; the navigation guard lives
  in `Browser/TabManager.cs` (`WebResourceRequested`). New window + lifecycle: `App.xaml.cs`,
  `MainWindow.xaml(.cs)`. Tear-off + portability: `Browser/TabManager.cs`, `Browser/BrowserTab.cs`,
  `MainWindow.xaml.cs`. Background tabs: `Browser/TabManager.cs`, `Storage/AppSettings.cs`,
  `MainWindow.xaml(.cs)`, and the settings/menu UI. Incognito: `MainWindow.xaml(.cs)`, `MainWindow.xaml`,
  possibly `Resources/newtab.html`. Updater popup: `MainWindow.xaml.cs`.
- **Build/verify:** build via the existing `Wisp.sln`/`dotnet build`; verify each item in the running
  app per the per-item verification notes. Do **not** force-close a running Wisp instance to swap the
  build (see project memory); build to the normal output and launch a fresh instance.
- **Risk ranking:** tear-off portability refactor (highest) > multi-window session/lifecycle >
  background-tab detection > updater defer / incognito polish / adblock guard (lowest).
