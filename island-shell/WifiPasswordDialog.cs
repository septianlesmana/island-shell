using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

/// Small macOS-style window for entering a Wi-Fi password.
static class WifiPasswordDialog
{
    public static string? Ask(string ssid)
    {
        var box = new PasswordBox
        {
            FontSize = 13, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 12, 0, 0),
            Background = Ui.Brush(0xFF, 0x1C, 0x1C, 0x1E), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = Ui.Brush(0xFF, 0x3A, 0x3A, 0x3C)
        };
        var show = new CheckBox { Content = "Show password", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        var plain = new TextBox
        {
            FontSize = 13, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed,
            Background = Ui.Brush(0xFF, 0x1C, 0x1C, 0x1E), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = Ui.Brush(0xFF, 0x3A, 0x3A, 0x3C)
        };
        show.Checked += (_, _) => { plain.Text = box.Password; plain.Visibility = Visibility.Visible; box.Visibility = Visibility.Collapsed; };
        show.Unchecked += (_, _) => { box.Password = plain.Text; box.Visibility = Visibility.Visible; plain.Visibility = Visibility.Collapsed; };

        var win = new Window
        {
            Title = "Wi-Fi", Width = 380, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        string? result = null;

        Button Btn(string text, bool primary)
        {
            var b = new Button
            {
                Content = text, Width = 90, Height = 28, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.White,
                Background = primary ? Ui.Brush(0xFF, 0x0A, 0x84, 0xFF) : Ui.Brush(0xFF, 0x48, 0x48, 0x4A),
                BorderThickness = new Thickness(0), IsDefault = primary, IsCancel = !primary
            };
            return b;
        }
        var join = Btn("Join", true);
        var cancel = Btn("Cancel", false);
        join.Click += (_, _) => { result = show.IsChecked == true ? plain.Text : box.Password; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(join);

        var content = new StackPanel();
        content.Children.Add(Ui.Text($"The Wi-Fi network \"{ssid}\" requires a password.", 14, FontWeights.SemiBold));
        content.Children.Add(box);
        content.Children.Add(plain);
        content.Children.Add(show);
        content.Children.Add(buttons);
        ((TextBlock)content.Children[0]).TextWrapping = TextWrapping.Wrap;

        win.Content = new Border
        {
            Background = Ui.Brush(0xF5, 0x26, 0x26, 0x28), CornerRadius = new CornerRadius(14), Padding = new Thickness(20),
            BorderBrush = Ui.Brush(0x33, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1), Child = content
        };
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };
        win.Loaded += (_, _) => { win.Activate(); box.Focus(); };
        win.ShowDialog();
        return string.IsNullOrEmpty(result) ? null : result;
    }
}
