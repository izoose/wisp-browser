using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Wisp;

/// <summary>
/// The browser window: tab strip, toolbar, address bar, content host, plus the find bar and
/// overflow menu. It owns a <see cref="TabManager"/> and reflects the active tab's state back
/// into the chrome.
/// </summary>
public partial class MainWindow : Window
{
    private readonly BrowserEnvironment _env;
    private readonly AppSettings _settings;
    private readonly TabManager _tabs;
    private readonly SleepManager _sleep;
    private readonly FindController _find;
    private readonly Bookmarks _bookmarks = Bookmarks.Load();
    private readonly History _history = History.Load();
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    private readonly bool _isPrivate;
    private readonly string? _privateUdf;
    private readonly string? _startupUrl;
    private readonly DispatcherTimer _sessionTimer;
    private DispatcherTimer? _toastTimer;

    private static readonly string[] Engines = { "Google", "DuckDuckGo", "Brave Search", "Bing" };

    public MainWindow(BrowserEnvironment env, AppSettings settings, bool isPrivate = false, string? privateUdf = null, string? startupUrl = null)
    {
        InitializeComponent();
        _env = env;
        _settings = settings;
        _isPrivate = isPrivate;
        _privateUdf = privateUdf;
        _startupUrl = startupUrl;
        AdBlockEngine.Enabled = settings.AdBlockEnabled;
        AdBlockEngine.SetAllowedHosts(settings.AdBlockAllowedHosts);
        _tabs = new TabManager(env, settings, ContentHost);
        _find = new FindController(env);
        _sleep = new SleepManager(_tabs, settings);

        _tabs.ActiveTabUpdated += OnActiveTabUpdated;
        _tabs.AcceleratorRequested += OnAcceleratorRequested;
        _tabs.PageVisited += (url, title) => { if (!_isPrivate) _history.Record(url, title); };
        _tabs.DownloadStarted += OnDownloadStarted;
        _tabs.FullScreenChanged += on => SetFullscreen(on, fromWeb: true);
        _tabs.Tabs.CollectionChanged += (_, _) => { RelayoutTabs(); ScheduleSessionSave(); };
        _find.CountChanged += OnFindCountChanged;
        DataContext = _tabs;
        DownloadsList.ItemsSource = _downloads;

        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); if (!_isPrivate) SessionStore.Save(_tabs.Snapshot()); };

        RegisterShortcuts();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _isFullscreen) { e.Handled = true; SetFullscreen(false); }
        };
        StateChanged += (_, _) =>
        {
            UpdateWindowChromeState();
            // Free the foreground tab's memory while the window is minimized.
            if (WindowState == WindowState.Minimized) _ = _tabs.SuspendActiveAsync();
            else _tabs.ResumeActive();
        };
        AddressBox.TextChanged += Address_TextChanged;
        ApplyBookmarksBar();

        if (_isPrivate)
        {
            Title = "Private — Wisp";
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1B, 0x33)); // subtle purple tint
        }

        Loaded += async (_, _) =>
        {
            await RestoreOrOpenHomeAsync();
            if (!string.IsNullOrWhiteSpace(_startupUrl))
                await _tabs.NewTabAsync(_startupUrl, true); // opened from a link/file (default browser)
            _tabs.Active?.View?.Focus();
            await RefreshExtensionsAsync();

            var extra = Environment.GetEnvironmentVariable("WISP_TEST_TABS");
            if (!string.IsNullOrWhiteSpace(extra))
                foreach (var url in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    await _tabs.OpenBackgroundLoadedTabAsync(url);

            _sleep.Start();

            var testInstall = Environment.GetEnvironmentVariable("WISP_TEST_INSTALL");
            if (!string.IsNullOrWhiteSpace(testInstall))
                await InstallFromStoreAsync(testInstall);

            if (Environment.GetEnvironmentVariable("WISP_TEST_READER") == "1")
            {
                await Task.Delay(4000);
                OpenReader();
            }
            if (Environment.GetEnvironmentVariable("WISP_TEST_SIDEPANEL") == "1")
            {
                await Task.Delay(3500);
                var ent = _extEntries.FirstOrDefault(x => !string.IsNullOrEmpty(x.SidePanelUrl));
                InstallLog(ent != null ? $"sidepanel: opening {ent.Name} {ent.SidePanelUrl}" : "sidepanel: none found");
                if (ent != null) ToggleSidePanel(ent);
            }
            if (Environment.GetEnvironmentVariable("WISP_TEST_EXTPOPUP") == "1")
            {
                await Task.Delay(3000);
                var ent = _extEntries.FirstOrDefault(x => !string.IsNullOrEmpty(x.PopupUrl));
                InstallLog(ent != null ? $"extpopup: opening {ent.Name} {ent.PopupUrl}" : "extpopup: no extension with a popup");
                if (ent != null) OpenExtensionPopup(PuzzleBtn, ent);
            }

            if (Environment.GetEnvironmentVariable("WISP_TEST_CLICK") == "1")
            {
                await Task.Delay(6000); // let the store button render
                var cw = _tabs.Active?.View?.CoreWebView2;
                if (cw != null)
                    await cw.ExecuteScriptAsync("var b=document.querySelector('.wisp-add-btn'); if(b){b.click();}");
            }

            await MaybeOfferImportAsync();
        };

        Closed += (_, _) =>
        {
            _sleep.Stop();
            if (_isPrivate)
            {
                try { if (_privateUdf != null && System.IO.Directory.Exists(_privateUdf)) System.IO.Directory.Delete(_privateUdf, true); } catch { }
                return;
            }
            SessionStore.Save(_tabs.Snapshot());
            _settings.Save();
            _history.Save();
        };
    }

    private void ScheduleSessionSave()
    {
        if (_isPrivate) return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void TabScroller_SizeChanged(object sender, SizeChangedEventArgs e) => RelayoutTabs();

    /// <summary>Shrinks tabs to fit the strip so the "+" stays reachable (Chrome-style).</summary>
    private void RelayoutTabs()
    {
        double avail = TabScroller.ActualWidth - 44; // reserve room for the + button
        if (avail <= 0 || _tabs.Tabs.Count == 0) return;

        const double pinnedW = 46, minW = 52, maxW = 200;
        int pinned = _tabs.Tabs.Count(t => t.IsPinned);
        int normal = _tabs.Tabs.Count - pinned;

        double per = normal > 0 ? (avail - pinned * pinnedW) / normal : maxW;
        per = Math.Clamp(per, minW, maxW);

        foreach (var t in _tabs.Tabs)
            t.Width = t.IsPinned ? 44 : per;
    }

    private void OnActiveTabUpdated()
    {
        var t = _tabs.Active;
        if (t == null) return;

        bool isNewTab = t.Url.StartsWith("https://wisp.newtab", StringComparison.OrdinalIgnoreCase);
        if (!AddressBox.IsKeyboardFocusWithin)
            AddressBox.Text = isNewTab ? string.Empty : ForDisplay(t.Url);

        Title = string.IsNullOrWhiteSpace(t.Title) ? "Wisp" : $"{t.Title} \u2014 Wisp";

        var cw = t.View?.CoreWebView2;
        BackBtn.IsEnabled = cw?.CanGoBack ?? false;
        ForwardBtn.IsEnabled = cw?.CanGoForward ?? false;

        double z = _tabs.ActiveZoom;
        if (Math.Abs(z - 1.0) > 0.001)
        {
            ZoomPill.Content = $"{Math.Round(z * 100)}%";
            ZoomPill.Visibility = Visibility.Visible;
        }
        else ZoomPill.Visibility = Visibility.Collapsed;

        UpdateShield();
        UpdateMenuZoom();
        ScheduleSessionSave();
    }

    // ---- chrome event handlers -------------------------------------------------------

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        CloseFind();
        await _tabs.ActivateAsync((BrowserTab)((FrameworkElement)sender).DataContext);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _tabs.CloseTab((BrowserTab)((FrameworkElement)sender).DataContext);
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).DataContext is BrowserTab tab)
        {
            e.Handled = true;
            _tabs.CloseTab(tab);
        }
    }

    private void TabAudio_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (TabOf(sender) is { } t) _tabs.ToggleMute(t);
    }

    // ---- tab context menu ------------------------------------------------------------

    private static BrowserTab? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as BrowserTab;

    private void Ctx_NewTab(object sender, RoutedEventArgs e) => OpenNewTab();
    private async void Ctx_Duplicate(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } t) await _tabs.DuplicateAsync(t); }
    private void Ctx_Pin(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } t) { _tabs.TogglePin(t); RelayoutTabs(); } }
    private void Ctx_Mute(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } t) _tabs.ToggleMute(t); }
    private void Ctx_Close(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } t) _tabs.CloseTab(t); }
    private void Ctx_CloseOthers(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } t) _tabs.CloseOthers(t); }
    private void Ctx_CloseRight(object sender, RoutedEventArgs e) { if (TabOf(sender) is { } t) _tabs.CloseToRight(t); }
    private async void Ctx_Reopen(object sender, RoutedEventArgs e) => await _tabs.ReopenClosedAsync();

    // Stubs filled in by later batches (bookmarks bar, private window).
    partial void OnToggleBookmarksBar();
    partial void OnOpenPrivateWindow();
    private void ToggleBookmarksBar() => OnToggleBookmarksBar();
    private void OpenPrivateWindow() => OnOpenPrivateWindow();

    // ---- drag to reorder tabs --------------------------------------------------------

    private BrowserTab? _dragTab;
    private Point _dragStart;
    private bool _dragging;

    private void TabStrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragTab = FindTab(e.OriginalSource as DependencyObject);
        _dragStart = e.GetPosition(TabStrip);
        _dragging = false;
    }

    private void TabStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(TabStrip);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < 8) return;
            _dragging = true;
            TabStrip.CaptureMouse();
        }
        var target = FindTabAt(pos);
        if (target != null && target != _dragTab)
            _tabs.MoveTab(_dragTab, _tabs.Tabs.IndexOf(target));
    }

    private void TabStrip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) { TabStrip.ReleaseMouseCapture(); e.Handled = true; }
        _dragTab = null;
        _dragging = false;
    }

    private static BrowserTab? FindTab(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is BrowserTab t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private BrowserTab? FindTabAt(Point p)
    {
        var hit = VisualTreeHelper.HitTest(TabStrip, p);
        return hit != null ? FindTab(hit.VisualHit) : null;
    }

    // ---- downloads -------------------------------------------------------------------

    private void OnDownloadStarted(CoreWebView2DownloadOperation op)
    {
        _downloads.Insert(0, new DownloadItem(op));
        DownloadsBtn.Visibility = Visibility.Visible;
        DownloadsPopup.IsOpen = true;
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) => DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
    private void Dl_Open(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DownloadItem d) d.Open(); }
    private void Dl_Folder(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DownloadItem d) d.ShowInFolder(); }
    private void Dl_Cancel(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is DownloadItem d) d.Cancel(); }

    // ---- zoom pill -------------------------------------------------------------------

    private void ZoomPill_Click(object sender, RoutedEventArgs e) => _tabs.ZoomReset();

    // ---- bookmarks bar ---------------------------------------------------------------

    partial void OnToggleBookmarksBar()
    {
        _settings.BookmarksBarVisible = !_settings.BookmarksBarVisible;
        _settings.Save();
        ApplyBookmarksBar();
    }

    private void ApplyBookmarksBar()
    {
        BookmarksBar.Visibility = _settings.BookmarksBarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
    }

    private void RebuildBookmarksBar()
    {
        BookmarksBarItems.Items.Clear();
        var style = (Style)FindResource("MenuButtonStyle");

        foreach (var node in _bookmarks.Roots)
            BookmarksBarItems.Items.Add(node.IsFolder ? MakeFolderButton(node, style) : MakeLinkButton(node, style));

        if (_bookmarks.Roots.Count == 0)
        {
            BookmarksBarItems.Items.Add(new TextBlock
            {
                Text = "No bookmarks yet — press Ctrl+D on a page",
                Foreground = (Brush)FindResource("TextDimBrush"), Margin = new Thickness(8, 4, 0, 0), FontSize = 12,
            });
            return;
        }

        // Trailing "new folder" affordance.
        var add = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
            Style = style, Height = 26, Padding = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand,
            ToolTip = "New bookmark folder", Foreground = (Brush)FindResource("TextDimBrush"),
        };
        add.Click += (_, _) => CreateFolderPrompt(null);
        BookmarksBarItems.Items.Add(add);
    }

    /// <summary>Icon-only when the bookmark has no real title (Brave shows just the favicon then),
    /// or when the user explicitly chose icon-only.</summary>
    private static bool IsIconOnly(BookmarkNode n)
    {
        if (n.IconOnly) return true;
        var t = n.Title?.Trim() ?? "";
        return t.Length == 0 || t == n.Url
            || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private Button MakeLinkButton(BookmarkNode node, Style style)
    {
        var url = node.Url ?? "";
        bool iconOnly = IsIconOnly(node);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var img = new Image
        {
            Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform,
            Margin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 7, 0),
        };
        SetFavicon(img, url);
        sp.Children.Add(img);
        if (!iconOnly)
            sp.Children.Add(new TextBlock { Text = Truncate(node.Title, 22), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });

        var btn = new Button
        {
            Content = sp, ToolTip = string.IsNullOrWhiteSpace(node.Title) ? url : node.Title, Style = style, Height = 26,
            Padding = iconOnly ? new Thickness(6, 0, 6, 0) : new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand,
        };
        btn.Click += async (_, _) => await _tabs.NavigateActiveAsync(url);
        btn.ContextMenu = BookmarkBarItemMenu(node);
        return btn;
    }

    /// <summary>Right-click menu for a bookmark on the bar: open, rename, icon-only toggle, remove.</summary>
    private ContextMenu BookmarkBarItemMenu(BookmarkNode node)
    {
        var cm = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var miStyle = (Style)FindResource("DarkMenuItem");
        var url = node.Url ?? "";

        var open = new MenuItem { Header = "Open in new tab", Style = miStyle };
        open.Click += async (_, _) => await _tabs.NewTabAsync(url, true);
        cm.Items.Add(open);

        var rename = new MenuItem { Header = "Rename…", Style = miStyle };
        rename.Click += (_, _) =>
        {
            var name = PromptDialog.Show(this, "Rename bookmark", node.Title);
            if (name == null) return;
            node.Title = name;
            node.IconOnly = string.IsNullOrWhiteSpace(name); // a real name means show it
            _bookmarks.Save();
            if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
        };
        cm.Items.Add(rename);

        bool showingIcon = IsIconOnly(node);
        var toggle = new MenuItem { Header = showingIcon ? "Show title" : "Show icon only", Style = miStyle };
        toggle.Click += (_, _) =>
        {
            if (showingIcon)
            {
                // Give it a title to show if it doesn't have one.
                if (string.IsNullOrWhiteSpace(node.Title) || node.Title == node.Url)
                {
                    var name = PromptDialog.Show(this, "Bookmark name", HostForTitle(url));
                    node.Title = string.IsNullOrWhiteSpace(name) ? HostForTitle(url) : name!;
                }
                node.IconOnly = false;
            }
            else node.IconOnly = true;
            _bookmarks.Save();
            if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
        };
        cm.Items.Add(toggle);

        var remove = new MenuItem { Header = "Remove", Style = miStyle };
        remove.Click += (_, _) =>
        {
            _bookmarks.RemoveNode(node);
            if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
            ShowToast("Bookmark removed");
        };
        cm.Items.Add(remove);
        return cm;
    }

    private static string HostForTitle(string url)
    {
        try { return new Uri(url.Contains("://") ? url : "https://" + url).Host.Replace("www.", ""); }
        catch { return url; }
    }

    private Button MakeFolderButton(BookmarkNode folder, Style style)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
            Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextDimBrush"),
        });
        sp.Children.Add(new TextBlock { Text = Truncate(folder.Title, 22), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = " ", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Foreground = (Brush)FindResource("TextDimBrush") });

        var btn = new Button { Content = sp, ToolTip = folder.Title, Style = style, Height = 26, Padding = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand };
        btn.Click += (_, _) =>
        {
            var cm = BuildFolderMenu(folder);
            cm.PlacementTarget = btn;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        };
        btn.ContextMenu = BuildFolderMenu(folder); // right-click opens the same menu
        return btn;
    }

    private ContextMenu BuildFolderMenu(BookmarkNode folder)
    {
        var cm = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        PopulateFolderMenu(cm.Items, folder);
        return cm;
    }

    private void PopulateFolderMenu(ItemCollection items, BookmarkNode folder)
    {
        var miStyle = (Style)FindResource("DarkMenuItem");

        if (folder.Children.Count == 0)
            items.Add(new MenuItem { Header = "(empty)", IsEnabled = false, Style = miStyle });

        foreach (var child in folder.Children)
        {
            if (child.IsFolder)
            {
                var sub = new MenuItem { Header = child.Title, Style = miStyle };
                PopulateFolderMenu(sub.Items, child);
                items.Add(sub);
            }
            else
            {
                var url = child.Url ?? "";
                var mi = new MenuItem { Header = Truncate(string.IsNullOrWhiteSpace(child.Title) ? url : child.Title, 40), Style = miStyle, ToolTip = url };
                var micon = new Image { Width = 16, Height = 16 };
                SetFavicon(micon, url);
                mi.Icon = micon;
                mi.Click += async (_, _) => await _tabs.NewTabAsync(url, true);
                items.Add(mi);
            }
        }

        items.Add(new Separator());
        var addHere = new MenuItem { Header = "Add current page here", Style = miStyle };
        addHere.Click += (_, _) =>
        {
            var t = _tabs.Active;
            if (t == null || string.IsNullOrWhiteSpace(t.Url)) return;
            _bookmarks.AddLink(t.Url, t.Title, folder);
            if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
            ShowToast("Added to " + folder.Title);
        };
        items.Add(addHere);

        var newSub = new MenuItem { Header = "New folder here…", Style = miStyle };
        newSub.Click += (_, _) => CreateFolderPrompt(folder);
        items.Add(newSub);

        var rename = new MenuItem { Header = "Rename folder…", Style = miStyle };
        rename.Click += (_, _) =>
        {
            var name = PromptDialog.Show(this, "Rename folder", folder.Title);
            if (!string.IsNullOrWhiteSpace(name)) { folder.Title = name!; _bookmarks.Save(); if (_settings.BookmarksBarVisible) RebuildBookmarksBar(); }
        };
        items.Add(rename);

        var del = new MenuItem { Header = "Delete folder", Style = miStyle };
        del.Click += (_, _) =>
        {
            _bookmarks.RemoveNode(folder);
            if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
            ShowToast("Folder deleted");
        };
        items.Add(del);
    }

    private void CreateFolderPrompt(BookmarkNode? parent)
    {
        var name = PromptDialog.Show(this, "New folder", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        _bookmarks.AddFolder(name!, parent);
        if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
    }

    // ---- omnibox: address-bar suggestions --------------------------------------------

    private enum OmniKind { Search, History, Bookmark, Nav }

    private sealed class OmniItem
    {
        public OmniKind Kind;
        public string Primary = "";     // main line
        public string? Secondary;       // dim second line (url or "Google Search")
        public string Nav = "";         // passed verbatim to the address-bar resolver
        public bool UseFavicon;         // load the favicon of FaviconHost; else draw Glyph
        public string? FaviconHost;
        public string Glyph = "";       // Segoe MDL2 glyph when no favicon
        public bool Removable;          // history rows show an X to forget them
    }

    private readonly List<OmniItem> _omniLocal = new();   // nav + history/bookmarks (instant)
    private List<string> _omniSearch = new();             // google suggestions (async)
    private readonly List<OmniItem> _omniRows = new();     // the composed, displayed list
    private int _omniIndex = -1;
    private bool _omniSuppressComplete;                    // set on backspace/delete
    private bool _omniInternalEdit;                        // guards our own text edits
    private CancellationTokenSource? _omniCts;

    private void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_omniInternalEdit) return;
        if (!AddressBox.IsKeyboardFocusWithin) return;

        var raw = AddressBox.Text;
        var q = raw.Trim();
        if (q.Length == 0) { CloseOmni(); return; }

        _omniTyped = raw;      // remember what the user typed, for arrow-key cycling back to it
        _omniIndex = -1;       // typing resets the highlight
        BuildLocalSuggestions(q);
        RenderOmni();
        if (!_omniSuppressComplete) TryInlineComplete(raw, q);
        FetchRemoteSuggestions(q);
    }

    /// <summary>Builds the instant (offline) part: a default action row plus history/bookmark hits.</summary>
    private void BuildLocalSuggestions(string q)
    {
        _omniLocal.Clear();

        bool looksLikeUrl = q.Contains("://")
            || (!q.Contains(' ') && q.Contains('.'))
            || q.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);

        if (looksLikeUrl)
            _omniLocal.Add(new OmniItem
            {
                Kind = OmniKind.Nav, Primary = q, Secondary = "Open site",
                Nav = q, UseFavicon = true, FaviconHost = HostOf(q),
            });
        else
            _omniLocal.Add(new OmniItem
            {
                Kind = OmniKind.Search, Primary = q, Secondary = _settings.SearchEngine + " Search",
                Nav = q, UseFavicon = true, FaviconHost = EngineHost(),
            });

        // History + bookmark URL matches (favicons), most-recent first, deduped, skip the typed URL.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeUrl(q) };
        int added = 0;
        foreach (var h in _history.Items)
        {
            if (added >= 5) break;
            bool match = h.Url.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || (h.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!match || !seen.Add(NormalizeUrl(h.Url))) continue;
            bool marked = _bookmarks.Contains(h.Url);
            _omniLocal.Add(new OmniItem
            {
                Kind = marked ? OmniKind.Bookmark : OmniKind.History,
                Primary = string.IsNullOrWhiteSpace(h.Title) ? ForDisplay(h.Url) : h.Title,
                Secondary = ForDisplay(h.Url), Nav = h.Url,
                UseFavicon = true, FaviconHost = HostOf(h.Url),
                Removable = !marked,
            });
            added++;
        }
        // Bookmarks that weren't already in history.
        foreach (var (url, title) in _bookmarks.AllUrls())
        {
            if (added >= 5) break;
            bool match = url.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || title.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!match || !seen.Add(NormalizeUrl(url))) continue;
            _omniLocal.Add(new OmniItem
            {
                Kind = OmniKind.Bookmark, Primary = string.IsNullOrWhiteSpace(title) ? ForDisplay(url) : title,
                Secondary = ForDisplay(url), Nav = url, UseFavicon = true, FaviconHost = HostOf(url),
            });
            added++;
        }
    }

    private async void FetchRemoteSuggestions(string q)
    {
        _omniCts?.Cancel();
        var cts = _omniCts = new CancellationTokenSource();
        var list = await SearchSuggest.FetchAsync(q, cts.Token);
        if (cts.IsCancellationRequested || list.Count == 0) return;
        if (!AddressBox.IsKeyboardFocusWithin || AddressBox.Text.Trim() != q) return;
        _omniSearch = list;
        RenderOmni();
    }

    private void RenderOmni()
    {
        // Compose: default/history/bookmark rows, then google search suggestions (deduped).
        _omniRows.Clear();
        var seenText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in _omniLocal) { _omniRows.Add(it); seenText.Add(it.Primary); }

        foreach (var s in _omniSearch)
        {
            if (_omniRows.Count >= 9) break;
            if (!seenText.Add(s)) continue;
            _omniRows.Add(new OmniItem
            {
                Kind = OmniKind.Search, Primary = s, Secondary = null,
                Nav = s, Glyph = "", // magnifier
            });
        }

        SuggestItems.Children.Clear();
        if (_omniRows.Count == 0) { SuggestPopup.IsOpen = false; return; }
        if (_omniIndex >= _omniRows.Count) _omniIndex = -1;

        for (int i = 0; i < _omniRows.Count; i++)
            SuggestItems.Children.Add(BuildOmniRow(_omniRows[i], i));

        HighlightOmni();
        SuggestPopup.IsOpen = true;
    }

    private Border BuildOmniRow(OmniItem it, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Icon: favicon for sites/engine, glyph for pure search suggestions.
        FrameworkElement icon;
        if (it.UseFavicon && !string.IsNullOrEmpty(it.FaviconHost))
        {
            var img = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform };
            SetFavicon(img, it.FaviconHost!);
            icon = img;
        }
        else
        {
            icon = new TextBlock
            {
                Text = string.IsNullOrEmpty(it.Glyph) ? "" : it.Glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
                Foreground = (Brush)FindResource("TextDimBrush"),
            };
        }
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // Text: primary + inline secondary, on one line (Chrome-style "title — url").
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        text.Inlines.Add(new System.Windows.Documents.Run(it.Primary) { Foreground = (Brush)FindResource("TextBrush") });
        if (!string.IsNullOrEmpty(it.Secondary))
        {
            text.Inlines.Add(new System.Windows.Documents.Run("  —  ") { Foreground = (Brush)FindResource("TextDimBrush") });
            text.Inlines.Add(new System.Windows.Documents.Run(it.Secondary) { Foreground = (Brush)FindResource("TextDimBrush") });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (it.Removable)
        {
            var x = new Button
            {
                Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                Style = (Style)FindResource("ToolButtonStyle"), Width = 22, Height = 22,
                ToolTip = "Remove from history", Foreground = (Brush)FindResource("TextDimBrush"),
            };
            var navUrl = it.Nav;
            x.Click += (s, ev) =>
            {
                ev.Handled = true;
                _history.Items.RemoveAll(h => h.Url == navUrl);
                _history.Save();
                BuildLocalSuggestions(AddressBox.Text.Trim());
                RenderOmni();
            };
            Grid.SetColumn(x, 2);
            grid.Children.Add(x);
        }

        var row = new Border { Padding = new Thickness(6, 7, 6, 7), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Child = grid, Tag = index };
        row.MouseEnter += (_, _) => { _omniIndex = index; HighlightOmni(); };
        row.MouseLeftButtonUp += async (_, _) => await NavigateOmniAsync(it.Nav);
        return row;
    }

    private static readonly Brush OmniSelBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x50));

    private void HighlightOmni()
    {
        for (int i = 0; i < SuggestItems.Children.Count; i++)
            if (SuggestItems.Children[i] is Border b)
                b.Background = i == _omniIndex ? OmniSelBrush : Brushes.Transparent;
    }

    private string _omniTyped = "";

    /// <summary>Moves the highlight through the suggestions and cycles back to the typed text,
    /// reflecting the current selection into the address bar (Chrome-style).</summary>
    private void MoveOmni(int delta)
    {
        if (_omniRows.Count == 0) return;

        int next;
        if (_omniIndex < 0) next = delta < 0 ? _omniRows.Count - 1 : 0; // from typed text
        else
        {
            next = _omniIndex + delta;
            if (next >= _omniRows.Count) next = -1; // past bottom -> back to typed text
            else if (next < 0) next = _omniRows.Count - 1; // above top -> wrap to bottom
        }
        _omniIndex = next;
        HighlightOmni();

        _omniInternalEdit = true;
        AddressBox.Text = _omniIndex < 0 ? _omniTyped : _omniRows[_omniIndex].Nav;
        AddressBox.CaretIndex = AddressBox.Text.Length;
        _omniInternalEdit = false;
    }

    /// <summary>Fills the address box with the best matching site so the completion is selected
    /// (type "cool" → "cool" + highlighted "mathgames.com"). Chrome-style inline autocomplete.</summary>
    private void TryInlineComplete(string rawTyped, string q)
    {
        // Only complete when the caret is at the end and nothing is selected.
        if (AddressBox.SelectionLength > 0 || AddressBox.CaretIndex != rawTyped.Length) return;

        foreach (var it in _omniLocal)
        {
            if (it.Kind == OmniKind.Search) continue;
            var cand = StripScheme(it.Nav);
            if (cand.Length <= q.Length) continue;
            if (!cand.StartsWith(q, StringComparison.OrdinalIgnoreCase)) continue;

            var completed = rawTyped + cand.Substring(q.Length);
            _omniInternalEdit = true;
            AddressBox.Text = completed;
            AddressBox.Select(rawTyped.Length, completed.Length - rawTyped.Length);
            _omniInternalEdit = false;
            return;
        }
    }

    private async Task NavigateOmniAsync(string navText)
    {
        CloseOmni();
        await _tabs.NavigateActiveAsync(navText);
        _tabs.Active?.View?.Focus();
    }

    private void CloseOmni()
    {
        _omniCts?.Cancel();
        _omniSearch = new List<string>();
        _omniLocal.Clear();
        _omniRows.Clear();
        _omniIndex = -1;
        SuggestPopup.IsOpen = false;
    }

    private string EngineHost() => _settings.SearchEngine switch
    {
        "DuckDuckGo" => "duckduckgo.com",
        "Brave Search" => "search.brave.com",
        "Bing" => "bing.com",
        _ => "google.com",
    };

    private static string HostOf(string url)
    {
        try
        {
            var u = url.Contains("://") ? url : "https://" + url;
            return new Uri(u).Host;
        }
        catch { return url; }
    }

    private static string StripScheme(string url)
    {
        var s = url;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
        else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
        return s.TrimEnd('/');
    }

    private static string NormalizeUrl(string url) => StripScheme(url).ToLowerInvariant();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    /// <summary>Favicon lookup at a crisp size. Accepts a full URL or a bare host.</summary>
    private static string FaviconUrl(string urlOrHost)
    {
        var host = HostOf(urlOrHost);
        return string.IsNullOrEmpty(host) ? "" : $"https://www.google.com/s2/favicons?sz=64&domain={host}";
    }

    private static readonly System.Net.Http.HttpClient _favHttp = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, ImageSource> _favCache = new();

    /// <summary>Sets a favicon on an <see cref="Image"/>: cache hit is instant, otherwise it
    /// downloads the bytes and decodes at <paramref name="decodePx"/> (sharp when shown at ~16px).
    /// We fetch the bytes ourselves rather than binding a remote UriSource because a remote
    /// BitmapImage can't be frozen until it finishes downloading.</summary>
    private static void SetFavicon(Image img, string urlOrHost, int decodePx = 32)
    {
        var u = FaviconUrl(urlOrHost);
        if (string.IsNullOrEmpty(u)) return;
        if (_favCache.TryGetValue(u, out var cached)) { img.Source = cached; return; }
        _ = LoadFaviconAsync(img, u, decodePx);
    }

    private static async Task LoadFaviconAsync(Image img, string u, int decodePx)
    {
        try
        {
            var bytes = await _favHttp.GetByteArrayAsync(u);
            var bmp = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.DecodePixelWidth = decodePx;
                bmp.EndInit();
            }
            bmp.Freeze();
            _favCache[u] = bmp;
            img.Source = bmp;   // continuation runs on the WPF UI thread
        }
        catch { }
    }

    private ContextMenu BookmarkContextMenu(string url)
    {
        var cm = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var miStyle = (Style)FindResource("DarkMenuItem");

        var open = new MenuItem { Header = "Open in new tab", Style = miStyle };
        open.Click += async (_, _) => await _tabs.NewTabAsync(url, true);

        var remove = new MenuItem { Header = "Remove bookmark", Style = miStyle };
        remove.Click += (_, _) =>
        {
            _bookmarks.Remove(url);
            if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
            if (ListPopup.IsOpen) ShowBookmarks();
            ShowToast("Bookmark removed");
        };

        cm.Items.Add(open);
        cm.Items.Add(remove);
        return cm;
    }

    // ---- extensions toolbar ----------------------------------------------------------

    private List<ExtensionEntry> _extEntries = new();
    private WebView2? _extPopupView;

    private async Task RefreshExtensionsAsync()
    {
        var cw = _tabs.Active?.View?.CoreWebView2;
        if (cw == null) return;
        _extEntries = await ExtensionsManager.ListAsync(cw, _settings);

        ExtIcons.Items.Clear();
        foreach (var ent in _extEntries.Where(e => e.IsEnabled))
        {
            var btn = new Button
            {
                Style = (Style)FindResource("ToolButtonStyle"), ToolTip = ent.Name, Tag = ent, Width = 30, Height = 30,
            };
            if (ent.Icon != null) btn.Content = new Image { Source = ent.Icon, Width = 16, Height = 16 };
            else { btn.Content = "\U0001F9E9"; btn.FontFamily = new FontFamily("Segoe UI Emoji"); btn.FontSize = 13; }
            btn.Click += ExtIcon_Click;
            ExtIcons.Items.Add(btn);
        }
    }

    private void ExtIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExtensionEntry ent) return;
        if (!string.IsNullOrEmpty(ent.PopupUrl)) OpenExtensionPopup((FrameworkElement)sender, ent);
        else if (!string.IsNullOrEmpty(ent.SidePanelUrl)) ToggleSidePanel(ent);
        else ShowToast($"{ent.Name} has no popup — it works on pages");
    }

    private WebView2? _sidePanelView;
    private string? _sidePanelExtId;

    private async void ToggleSidePanel(ExtensionEntry ent)
    {
        if (SidePanel.Visibility == Visibility.Visible && _sidePanelExtId == ent.Id)
        {
            SidePanel.Visibility = Visibility.Collapsed;
            return;
        }

        SidePanel.Visibility = Visibility.Visible;   // show first so the WebView2 can initialize
        SidePanelTitle.Text = ent.Name;
        bool switching = _sidePanelExtId != ent.Id;
        _sidePanelExtId = ent.Id;

        if (_sidePanelView == null)
        {
            _sidePanelView = new WebView2();
            SidePanelHost.Children.Add(_sidePanelView);
            await _sidePanelView.EnsureCoreWebView2Async(_env.Core);
            _sidePanelView.CoreWebView2.Settings.IsReputationCheckingRequired = false;
            _sidePanelView.CoreWebView2.NavigationCompleted += (_, e) =>
                InstallLog($"sidepanel nav success={e.IsSuccess} status={e.WebErrorStatus}");
        }
        if (switching || string.IsNullOrEmpty(_sidePanelView.CoreWebView2.Source))
        {
            try { _sidePanelView.CoreWebView2.Navigate(ent.SidePanelUrl!); }
            catch { ShowToast("Couldn't open the side panel"); }
        }
    }

    private void SidePanelClose_Click(object sender, RoutedEventArgs e) => SidePanel.Visibility = Visibility.Collapsed;

    private async void OpenExtensionPopup(FrameworkElement anchor, ExtensionEntry ent)
    {
        // Open the flyout first so the hosted WebView2 gets a window to initialize into.
        ExtActionPopup.PlacementTarget = anchor;
        ExtActionPopup.IsOpen = true;

        if (_extPopupView == null)
        {
            _extPopupView = new WebView2();
            ExtPopupHost.Children.Add(_extPopupView);
            await _extPopupView.EnsureCoreWebView2Async(_env.Core);
            _extPopupView.CoreWebView2.NavigationCompleted += (_, e) =>
                InstallLog($"extpopup nav success={e.IsSuccess} status={e.WebErrorStatus}");
        }
        try { _extPopupView.CoreWebView2.Navigate(ent.PopupUrl!); }
        catch (Exception ex) { InstallLog("extpopup navigate threw: " + ex.Message); ShowToast("Couldn't open the extension popup"); return; }
    }

    private async void Puzzle_Click(object sender, RoutedEventArgs e)
    {
        await RefreshExtensionsAsync();
        BuildManageList();
        ExtPopup.IsOpen = !ExtPopup.IsOpen;
    }

    private void BuildManageList()
    {
        ExtManageList.Children.Clear();
        if (_extEntries.Count == 0)
        {
            ExtManageList.Children.Add(new TextBlock
            {
                Text = "No extensions installed", Foreground = (Brush)FindResource("TextDimBrush"),
                Margin = new Thickness(8, 6, 0, 6), FontSize = 12,
            });
            return;
        }
        foreach (var ent in _extEntries)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = ent.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource(ent.IsEnabled ? "TextBrush" : "TextDimBrush"), Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
            };
            Grid.SetColumn(name, 0);

            var toggle = new Button { Content = ent.IsEnabled ? "On" : "Off", Style = (Style)FindResource("MenuButtonStyle"), Height = 26, Padding = new Thickness(8, 0, 8, 0) };
            toggle.Click += async (_, _) => { try { await ent.Ext.EnableAsync(!ent.IsEnabled); } catch { } await RefreshExtensionsAsync(); BuildManageList(); };
            Grid.SetColumn(toggle, 1);

            var remove = new Button { Content = "Remove", Style = (Style)FindResource("MenuButtonStyle"), Height = 26, Padding = new Thickness(8, 0, 8, 0) };
            remove.Click += async (_, _) => { var n = ent.Name; try { await ent.Ext.RemoveAsync(); } catch { } await RefreshExtensionsAsync(); BuildManageList(); ShowToast("Removed " + n); };
            Grid.SetColumn(remove, 2);

            row.Children.Add(name);
            row.Children.Add(toggle);
            row.Children.Add(remove);
            ExtManageList.Children.Add(row);
        }
    }

    private async void ExtLoad_Click(object sender, RoutedEventArgs e)
    {
        ExtPopup.IsOpen = false;
        await LoadExtensionAsync();
        await RefreshExtensionsAsync();
    }

    // ---- reader / settings / private window ------------------------------------------

    partial void OnOpenPrivateWindow() => OpenPrivate();

    private async void OpenPrivate()
    {
        try
        {
            var (env, udf) = await BrowserEnvironment.CreatePrivateAsync(_settings);
            var w = new MainWindow(env, _settings, isPrivate: true, privateUdf: udf);
            w.Show();
        }
        catch (Exception ex) { ShowToast("Couldn't open private window: " + ex.Message); }
    }

    private async void OpenReader()
    {
        var cw = _tabs.Active?.View?.CoreWebView2;
        if (cw == null) return;
        ShowToast("Extracting article…");
        var result = await Reader.BuildAsync(cw);
        if (result is { } r) await _tabs.OpenHtmlTabAsync(r.html, "Reader — " + r.title);
        else ShowToast("This page isn't an article");
    }

    private const string SettingsUrl = "https://wisp.newtab/settings.html";

    private async void OpenSettings()
    {
        // Reuse an open settings tab if there is one; otherwise open a new one.
        var existing = _tabs.Tabs.FirstOrDefault(t => t.Url.StartsWith(SettingsUrl, StringComparison.OrdinalIgnoreCase));
        if (existing != null) await _tabs.ActivateAsync(existing);
        else await _tabs.NewTabAsync(SettingsUrl, true);
    }

    private void ApplySettings()
    {
        ApplyBookmarksBar();
        RefreshMenuLabels();
    }

    /// <summary>Opens a URL/file handed to us by Windows (default-browser / link click).</summary>
    public async void OpenUrlExternally(string url)
    {
        BringToForeground();
        await _tabs.NewTabAsync(url, true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    /// <summary>Robustly pulls the window to the foreground. Windows blocks a background process
    /// from stealing focus (you get a taskbar flash instead), so we temporarily attach our input
    /// thread to the current foreground thread — the standard way around the foreground lock —
    /// and then call SetForegroundWindow. The forwarding instance also grants us rights via
    /// AllowSetForegroundWindow (see App) as a belt-and-suspenders.</summary>
    public void BringToForeground()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();

        IntPtr hwnd;
        try { hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle(); }
        catch { Activate(); return; }

        ShowWindow(hwnd, SW_RESTORE);

        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != thisThread)
        {
            AttachThreadInput(fgThread, thisThread, true);
            try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
            finally { AttachThreadInput(fgThread, thisThread, false); }
        }
        else
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }

        Activate();
        Focus();
    }

    private void MakeDefaultBrowser()
    {
        try { DefaultBrowser.Register(); } catch { }
        DefaultBrowser.OpenDefaultAppsSettings();
        ShowToast("Now pick Wisp under 'Web browser' in Windows Settings");
    }

    // ---- ad-block shield -------------------------------------------------------------

    private string ActiveHost()
    {
        var url = _tabs.Active?.Url;
        if (string.IsNullOrEmpty(url)) return "";
        try { return new Uri(url.Contains("://") ? url : "https://" + url).Host; } catch { return ""; }
    }

    /// <summary>Refreshes the toolbar shield badge (blocked count + dim when off).</summary>
    private void UpdateShield()
    {
        var host = ActiveHost();
        bool siteOn = _settings.AdBlockEnabled && !AdBlockEngine.IsSiteAllowed(host);
        int n = _tabs.Active?.BlockedCount ?? 0;
        ShieldCount.Text = (siteOn && n > 0) ? (n > 999 ? $"{n / 1000.0:0.#}k" : n.ToString()) : "";
        ShieldGlyph.Opacity = siteOn ? 1.0 : 0.4;
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        var host = ActiveHost();
        bool globalOn = _settings.AdBlockEnabled;
        bool siteAllowed = AdBlockEngine.IsSiteAllowed(host);
        int n = _tabs.Active?.BlockedCount ?? 0;

        ShieldSite.Text = string.IsNullOrEmpty(host) ? "This page" : host;
        ShieldBlockedText.Text = !globalOn
            ? "Ad & tracker blocking is off"
            : siteAllowed ? "Blocking is off for this site"
            : n == 1 ? "1 tracker blocked on this page"
            : $"{n:N0} trackers blocked on this page";

        ShieldSiteToggle.IsEnabled = globalOn && !string.IsNullOrEmpty(host);
        ShieldSiteToggle.IsChecked = globalOn && !siteAllowed;
        ShieldSiteSub.Text = ShieldSiteToggle.IsChecked == true ? "Blocking ads & trackers here" : "Ads & trackers allowed here";
        ShieldGlobalToggle.IsChecked = globalOn;

        ShieldPopup.IsOpen = !ShieldPopup.IsOpen;
    }

    private void ShieldSiteToggle_Click(object sender, RoutedEventArgs e)
    {
        var host = ActiveHost();
        if (string.IsNullOrEmpty(host)) return;
        if (ShieldSiteToggle.IsChecked == true) AdBlockEngine.BlockSite(host); // shield ON  -> not allowlisted
        else AdBlockEngine.AllowSite(host);                                    // shield OFF -> allowlisted
        _settings.AdBlockAllowedHosts = AdBlockEngine.AllowedHosts.ToList();
        _settings.Save();
        ShieldSiteSub.Text = ShieldSiteToggle.IsChecked == true ? "Blocking ads & trackers here" : "Ads & trackers allowed here";
        try { _tabs.Active?.View?.CoreWebView2?.Reload(); } catch { }
        UpdateShield();
    }

    private void ShieldGlobalToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AdBlockEnabled = ShieldGlobalToggle.IsChecked == true;
        AdBlockEngine.Enabled = _settings.AdBlockEnabled;
        _settings.Save();
        var host = ActiveHost();
        ShieldSiteToggle.IsEnabled = _settings.AdBlockEnabled && !string.IsNullOrEmpty(host);
        ShieldSiteToggle.IsChecked = _settings.AdBlockEnabled && !AdBlockEngine.IsSiteAllowed(host);
        try { _tabs.Active?.View?.CoreWebView2?.Reload(); } catch { }
        UpdateShield();
    }

    /// <summary>Detects installed Chromium browsers and imports the user's chosen data from one.</summary>
    private async Task StartImportAsync()
    {
        if (_isPrivate) { ShowToast("Import isn't available in a private window"); return; }

        var browsers = ChromiumImport.DetectBrowsers();
        if (browsers.Count == 0) { ShowToast("No Chrome, Brave, or Edge profile found to import from"); return; }

        var choice = ImportDialog.Show(this, browsers);
        if (choice == null) return;

        var cw = _tabs.Active?.View?.CoreWebView2;
        if (cw == null) { ShowToast("Open a tab first, then try importing"); return; }

        ShowToast($"Importing from {choice.Profile.Name}…");
        var res = await ChromiumImport.ImportAsync(
            choice.Profile, cw, _bookmarks, _history, choice.Cookies, choice.Bookmarks, choice.History);

        // Surface imported bookmarks: turn the bar on if it was hidden.
        if (res.Bookmarks > 0 && !_settings.BookmarksBarVisible)
        {
            _settings.BookmarksBarVisible = true;
            _settings.Save();
        }
        ApplyBookmarksBar();

        // Reload the current page so restored cookies take effect (you appear logged in).
        if (res.Cookies > 0)
            try { _tabs.Active?.View?.CoreWebView2?.Reload(); } catch { }

        if (res.Error != null)
            ShowToast("Import finished with an issue: " + res.Error);
        else if (res.Any)
            ShowToast($"Imported {res.Cookies} logins, {res.Bookmarks} bookmarks, {res.History} history entries");
        else
            ShowToast("Nothing new to import");
    }

    /// <summary>First-run offer: if this looks like a fresh Wisp and another browser is present,
    /// invite the user to bring their data over so they don't start from scratch.</summary>
    private async Task MaybeOfferImportAsync()
    {
        if (_isPrivate || _settings.ImportOffered) return;
        // Don't pop a modal during automated test launches.
        if (Environment.GetEnvironmentVariable("WISP_NO_FIRSTRUN") == "1"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WISP_TEST_TABS"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WISP_TEST_INSTALL"))) return;

        _settings.ImportOffered = true;
        _settings.Save();

        bool fresh = _bookmarks.Roots.Count == 0 && _history.Items.Count == 0;
        if (!fresh) return;
        if (ChromiumImport.DetectBrowsers().Count == 0) return;

        await StartImportAsync();
    }

    private void HandleSettingsMessage(string rest)
    {
        var cw = _tabs.Active?.View?.CoreWebView2;
        if (rest == "get")
            cw?.PostWebMessageAsString("wisp:settings:data:" + SettingsJson());
        else if (rest.StartsWith("set:", StringComparison.Ordinal))
            ApplySettingsJson(rest.Substring(4));
        else if (rest == "clear")
            _ = ClearBrowsingDataAsync();
        else if (rest == "import")
            _ = StartImportAsync();
    }

    private string SettingsJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        searchEngine = _settings.SearchEngine,
        forceDark = _settings.ForceDark,
        adBlock = _settings.AdBlockEnabled,
        bookmarksBar = _settings.BookmarksBarVisible,
        focusAddress = _settings.FocusAddressOnNewTab,
        memorySaver = _settings.MemorySaver,
        suspendMin = _settings.SuspendAfterMinutes,
        discardMin = _settings.DiscardAfterMinutes,
    });

    private void ApplySettingsJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("searchEngine", out var se)) _settings.SearchEngine = se.GetString() ?? _settings.SearchEngine;
            if (r.TryGetProperty("forceDark", out var fd)) _settings.ForceDark = fd.GetBoolean();
            if (r.TryGetProperty("adBlock", out var ab)) { _settings.AdBlockEnabled = ab.GetBoolean(); AdBlockEngine.Enabled = _settings.AdBlockEnabled; }
            if (r.TryGetProperty("bookmarksBar", out var bb)) _settings.BookmarksBarVisible = bb.GetBoolean();
            if (r.TryGetProperty("focusAddress", out var fa)) _settings.FocusAddressOnNewTab = fa.GetBoolean();
            if (r.TryGetProperty("memorySaver", out var ms)) _settings.MemorySaver = ms.GetBoolean();
            if (r.TryGetProperty("suspendMin", out var sm) && sm.TryGetInt32(out var smv) && smv > 0) _settings.SuspendAfterMinutes = smv;
            if (r.TryGetProperty("discardMin", out var dm) && dm.TryGetInt32(out var dmv) && dmv > 0) _settings.DiscardAfterMinutes = dmv;
            _settings.Save();
            ApplySettings();
        }
        catch { }
    }

    private async Task ClearBrowsingDataAsync()
    {
        try
        {
            var profile = _tabs.Active?.View?.CoreWebView2?.Profile;
            if (profile != null) await profile.ClearBrowsingDataAsync();
        }
        catch { }
        _history.Items.Clear();
        _history.Save();
        _downloads.Clear();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenNewTab();
    private void Back_Click(object sender, RoutedEventArgs e) => _tabs.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _tabs.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _tabs.Reload();

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        RefreshMenuLabels();
        UpdateMenuZoom();
        MenuPopup.IsOpen = !MenuPopup.IsOpen;
    }

    private void UpdateMenuZoom() => MenuZoomPct.Text = $"{Math.Round(_tabs.ActiveZoom * 100)}%";

    private void MenuZoomIn_Click(object sender, RoutedEventArgs e) { _tabs.ZoomIn(); UpdateMenuZoom(); }
    private void MenuZoomOut_Click(object sender, RoutedEventArgs e) { _tabs.ZoomOut(); UpdateMenuZoom(); }
    private void MenuFullscreen_Click(object sender, RoutedEventArgs e) { MenuPopup.IsOpen = false; ToggleFullscreen(); }

    private void PrintActive()
    {
        var cw = _tabs.Active?.View?.CoreWebView2;
        if (cw == null) return;
        // Use the OS print dialog, not the browser preview. Wisp's aggressive memory flags
        // (inactive-tab memory pressure) can freeze the preview renderer, leaving its preview
        // pane spinning forever; the system dialog doesn't rely on it.
        try { cw.ShowPrintUI(CoreWebView2PrintDialogKind.System); }
        catch { try { _ = cw.ExecuteScriptAsync("window.print()"); } catch { } }
    }

    // ---- window caption controls -----------------------------------------------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowChromeState()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "\uE923" : "\uE922"; // Restore : Maximize glyph
        MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        // A WindowChrome window maximizes slightly past the screen edges; pull the content
        // back in so the tab strip and page aren't clipped. (No inset in fullscreen.)
        RootGrid.Margin = (max && !_isFullscreen) ? new Thickness(8) : new Thickness(0);
    }

    // ---- fullscreen ------------------------------------------------------------------

    private bool _isFullscreen;
    private bool _fsFromWeb;
    private WindowState _preFsState = WindowState.Normal;
    private System.Windows.Shell.WindowChrome? _savedChrome;

    /// <summary>Enters/exits borderless fullscreen: hides the tab strip, toolbar and bookmarks
    /// bar and fills the monitor. Driven by F11 or by web content (e.g. a video going fullscreen).</summary>
    private void SetFullscreen(bool on, bool fromWeb = false)
    {
        if (on == _isFullscreen) return;
        _isFullscreen = on;

        if (on)
        {
            _fsFromWeb = fromWeb;
            _preFsState = WindowState;
            _savedChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);

            TitleTabBar.Visibility = Visibility.Collapsed;
            ToolbarRow.Visibility = Visibility.Collapsed;
            BookmarksBar.Visibility = Visibility.Collapsed;
            SuggestPopup.IsOpen = false; MenuPopup.IsOpen = false; ShieldPopup.IsOpen = false;

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            RootGrid.Margin = new Thickness(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-apply so it covers the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            TitleTabBar.Visibility = Visibility.Visible;
            ToolbarRow.Visibility = Visibility.Visible;
            ApplyBookmarksBar();

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, _savedChrome);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFsState;
            UpdateWindowChromeState();

            // If web content is still in element-fullscreen, ask it to exit too.
            if (_fsFromWeb)
                try { _ = _tabs.Active?.View?.CoreWebView2?.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()"); }
                catch { }
            _fsFromWeb = false;
        }
    }

    private void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

    private async void Address_KeyDown(object sender, KeyEventArgs e)
    {
        // Don't inline-autocomplete when the user is deleting.
        _omniSuppressComplete = e.Key == Key.Back || e.Key == Key.Delete;

        if (e.Key == Key.Down && SuggestPopup.IsOpen) { e.Handled = true; MoveOmni(+1); }
        else if (e.Key == Key.Up && SuggestPopup.IsOpen) { e.Handled = true; MoveOmni(-1); }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string target = (SuggestPopup.IsOpen && _omniIndex >= 0 && _omniIndex < _omniRows.Count)
                ? _omniRows[_omniIndex].Nav
                : AddressBox.Text;
            CloseOmni();
            await _tabs.NavigateActiveAsync(target);
            _tabs.Active?.View?.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            if (SuggestPopup.IsOpen) { e.Handled = true; CloseOmni(); return; }
            AddressBox.Text = ForDisplay(_tabs.Active?.Url ?? string.Empty);
            _tabs.Active?.View?.Focus();
        }
    }

    /// <summary>On focus, reveal the full URL (with scheme) so it can be edited/copied.</summary>
    private void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var t = _tabs.Active;
        // Reveal the full URL without triggering the suggestion dropdown (only typing should).
        _omniInternalEdit = true;
        if (t != null && !t.Url.StartsWith("https://wisp.newtab", StringComparison.OrdinalIgnoreCase))
            AddressBox.Text = t.Url;
        AddressBox.SelectAll();
        _omniInternalEdit = false;
    }

    /// <summary>On blur, hide the https:// scheme again (Brave-style clean look).</summary>
    private void Address_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CloseOmni();
        var t = _tabs.Active;
        AddressBox.Text = (t == null || t.Url.StartsWith("https://wisp.newtab", StringComparison.OrdinalIgnoreCase))
            ? string.Empty : ForDisplay(t.Url);
    }

    /// <summary>Hides a leading https:// and www. for display (Brave-style). http:// stays
    /// visible so insecure sites are obvious.</summary>
    private static string ForDisplay(string url)
    {
        var u = url;
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = u.Substring(8);
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = u.Substring(4);
        return u;
    }

    // ---- session --------------------------------------------------------------------

    private async Task RestoreOrOpenHomeAsync()
    {
        if (_isPrivate) { await _tabs.NewTabAsync(TabManager.NewTabUrl, true); return; }

        var session = SessionStore.Load();
        if (session is { Tabs.Count: > 0 })
        {
            BrowserTab? toActivate = null;
            for (int i = 0; i < session.Tabs.Count; i++)
            {
                var st = session.Tabs[i];
                if (string.IsNullOrWhiteSpace(st.Url)) continue;
                var tab = _tabs.AddLazyTab(st.Url, st.Title, st.IsPinned);
                if (i == session.ActiveIndex) toActivate = tab;
            }
            if (_tabs.Tabs.Count > 0)
            {
                await _tabs.ActivateAsync(toActivate ?? _tabs.Tabs[0]);
                return;
            }
        }
        await _tabs.NewTabAsync(TabManager.NewTabUrl, true);
    }

    // ---- shared commands -------------------------------------------------------------

    private async void OpenNewTab()
    {
        var tab = await _tabs.NewTabAsync(TabManager.NewTabUrl, true);
        if (_settings.FocusAddressOnNewTab) FocusAddress();
        else tab.View?.Focus(); // let the new-tab page's search box take focus
    }

    private void CloseActive()
    {
        if (_tabs.Active != null) _tabs.CloseTab(_tabs.Active);
    }

    private void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    // ---- find in page ----------------------------------------------------------------

    private void ShowFind()
    {
        FindPopup.PlacementTarget = ContentHost;
        FindPopup.Placement = PlacementMode.Relative;
        FindPopup.HorizontalOffset = Math.Max(0, ContentHost.ActualWidth - 430);
        FindPopup.VerticalOffset = 8;
        FindPopup.IsOpen = true;
        // Focus after the popup's own window is up, or the keystrokes miss the box.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FindBox.Focus();
            Keyboard.Focus(FindBox);
            FindBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void CloseFind()
    {
        if (!FindPopup.IsOpen) return;
        _find.Stop();
        FindPopup.IsOpen = false;
        _tabs.Active?.View?.Focus();
    }

    private async void FindBox_TextChanged(object sender, TextChangedEventArgs e)
        => await _find.StartAsync(_tabs.Active?.View?.CoreWebView2, FindBox.Text);

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _find.Prev();
            else _find.Next();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFind();
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => _find.Next();
    private void FindPrev_Click(object sender, RoutedEventArgs e) => _find.Prev();
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindCountChanged(int active, int total)
    {
        FindCount.Text = total > 0 ? $"{active}/{total}" : (FindBox.Text.Length > 0 ? "0/0" : "");
        var p = Environment.GetEnvironmentVariable("WISP_LOG");
        if (p != null) { try { System.IO.File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss} [find] {active}/{total}{Environment.NewLine}"); } catch { } }
    }

    // ---- bookmarks & history ---------------------------------------------------------

    private void ToggleBookmark()
    {
        var t = _tabs.Active;
        if (t == null || string.IsNullOrWhiteSpace(t.Url)) return;
        bool nowMarked = _bookmarks.Toggle(t.Url, t.Title);
        ShowToast(nowMarked ? "Bookmarked" : "Bookmark removed");
        if (_settings.BookmarksBarVisible) RebuildBookmarksBar();
    }

    private void ShowBookmarks()
    {
        ShowList("Bookmarks", _bookmarks.AllUrls().Select(b => (b.url, Label(b.title, b.url))), BookmarkContextMenu);
    }

    private void ShowHistory()
    {
        ShowList("History", _history.Recent(200).Select(h => (h.Url, Label(h.Title, h.Url))));
    }

    private void ShowList(string title, System.Collections.Generic.IEnumerable<(string url, string label)> items,
        Func<string, ContextMenu>? menuFactory = null)
    {
        MenuPopup.IsOpen = false;
        ListTitle.Text = title;
        ListItems.Children.Clear();

        var style = (Style)FindResource("MenuButtonStyle");
        int n = 0;
        foreach (var (url, label) in items)
        {
            var btn = new Button { Content = label, Tag = url, Style = style, ToolTip = url + "  (right-click for options)" };
            btn.Click += async (_, _) =>
            {
                ListPopup.IsOpen = false;
                await _tabs.NewTabAsync(url, true);
            };
            if (menuFactory != null) btn.ContextMenu = menuFactory(url);
            ListItems.Children.Add(btn);
            n++;
        }
        if (n == 0)
            ListItems.Children.Add(new TextBlock
            {
                Text = "(empty)",
                Foreground = (Brush)FindResource("TextDimBrush"),
                Margin = new Thickness(10, 6, 0, 6),
            });

        ListPopup.IsOpen = true;
    }

    // ---- menu actions ----------------------------------------------------------------

    private async void Menu_Action(object sender, RoutedEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "newtab": MenuPopup.IsOpen = false; OpenNewTab(); break;
            case "bookmark": MenuPopup.IsOpen = false; ToggleBookmark(); break;
            case "bookmarks": ShowBookmarks(); break;
            case "history": ShowHistory(); break;
            case "find": MenuPopup.IsOpen = false; ShowFind(); break;
            case "print": MenuPopup.IsOpen = false; PrintActive(); break;
            case "reader": MenuPopup.IsOpen = false; OpenReader(); break;
            case "private": MenuPopup.IsOpen = false; OpenPrivate(); break;
            case "settings": MenuPopup.IsOpen = false; OpenSettings(); break;
            case "forcedark": ToggleForceDark(); break;
            case "search": CycleSearchEngine(); break;
            case "sleepnow": MenuPopup.IsOpen = false; await SleepNowAsync(); break;
            case "import": MenuPopup.IsOpen = false; await StartImportAsync(); break;
            case "default": MenuPopup.IsOpen = false; MakeDefaultBrowser(); break;
        }
    }

    private void RefreshMenuLabels()
    {
        bool marked = _tabs.Active != null && _bookmarks.Contains(_tabs.Active.Url);
        BookmarkThisBtn.Content = marked ? "Remove bookmark" : "Bookmark this page";
        ForceDarkBtn.Content = "Force dark: " + (_settings.ForceDark ? "On" : "Off");
        SearchEngineBtn.Content = "Search: " + _settings.SearchEngine;
    }

    private void ToggleForceDark()
    {
        _settings.ForceDark = !_settings.ForceDark;
        _settings.Save();
        RefreshMenuLabels();
        ShowToast(_settings.ForceDark
            ? "Force dark ON \u2014 restart Wisp to apply"
            : "Force dark OFF \u2014 restart Wisp to apply");
    }

    private void CycleSearchEngine()
    {
        int i = Array.IndexOf(Engines, _settings.SearchEngine);
        _settings.SearchEngine = Engines[(i + 1) % Engines.Length];
        _settings.Save();
        RefreshMenuLabels();
    }

    private async Task SleepNowAsync()
    {
        int n = 0;
        foreach (var t in _tabs.Tabs.ToArray())
            if (t != _tabs.Active && await _tabs.TrySuspendTabAsync(t)) n++;
        ShowToast(n > 0 ? $"Put {n} background tab(s) to sleep" : "No background tabs to sleep");
    }

    /// <summary>Loads an unpacked Manifest V3 extension from a folder the user picks.</summary>
    private async Task LoadExtensionAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select an unpacked extension folder (must contain manifest.json)"
        };
        if (dlg.ShowDialog() != true) return;

        var cw = _tabs.Active?.View?.CoreWebView2;
        if (cw == null) { ShowToast("Open a page first, then load the extension"); return; }

        try
        {
            var ext = await cw.Profile.AddBrowserExtensionAsync(dlg.FolderName);
            ShowToast($"Loaded extension: {ext.Name}");
        }
        catch (Exception ex)
        {
            ShowToast("Couldn't load extension: " + ex.Message);
        }
    }

    /// <summary>Installs an extension straight from a Chrome Web Store page (the "Add to Wisp" button).</summary>
    private async Task InstallFromStoreAsync(string extId)
    {
        var cw = _tabs.Active?.View?.CoreWebView2;
        ShowToast("Downloading extension…");
        try
        {
            var version = _env.Core.BrowserVersionString.Split(' ')[0];
            var folder = await WebStore.InstallAsync(extId, version);
            if (cw == null) { ShowToast("Open a tab first, then try again"); return; }
            var ext = await cw.Profile.AddBrowserExtensionAsync(folder);
            ShowToast($"Installed: {ext.Name}");
            InstallLog($"OK name='{ext.Name}' enabled={ext.IsEnabled} folder={folder}");
            TryReportToPage(cw, "ok");
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            ShowToast("Install failed: " + ex.Message);
            InstallLog("FAIL " + ex);
            TryReportToPage(cw, "fail");
        }
    }

    /// <summary>Tells the Web Store page's "Add to Wisp" button whether the install finished.</summary>
    private static void TryReportToPage(Microsoft.Web.WebView2.Core.CoreWebView2? cw, string result)
    {
        try { cw?.PostWebMessageAsString("wisp:installed:" + result); } catch { }
    }

    private static void InstallLog(string msg)
    {
        var p = Environment.GetEnvironmentVariable("WISP_LOG");
        if (p == null) return;
        try { System.IO.File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss} [install] {msg}{Environment.NewLine}"); } catch { }
    }

    // ---- toast -----------------------------------------------------------------------

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastPopup.PlacementTarget = ContentHost;
        ToastPopup.Placement = PlacementMode.Relative;
        ToastPopup.HorizontalOffset = 16;
        ToastPopup.VerticalOffset = Math.Max(0, ContentHost.ActualHeight - 64);
        ToastPopup.IsOpen = true;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) => { ToastPopup.IsOpen = false; _toastTimer!.Stop(); };
        _toastTimer.Start();
    }

    // ---- keyboard shortcuts ----------------------------------------------------------

    private void RegisterShortcuts()
    {
        void Add(Key key, ModifierKeys mod, Action action)
            => InputBindings.Add(new KeyBinding(new RelayCommand(action), key, mod));

        Add(Key.T, ModifierKeys.Control, OpenNewTab);
        Add(Key.W, ModifierKeys.Control, CloseActive);
        Add(Key.L, ModifierKeys.Control, FocusAddress);
        Add(Key.R, ModifierKeys.Control, () => _tabs.Reload());
        Add(Key.F, ModifierKeys.Control, ShowFind);
        Add(Key.D, ModifierKeys.Control, ToggleBookmark);
        Add(Key.Tab, ModifierKeys.Control, () => _tabs.NextTab());
        Add(Key.Left, ModifierKeys.Alt, () => _tabs.GoBack());
        Add(Key.Right, ModifierKeys.Alt, () => _tabs.GoForward());
        Add(Key.T, ModifierKeys.Control | ModifierKeys.Shift, async () => await _tabs.ReopenClosedAsync());
        Add(Key.OemPlus, ModifierKeys.Control, () => _tabs.ZoomIn());
        Add(Key.Add, ModifierKeys.Control, () => _tabs.ZoomIn());
        Add(Key.OemMinus, ModifierKeys.Control, () => _tabs.ZoomOut());
        Add(Key.Subtract, ModifierKeys.Control, () => _tabs.ZoomOut());
        Add(Key.D0, ModifierKeys.Control, () => _tabs.ZoomReset());
        Add(Key.NumPad0, ModifierKeys.Control, () => _tabs.ZoomReset());
        Add(Key.B, ModifierKeys.Control | ModifierKeys.Shift, ToggleBookmarksBar);
        Add(Key.N, ModifierKeys.Control | ModifierKeys.Shift, OpenPrivateWindow);
        Add(Key.F11, ModifierKeys.None, ToggleFullscreen);
        Add(Key.P, ModifierKeys.Control, PrintActive);
    }

    /// <summary>Shortcuts relayed from web content (fired on the UI thread).</summary>
    private void OnAcceleratorRequested(string cmd)
    {
        switch (cmd)
        {
            case "newtab": OpenNewTab(); break;
            case "closetab": CloseActive(); break;
            case "focusaddress": FocusAddress(); break;
            case "reload": _tabs.Reload(); break;
            case "nexttab": _tabs.NextTab(); break;
            case "back": _tabs.GoBack(); break;
            case "forward": _tabs.GoForward(); break;
            case "find": ShowFind(); break;
            case "bookmark": ToggleBookmark(); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "print": PrintActive(); break;
            default:
                if (cmd.StartsWith("navigate:", StringComparison.Ordinal))
                {
                    var query = Uri.UnescapeDataString(cmd.Substring("navigate:".Length));
                    _ = _tabs.NavigateActiveAsync(query);
                }
                else if (cmd.StartsWith("install:", StringComparison.Ordinal))
                {
                    _ = InstallFromStoreAsync(cmd.Substring("install:".Length));
                }
                else if (cmd.StartsWith("settings:", StringComparison.Ordinal))
                {
                    HandleSettingsMessage(cmd.Substring("settings:".Length));
                }
                break;
        }
    }

    // ---- helpers ---------------------------------------------------------------------

    private static string Label(string title, string url)
    {
        var s = string.IsNullOrWhiteSpace(title) ? url : title;
        return s.Length <= 58 ? s : s.Substring(0, 58) + "\u2026";
    }
}
