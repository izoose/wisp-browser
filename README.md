<p align="center">
  <img src="wisp-logo-transparent.png" alt="Wisp" width="120">
</p>

<h1 align="center">Wisp</h1>

<p align="center">A lightweight, dark, barebones Windows browser — Chromium power without the RAM.</p>

---

Wisp is a minimal browser built as a thin **WPF (.NET 8)** shell over **WebView2** (the Edge/Chromium
runtime already on Windows 11). No Rewards, Wallet, telemetry, updaters, or extra helper processes —
just tabs, and the one feature that actually saves memory: **background tabs go to sleep**.

## Why it's lighter than Brave

Brave keeps every tab's renderer fully alive, plus a stack of background services. Wisp:

- runs one shared browser process for all tabs (shared cookies/logins),
- **suspends** background tabs after a few minutes (freezes the renderer, frees its memory),
- **discards** long-idle tabs entirely (renderer torn down; the page reloads when you click back),
- ships none of Brave's extra services, and blocks ads/trackers natively.

Measured (Release build, 7 real content tabs):

| State | RAM |
|---|---|
| all 7 tabs loaded | ~1.4 GB |
| 6 background tabs discarded | ~0.7 GB |

The same 7-tab session in Brave was using ~7 GB.

> Any Chromium browser uses similar RAM *per live tab* — the win is not keeping idle tabs live.

## Features

**Core**
- Tabbed browsing with a merged title bar (tabs live in the caption like Chrome/Brave), dark UI, sharp favicons
- **Rich omnibox** — live Google search suggestions, history/bookmark matches with favicons, inline
  autocomplete, and arrow-key navigation; hides `https://`/`www.` (Brave-style)
- Automatic tab **sleeping/discarding** (the memory lever) + lazy **session restore** (crash-safe)
- **Native ad & tracker blocking** with a toolbar **shield** — per-site toggle, global toggle, live blocked count
- Dark web content; optional **Force dark** for light-only sites

**Bring your stuff over**
- **Import from Brave / Chrome / Edge** — cookies & logins (so you stay signed in), bookmarks (with
  folders), and history. Cookies are DPAPI + AES-GCM decrypted and re-added to WebView2's own store.

**Tabs**
- Reopen closed tab (Ctrl+Shift+T), pin (favicon-only), mute noisy tabs, drag to reorder,
  middle-click to close, and a right-click menu (duplicate, pin, mute, close others / to the right)

**Chrome compatibility**
- **Extensions** — install from the **Chrome Web Store** ("Add to Wisp"), toolbar icons with clickable
  popups and side panels, and a manager (enable / disable / remove / load unpacked)
- **Native PDF viewer**, **Print** (Ctrl+P), **Downloads** panel, **Find in page**, **page zoom**,
  **fullscreen** (F11 + video fullscreen)
- **Bookmarks bar** with **folders** (dropdowns) and Brave-style **icon-only** bookmarks

**More**
- **Settings** page (search engine, sleep timings, force-dark, ad-block, import, clear data)
- **Private window** (Ctrl+Shift+N) — throwaway profile, purple-tinted
- **Reader mode** — clean dark reading view (Mozilla Readability)
- **Default browser** registration (opens links and PDFs from other apps, focuses the window)

## Keyboard shortcuts

| Key | Action | Key | Action |
|---|---|---|---|
| Ctrl+T | New tab | Ctrl+Shift+T | Reopen closed tab |
| Ctrl+W | Close tab | Ctrl+Shift+N | Private window |
| Ctrl+L | Focus address bar | Ctrl+Shift+B | Toggle bookmarks bar |
| Ctrl+R | Reload | Ctrl + / − / 0 | Zoom in / out / reset |
| Ctrl+F | Find in page | Ctrl+P | Print |
| Ctrl+D | Bookmark page | F11 | Fullscreen |
| Ctrl+Tab | Next tab | Alt+← / Alt+→ | Back / Forward |

## Getting it

**Just want to use it?** Download **`WispSetup.exe`** from the
[Releases](https://github.com/izoose/wisp-browser/releases) page and run it. No .NET, no dependencies —
the build is self-contained. It installs per-user (no admin prompt) and adds Start Menu / Desktop
shortcuts. A portable `.zip` (unzip and run `Wisp.exe`) is also attached.

> First run may show a Windows SmartScreen notice because the build isn't code-signed —
> click **More info → Run anyway**.

**Build from source** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download); WebView2 Runtime is
preinstalled on Windows 11):

```sh
git clone https://github.com/izoose/wisp-browser.git
cd wisp-browser
dotnet publish Wisp/Wisp.csproj -c Release -o dist   # framework-dependent
dist/Wisp.exe
```

To reproduce the release installer: publish self-contained, then compile `installer/wisp.iss`
with [Inno Setup](https://jrsoftware.org/isinfo.php) — see the header comment in that file.

## Where your data lives

- Settings / session / bookmarks / history: `%APPDATA%\Wisp\`
- Cookies / cache / logins / extensions: `%LOCALAPPDATA%\Wisp\WebView2\`

## Project layout

```
Wisp/
  App.xaml(.cs)              single-instance app, default-browser registration
  MainWindow.xaml(.cs)       the browser window (chrome, omnibox, menus, shield)
  Browser/                   TabManager, SleepManager, BrowserEnvironment, AddressBar
  Features/                  AdBlockEngine, ChromiumImport, Bookmarks, History,
                             ExtensionsManager, WebStore, Reader, SearchSuggest, Find
  Storage/                   AppSettings, SessionStore, AppPaths
  Ui/                        Theme.xaml (dark theme), dialogs
  Resources/                 new-tab page, settings page, ad-block list
```

## Tech & credits

- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) — the Chromium engine
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) — reading Chromium profile databases for import
- [Mozilla Readability](https://github.com/mozilla/readability) — reader mode
- Ad/tracker blocklist derived from public lists (Peter Lowe's + a curated core)

## License

[MIT](LICENSE) © 2026 izoose

> Wisp is an independent project and is not affiliated with Brave, Google, or Microsoft.
