using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp;

/// <summary>A borderless, dark, Wisp-styled modal dialog — used for folder naming and for
/// replacing the browser's default alert()/confirm()/prompt() popups so site dialogs look native
/// to Wisp instead of Edge's gray boxes.</summary>
public static class PromptDialog
{
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE7, 0xEB, 0xF3));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA9));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x38));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x42));

    /// <summary>Folder naming etc. Returns the entered text, or null if cancelled.</summary>
    public static string? Show(Window owner, string title, string initial = "")
        => Run(owner, title, null, hasInput: true, initial, hasCancel: true, "OK");

    /// <summary>Styled replacement for a site's alert(). Returns when dismissed.</summary>
    public static void Alert(Window owner, string? site, string message)
        => Run(owner, message, site, hasInput: false, "", hasCancel: false, "OK");

    /// <summary>Styled replacement for confirm() / beforeunload. True if the user accepted.</summary>
    public static bool Confirm(Window owner, string? site, string message)
        => Run(owner, message, site, hasInput: false, "", hasCancel: true, "OK") != null;

    /// <summary>Styled replacement for prompt(). Returns the text, or null if cancelled.</summary>
    public static string? PromptWeb(Window owner, string? site, string message, string initial)
        => Run(owner, message, site, hasInput: true, initial, hasCancel: true, "OK");

    private static string? Run(Window owner, string title, string? subtitle, bool hasInput,
        string initial, bool hasCancel, string okText)
    {
        var win = new Window
        {
            Width = 400, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner.IsLoaded ? owner : null,
        };

        var card = new Border
        {
            Background = Card, CornerRadius = new CornerRadius(14), BorderBrush = Line, BorderThickness = new Thickness(1),
            Margin = new Thickness(14), Padding = new Thickness(24, 22, 24, 20),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 34, ShadowDepth = 6, Opacity = .5, Color = Colors.Black },
        };
        var panel = new StackPanel();
        card.Child = panel;

        if (!string.IsNullOrWhiteSpace(subtitle))
            panel.Children.Add(new TextBlock
            {
                Text = subtitle, Foreground = Accent, FontSize = 11.5, FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis,
            });

        panel.Children.Add(new TextBlock
        {
            Text = title, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, LineHeight = 21,
        });

        TextBox? box = null;
        if (hasInput)
        {
            box = new TextBox
            {
                Text = initial, Background = Field, Foreground = Fg, CaretBrush = Brushes.White,
                BorderBrush = Accent, BorderThickness = new Thickness(1), Padding = new Thickness(9, 7, 9, 7),
                FontSize = 14, Margin = new Thickness(0, 14, 0, 0),
            };
            panel.Children.Add(box);
        }

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };

        Button Make(string text, bool primary) => new()
        {
            Content = text, MinWidth = 80, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0),
            Foreground = primary ? Brushes.White : Fg, FontSize = 13.5,
            Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3E)),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };

        if (hasCancel)
        {
            var cancel = Make("Cancel", false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => win.DialogResult = false;
            buttons.Children.Add(cancel);
        }
        var ok = Make(okText, true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = box?.Text ?? ""; win.DialogResult = true; };
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        win.Content = card;
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.DialogResult = false; };
        win.Loaded += (_, _) => { if (box != null) { box.Focus(); box.SelectAll(); } else ok.Focus(); };
        return win.ShowDialog() == true ? (result ?? "") : null;
    }
}
