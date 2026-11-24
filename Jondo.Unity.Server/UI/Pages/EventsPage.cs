using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.UI.Skin;

namespace Jondo.Unity.Server.UI.Pages;

/// <summary>
/// The year's events: each one on its calendar, open or closed, and the event monsters. A change
/// applies at once (<see cref="EventSettings.Apply"/>) and is kept for the next start.
/// </summary>
internal sealed class EventsPage : UserControl, IServerPage
{
    /// <summary>The events in the order the page shows them, each with its accent.</summary>
    internal static readonly (int Id, uint Accent)[] Shown =
    {
        (SeasonalEvents.Kwismas, 0xFF8FD3FF),
        (SeasonalEvents.Halouine, 0xFFFF9A3C),
        (SeasonalEvents.Vulkania, 0xFFFF5A3C),
        (SeasonalEvents.SainBallotin, 0xFFFF7FB0),
        (SeasonalEvents.Pwak, 0xFFB6E36A),
        (SeasonalEvents.Ripate, 0xFFE6C280),
    };

    private sealed class EventCard
    {
        internal int Id;
        internal string Key = "";
        internal SpacedText Title = null!;
        internal TextBlock Hint = null!, State = null!, Calendar = null!;
        internal StatusDot Dot = null!;
        internal readonly Dictionary<EventMode, Button> Modes = new();
    }

    private readonly Func<EventSettings> _current;
    private readonly Action<EventSettings> _apply;
    private readonly Func<DateTime> _clock;
    private readonly List<EventCard> _cards = new();
    internal readonly ToggleSwitch Monsters = new() { Name = "EventMonsters" };
    internal readonly Slider Chance = new() { Name = "EventChance", Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5, TickFrequency = 5, IsSnapToTickEnabled = false, Width = 320 };
    private readonly TextBlock _chanceValue = ServerTheme.Text("", 14, LauncherPalette.LightGold);
    private readonly TextBlock _chanceLabel = ServerTheme.Text("", 13, LauncherPalette.CardText);
    private readonly TextBlock _monstersHint = ServerTheme.Text("", 12, LauncherPalette.TextSecondary);
    internal readonly TextBlock Status = ServerTheme.Text("", 12, LauncherPalette.HighlightText);
    private readonly SpacedText _monstersTitle;
    private readonly DispatcherTimer _chanceDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _building = true;
    private Language _language;
    private string T(string key) => ServerSettingsTexts.Get(_language, key);

    /// <param name="current">The events as they stand (<see cref="EventSettings.Current"/>).</param>
    /// <param name="apply">Makes a copy the server's events (<see cref="EventSettings.Apply"/>).</param>
    /// <param name="clock">The date the calendars are read at (<see cref="Almanax.Clock"/>).</param>
    internal EventsPage(Language language, Func<EventSettings> current, Action<EventSettings> apply, Func<DateTime> clock)
    {
        _language = language;
        _current = current;
        _apply = apply;
        _clock = clock;
        var grid = new UniformGrid { Columns = 2 };
        foreach (var (id, accent) in Shown)
        {
            var card = BuildCard(id, accent, out var border);
            _cards.Add(card);
            border.Margin = new Thickness(0, 0, 14, 14);
            grid.Children.Add(border);
        }

        Monsters.IsCheckedChanged += (_, _) => { if (!_building) Change(s => s.EventMonsters = Monsters.IsChecked == true); Chance.IsEnabled = Monsters.IsChecked == true; };
        Chance.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _chanceValue.Text = $"{(int)Math.Round(Chance.Value)} %";
            if (_building) return;
            _chanceDelay.Stop();
            _chanceDelay.Start();
        };
        _chanceDelay.Tick += (_, _) => Flush();
        var chanceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        chanceRow.Children.Add(_chanceLabel);
        chanceRow.Children.Add(Chance);
        chanceRow.Children.Add(_chanceValue);
        _monstersHint.TextWrapping = TextWrapping.Wrap;
        var monstersCard = ServerTheme.TitledCard("MonstersCard", out _monstersTitle, Monsters, chanceRow, _monstersHint);
        monstersCard.Margin = new Thickness(0, 0, 14, 0);

