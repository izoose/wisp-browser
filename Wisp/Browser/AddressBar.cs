using System;

namespace Wisp;

/// <summary>
/// Turns whatever the user typed into a URL to navigate to: a real address gets a scheme,
/// anything that looks like a search query goes to the configured search engine.
/// </summary>
public static class AddressBar
{
    public static string Resolve(string input, AppSettings settings)
        => Resolve(input, settings, out _);

    /// <summary><paramref name="domainGuess"/> is true when we guessed a bare token was a domain
    /// (e.g. "vs.code") — the caller can fall back to a search if that host fails to resolve.</summary>
    public static string Resolve(string input, AppSettings settings, out bool domainGuess)
    {
        domainGuess = false;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return "https://www.google.com";

        // Already a full URL or a known scheme.
        if (text.Contains("://") ||
            text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("edge:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("wisp:", StringComparison.OrdinalIgnoreCase))
            return text;

        // Local file path: "C:\...", "C:/...", or a UNC "\\server\share" -> file:/// URI.
        if (LooksLikeLocalPath(text))
        {
            try { return new Uri(text).AbsoluteUri; } catch { }
        }

        // Keyword shortcut: "yt cats" -> YouTube search. First token must match a keyword exactly.
        int sp = text.IndexOf(' ');
        if (sp > 0 && settings.SearchKeywords != null)
        {
            var kw = text.Substring(0, sp);
            var rest = text.Substring(sp + 1).Trim();
            foreach (var k in settings.SearchKeywords)
                if (rest.Length > 0 && !string.IsNullOrEmpty(k.Template)
                    && string.Equals(k.Keyword, kw, StringComparison.OrdinalIgnoreCase))
                    return k.Template.Replace("%s", Uri.EscapeDataString(rest));
        }

        // localhost / localhost:port
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
            return "http://" + text;

        // Looks like a domain: contains a dot, no spaces (e.g. "github.com", "a.b/c").
        bool hasSpace = text.Contains(' ');
        bool hasDot = text.Contains('.');
        if (!hasSpace && hasDot)
        {
            domainGuess = true; // caller falls back to search if the host doesn't resolve
            return "https://" + text;
        }

        // Otherwise treat it as a search.
        return settings.BuildSearchUrl(text);
    }

    private static bool LooksLikeLocalPath(string t)
    {
        // Drive path: C:\ or C:/
        if (t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/')) return true;
        // UNC path: \\server\share
        if (t.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return false;
    }
}
