using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp;

/// <summary>What the user chose to clear. Range is null for "all time".</summary>
public class ClearDataChoice
{
    public bool History, Cookies, Cache, Downloads;
    public TimeSpan? Range;
}

/// <summary>Borderless dark "Clear browsing data" dialog: checkboxes + a time range.</summary>
public static class ClearDataDialog
{
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE7, 0xEB, 0xF3));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA9));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x38));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x2A, 0x32, 0x42));

    private static readonly (string Label, TimeSpan? Span)[] Ranges =
    {
        ("Last hour", TimeSpan.FromHours(1)),
        ("Last 24 hours", TimeSpan.FromHours(24)),
        ("Last 7 days", TimeSpan.FromDays(7)),
        ("Last 4 weeks", TimeSpan.FromDays(28)),
        ("All time", null),
    };

    public static ClearDataChoice? Show(Window owner)
    {
        var win = new Window
        {
            Width = 380, SizeToContent = SizeToContent.Height,
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

        panel.Children.Add(new TextBlock
        {
            Text = "Clear browsing data", Foreground = Fg, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        // Time range
        panel.Children.Add(new TextBlock { Text = "Time range", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 14, 0, 6) });
        var combo = new ComboBox
        {
            Background = Field, Foreground = Fg, BorderBrush = Line, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5), FontSize = 13,
        };
        foreach (var r in Ranges) combo.Items.Add(r.Label);
        combo.SelectedIndex = 1; // Last 24 hours
        panel.Children.Add(combo);

        CheckBox Check(string text, bool on)
        {
            var cb = new CheckBox
            {
                Content = text, Foreground = Fg, FontSize = 13.5, IsChecked = on,
                Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand,
            };
            return cb;
        }

        panel.Children.Add(new TextBlock { Text = "Clear", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 16, 0, 2) });
        var cHistory = Check("Browsing history", true);
        var cCookies = Check("Cookies and site data (signs you out)", false);
        var cCache = Check("Cached images and files", true);
        var cDownloads = Check("Download history", false);
        panel.Children.Add(cHistory);
        panel.Children.Add(cCookies);
        panel.Children.Add(cCache);
        panel.Children.Add(cDownloads);

        ClearDataChoice? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };

        Button Make(string text, bool primary) => new()
        {
            Content = text, MinWidth = 80, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0),
            Foreground = primary ? Brushes.White : Fg, FontSize = 13.5,
            Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3E)),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };

        var cancel = Make("Cancel", false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => win.DialogResult = false;
        buttons.Children.Add(cancel);

        var ok = Make("Clear data", true);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            result = new ClearDataChoice
            {
                History = cHistory.IsChecked == true,
                Cookies = cCookies.IsChecked == true,
                Cache = cCache.IsChecked == true,
                Downloads = cDownloads.IsChecked == true,
                Range = Ranges[combo.SelectedIndex < 0 ? 4 : combo.SelectedIndex].Span,
            };
            win.DialogResult = true;
        };
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        win.Content = card;
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.DialogResult = false; };
        return win.ShowDialog() == true ? result : null;
    }
}
