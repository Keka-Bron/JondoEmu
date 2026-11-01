using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI;

/// <summary>The server's Avalonia presentation uses the same toolkit-neutral palette.</summary>
internal static class ServerTheme
{
    internal static IBrush Brush(uint color) => new SolidColorBrush(Color.FromUInt32(color));

    internal static TextBlock Text(string text, double size = 13, uint color = LauncherPalette.BaseText)
        => new() { Text = text, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };

    internal static Button Button(string text, Action action, uint color = LauncherPalette.SoftGold)
    {
        var button = new Button
        {
            Content = text, Foreground = Brush(color), Background = Brush(LauncherPalette.BarFill),
            BorderBrush = Brush(LauncherPalette.BorderBrown), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(16, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }

    internal static Border Card(Control content, uint border = LauncherPalette.BorderBrown)
        => new()
        {
            Child = content, Background = Brush(LauncherPalette.CardFill), BorderBrush = Brush(border),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12),
        };

    internal static void Window(Window window)
    {
        window.Background = Brush(LauncherPalette.Background);
        window.Foreground = Brush(LauncherPalette.BaseText);
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Jondo Server/Assets/icono_servidor.ico"));
            window.Icon = new WindowIcon(stream);
        }
        catch (Exception ex) { Console.WriteLine($"[Server] Could not load the window icon: {ex.Message}"); }
    }

    internal static async Task<bool> Confirm(Window owner, string message, string yes, string no)
    {
        var dialog = new Window
        {
            Title = "Jondo Server", Width = 500, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Window(dialog);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button(no, () => dialog.Close(false)); cancel.Name = "CancelStop";
        var confirm = Button(yes, () => dialog.Close(true), LauncherPalette.Red); confirm.Name = "ConfirmStop";
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 24 };
        var text = Text(message, 15);
        text.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(text);
        body.Children.Add(buttons);
        dialog.Content = body;
        dialog.Opened += (_, _) => buttons.Children[0].Focus();
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(false); };
        return await dialog.ShowDialog<bool>(owner);
    }
}
