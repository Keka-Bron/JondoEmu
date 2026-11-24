using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI.Skin;

namespace Jondo.Unity.Server.UI.Pages;

/// <summary>A page of the server window: its texts follow the window's language and it refreshes with the window.</summary>
internal interface IServerPage
{
    void ApplyLanguage(Language language);
    /// <summary>Every half second while the window is open, and when the page is shown.</summary>
    void Tick();
}

/// <summary>The console: the server's metrics, and its log with a filter.</summary>
internal sealed class ConsolePage : UserControl, IServerPage
{
    private readonly ServerMetrics _metrics = new();
    private readonly ServerLog _log;
    internal readonly ListBox List;
    internal readonly CheckBox Follow = new() { Name = "FollowLog", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    internal readonly TextBox Filter = new() { Name = "LogFilter", MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clear;
    private readonly StackPanel _cards = new() { Spacing = 12 };
    private readonly List<(ServerMetrics.Metric Metric, SpacedText? Heading, TextBlock Label, TextBlock Value)> _rows = new();
    private readonly ObservableCollection<ServerLog.Line> _filtered = new();
    private string _shownFilter = "";
    private Language _language;
    private LauncherTexts Texts => LauncherTexts.Get(_language);

    /// <summary>The lines the list shows now: the whole log, or the ones the filter keeps.</summary>
    internal IReadOnlyList<ServerLog.Line> Shown => _shownFilter.Length == 0 ? _log.Lines : _filtered;

    internal ConsolePage(ServerLog log, Language language)
    {
        _log = log;
        _language = language;
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("290,*") };
        body.Children.Add(new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) });
        BuildMetrics();

