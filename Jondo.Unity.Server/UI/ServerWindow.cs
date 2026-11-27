using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.UI.Pages;
using Jondo.Unity.Server.UI.Skin;

namespace Jondo.Unity.Server.UI;

/// <summary>
/// What the server window reads and changes in the running server. <see cref="Live"/> is the real
/// server; the desktop tests give their own, so they never touch the operator's files or players.
/// </summary>
internal sealed record ServerWindowServices(
    Func<bool> IsReady,
    Func<IReadOnlyList<ServerControl.Player>> Players,
    Func<Guid, bool> Kick,
    Func<string, Task<int>> Announce,
    Func<EventSettings> Events,
    Action<EventSettings> ApplyEvents,
    Func<DateTime> Clock,
    Func<ServerSettings> SavedSettings,
    Func<ServerSettings> RunningSettings,
    Action<Language>? RememberLanguage = null)
{
    internal static ServerWindowServices Live => new(
        () => Program.Ready,
        () => Program.Ready ? ServerControl.Players() : Array.Empty<ServerControl.Player>(),
        ServerControl.Kick,
        ServerControl.Announce,
        () => EventSettings.Current,
        settings => EventSettings.Apply(settings),
        () => Almanax.Clock(),
        ServerSettings.Load,
        () => ServerSettings.Current,
        language => ServerPreferences.Language = language);
}

/// <summary>
/// The server's window: a sidebar with the pages (console, players, events, settings, mods), the
/// language and the stop button, and a top bar with the server's state, uptime and players.
/// </summary>
internal sealed class ServerWindow : Window
{
    internal enum Page { Console, Players, Events, Settings, Mods }

    private static readonly (Page Page, string Icon, string Key, string Subtitle)[] Navigation =
    {
        (Page.Console, "terminal", "NavConsole", "ConsoleSubtitle"),
        (Page.Players, "users", "NavPlayers", "PlayersSubtitle"),
        (Page.Events, "calendar", "NavEvents", "EventsSubtitle"),
        (Page.Settings, "sliders", "NavSettings", "Subtitle"),
        (Page.Mods, "box", "NavMods", "ModsSubtitle"),
    };

    private readonly ServerLog _log;
    private readonly ServerWindowServices _services;
    internal readonly ConsolePage Console;
    internal readonly PlayersPage Players;
    internal readonly EventsPage Events;
    internal readonly SettingsPage Settings;
    internal readonly ModsPage Mods;
    private readonly Dictionary<Page, (Control View, Button Nav, TextBlock Label)> _pages = new();
    private readonly Dictionary<Language, Button> _languages = new();
    private readonly Button _stop;
    private readonly SpacedText _title = new() { Spacing = 2.4, FontSize = 22, FontWeight = FontWeight.Bold, Height = 34,
        FontFamily = LauncherSkin.Title, Foreground = ServerTheme.Brush(LauncherPalette.LightGold), Shadow = true, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _subtitle = ServerTheme.Text("", 13, LauncherPalette.TextSecondary);
    private readonly StatusDot _dot = new() { Name = "ServerDot" };
    private readonly TextBlock _state = ServerTheme.Text("", 12, LauncherPalette.CardText);
    private readonly TextBlock _uptime = ServerTheme.Text("", 12, LauncherPalette.CardText);
    private readonly TextBlock _playerCount = ServerTheme.Text("", 12, LauncherPalette.CardText);
    private readonly DispatcherTimer _timer;
    private readonly DateTime _started;
    private Language _language;
    private bool _confirming;
    private Bitmap? _background;
    private readonly Func<bool> _isShuttingDown;
    private readonly Action _requestShutdown;
    private LauncherTexts Texts => LauncherTexts.Get(_language);
    private string T(string key) => ServerSettingsTexts.Get(_language, key);

    /// <summary>The page shown now.</summary>
    internal Page Current { get; private set; } = Page.Console;

    internal ServerWindow() : this(() => Program.IsShuttingDown, () => Program.RequestShutdown("server window"),
        new ServerLog(), ServerPreferences.Language) { }

    internal ServerWindow(Func<bool> isShuttingDown, Action requestShutdown, ServerLog log, Language initialLanguage)
        : this(isShuttingDown, requestShutdown, log, initialLanguage, ServerWindowServices.Live) { }

    internal ServerWindow(Func<bool> isShuttingDown, Action requestShutdown, ServerLog log, Language initialLanguage,
        ServerWindowServices services)
    {
        _isShuttingDown = isShuttingDown;
        _requestShutdown = requestShutdown;
        _log = log;
        _services = services;
        _language = initialLanguage;
        try { _started = Process.GetCurrentProcess().StartTime; } catch { _started = DateTime.Now; }
        Title = "Jondo Server";
        Width = 1280; Height = 760; MinWidth = 980; MinHeight = 600;
        WindowState = WindowState.Maximized;
        ServerTheme.Window(this);

        var layers = new Grid();
        AddBackground(layers);

        Console = new ConsolePage(log, _language) { Name = "ConsolePage" };
        Players = new PlayersPage(_language, services.Players, services.Kick, services.Announce) { Name = "PlayersPage" };
        Events = new EventsPage(_language, services.Events, services.ApplyEvents, services.Clock) { Name = "EventsPage" };
        Settings = new SettingsPage(_language, services.SavedSettings, services.RunningSettings) { Name = "SettingsPage" };
        Mods = new ModsPage(_language, services.SavedSettings, services.RunningSettings) { Name = "ModsPage" };

        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("236,*") };
        _stop = ServerTheme.Button("", async () => await Stop(), LauncherPalette.Red);
        _stop.Name = "StopServer";
        shell.Children.Add(BuildSidebar());

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(28, 20, 22, 18) };
        Grid.SetColumn(content, 1);
        content.Children.Add(BuildTopBar());
        var host = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        Grid.SetRow(host, 1);
        foreach (var (page, view) in new (Page, Control)[]
        {
            (Page.Console, Console), (Page.Players, Players), (Page.Events, Events), (Page.Settings, Settings), (Page.Mods, Mods),
        })
        {
            view.IsVisible = page == Current;
            host.Children.Add(view);
            _pages[page] = (view, _pages[page].Nav, _pages[page].Label);
        }
        content.Children.Add(host);
        shell.Children.Add(content);
        layers.Children.Add(shell);
        Content = layers;

