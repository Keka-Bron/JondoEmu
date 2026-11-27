using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.UI.Pages;

internal sealed class ModsPage : UserControl, IServerPage
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private readonly Func<ServerSettings> _saved, _running;
    private readonly Func<ServerModDescription[]> _packages;
    private readonly Func<string[]> _clientFiles;
    private readonly StackPanel _rows = new() { Name = "ModRows", Spacing = 4 };
    private readonly Grid _layout = new() { RowDefinitions = new RowDefinitions("*,Auto") };
    private Language _language;
    private ServerSettingsActions? _actions;
    private ServerModDescription[] _installed = [];
    private HashSet<string> _packagedAssets = new(Ids);
    private HashSet<string> _serverFiles = new(Ids);
    private string[] _standalone = [];

    internal ServerSettingsEditor? Editor { get; private set; }
    internal Action<ServerSettings> Write { get; set; } = settings => settings.Save();

    internal ModsPage(Language language, Func<ServerSettings> saved, Func<ServerSettings> running,
        Func<ServerModDescription[]>? packages = null, Func<string[]>? clientFiles = null)
    {
        _language = language;
        _saved = saved;
        _running = running;
        _packages = packages ?? (() => ServerModLoader.ListInstalled(Paths.ModsDirectory));
        _clientFiles = clientFiles ?? (() => ClientModCatalog.ListAvailable(Paths.ModsDirectory));
        var card = ServerTheme.Card(_rows);
        card.Padding = new Thickness(18, 14);
        card.Background = ServerTheme.Brush(0xE61A130C);
        _layout.Children.Add(new ScrollViewer
        {
            Content = card, Margin = new Thickness(0, 0, 12, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
        });
        Content = _layout;
        ServerSettingsActions.AttachShortcuts(this, () => _actions);
    }

    internal void EnsureBuilt()
    {
        if (Editor?.Dirty == true) return;
        Rebuild();
    }

    private void Rebuild()
    {
        _installed = _packages();
        _packagedAssets = _installed.Select(mod => mod.ClientAsset).OfType<string>().ToHashSet(Ids);
        var saved = _saved();
        var looseFiles = _clientFiles();
        _serverFiles = _installed.Where(mod => mod.IsStandalone).Select(mod => mod.Id)
            .Concat(looseFiles.Intersect(saved.ServerMods, Ids)).ToHashSet(Ids);
        _standalone = looseFiles.Except(_packagedAssets, Ids).Except(_serverFiles, Ids).ToArray();
        Editor = new ServerSettingsEditor(saved, _running());
        if (_actions != null) _layout.Children.Remove(_actions);
        _actions = new ServerSettingsActions(_language, Editor, Save, Rebuild, () => true, embedded: true, "Mods");
        Grid.SetRow(_actions, 1);
        _layout.Children.Add(_actions);
        Render();
    }

    public void ApplyLanguage(Language language)
    {
        _language = language;
        _actions?.ApplyLanguage(language);
        if (Editor != null) Render();
    }

    public void Tick() { }

    private string T(string key) => ServerSettingsTexts.Get(_language, key);

    private void Save(ServerSettings edited)
    {
        var next = _saved().Copy();
        next.CopyModsFrom(edited);
        // Installed packages own their client assets. Their order follows server startup.
        next.ClientMods = next.ClientMods.Where(name => !_packagedAssets.Contains(name)).ToArray();
        Write(next);
    }

    private void Render(string? focusName = null)
    {
        _rows.Children.Clear();
        var settings = Editor!.Edited;
        var serverOrder = settings.ServerMods.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(Ids).ToArray();
        var clientOrder = settings.ClientMods.Where(id => !string.IsNullOrWhiteSpace(id) && !_packagedAssets.Contains(id))
            .Distinct(Ids).ToArray();
        var packages = _installed.ToDictionary(mod => mod.Id, Ids);
        var clientFiles = _standalone.ToHashSet(Ids);
        if (serverOrder.Length + packages.Count + clientOrder.Length + clientFiles.Count == 0)
        {
            _rows.Children.Add(ServerTheme.Text(T("NoMods"), 13, LauncherPalette.TextTertiary));
            return;
        }

        var headings = RowGrid();
        headings.Margin = new Thickness(15, 4, 15, 4);
        string[] columns = ["ModActive", "ModName", "ModVersion", "NavSettings", "ModLoadOrder"];
        for (int i = 0; i < columns.Length; i++)
        {
            var heading = ServerTheme.Text(T(columns[i]).ToUpperInvariant(), 11, LauncherPalette.MutedGold);
            heading.FontWeight = FontWeight.Bold;
            AddCell(headings, heading, i);
        }
        _rows.Children.Add(headings);

        var serverIds = serverOrder.Union(packages.Keys.Order(Ids), Ids).ToArray();
        var activeServerIds = serverOrder.Where(id => !packages.TryGetValue(id, out var mod) ||
            ServerModConfiguration.IsActive(mod.Configuration,
                ServerModConfiguration.Resolve(settings, id, mod.Configuration))).ToArray();
        if (serverIds.Length > 0) AddGroup("ServerMods");
        foreach (string id in activeServerIds.Union(serverIds, Ids))
        {
            packages.TryGetValue(id, out var mod);
            AddRow(id, mod, mod != null || _serverFiles.Contains(id), server: true, serverOrder,
                activeServerIds, canEnable: true, focusName);
        }

        var clientIds = clientOrder.Union(_standalone, Ids).ToArray();
        bool canEnableClient = clientOrder.Count(clientFiles.Contains) < ClientModManifest.MaxEntries;
        if (clientIds.Length > 0) AddGroup("ClientMods");
        foreach (string id in clientIds)
            AddRow(id, null, clientFiles.Contains(id), server: false, clientOrder, clientOrder, canEnableClient, focusName);
    }

    private void AddGroup(string key)
    {
        var label = ServerTheme.Text(T(key), 12, LauncherPalette.SoftGold);
        label.Margin = new Thickness(15, 8, 15, 2);
        _rows.Children.Add(label);
    }

    private void AddRow(string id, ServerModDescription? mod, bool installed, bool server,
        string[] order, string[] activeOrder, bool canEnable, string? focusName)
    {
        var grid = RowGrid();
        string name = string.IsNullOrWhiteSpace(mod?.Name) ? id : mod.Name;
        int index = Array.FindIndex(activeOrder, item => Ids.Equals(item, id));
        bool active = index >= 0;
        string rowId = (server ? "Server." : "Client.") + id;
        var toggle = new ToggleSwitch
        {
            Name = "Active." + rowId, IsChecked = active, IsEnabled = active || (installed && canEnable),
            OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(toggle, $"{T("ModActive")} {name}");
        toggle.IsCheckedChanged += (_, _) =>
        {
            bool enabled = toggle.IsChecked == true;
            var next = order.Where(item => !Ids.Equals(item, id));
            if (enabled && mod?.Configuration is { ActivationSetting: { } activation } schema &&
                !ServerModConfiguration.IsActive(schema, ServerModConfiguration.Resolve(Editor!.Edited, id, schema)))
                ServerModConfiguration.Set(Editor!.Edited, schema, activation, true);
            SetOrder(server, enabled ? next.Append(id) : next, toggle.Name);
        };
        AddCell(grid, toggle, 0);

        var title = ServerTheme.Text(installed ? name : $"{name} ({T("ModMissing")})", 13, LauncherPalette.CardText);
        title.FontWeight = FontWeight.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(title, title.Text);
        AddCell(grid, title, 1);
        var version = ServerTheme.Text(mod?.Version ?? "—", 12, LauncherPalette.TextSecondary);
        version.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(version, version.Text);
        AddCell(grid, version, 2);

        var settings = RowButton(T("NavSettings"), "NavSettings", "Settings." + rowId,
            mod?.Configuration is { } configuration && ModSettingsDialog.HasFields(configuration),
            () => _ = EditSettings(name, mod?.Configuration));
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        AddCell(grid, settings, 3);
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var up = RowButton("↑", "ModUp", "Up." + rowId, index > 0, () => Move(index - 1, "Up." + rowId));
        var down = RowButton("↓", "ModDown", "Down." + rowId, active && index < activeOrder.Length - 1,
            () => Move(index + 1, "Down." + rowId));
        arrows.Children.Add(up);
        arrows.Children.Add(down);
        AddCell(grid, arrows, 4);
        _rows.Children.Add(ServerTheme.TableRow(grid));

        var focused = new Control[] { toggle, up, down }.FirstOrDefault(control => control.Name == focusName);
        if (focused != null) (focused.IsEnabled ? focused : toggle).Focus();

        Button RowButton(string text, string key, string buttonId, bool enabled, Action click)
        {
            var button = ServerTheme.Button(text, click);
            button.Name = buttonId;
            button.IsEnabled = enabled;
            button.Padding = new Thickness(12, 4);
            button.FontSize = 12;
            string description = $"{T(key)} {name}";
            AutomationProperties.SetName(button, description);
            ToolTip.SetTip(button, description);
            return button;
        }

        void Move(int target, string focus)
        {
            if (!active || target < 0 || target >= activeOrder.Length) return;
            string other = activeOrder[target];
            int from = Array.FindIndex(order, item => Ids.Equals(item, id));
            int to = Array.FindIndex(order, item => Ids.Equals(item, other));
            var next = (string[])order.Clone();
            (next[from], next[to]) = (next[to], next[from]);
            SetOrder(server, next, focus);
        }
    }

    private void SetOrder(bool server, IEnumerable<string> order, string? focus)
    {
        var settings = Editor!.Edited;
        if (server) settings.ServerMods = order.ToArray();
        else settings.ClientMods = order.ToArray();
        Render(focus);
        _actions?.Refresh();
    }

    private async Task EditSettings(string name, ServerModConfiguration? schema)
    {
        if (schema == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new ModSettingsDialog(_language, name, schema, Editor!.Edited);
        if (await dialog.ShowDialog<bool>(owner)) _actions?.Refresh();
    }

    private static Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("80,12,*,12,96,12,112,12,116") };

    private static void AddCell(Grid row, Control cell, int column)
    {
        Grid.SetColumn(cell, column * 2);
        row.Children.Add(cell);
    }
}
