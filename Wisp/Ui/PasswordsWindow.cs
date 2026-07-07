using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp;

/// <summary>Native dark "Passwords" manager: view / reveal / copy / delete Wisp's saved logins,
/// plus a strong-password generator. Native (not a web page) so plaintext passwords never touch
/// the DOM.</summary>
public class PasswordsWindow : Window
{
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE7, 0xEB, 0xF3));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA9));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1B));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x38));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x42));

    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private List<ChromiumImport.SavedLogin> _all = new();
    private string _filter = "";

    public PasswordsWindow(Window? owner)
    {
        Title = "Passwords";
        Width = 580; Height = 660; Background = Bg;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        Owner = owner is { IsLoaded: true } ? owner : null;

        var root = new DockPanel { Margin = new Thickness(18) };

        var header = new TextBlock
        {
            Text = "Saved passwords", Foreground = Fg, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Search + generate row
        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new TextBox
        {
            Background = Field, Foreground = Fg, CaretBrush = Brushes.White, BorderBrush = Line,
            BorderThickness = new Thickness(1), Padding = new Thickness(9, 7, 9, 7), FontSize = 13.5,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        search.TextChanged += (_, _) => { _filter = search.Text.Trim(); Render(); };
        Grid.SetColumn(search, 0);
        top.Children.Add(search);

        var gen = MakeButton("Generate password", true);
        gen.Margin = new Thickness(10, 0, 0, 0);
        gen.Click += (_, _) => GeneratePassword();
        Grid.SetColumn(gen, 1);
        top.Children.Add(gen);

        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = _list;
        root.Children.Add(scroll);

        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { Reload(); search.Focus(); };
    }

    private void Reload()
    {
        _all = ChromiumImport.ReadWispLogins()
            .OrderBy(l => HostOf(l.Origin), StringComparer.OrdinalIgnoreCase).ToList();
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        var rows = string.IsNullOrEmpty(_filter)
            ? _all
            : _all.Where(l => l.Origin.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                              || l.Username.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (rows.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = _all.Count == 0 ? "No saved passwords yet. They appear here as you sign in to sites (or after importing)."
                                       : "No matches.",
                Foreground = Dim, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 16, 2, 0),
            });
        }
        else
        {
            foreach (var login in rows) _list.Children.Add(BuildRow(login));
        }
        _status.Text = $"{_all.Count} saved {(_all.Count == 1 ? "login" : "logins")}";
    }

    private Border BuildRow(ChromiumImport.SavedLogin login)
    {
        var card = new Border
        {
            Background = Card, CornerRadius = new CornerRadius(10), BorderBrush = Line, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 10, 10),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = HostOf(login.Origin), Foreground = Fg, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(login.Username) ? "(no username)" : login.Username,
            Foreground = Dim, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Password row: masked box + reveal toggle.
        var pwRow = new StackPanel { Orientation = Orientation.Horizontal };
        var pwText = new TextBlock
        {
            Text = new string('•', Math.Min(12, Math.Max(6, login.Password.Length))),
            Foreground = Fg, FontSize = 13, FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 120,
        };
        bool revealed = false;
        var reveal = MakeMini("Show");
        reveal.Click += (_, _) =>
        {
            revealed = !revealed;
            pwText.Text = revealed ? login.Password : new string('•', Math.Min(12, Math.Max(6, login.Password.Length)));
            reveal.Content = revealed ? "Hide" : "Show";
        };
        pwRow.Children.Add(pwText);
        pwRow.Children.Add(reveal);
        info.Children.Add(pwRow);

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // Actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var copyUser = MakeMini("Copy user");
        copyUser.Click += (_, _) => Flash(TrySetClipboard(login.Username) ? "Username copied" : "No username to copy");
        var copyPass = MakeMini("Copy pass");
        copyPass.Click += (_, _) => Flash(TrySetClipboard(login.Password) ? "Password copied" : "No password to copy");
        var del = MakeMini("Delete");
        del.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        del.Click += (_, _) => DeleteLogin(login);
        actions.Children.Add(copyUser);
        actions.Children.Add(copyPass);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private void DeleteLogin(ChromiumImport.SavedLogin login)
    {
        if (!PromptDialog.Confirm(this, HostOf(login.Origin), $"Delete the saved password for {HostOf(login.Origin)}?"))
            return;
        if (ChromiumImport.DeleteWispLogin(login.Id))
        {
            _all.RemoveAll(l => l.Id == login.Id);
            Render();
            Flash("Password deleted");
        }
        else
        {
            Flash("Couldn't delete right now — try again in a moment");
        }
    }

    private void GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz",
                     digits = "23456789", sym = "!@#$%^&*-_=+?";
        const string all = upper + lower + digits + sym;
        var chars = new char[18];
        // Guarantee one of each class, then fill the rest.
        chars[0] = Pick(upper); chars[1] = Pick(lower); chars[2] = Pick(digits); chars[3] = Pick(sym);
        for (int i = 4; i < chars.Length; i++) chars[i] = Pick(all);
        // Fisher–Yates shuffle so the guaranteed chars aren't always up front.
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        var pw = new string(chars);
        TrySetClipboard(pw);
        Flash($"Generated & copied:  {pw}");

        static char Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)];
    }

    private void Flash(string msg) => _status.Text = msg;

    private static bool TrySetClipboard(string s)
    {
        if (string.IsNullOrEmpty(s)) return false; // WPF Clipboard.SetText throws on empty
        try { Clipboard.SetText(s); return true; } catch { return false; }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host is { Length: > 0 } h ? h : url; }
        catch { return url; }
    }

    private Button MakeButton(string text, bool primary) => new()
    {
        Content = text, Padding = new Thickness(14, 7, 14, 7),
        Foreground = primary ? Brushes.White : Fg, FontSize = 13,
        Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3E)),
        BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
    };

    private Button MakeMini(string text) => new()
    {
        Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0),
        Foreground = Fg, FontSize = 11.5, Background = Field, BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
    };
}
