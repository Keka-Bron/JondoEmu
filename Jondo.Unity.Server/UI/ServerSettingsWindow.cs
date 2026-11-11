using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.UI;

internal sealed class ServerSettingsWindow : Window
{
    internal const int CountdownWithPlayers = 60, CountdownEmpty = 3;
    private readonly Language _language;
    internal readonly ServerSettingsEditor Editor;
    private readonly TextBlock _status = ServerTheme.Text("");
    private readonly Button _save, _restart, _close;
    private readonly List<NumericUpDown> _numbers = new();
    private readonly SettingsCardsPanel _cards = new() { Name = "SettingsCards", Margin = new Thickness(24, 0, 24, 16) };
    private bool _confirming, _building = true;
    private int _restartSeconds;
    private string T(string key) => ServerSettingsTexts.Get(_language, key);
    private System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(
        _language switch { Language.En => "en-GB", Language.Fr => "fr-FR", _ => "es-ES" });

    internal ServerSettingsWindow(Language language) : this(language, ServerSettings.Load(), ServerSettings.Current) { }

    // The second constructor permits desktop tests without touching the real settings/database.
    internal ServerSettingsWindow(Language language, ServerSettings saved, ServerSettings running)
    {
        _language = language;
        Editor = new(saved, running);
        Title = T("Caption"); Width = 1000; Height = 800; MinWidth = 360; MinHeight = 420;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ServerTheme.Window(this);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = new StackPanel { Margin = new Thickness(24, 20), Spacing = 8 };
        header.Children.Add(ServerTheme.Text(T("Caption").ToUpperInvariant(), 24, LauncherPalette.LightGold));
        header.Children.Add(Note("Subtitle"));
        layout.Children.Add(header);
        var s = Editor.Edited;
        var rates = Card("Rates",
            Number("Xp", null, s.ExperienceBonusPercent, 0, 10000, 10, "%", v => s.ExperienceBonusPercent = (int)v),
            Number("JobXp", null, s.JobExperienceBonusPercent, 0, 10000, 10, "%", v => s.JobExperienceBonusPercent = (int)v),
            Number("Drop", null, s.DropBonusPercent, 0, 10000, 10, "%", v => s.DropBonusPercent = (int)v),
            Number("Kamas", null, s.KamasBonusPercent, 0, 10000, 10, "%", v => s.KamasBonusPercent = (int)v), Note("RatesHint"));
        var characters = Card("Characters",
            Number("MaxAp", null, s.MaxActionPoints, 0, 30, 1, "", v => s.MaxActionPoints = (int)v),
            Number("MaxMp", "CapHint", s.MaxMovementPoints, 0, 20, 1, "", v => s.MaxMovementPoints = (int)v),
            Toggle("NoEnergy", "NoEnergyHint", s.NoEnergyLoss, v => s.NoEnergyLoss = v));
        var conquests = Card("Conquests",
            Number("Weakened", "WeakenedHint", s.PrismWeakenedHours, 0, 168, 1, "h", v => s.PrismWeakenedHours = (int)v),
            Number("ConquestMax", "ConquestMaxHint", s.ConquestMaxMinutes, 20, 1440, 10, "min", v => s.ConquestMaxMinutes = (int)v),
            Number("Invulnerable", "InvulnerableHint", s.ConquestInvulnerabilityHours, 0, 168, 1, "h", v => s.ConquestInvulnerabilityHours = (int)v),
            Number("Protection", "ProtectionHint", s.PrismProtectionMinutes, 0, 1440, 10, "min", v => s.PrismProtectionMinutes = (int)v));
        var startingBonuses = Card("StartingBonuses", 
            Number("StartingKamas", "StartingKamasHint", s.StartingKamas, 0, 1_000_000_000L, 1, "", v => s.StartingKamas = (long)v),
            Number("StartingScrollStats", "StartingScrollStatsHint", s.StartingScrollStats, 0, 10_000, 1, "", v => s.StartingScrollStats = (int)v),
            Toggle("StartingAdventurerSet", "StartingAdventurerSetHint", s.StartingAdventurerSet, v => s.StartingAdventurerSet = v));
        var welcome = new TextBox { Name = "WelcomeMessage", Text = s.WelcomeMessage, Watermark = T("WelcomePlaceholder"), MaxLength = 300 };
        welcome.TextChanged += (_, _) => { s.WelcomeMessage = welcome.Text ?? ""; Changed(); };
        var welcomeCard = Card("Welcome", Note("WelcomeHint"), welcome);
        var botsMax = Number("BotsMax", "BotsMaxHint", s.SimulatedPlayersMax, 1, Bots.SimulatedPlayers.Most, 5, "", v => s.SimulatedPlayersMax = (int)v);
        var bots = Card("Bots", Toggle("BotsOn", "BotsOnHint", s.SimulatedPlayers, v => { s.SimulatedPlayers = v; botsMax.IsEnabled = v; }), botsMax);
        botsMax.IsEnabled = s.SimulatedPlayers;
        var huntMinutes = Number("HuntMinutes", null, s.HeadHuntWindowMinutes, 1, 120, 5, "min", v => s.HeadHuntWindowMinutes = (int)v);
        var restMinutes = Number("RestMinutes", null, s.HeadHuntRestMinutes, 0, 600, 5, "min", v => s.HeadHuntRestMinutes = (int)v);
        void HuntersState(bool on) { huntMinutes.IsEnabled = on; restMinutes.IsEnabled = on; }
        var hunters = Toggle("HuntersOn", "HuntersOnHint", s.HeadHunters, v => { s.HeadHunters = v; HuntersState(v); });
        HuntersState(s.HeadHunters);

        var chance = Number("Chance", "ChanceHint", (decimal)Math.Clamp(double.IsFinite(s.RandomLootChancePercent) ? s.RandomLootChancePercent : 10, 0, 100),
            0, 100, 1, "%", v => s.RandomLootChancePercent = (double)v, 2);
        var rarities = Rarities();
        var levels = Note("LootLevels");
        void LootState(bool on) { chance.IsEnabled = on; rarities.IsEnabled = on; levels.IsEnabled = on; }
        var loot = Toggle("RandomLoot", "RandomLootHint", s.RandomLoot, v => { s.RandomLoot = v; LootState(v); });
        LootState(s.RandomLoot);
        var lootCard = Card("Loot", loot, chance, rarities, levels);
        var modes = Card("Modes", Toggle("Hardcore", "HardcoreHint", s.Hardcore, v => s.Hardcore = v), hunters, huntMinutes, restMinutes);
        var raids = Card("Raids",
            Number("RaidMin", "RaidMinHint", s.RaidMinPlayers, 0, 16, 1, "", v => s.RaidMinPlayers = (int)v),
            Number("Matchmaking", "MatchmakingHint", s.MatchmakingGroupSize, 0, 4, 1, "", v => s.MatchmakingGroupSize = (int)v));
        foreach (var card in new[] { rates, lootCard, characters, modes, conquests, raids, bots, welcomeCard, startingBonuses })
            _cards.Children.Add(card);
        var scroll = new ScrollViewer { Name = "SettingsScroll", Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) =>
        {
            _cards.ViewportHeight = Math.Max(0, scroll.Bounds.Height - _cards.Margin.Top - _cards.Margin.Bottom);
            _cards.InvalidateMeasure();
        };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(24, 12, 24, 18), Spacing = 12 };
        _status.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_status);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _close = ServerTheme.Button(T("Close"), CloseClicked);
        _save = ServerTheme.Button(T("Save"), SaveClicked);
        _restart = ServerTheme.Button(T("SaveRestart"), RestartClicked, LauncherPalette.OnlineGreen);
        _close.Name = "CloseSettings"; _save.Name = "SaveSettings"; _restart.Name = "RestartServer";
        foreach (var b in new[] { _close, _save, _restart }) { b.Margin = new Thickness(6, 0, 0, 6); buttons.Children.Add(b); }
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
        Opened += (_, _) =>
        {
            // Opened maximized from the server window, it fills the screen as that one does: the
            // limits below would hold a maximized window to the screen less 40.
            if (WindowState == WindowState.Maximized) return;
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null) return;
            MaxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - 40);
            MaxWidth = Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling - 40);
            Height = Math.Min(Height, MaxHeight);
            Width = Math.Min(Width, MaxWidth);
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseClicked(); e.Handled = true; }
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SaveClicked(); e.Handled = true; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _building = false;
        Changed();
    }

    private TextBlock Note(string key)
    {
        var note = ServerTheme.Text(T(key), 12, LauncherPalette.MutedGold);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    private Border Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 14 };
        var heading = ServerTheme.Text(T(title), 14, LauncherPalette.LightGold);
        heading.FontWeight = FontWeight.Bold; heading.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(heading);
        foreach (var child in children) stack.Children.Add(child);
        return ServerTheme.Card(stack);
    }

    private Control Row(string label, string hint, Control input)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        input.Margin = new Thickness(12, 0, 0, 0);
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var text = ServerTheme.Text(T(label)); text.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(text);
        if (hint != null)
        {
            var note = Note(hint);
            labels.Children.Add(note);
            _cards.Hints.Add(note);
            ToolTip.SetTip(row, T(hint));
        }
        row.Children.Add(labels); Grid.SetColumn(input, 1); row.Children.Add(input);
        return row;
    }

    private Control Number(string label, string hint, decimal value, decimal min, decimal max, decimal step,
        string unit, Action<decimal> set, int decimals = 0)
    {
        var number = new NumericUpDown
        {
            Name = label, Minimum = min, Maximum = max, Increment = step, Value = Math.Clamp(value, min, max),
            FormatString = decimals == 0 ? "0" : "0.##", Width = 114,
            NumberFormat = Culture.NumberFormat,
        };
        _numbers.Add(number);
        // Clamped legacy file values must agree with what is displayed and will be saved.
        set(number.Value.Value);
        number.ValueChanged += (_, _) =>
        {
            if (number.Value is decimal v) set(v);
            Changed();
        };
        number.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty) Changed(); };
        var input = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        input.Children.Add(number);
        if (unit.Length > 0) input.Children.Add(ServerTheme.Text(unit, 12, LauncherPalette.MutedGold));
        return Row(label, hint, input);
    }

    private Control Toggle(string label, string hint, bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { Name = label, IsChecked = value, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) => { set(toggle.IsChecked == true); Changed(); };
        return Row(label, hint, toggle);
    }

    private Control Rarities()
    {
        var table = new StackPanel { Spacing = 6 };
        void Add(string key, double percent, uint color)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(ServerTheme.Text(T(key), 12, color));
            var value = ServerTheme.Text(percent.ToString("0.##", Culture) + " %", 12, color);
            Grid.SetColumn(value, 1); row.Children.Add(value); table.Children.Add(row);
        }
        Add("Consumables", 100 - RandomLoot.Odds.Sum(o => o.Percent), LauncherPalette.MutedGold);
        foreach (var (rarity, key, color) in new[]
        {
            (RandomLoot.Rarity.Equipment, "Equipment", LauncherPalette.CardText),
            (RandomLoot.Rarity.Cosmetic, "Cosmetics", 0xFF78BEFFu),
            (RandomLoot.Rarity.Creature, "Creatures", LauncherPalette.OnlineGreen),
            (RandomLoot.Rarity.Dofus, "Dofus", LauncherPalette.LightGold),
            (RandomLoot.Rarity.Legendary, "Legendary", 0xFFFF8C3Au),
            (RandomLoot.Rarity.Perfect, "Perfect", LauncherPalette.PurpleBorder),
        }) Add(key, RandomLoot.Odds.First(o => o.Rarity == rarity).Percent, color);
        return table;
    }

    private void Changed()
    {
        if (_building) return;
        _confirming = false;
        _restart.Content = T("SaveRestart"); _save.IsVisible = true;
        bool valid = ValidInputs();
        _save.IsEnabled = Editor.Dirty && valid;
        _restart.IsEnabled = (Editor.Dirty || Editor.Pending) && valid;
        _close.Content = T(Editor.Dirty ? "Cancel" : "Close");
        Status(Editor.Dirty ? T("Unsaved") : Editor.Pending ? T("Pending") : "");
    }

    private bool ValidInputs() => _numbers.All(n => n.Value != null &&
        decimal.TryParse(n.Text, System.Globalization.NumberStyles.Number, n.NumberFormat, out var parsed) &&
        parsed >= n.Minimum && parsed <= n.Maximum &&
        (n.FormatString != "0" || parsed == decimal.Truncate(parsed)));

    private void Status(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = ServerTheme.Brush(error ? LauncherPalette.Red : LauncherPalette.HighlightText);
    }

    private bool Save()
    {
        try
        {
            Editor.Save(settings =>
            {
                // Preserve immediate language changes independently of game settings edits.
                settings.WindowLanguage = ServerSettings.Load().WindowLanguage;
                settings.Save();
            });
            Console.WriteLine($"[Settings] Saved to {Jondo.Unity.Launcher.Paths.ServerSettingsFile}; they apply at the next start.");
            return true;
        }
        catch (Exception ex) { Status(T("SaveError") + ex.Message, true); return false; }
    }

    private void SaveClicked()
    {
        if (_save.IsEnabled && ValidInputs() && Editor.Dirty && Save()) Changed();
    }

    private void RestartClicked()
    {
        if (!_restart.IsEnabled || !ValidInputs()) return;
        if (ServerRestart.Counting) { Status(T("Counting"), true); return; }
        if (!_confirming)
        {
            int players = ServerRestart.PlayersInside;
            _restartSeconds = players > 0 ? CountdownWithPlayers : CountdownEmpty;
            _confirming = true; _restart.Content = T("ConfirmRestart"); _close.Content = T("Back"); _save.IsVisible = false;
            Status(players > 0 ? string.Format(T("RestartAsk"), players, _restartSeconds) : string.Format(T("RestartAskEmpty"), _restartSeconds));
            return;
        }
        if (Editor.Dirty && !Save()) return;
        ServerRestart.Begin(_restartSeconds);
        Close();
    }

    private void CloseClicked()
    {
        if (_confirming) Changed();
        else Close();
    }
}