        ApplyLanguage();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _background?.Dispose(); };
        Closing += (_, e) =>
        {
            if (_isShuttingDown()) return;
            e.Cancel = true;
            _ = Stop();
        };
        KeyDown += (_, e) =>
        {
            // Ctrl+1..5 follow the sidebar order.
            if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
            int index = e.Key - Avalonia.Input.Key.D1;
            if (index >= 0 && index < Navigation.Length) { Show(Navigation[index].Page); e.Handled = true; }
        };
    }

    private void AddBackground(Grid layers)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Jondo Server/Assets/servidor_fondo.jpg"));
            _background = new Bitmap(stream);
            // Crop the watermark strip before mirroring, like the original server background.
            var crop = new CroppedBitmap(_background, new PixelRect(0, 0, (int)(_background.PixelSize.Width * .82), _background.PixelSize.Height));
            layers.Children.Add(new Image
            {
                Source = crop, Stretch = Stretch.UniformToFill, Opacity = .5,
                RenderTransform = new ScaleTransform(-1, 1), RenderTransformOrigin = RelativePoint.Center,
            });
        }
        catch (Exception ex) { System.Console.WriteLine($"[Server] Could not load the background: {ex.Message}"); }
        // A dusk over the picture, darker where the text is, so every page reads on it.
        layers.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 8, 4, 2), 0),
                    new GradientStop(Color.FromArgb(205, 10, 6, 3), .55),
                    new GradientStop(Color.FromArgb(235, 6, 3, 1), 1),
                },
            },
        });
        layers.Children.Add(new StarField { IsHitTestVisible = false, Opacity = .55 });
    }

    private Control BuildSidebar()
    {
        var top = new StackPanel { Spacing = 4 };
        top.Children.Add(new LogoBanner { First = "JONDO", Second = "SERVER", Height = 64, Margin = new Thickness(10, 18, 10, 22) });
        foreach (var (page, icon, _, _) in Navigation)
        {
            var label = ServerTheme.Text("", 14, LauncherPalette.TextSecondary);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            row.Children.Add(ServerTheme.Glyph(icon, LauncherPalette.SoftGold, 18));
            row.Children.Add(label);
            var nav = new Button
            {
                Name = "Nav." + page, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 11),
                Background = Brushes.Transparent, BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(0, 8, 8, 0),
                Margin = new Thickness(0, 0, 14, 0),
            };
            var shown = page;
            nav.Click += (_, _) => Show(shown);
            top.Children.Add(nav);
            _pages[page] = (null!, nav, label);
        }

        var bottom = new StackPanel { Spacing = 14, Margin = new Thickness(18, 0, 18, 20) };
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var language in new[] { Language.Es, Language.En, Language.Fr })
        {
            var selected = language;
            var code = LauncherTexts.Code(language);
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            face.Children.Add(new FlagIcon { Code = code });
            face.Children.Add(ServerTheme.Text(code.ToUpperInvariant(), 11, LauncherPalette.SoftGold));
            var button = new Button
            {
                Name = "Lang." + code, Content = face, Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(5),
                Background = ServerTheme.Brush(LauncherPalette.BarFill), BorderThickness = new Thickness(1),
            };
            button.Click += (_, _) => ChangeLanguage(selected);
            _languages.Add(language, button);
            flags.Children.Add(button);
        }
        bottom.Children.Add(flags);
        var stopFace = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        stopFace.Children.Add(ServerTheme.Glyph("stop", LauncherPalette.Red, 15));
        var stopText = ServerTheme.Text("", 13, LauncherPalette.Red);
        stopText.FontWeight = FontWeight.Bold;
        stopFace.Children.Add(stopText);
        _stop.Content = stopFace;
        _stop.HorizontalAlignment = HorizontalAlignment.Stretch;
        _stop.Padding = new Thickness(12, 10);
        _stop.Tag = stopText;
        bottom.Children.Add(_stop);

        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(top);
        return new Border
        {
            Child = dock, Background = ServerTheme.Brush(0xD90C0704),
            BorderBrush = ServerTheme.Brush(LauncherPalette.HairlineStrong), BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    private Control BuildTopBar()
    {
        var bar = new DockPanel { LastChildFill = true };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        _state.Name = "ServerState";
        _uptime.Name = "Uptime";
        _playerCount.Name = "PlayerCount";
        chips.Children.Add(ServerTheme.Chip(_dot, _state));
        chips.Children.Add(ServerTheme.Chip(ServerTheme.Glyph("clock", LauncherPalette.SoftGold, 14), _uptime));
        chips.Children.Add(ServerTheme.Chip(ServerTheme.Glyph("users", LauncherPalette.SoftGold, 14), _playerCount));
        DockPanel.SetDock(chips, Dock.Right);
        bar.Children.Add(chips);
        var titles = new StackPanel { Spacing = 4 };
        _subtitle.TextWrapping = TextWrapping.Wrap;
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        bar.Children.Add(titles);
        return bar;
    }

    /// <summary>Shows a page. Settings waits for the world to be ready, as its editor reads the database.</summary>
    internal void Show(Page page)
    {
        if (page == Page.Settings)
        {
            if (!_services.IsReady()) return;
            Settings.EnsureBuilt();
        }
        if (page == Page.Mods)
        {
            if (!_services.IsReady()) return;
            Mods.EnsureBuilt();
        }
        Current = page;
        foreach (var (key, (view, _, _)) in _pages) view.IsVisible = key == page;
        Paint();
        ((IServerPage)_pages[page].View).Tick();
    }

    private void Paint()
    {
        var current = Navigation.First(n => n.Page == Current);
        _title.Text = T(current.Key).ToUpperInvariant();
        _subtitle.Text = T(current.Subtitle);
        foreach (var (key, (_, nav, label)) in _pages)
        {
            bool selected = key == Current;
            nav.Background = selected ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(90, 230, 184, 0), 0), new GradientStop(Color.FromArgb(0, 230, 184, 0), 1) },
            } : Brushes.Transparent;
            nav.BorderBrush = ServerTheme.Brush(selected ? LauncherPalette.GoldBorder : 0x00000000);
            label.Foreground = ServerTheme.Brush(selected ? LauncherPalette.LightGold : LauncherPalette.TextSecondary);
            label.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void ChangeLanguage(Language language)
    {
        _language = language;
        _services.RememberLanguage?.Invoke(language);
        ApplyLanguage();
    }

    /// <summary>The window's language now.</summary>
    internal Language Language => _language;

    private void ApplyLanguage()
    {
        foreach (var (page, _, key, _) in Navigation) _pages[page].Label.Text = T(key);
        ((TextBlock)_stop.Tag!).Text = Texts.StopServer.ToUpperInvariant();
        foreach (var pair in _languages)
            pair.Value.BorderBrush = ServerTheme.Brush(pair.Key == _language ? LauncherPalette.GoldBorder : LauncherPalette.BorderBrown);
        foreach (var (view, _, _) in _pages.Values) ((IServerPage)view).ApplyLanguage(_language);
        Paint();
        RefreshStatus();
    }

    private void Refresh()
    {
        Console.Tick();
        if (Current != Page.Console) ((IServerPage)_pages[Current].View).Tick();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool ready = _services.IsReady();
        bool failed = Program.Failure != null;
        _state.Text = failed ? T("Failed") : _isShuttingDown() ? T("Stopping") : ready ? T("Online") : T("Starting");
        _state.Foreground = ServerTheme.Brush(failed ? LauncherPalette.Red : ready && !_isShuttingDown() ? LauncherPalette.OnlineGreen : LauncherPalette.Warning);
        _dot.Online = ready && !failed && !_isShuttingDown();
        var up = DateTime.Now - _started;
        _uptime.Text = string.Format(T("Uptime"), up.TotalHours >= 1 ? $"{(int)up.TotalHours} h {up.Minutes:00} min" : $"{up.Minutes} min {up.Seconds:00} s");
        int players;
        try { players = _services.Players().Count(p => !p.Simulated); } catch { players = 0; }
        _playerCount.Text = string.Format(T("PlayersCount"), players);
        bool settings = ready && !_isShuttingDown();
        _pages[Page.Settings].Nav.IsEnabled = settings;
        _pages[Page.Mods].Nav.IsEnabled = settings;
    }

    internal void ScrollLogToLatest() => Console.ScrollToLatest();

    private async Task Stop()
    {
        if (_confirming || _isShuttingDown()) return;
        _confirming = true;
        try
        {
            int count = Network.SessionRegistry.Count;
            string message = count > 0 ? string.Format(Texts.StopServerWithPlayers, count) : Texts.StopServerConfirm;
            if (Program.Failure == null && !await ServerTheme.Confirm(this, message, T("Yes"), T("Cancel"))) return;
            _stop.IsEnabled = false;
            _requestShutdown();
        }
        finally { _confirming = false; }
    }
}
