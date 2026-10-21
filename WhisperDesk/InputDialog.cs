using System.Windows;
using System.Windows.Controls;
using WhisperDesk.Services;

namespace WhisperDesk;

/// <summary>Tiny themed "enter a name" dialog.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 14) };
        var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title, Owner = owner, Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize,
        };
        win.SetResourceReference(Window.BackgroundProperty, "Bg");
        win.SetResourceReference(Window.ForegroundProperty, "Fg");
        win.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(win);
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
