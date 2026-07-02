using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp;

/// <summary>Modal that lets the user pick a detected browser and what to import from it.</summary>
public static class ImportDialog
{
    public class Choice
    {
        public BrowserProfile Profile = null!;
        public bool Cookies, Bookmarks, History, Passwords;
    }

    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xe6, 0xe6, 0xe6));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xa2));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4c, 0x8d, 0xff));
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1d));

    public static Choice? Show(Window owner, List<BrowserProfile> browsers)
    {
        var win = new Window
        {
            Title = "Import browser data",
            Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow, Background = Bg,
        };

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Bring your stuff to Wisp", Foreground = Fg, FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Import from another browser so you don't start from scratch. Your logins come over as cookies, so you stay signed in.",
            Foreground = Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16),
        });

        panel.Children.Add(new TextBlock { Text = "FROM", Foreground = Dim, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var radios = new List<RadioButton>();
        foreach (var b in browsers)
        {
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = b.Name + (b.HasCookies ? "" : "  (no cookies)"), Foreground = Fg, FontSize = 14 },
                GroupName = "browser", Margin = new Thickness(0, 3, 0, 3), Foreground = Fg,
                IsChecked = radios.Count == 0, Tag = b,
            };
            radios.Add(rb);
            panel.Children.Add(rb);
        }

        panel.Children.Add(new TextBlock { Text = "WHAT TO IMPORT", Foreground = Dim, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        var cCookies = Check("Cookies & logins  (stay signed in)", true);
        var cBookmarks = Check("Bookmarks  (with folders)", true);
        var cHistory = Check("Browsing history", true);
        var cPasswords = Check("Saved passwords  (needs a restart)", true);
        panel.Children.Add(cCookies);
        panel.Children.Add(cBookmarks);
        panel.Children.Add(cHistory);
        panel.Children.Add(cPasswords);

        Choice? choice = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = MakeButton("Cancel", false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => win.DialogResult = false;
        var import = MakeButton("Import", true);
        import.IsDefault = true;
        import.Click += (_, _) =>
        {
            BrowserProfile? sel = null;
            foreach (var rb in radios) if (rb.IsChecked == true) sel = (BrowserProfile)rb.Tag;
            if (sel == null) { win.DialogResult = false; return; }
            choice = new Choice
            {
                Profile = sel,
                Cookies = cCookies.IsChecked == true,
                Bookmarks = cBookmarks.IsChecked == true,
                History = cHistory.IsChecked == true,
                Passwords = cPasswords.IsChecked == true,
            };
            win.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(import);
        panel.Children.Add(buttons);

        win.Content = panel;
        return win.ShowDialog() == true ? choice : null;
    }

    private static CheckBox Check(string text, bool on) => new()
    {
        Content = new TextBlock { Text = text, Foreground = Fg, FontSize = 14 },
        IsChecked = on, Margin = new Thickness(0, 3, 0, 3), Foreground = Fg,
    };

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text, MinWidth = 84, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0),
        Foreground = primary ? Brushes.White : Fg,
        Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x32)),
        BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 13,
    };
}
