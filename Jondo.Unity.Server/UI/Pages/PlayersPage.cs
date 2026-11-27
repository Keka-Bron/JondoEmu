using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI.Skin;

namespace Jondo.Unity.Server.UI.Pages;

/// <summary>Who is in the world, an announcement to all of them, and a way to kick one out.</summary>
internal sealed class PlayersPage : UserControl, IServerPage
{
    private readonly Func<IReadOnlyList<ServerControl.Player>> _players;
    private readonly Func<Guid, bool> _kick;
    private readonly Func<string, Task<int>> _announce;
    internal readonly TextBox AnnounceText = new() { Name = "AnnounceText", MaxLength = 200, AcceptsReturn = false };
    internal readonly Button AnnounceSend;
    internal readonly TextBlock AnnounceStatus = ServerTheme.Text("", 12, LauncherPalette.HighlightText);
    internal readonly TextBox Search = new() { Name = "PlayerSearch", Width = 240 };
    internal readonly CheckBox ShowBots = new() { Name = "ShowBots", IsChecked = false };
    internal readonly StackPanel Rows = new() { Name = "PlayerRows", Spacing = 4 };
    private readonly TextBlock _empty = ServerTheme.Text("", 14, LauncherPalette.TextTertiary);
    private readonly TextBlock _status = ServerTheme.Text("", 12, LauncherPalette.HighlightText);
    private readonly SpacedText _announceTitle, _listTitle;
    private readonly TextBlock _announceHint = ServerTheme.Text("", 12, LauncherPalette.TextSecondary);
    private readonly Grid _columns;
    private string _shown = "\0";
    private Language _language;
    private string T(string key) => ServerSettingsTexts.Get(_language, key);

    /// <summary>Asks before a kick; the window's dialog, unless a test answers.</summary>
    internal Func<string, Task<bool>> Confirm { get; set; }

