using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Wisp;

/// <summary>Drives the CoreWebView2 Find API behind the find-in-page bar.</summary>
public class FindController
{
    private readonly BrowserEnvironment _env;
    private CoreWebView2Find? _find;

    /// <summary>(activeMatchIndex, matchCount) — activeMatchIndex is 1-based, 0 when none.</summary>
    public event Action<int, int>? CountChanged;

    public FindController(BrowserEnvironment env) => _env = env;

    public async Task StartAsync(CoreWebView2? cw, string term)
    {
        Detach();
        if (cw == null) return;

        if (string.IsNullOrEmpty(term))
        {
            try { cw.Find.Stop(); } catch { }
            CountChanged?.Invoke(0, 0);
            return;
        }

        _find = cw.Find;
        _find.MatchCountChanged += OnChanged;
        _find.ActiveMatchIndexChanged += OnChanged;

        var opts = _env.Core.CreateFindOptions();
        opts.FindTerm = term;
        opts.SuppressDefaultFindDialog = true;
        opts.ShouldHighlightAllMatches = true;

        try { await cw.Find.StartAsync(opts); } catch { }
        Raise();
    }

    public void Next() { try { _find?.FindNext(); } catch { } }
    public void Prev() { try { _find?.FindPrevious(); } catch { } }

    public void Stop()
    {
        try { _find?.Stop(); } catch { }
        Detach();
        CountChanged?.Invoke(0, 0);
    }

    private void OnChanged(object? sender, object e) => Raise();

    private void Raise()
    {
        if (_find != null) CountChanged?.Invoke(_find.ActiveMatchIndex, _find.MatchCount);
    }

    private void Detach()
    {
        if (_find == null) return;
        _find.MatchCountChanged -= OnChanged;
        _find.ActiveMatchIndexChanged -= OnChanged;
        _find = null;
    }
}
