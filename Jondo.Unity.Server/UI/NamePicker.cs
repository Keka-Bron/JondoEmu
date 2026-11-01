using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.UI;

internal sealed class NamePicker : Window
{
    private readonly TextBox _filter = new();
    private readonly ListBox _list = new();
    private readonly HashSet<string> _hints;
    internal string Chosen { get; private set; } = "";

    internal NamePicker(string opcode, Language language)
    {
        string T(string key) => ServerSettingsTexts.Get(language, key);
        Title = $"{T("Name")} — {opcode}";
        Width = 640; Height = 580; MinWidth = 440; MinHeight = 360;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ServerTheme.Window(this);
        _hints = new(NameBinding.Hints(opcode), StringComparer.Ordinal);
        string current = NameBinding.Of(opcode);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(20) };
        body.Children.Add(ServerTheme.Text(opcode + (current.Length > 0 ? " — " + current : ""), 20, LauncherPalette.LightGold));
        var meaning = ServerTheme.Text(NameBinding.Meaning(opcode) +
            (_hints.Count > 0 ? "\n" + string.Join(", ", _hints.Take(6)) : ""), 13, LauncherPalette.MutedGold);
        meaning.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(meaning, 1); body.Children.Add(meaning);
        _filter.Watermark = T("Filter");
        _filter.TextChanged += (_, _) => Fill();
        Grid.SetRow(_filter, 2); body.Children.Add(_filter);
        _list.DoubleTapped += (_, _) => Accept();
        Grid.SetRow(_list, 3); body.Children.Add(_list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(ServerTheme.Button(T("Bind"), Accept));
        buttons.Children.Add(ServerTheme.Button(T("Unbind"), () => { Chosen = ""; Close(true); }));
        buttons.Children.Add(ServerTheme.Button(T("Cancel"), () => Close(false)));
        Grid.SetRow(buttons, 4); body.Children.Add(buttons);
        foreach (var child in body.Children) child.Margin = new Thickness(0, 0, 0, 12);
        Content = body;
        Fill();
        Opened += (_, _) => _filter.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
            if (e.Key == Key.Enter) Accept();
        };
    }

    private bool Suggested(string name) => _hints.Contains(NameBinding.Domain(name)) ||
        _hints.Any(h => h.Length >= 6 && name.Contains(h, StringComparison.OrdinalIgnoreCase));

    private void Fill()
    {
        string[] parts = (_filter.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = NameBinding.Catalogue().Where(n => parts.All(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => Suggested(n) ? 0 : 1)
            .ThenBy(n => parts.Length > 0 && n.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).Take(400).ToArray();
        _list.ItemsSource = matches;
        if (matches.Length > 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not string name) return;
        Chosen = name;
        Close(true);
    }
}
