using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Wisp;

/// <summary>A tiny dark modal text-input dialog (for naming/renaming bookmark folders, etc.).
/// Built in code so it needs no XAML. Returns the entered text, or null if cancelled.</summary>
public static class PromptDialog
{
    public static string? Show(Window owner, string title, string initial = "")
    {
        var win = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1d)),
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xe6, 0xe6)),
            FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
        });

        var box = new TextBox
        {
            Text = initial,
            Background = new SolidColorBrush(Color.FromRgb(0x37, 0x37, 0x3d)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xe6, 0xe6)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4c, 0x8d, 0xff)),
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), FontSize = 14,
            CaretBrush = new SolidColorBrush(Colors.White),
        };
        panel.Children.Add(box);

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };

        Button Make(string text, bool primary)
        {
            var b = new Button
            {
                Content = text, MinWidth = 78, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0),
                Foreground = new SolidColorBrush(primary ? Colors.White : Color.FromRgb(0xe6, 0xe6, 0xe6)),
                Background = new SolidColorBrush(primary ? Color.FromRgb(0x4c, 0x8d, 0xff) : Color.FromRgb(0x2c, 0x2c, 0x32)),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            };
            return b;
        }

        var ok = Make("OK", true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };

        var cancel = Make("Cancel", false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { win.DialogResult = false; };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }
}
