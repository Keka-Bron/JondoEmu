using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI;

internal sealed class ServerWindow : Window
{
    private readonly ServerMetrics _metrics = new();
    private readonly ServerLog _log;
    private readonly ListBox _list;
    private readonly CheckBox _follow = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _stop, _settings, _clear;
    private readonly TextBlock _state = ServerTheme.Text("", 13, LauncherPalette.HighlightText);
    private readonly StackPanel _cards = new() { Spacing = 10 };
    private readonly List<(ServerMetrics.Metric Metric, TextBlock Label, TextBlock Value)> _rows = new();
    private readonly Dictionary<Language, Button> _languages = new();
    private readonly DispatcherTimer _timer;
    private Language _language;
    private LauncherTexts Texts => LauncherTexts.Get(_language);
    private bool _confirming;
    private Bitmap _background;
    private readonly Func<bool> _isShuttingDown;
    private readonly Action _requestShutdown;

    internal ServerWindow() : this(() => Program.IsShuttingDown, () => Program.RequestShutdown("server window"),
        new ServerLog(), ServerPreferences.Language) { }

    internal ServerWindow(Func<bool> isShuttingDown, Action requestShutdown, ServerLog log, Language initialLanguage)
    {
        _isShuttingDown = isShuttingDown;
        _requestShutdown = requestShutdown;
        _log = log;
        _language = initialLanguage;
        Title = "Jondo Server";
        Width = 1280; Height = 760; MinWidth = 900; MinHeight = 560;
        WindowState = WindowState.Maximized;
        ServerTheme.Window(this);
        var layers = new Grid();
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Jondo Server/Assets/servidor_fondo.jpg"));
            _background = new Bitmap(stream);
            // Crop the watermark strip before mirroring, like the original server background.
            var crop = new CroppedBitmap(_background, new PixelRect(0, 0, (int)(_background.PixelSize.Width * .82), _background.PixelSize.Height));
            layers.Children.Add(new Image
            {
                Source = crop, Stretch = Stretch.UniformToFill, Opacity = .65,
                RenderTransform = new ScaleTransform(-1, 1), RenderTransformOrigin = RelativePoint.Center,
            });
        }
        catch (Exception ex) { Console.WriteLine($"[Server] Could not load the background: {ex.Message}"); }

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        var header = new StackPanel { Margin = new Thickness(8, 0, 0, 16), Spacing = 2 };
        var logo = ServerTheme.Text("JONDO  SERVER", 32, LauncherPalette.LightGold);
        logo.FontWeight = FontWeight.Bold;
        header.Children.Add(logo);
        header.Children.Add(_state);
        layout.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetRow(body, 1);
        body.Children.Add(new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        BuildMetrics();
        _list = new ListBox
        {
            Name = "ServerLog",
            ItemsSource = _log.Lines, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            // Recycling clears the container's content before assigning its next row.
            ItemTemplate = new FuncDataTemplate<ServerLog.Line>((line, _) => line == null ? null : LogRow(line)),
            FontFamily = new FontFamily("Cascadia Mono,DejaVu Sans Mono,Consolas,monospace"), FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var logCard = ServerTheme.Card(_list, LauncherPalette.GoldBorder);
        logCard.Margin = new Thickness(14, 0, 0, 0);
        logCard.Background = ServerTheme.Brush(LauncherPalette.ConsoleFill);
        Grid.SetColumn(logCard, 1);
        body.Children.Add(logCard);
        layout.Children.Add(body);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        Grid.SetRow(footer, 2);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        DockPanel.SetDock(actions, Dock.Right);
        _settings = ServerTheme.Button("", async () => await new ServerSettingsWindow(_language).ShowDialog(this), LauncherPalette.LightGold);
        _clear = ServerTheme.Button("", _log.Clear);
        _stop = ServerTheme.Button("", async () => await Stop(), LauncherPalette.Red);
        _stop.Name = "StopServer";
        actions.Children.Add(_settings); actions.Children.Add(_clear); actions.Children.Add(_stop);
        footer.Children.Add(actions);
        var preferences = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        preferences.Children.Add(_follow);
        foreach (var language in new[] { Language.Es, Language.En, Language.Fr })
        {
            var selected = language;
            var button = ServerTheme.Button(LauncherTexts.Code(language).ToUpperInvariant(), () => ChangeLanguage(selected));
            button.Padding = new Thickness(10, 8);
            _languages.Add(language, button);
            preferences.Children.Add(button);
        }
        footer.Children.Add(preferences);
        layout.Children.Add(footer);
        layers.Children.Add(layout);
        Content = layers;
        ApplyLanguage();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _metrics.Dispose(); _background?.Dispose(); };
        Closing += (_, e) =>
        {
            if (_isShuttingDown()) return;
            e.Cancel = true;
            _ = Stop();
        };
    }