        var page = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        Status.Margin = new Thickness(2, 0, 0, 10);
        page.Children.Add(Status);
        page.Children.Add(grid);
        page.Children.Add(monstersCard);
        Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Read();
        ApplyLanguage(language);
        _building = false;
    }

    private EventCard BuildCard(int id, uint accent, out Border border)
    {
        var card = new EventCard { Id = id, Key = SeasonalEvents.KeyOf(id) };
        card.Title = ServerTheme.Heading("", 15, accent);
        card.Hint = ServerTheme.Text("", 12, LauncherPalette.TextSecondary);
        card.Hint.TextWrapping = TextWrapping.Wrap;
        card.Hint.MinHeight = 34;
        card.Dot = new StatusDot { Name = "Dot." + card.Key };
        card.State = ServerTheme.Text("", 12, LauncherPalette.CardText);
        card.State.Name = "State." + card.Key;
        card.Calendar = ServerTheme.Text("", 11, LauncherPalette.TextTertiary);
        card.Calendar.Name = "Calendar." + card.Key;

        var head = new DockPanel();
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        state.Children.Add(card.Dot);
        state.Children.Add(card.State);
        DockPanel.SetDock(state, Dock.Right);
        head.Children.Add(state);
        head.Children.Add(card.Title);

        var segmented = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var modes = new[] { EventMode.Auto, EventMode.On, EventMode.Off };
        for (int i = 0; i < modes.Length; i++)
        {
            var mode = modes[i];
            var button = new Button
            {
                Name = $"Mode.{card.Key}.{mode}", Padding = new Thickness(16, 6), FontSize = 11, FontWeight = FontWeight.Bold,
                BorderThickness = new Thickness(1), BorderBrush = ServerTheme.Brush(LauncherPalette.BorderBrown),
                CornerRadius = i == 0 ? new CornerRadius(6, 0, 0, 6) : i == modes.Length - 1 ? new CornerRadius(0, 6, 6, 0) : new CornerRadius(0),
                Margin = i == 0 ? new Thickness(0) : new Thickness(-1, 0, 0, 0),
            };
            button.Click += (_, _) => SetMode(card.Id, mode);
            card.Modes[mode] = button;
            segmented.Children.Add(button);
        }

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(head);
        body.Children.Add(card.Hint);
        body.Children.Add(segmented);
        body.Children.Add(card.Calendar);
        border = new Border
        {
            Name = "Event." + card.Key, Child = body, Padding = new Thickness(18, 14), CornerRadius = new CornerRadius(8),
            Background = ServerTheme.Brush(0xE61A130C), BorderThickness = new Thickness(1, 1, 1, 3),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromUInt32(LauncherPalette.BorderBrown), 0), new GradientStop(Color.FromUInt32(accent), 1) },
            },
        };
        return card;
    }

    /// <summary>Sets one event's mode, applied at once.</summary>
    internal void SetMode(int id, EventMode mode)
    {
        if (_current().ModeOf(id) == mode) return;
        Change(s => s.SetMode(id, mode));
    }

    private void Change(Action<EventSettings> edit)
    {
        var copy = _current().Copy();
        edit(copy);
        try
        {
            _apply(copy);
            Status.Text = string.Format(T("Applied"), Summary(copy));
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        Read();
    }

    /// <summary>Applies a chance the slider is still waiting to apply.</summary>
    internal void Flush()
    {
        _chanceDelay.Stop();
        int chance = (int)Math.Round(Chance.Value);
        if (_current().EventMonsterChance != chance) Change(s => s.EventMonsterChance = chance);
    }

    private string Summary(EventSettings settings)
    {
        var parts = Shown.Select(e => $"{T("Event." + SeasonalEvents.KeyOf(e.Id))} {ModeText(settings.ModeOf(e.Id)).ToLowerInvariant()}").ToList();
        parts.Add(settings.EventMonsters ? $"{T("EventMonsters").ToLowerInvariant()} {settings.EventMonsterChance} %" : $"{T("EventMonsters").ToLowerInvariant()}: {T("ModeOff").ToLowerInvariant()}");
        return string.Join(", ", parts);
    }

    private string ModeText(EventMode mode) => T(mode switch { EventMode.On => "ModeOn", EventMode.Off => "ModeOff", _ => "ModeAuto" });

    /// <summary>Shows the settings as they stand: each event's mode and whether it runs now.</summary>
    private void Read()
    {
        bool building = _building;
        _building = true;
        var settings = _current();
        DateTime now = _clock();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(_language switch { Language.En => "en-GB", Language.Fr => "fr-FR", _ => "es-ES" });
        foreach (var card in _cards)
        {
            var mode = settings.ModeOf(card.Id);
            foreach (var (m, button) in card.Modes)
            {
                bool selected = m == mode;
                button.Background = ServerTheme.Brush(selected ? m switch { EventMode.On => 0xFF3E5A16u, EventMode.Off => 0xFF5A1C16u, _ => 0xFF4A3A16u } : LauncherPalette.BarFill);
                button.Foreground = ServerTheme.Brush(selected ? LauncherPalette.BaseText : LauncherPalette.TextTertiary);
                button.Classes.Set("selected", selected);
            }
            // Read from the settings the page shows, so the dots follow them before the server does.
            bool on = SeasonalEvents.ForcedOn.ContainsKey(card.Id) || mode switch
            {
                EventMode.On => true,
                EventMode.Off => false,
                _ => SeasonalEvents.OnTheCalendar(card.Id, now),
            };
            card.Dot.Online = on;
            card.State.Text = T(on ? "StateOn" : "StateOff");
            var (start, end) = SeasonalEvents.WindowAround(card.Id, now);
            card.Calendar.Text = string.Format(T("NextWindow"), start.ToString("d MMM", culture), end.AddDays(-1).ToString("d MMM", culture));
        }
        Monsters.IsChecked = settings.EventMonsters;
        Chance.IsEnabled = settings.EventMonsters;
        if (!_chanceDelay.IsEnabled) Chance.Value = settings.EventMonsterChance;
        _chanceValue.Text = $"{(int)Math.Round(Chance.Value)} %";
        _building = building;
    }

    public void ApplyLanguage(Language language)
    {
        _language = language;
        foreach (var card in _cards)
        {
            card.Title.Text = T("Event." + card.Key).ToUpperInvariant();
            card.Hint.Text = T($"Event.{card.Key}.Hint");
            foreach (var (mode, button) in card.Modes) button.Content = ModeText(mode);
        }
        _monstersTitle.Text = T("EventMonsters");
        Monsters.OnContent = Monsters.OffContent = T("EventMonstersOn");
        _chanceLabel.Text = T("EventChance");
        _monstersHint.Text = T("EventMonstersHint");
        Read();
    }

    public void Tick()
    {
        // Another window, or the calendar turning over midnight, may have changed what runs.
        if (!_chanceDelay.IsEnabled) Read();
    }
}