        List = new ListBox
        {
            Name = "ServerLog",
            ItemsSource = _log.Lines, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            // Recycling clears the container's content before assigning its next row.
            ItemTemplate = new FuncDataTemplate<ServerLog.Line>((line, _) => line == null ? null : LogRow(line)),
            FontFamily = new FontFamily("Cascadia Mono,DejaVu Sans Mono,Consolas,monospace"), FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        // A log line is one text row: the list's default item padding would space them like buttons.
        List.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(6, 2)),
                new Avalonia.Styling.Setter(MinHeightProperty, 0d),
            },
        });

        // The filter takes the room the other tools leave, so they stay inside a narrow window.
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*,*,12,Auto,12,Auto"), Margin = new Thickness(4, 0, 4, 10) };
        toolbar.Children.Add(ServerTheme.Glyph("search", LauncherPalette.MutedGold));
        toolbar.ColumnDefinitions[2].MaxWidth = 320;
        Grid.SetColumn(Filter, 2); toolbar.Children.Add(Filter);
        Grid.SetColumn(Follow, 5); toolbar.Children.Add(Follow);
        _clear = ServerTheme.Button("", _log.Clear);
        _clear.Name = "ClearLog";
        _clear.Padding = new Thickness(14, 6);
        Grid.SetColumn(_clear, 7);
        toolbar.Children.Add(_clear);
        Filter.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refilter(); };

        var logArea = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        logArea.Children.Add(toolbar);
        logArea.Children.Add(List);
        var logCard = ServerTheme.Card(logArea, LauncherPalette.GoldBorder);
        logCard.Margin = new Thickness(16, 0, 0, 0);
        logCard.Background = ServerTheme.Brush(0xD90A0603);
        logCard.Padding = new Thickness(12, 12, 6, 8);
        Grid.SetColumn(logCard, 1);
        body.Children.Add(logCard);
        Content = body;
        ApplyLanguage(language);
        DetachedFromVisualTree += (_, _) => _metrics.Dispose();
    }

    private Control LogRow(ServerLog.Line line)
    {
        var text = new TextBlock { FontFamily = List?.FontFamily ?? FontFamily.Default, FontSize = 12 };
        foreach (var piece in line.Pieces)
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(piece.Text) { Foreground = ServerTheme.Brush(piece.Color) });
        if (line.Opcode.Length > 0)
            text.PointerReleased += async (_, e) =>
            {
                if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right) return;
                if (TopLevel.GetTopLevel(this) is not Window owner) return;
                var picker = new NamePicker(line.Opcode, _language);
                if (await picker.ShowDialog<bool>(owner))
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
        StackPanel? group = null;
        foreach (var metric in _metrics.Items)
        {
            var label = ServerTheme.Text("", 12, LauncherPalette.TextSecondary);
            var value = ServerTheme.Text("", 12, metric.Color);
            if (metric.Group)
            {
                group = new StackPanel { Spacing = 7 };
                var heading = ServerTheme.Heading("", 11);
                group.Children.Add(heading);
                var card = ServerTheme.Card(group);
                card.Background = ServerTheme.Brush(0xE61A130C);
                card.Padding = new Thickness(16, 12);
                _cards.Children.Add(card);
                _rows.Add((metric, heading, label, value));
                continue;
            }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            value.Margin = new Thickness(8, 0, 0, 0);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            value.FontWeight = FontWeight.Bold;
            Grid.SetColumn(value, 1);
            row.Children.Add(label); row.Children.Add(value); group!.Children.Add(row);
            _rows.Add((metric, null, label, value));
        }
    }

    public void ApplyLanguage(Language language)
    {
        _language = language;
        Follow.Content = Texts.AutoScroll;
        _clear.Content = Texts.ClearButton;
        Filter.Watermark = ServerSettingsTexts.Get(language, "LogFilter");
        foreach (var row in _rows)
        {
            if (row.Heading != null) row.Heading.Text = row.Metric.Label(Texts).ToUpperInvariant();
            else row.Label.Text = row.Metric.Label(Texts);
        }
    }

    public void Tick()
    {
        try { _metrics.Sample(); } catch { }
        foreach (var row in _rows)
        {
            if (row.Metric.Group) continue;
            // Do not inspect mutable world registries until initialization has finished.
            try { row.Value.Text = Program.Ready ? row.Metric.Read() : "—"; }
            catch { row.Value.Text = "—"; }
        }
        int before = _log.Lines.Count;
        long cursor = _log.LastId;
        _log.Append(ConsoleLogBuffer.ReadSince(cursor));
        bool grew = _log.LastId != cursor || before == 0;
        if (_shownFilter.Length > 0 && (grew || _log.Lines.Count != before)) Refilter(force: true);
        if (Follow.IsChecked == true && Shown.Count > 0 && grew) ScrollToLatest();
    }

    /// <summary>Shows only the lines with the filter's text, or every line when it is empty.</summary>
    internal void Refilter(bool force = false)
    {
        string wanted = (Filter.Text ?? "").Trim();
        if (!force && wanted == _shownFilter) return;
        _shownFilter = wanted;
        if (wanted.Length == 0)
        {
            _filtered.Clear();
            if (!ReferenceEquals(List.ItemsSource, _log.Lines)) List.ItemsSource = _log.Lines;
            return;
        }
        // At most the log's capacity, and only when a line came or the filter changed.
        var kept = _log.Lines.Where(line => line.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!kept.SequenceEqual(_filtered))
        {
            int common = 0;
            // Lines only ever leave from the start and arrive at the end: keep the shared part.
            while (_filtered.Count > 0 && (kept.Count == 0 || !ReferenceEquals(_filtered[0], kept[0])) && kept.Contains(_filtered[0]) == false)
                _filtered.RemoveAt(0);
            while (common < _filtered.Count && common < kept.Count && ReferenceEquals(_filtered[common], kept[common])) common++;
            while (_filtered.Count > common) _filtered.RemoveAt(_filtered.Count - 1);
            for (int i = common; i < kept.Count; i++) _filtered.Add(kept[i]);
        }
        if (!ReferenceEquals(List.ItemsSource, _filtered)) List.ItemsSource = _filtered;
    }

    internal void ScrollToLatest()
    {
        var shown = Shown;
        if (shown.Count == 0) return;
        // Trimming invalidates the virtual panel's realized indices. Arrange the remaining rows
        // before ScrollIntoView uses those indices to position newly realized containers.
        List.UpdateLayout();
        List.ScrollIntoView(shown[^1]);
    }
}