    private Control LogRow(ServerLog.Line line)
    {
        var text = new TextBlock { FontFamily = _list?.FontFamily ?? FontFamily.Default, FontSize = 12 };
        foreach (var piece in line.Pieces)
            text.Inlines.Add(new Avalonia.Controls.Documents.Run(piece.Text) { Foreground = ServerTheme.Brush(piece.Color) });
        if (line.Opcode.Length > 0)
            text.PointerReleased += async (_, e) =>
            {
                if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right) return;
                var picker = new NamePicker(line.Opcode, _language);
                if (await picker.ShowDialog<bool>(this))
                {
                    try
                    {
                        Managers.NameBinding.Bind(line.Opcode, picker.Chosen);
                        Console.WriteLine(picker.Chosen.Length > 0 ? $"[Names] {line.Opcode} is {picker.Chosen}." : $"[Names] {line.Opcode} is unbound again.");
                    }
                    catch (Exception ex) { Console.WriteLine($"[!] Could not save the name binding: {ex.Message}"); }
                }
            };
        return text;
    }

    private void BuildMetrics()
    {
        StackPanel group = null;
        foreach (var metric in _metrics.Items)
        {
            var label = ServerTheme.Text("", 12, metric.Group ? LauncherPalette.SoftGold : LauncherPalette.MutedGold);
            var value = ServerTheme.Text("", 12, metric.Color);
            if (metric.Group)
            {
                group = new StackPanel { Spacing = 8 };
                label.FontWeight = FontWeight.Bold;
                group.Children.Add(label);
                _cards.Children.Add(ServerTheme.Card(group));
            }
            else
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                value.Margin = new Thickness(8, 0, 0, 0);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                value.FontWeight = FontWeight.Bold;
                Grid.SetColumn(value, 1);
                row.Children.Add(label); row.Children.Add(value); group.Children.Add(row);
            }
            _rows.Add((metric, label, value));
        }
    }

    private void ChangeLanguage(Language language)
    {
        _language = language;
        ServerPreferences.Language = language;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        _follow.Content = Texts.AutoScroll;
        _clear.Content = Texts.ClearButton;
        _settings.Content = Texts.ServerSettingsButton;
        _stop.Content = Texts.StopServer;
        foreach (var row in _rows) row.Label.Text = row.Metric.Label(Texts);
        foreach (var pair in _languages)
            pair.Value.BorderBrush = ServerTheme.Brush(pair.Key == _language ? LauncherPalette.GoldBorder : LauncherPalette.BorderBrown);
    }

    private void Refresh()
    {
        try { _metrics.Sample(); } catch { }
        foreach (var row in _rows)
        {
            if (row.Metric.Group) continue;
            // Do not inspect mutable world registries until initialization has finished.
            try { row.Value.Text = Program.Ready ? row.Metric.Read() : "—"; }
            catch { row.Value.Text = "—"; }
        }
        _state.Text = Program.Failure != null ? ServerSettingsTexts.Get(_language, "Failed") :
            _isShuttingDown() ? ServerSettingsTexts.Get(_language, "Stopping") :
            Program.Ready ? "" : ServerSettingsTexts.Get(_language, "Starting");
        _settings.IsEnabled = Program.Ready && !_isShuttingDown();
        int before = _log.Lines.Count;
        long cursor = _log.LastId;
        _log.Append(ConsoleLogBuffer.ReadSince(cursor));
        if (_follow.IsChecked == true && _log.Lines.Count > 0 && (_log.LastId != cursor || before == 0))
            ScrollLogToLatest();
    }

    internal void ScrollLogToLatest()
    {
        if (_log.Lines.Count == 0) return;
        // Trimming invalidates the virtual panel's realized indices. Arrange the remaining rows
        // before ScrollIntoView uses those indices to position newly realized containers.
        _list.UpdateLayout();
        _list.ScrollIntoView(_log.Lines[^1]);
    }

    private async Task Stop()
    {
        if (_confirming || _isShuttingDown()) return;
        _confirming = true;
        try
        {
            int count = Network.SessionRegistry.Count;
            string message = count > 0 ? string.Format(Texts.StopServerWithPlayers, count) : Texts.StopServerConfirm;
            if (Program.Failure == null && !await ServerTheme.Confirm(this, message,
                ServerSettingsTexts.Get(_language, "Yes"), ServerSettingsTexts.Get(_language, "Cancel"))) return;
            _stop.IsEnabled = false;
            _requestShutdown();
        }
        finally { _confirming = false; }
    }
}
