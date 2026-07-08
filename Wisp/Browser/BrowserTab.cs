using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace Wisp;

public enum TabState
{
    /// <summary>Foreground tab.</summary>
    Active,
    /// <summary>Live but hidden; renderer still resident.</summary>
    Background,
    /// <summary>Renderer suspended (frozen) to reclaim memory; wakes on activation.</summary>
    Suspended,
    /// <summary>No WebView2 at all; recreated + reloaded when activated.</summary>
    Discarded,
}

/// <summary>
/// One browser tab. Holds the live <see cref="WebView2"/> when it has one (null while
/// discarded), plus the metadata we need to show it in the tab strip and to recreate it.
/// </summary>
public class BrowserTab : INotifyPropertyChanged
{
    private string _title = "New Tab";
    private bool _isActive;
    private bool _isPinned;
    private bool _isPlayingAudio;
    private bool _isMuted;
    private double _width = 190;
    private ImageSource? _favicon;

    public string Url { get; set; } = "https://www.google.com";

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnChanged(); } }
    }

    /// <summary>Drives the selected-tab highlight in the tab strip.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; OnChanged(); } }
    }

    /// <summary>Site favicon shown in the tab strip (null until loaded).</summary>
    public ImageSource? Favicon
    {
        get => _favicon;
        set { _favicon = value; OnChanged(); }
    }

    /// <summary>Pinned tabs shrink to a favicon and stay at the left of the strip.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; OnChanged(); OnChanged(nameof(ShowClose)); OnChanged(nameof(ShowTitle)); } }
    }

    /// <summary>Live width — tabs shrink to fit the strip (set by the window's relayout).</summary>
    public double Width
    {
        get => _width;
        set { if (Math.Abs(_width - value) > 0.5) { _width = value; OnChanged(); } }
    }

    public bool IsPlayingAudio
    {
        get => _isPlayingAudio;
        set { if (_isPlayingAudio != value) { _isPlayingAudio = value; OnChanged(); OnChanged(nameof(ShowAudio)); } }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { if (_isMuted != value) { _isMuted = value; OnChanged(); OnChanged(nameof(ShowAudio)); } }
    }

    /// <summary>Show the speaker glyph when audio is playing or the tab is muted.</summary>
    public bool ShowAudio => _isPlayingAudio || _isMuted;
    public bool ShowClose => !_isPinned;
    public bool ShowTitle => !_isPinned;

    public TabState State { get; set; } = TabState.Discarded;
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When true, this tab is exempt from sleep/suspend/discard (user chose "Keep awake").
    /// Persisted with the session.</summary>
    private bool _neverSleep;
    public bool NeverSleep
    {
        get => _neverSleep;
        set { if (_neverSleep != value) { _neverSleep = value; OnChanged(); } }
    }

    /// <summary>Tab-group color as a hex string (null = ungrouped). Tabs sharing a color read as a
    /// group; shown as a colored strip on the tab. Persisted with the session.</summary>
    private string? _groupColor;
    public string? GroupColor
    {
        get => _groupColor;
        set { if (_groupColor != value) { _groupColor = value; OnChanged(); OnChanged(nameof(GroupBrush)); OnChanged(nameof(HasGroup)); } }
    }
    public bool HasGroup => _groupColor != null;
    public Brush? GroupBrush
    {
        get { try { return _groupColor is { } c ? (Brush)new BrushConverter().ConvertFromString(c)! : null; } catch { return null; } }
    }

    /// <summary>Ad/tracker requests blocked on the current page (reset when navigating).</summary>
    public int BlockedCount { get; set; }

    /// <summary>The tab this one was opened from (a link/middle-click), so it can be placed
    /// right next to its parent instead of at the end of the strip. Runtime-only.</summary>
    public BrowserTab? Opener { get; set; }

    /// <summary>Unsubmitted text the user typed in the address bar, kept per-tab so switching
    /// away and back doesn't erase it. Cleared on navigation.</summary>
    public string? AddressDraft { get; set; }

    /// <summary>If we guessed the typed text was a domain, the original text — so a failed DNS
    /// lookup can fall back to a search instead of an error page. Cleared once the page loads.</summary>
    public string? SearchFallback { get; set; }

    /// <summary>The live control, or null when the tab is discarded.</summary>
    public WebView2? View { get; set; }

    /// <summary>Set by the injected link-click script when the last click on a link was a
    /// middle-click or Ctrl+click, so the resulting NewWindowRequested opens in the background.</summary>
    public DateTime BgHintUtc { get; set; }

    /// <summary>The TabManager/window currently hosting this tab. Set when the tab's view is created
    /// and updated when the tab is adopted by another window. Per-tab WebView2 handlers route through
    /// this so a moved tab drives its new window, not the one that created it.</summary>
    public TabManager? Owner { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
