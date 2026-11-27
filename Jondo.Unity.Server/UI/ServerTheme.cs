using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI.Skin;

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

    internal static Border TableRow(Control content) => new()
    {
        Child = content, Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(6),
        Background = Brush(LauncherPalette.SurfaceRaised), BorderBrush = Brush(LauncherPalette.Hairline),
        BorderThickness = new Thickness(1),
    };

    /// <summary>A card's title: small capitals in gold, as the launcher's cards have them.</summary>
    internal static SpacedText Heading(string text, double size = 12, uint color = LauncherPalette.SoftGold)
        => new() { Text = text, Spacing = 1.6, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Brush(color),
                   FontFamily = LauncherSkin.Title, Height = size * 1.7, HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>A rounded pill with an icon and a line of text: the top bar's status, uptime and players.</summary>
    internal static Border Chip(Control icon, TextBlock text, uint border = LauncherPalette.BorderBrown)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        row.Children.Add(text);
        return new Border
        {
            Child = row, Background = Brush(0xCC120D08), BorderBrush = Brush(border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    internal static Icon Glyph(string kind, uint color = LauncherPalette.SoftGold, double size = 16)
        => new() { Kind = kind, Foreground = Brush(color), Width = size, Height = size };

    /// <summary>A card with a title over its content.</summary>
    internal static Border TitledCard(string name, out SpacedText title, params Control[] content)
    {
        var stack = new StackPanel { Spacing = 10 };
        title = Heading("");
        stack.Children.Add(title);
        foreach (var control in content) stack.Children.Add(control);
        var card = Card(stack);
        card.Name = name;
        card.Padding = new Thickness(18, 14);
        card.Background = Brush(0xE61A130C);
        return card;
    }

    internal static void Window(Window window)
    {
        window.Background = Brush(LauncherPalette.Background);
        window.Foreground = Brush(LauncherPalette.BaseText);
        Accent(window.Resources);
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Jondo Server/Assets/icono_servidor.ico"));
            window.Icon = new WindowIcon(stream);
        }
        catch (Exception ex) { Console.WriteLine($"[Server] Could not load the window icon: {ex.Message}"); }
    }

    /// <summary>
    /// Fluent's blue accent turned to the house gold: switches, ticks and the focus line of a
    /// box, the way the launcher draws them.
    /// </summary>
    internal static void Accent(IResourceDictionary resources)
    {
        IBrush gold = Brush(LauncherPalette.Gold), light = Brush(LauncherPalette.LightGold), muted = Brush(LauncherPalette.MutedGold);
        IBrush dark = Brush(LauncherPalette.Background);
        resources["SystemAccentColor"] = Color.FromUInt32(LauncherPalette.Gold);
        foreach (string key in new[] { "SystemControlHighlightAccentBrush", "ToggleSwitchFillOn", "ToggleSwitchStrokeOn",
                                       "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked",
                                       "TextControlBorderBrushFocused" })
            resources[key] = gold;
        foreach (string key in new[] { "ToggleSwitchFillOnPointerOver", "CheckBoxCheckBackgroundFillCheckedPointerOver",
                                       "CheckBoxCheckBackgroundStrokeCheckedPointerOver" })
            resources[key] = light;
        foreach (string key in new[] { "ToggleSwitchFillOnPressed", "CheckBoxCheckBackgroundFillCheckedPressed",
                                       "CheckBoxCheckBackgroundStrokeCheckedPressed" })
            resources[key] = muted;
        foreach (string key in new[] { "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
                                       "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
                                       "CheckBoxCheckGlyphForegroundCheckedPressed" })
            resources[key] = dark;
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