    internal PlayersPage(Language language, Func<IReadOnlyList<ServerControl.Player>> players,
        Func<Guid, bool> kick, Func<string, Task<int>> announce)
    {
        _language = language;
        _players = players;
        _kick = kick;
        _announce = announce;
        Confirm = async message => TopLevel.GetTopLevel(this) is Window owner &&
            await ServerTheme.Confirm(owner, message, T("Kick"), T("Cancel"));

        AnnounceSend = ServerTheme.Button("", () => _ = Send(), LauncherPalette.LightGold);
        AnnounceSend.Name = "AnnounceSend";
        AnnounceText.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = Send(); } };
        // TextChanged comes later through the dispatcher; the property itself changes at once.
        AnnounceText.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) AnnounceSend.IsEnabled = !string.IsNullOrWhiteSpace(AnnounceText.Text); };
        AnnounceSend.IsEnabled = false;
        var sendRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,Auto") };
        sendRow.Children.Add(AnnounceText);
        Grid.SetColumn(AnnounceSend, 2);
        sendRow.Children.Add(AnnounceSend);
        _announceHint.TextWrapping = TextWrapping.Wrap;
        var announceCard = ServerTheme.TitledCard("AnnounceCard", out _announceTitle, _announceHint, sendRow, AnnounceStatus);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        searchRow.Children.Add(ServerTheme.Glyph("search", LauncherPalette.MutedGold));
        searchRow.Children.Add(Search);
        filters.Children.Add(searchRow);
        filters.Children.Add(ShowBots);
        Search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Tick(); };
        ShowBots.IsCheckedChanged += (_, _) => Tick();

        _columns = RowGrid();
        foreach (var (key, column) in new[] { ("ColName", 0), ("ColLevel", 2), ("ColMap", 4), ("ColState", 6) })
        {
            var heading = ServerTheme.Text("", 11, LauncherPalette.MutedGold);
            heading.FontWeight = FontWeight.Bold;
            heading.Tag = key;
            Grid.SetColumn(heading, column);
            _columns.Children.Add(heading);
        }
        _columns.Margin = new Thickness(14, 4, 14, 4);
        _empty.Margin = new Thickness(14, 18);
        _empty.Name = "NoPlayers";
        var listBody = new StackPanel { Spacing = 8 };
        listBody.Children.Add(filters);
        listBody.Children.Add(_status);
        listBody.Children.Add(_columns);
        listBody.Children.Add(Rows);
        listBody.Children.Add(_empty);
        var listCard = ServerTheme.TitledCard("PlayersCard", out _listTitle, listBody);

        var page = new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 12, 12) };
        page.Children.Add(announceCard);
        page.Children.Add(listCard);
        Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ApplyLanguage(language);
    }

    private static Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("2*,12,70,12,1.3*,12,1.2*,12,Auto") };

    public void ApplyLanguage(Language language)
    {
        _language = language;
        _announceTitle.Text = T("Announce");
        _announceHint.Text = T("AnnounceHint");
        AnnounceText.Watermark = T("AnnouncePlaceholder");
        AnnounceSend.Content = T("Send");
        Search.Watermark = T("Searching");
        ShowBots.Content = T("ShowBots");
        _listTitle.Text = T("NavPlayers").ToUpperInvariant();
        _empty.Text = T("NoPlayers");
        foreach (var heading in _columns.Children.OfType<TextBlock>()) heading.Text = T((string)heading.Tag!);
        _shown = "\0";
        Tick();
    }

    /// <summary>The players the list shows: the search and the simulated-players box applied.</summary>
    internal IReadOnlyList<ServerControl.Player> Visible()
    {
        IEnumerable<ServerControl.Player> all;
        try { all = _players(); }
        catch { all = Array.Empty<ServerControl.Player>(); }
        string wanted = (Search.Text ?? "").Trim();
        return all.Where(p => ShowBots.IsChecked == true || !p.Simulated)
                  .Where(p => wanted.Length == 0 || p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                  .ToList();
    }

    public void Tick()
    {
        var visible = Visible();
        // Rebuilt only when someone came, left, moved or started a fight.
        string shown = string.Join("|", visible.Select(p => $"{p.Session}:{p.Level}:{p.MapId}:{p.Fighting}"));
        if (shown == _shown) return;
        _shown = shown;
        Rows.Children.Clear();
        foreach (var player in visible) Rows.Children.Add(Row(player));
        _empty.IsVisible = visible.Count == 0;
        _columns.IsVisible = visible.Count > 0;
    }

    private Control Row(ServerControl.Player player)
    {
        var grid = RowGrid();
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(ServerTheme.Glyph(player.Simulated ? "monitor" : "users", player.Simulated ? LauncherPalette.TextTertiary : LauncherPalette.SoftGold, 14));
        var nameText = ServerTheme.Text(player.Name, 13, LauncherPalette.CardText);
        nameText.FontWeight = FontWeight.SemiBold;
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Children.Add(nameText);
        grid.Children.Add(name);
        var level = ServerTheme.Text(player.Level.ToString(), 13, LauncherPalette.HighlightText);
        Grid.SetColumn(level, 2); grid.Children.Add(level);
        var map = ServerTheme.Text(player.MapId.ToString(), 12, LauncherPalette.TextSecondary);
        Grid.SetColumn(map, 4); grid.Children.Add(map);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        state.Children.Add(ServerTheme.Glyph(player.Fighting ? "sword" : "map", player.Fighting ? LauncherPalette.Danger : LauncherPalette.OnlineGreen, 13));
        state.Children.Add(ServerTheme.Text(T(player.Simulated ? "Bot" : player.Fighting ? "InFight" : "Exploring"), 12, LauncherPalette.TextSecondary));
        Grid.SetColumn(state, 6); grid.Children.Add(state);
        var kick = ServerTheme.Button(T("Kick"), () => _ = KickClicked(player), LauncherPalette.Red);
        kick.Name = "Kick." + player.Session;
        kick.Padding = new Thickness(12, 4);
        kick.FontSize = 11;
        // A simulated player has no connection to close: the bots' own settings remove them.
        kick.IsEnabled = !player.Simulated;
        Grid.SetColumn(kick, 8); grid.Children.Add(kick);
        return ServerTheme.TableRow(grid);
    }

    internal async Task KickClicked(ServerControl.Player player)
    {
        if (!await Confirm(string.Format(T("KickAsk"), player.Name))) return;
        _status.Text = _kick(player.Session) ? string.Format(T("Kicked"), player.Name) : string.Format(T("KickGone"), player.Name);
        _shown = "\0";
        Tick();
    }

    internal async Task Send()
    {
        string text = (AnnounceText.Text ?? "").Trim();
        if (text.Length == 0) return;
        AnnounceSend.IsEnabled = false;
        try
        {
            int told = await _announce(text);
            AnnounceStatus.Text = told > 0 ? string.Format(T("Sent"), told) : T("NobodyToTell");
            if (told > 0) AnnounceText.Text = "";
        }
        catch (Exception ex) { AnnounceStatus.Text = ex.Message; }
        finally { AnnounceSend.IsEnabled = !string.IsNullOrWhiteSpace(AnnounceText.Text); }
    }
}
