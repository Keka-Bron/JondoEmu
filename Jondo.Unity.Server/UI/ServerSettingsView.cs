using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.UI;

/// <summary>
/// The settings editor: the cards of <see cref="ServerSettings"/>, with Save and Save and restart.
/// It stands alone in <see cref="ServerSettingsWindow"/> and inside the dashboard's Settings page.
/// </summary>
internal sealed class ServerSettingsView : UserControl
{
    private readonly Language _language;
    internal readonly ServerSettingsEditor Editor;
    private readonly ServerSettingsActions _actions;

    internal Action<ServerSettings> Write { get; set; } = settings =>
    {
        settings.Save();
        Console.WriteLine($"[Settings] Saved to {Paths.ServerSettingsFile}; they apply at the next start.");
    };
    private readonly List<NumericUpDown> _numbers = new();
    private readonly SettingsCardsPanel _cards = new() { Name = "SettingsCards", Margin = new Thickness(24, 0, 24, 16) };
    private string T(string key) => ServerSettingsTexts.Get(_language, key);
    private System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(
        _language switch { Language.En => "en-GB", Language.Fr => "fr-FR", _ => "es-ES" });

    /// <param name="saved">The settings as the file has them, which the editor starts from.</param>
    /// <param name="running">The settings the server runs with, to tell what waits for a restart.</param>
    /// <param name="leave">
    /// Called by Close, and after a restart is started: the window closes; the page throws the
    /// edits away and starts again from the file.
    /// </param>
    /// <param name="embedded">Inside the dashboard, whose page already has the title.</param>
    /// <param name="latest">Reloads the saved file so a save preserves edits from the Mods page.</param>
    internal ServerSettingsView(Language language, ServerSettings saved, ServerSettings running, Action leave,
        bool embedded = false, Func<ServerSettings>? latest = null)
    {
        _language = language;
        Editor = new(saved, running);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        if (!embedded)
        {
            var header = new StackPanel { Margin = new Thickness(24, 20), Spacing = 8 };
            header.Children.Add(ServerTheme.Text(T("Caption").ToUpperInvariant(), 24, LauncherPalette.LightGold));
            header.Children.Add(Note("Subtitle"));
            layout.Children.Add(header);
        }
        var s = Editor.Edited;
        var rates = Card("Rates",
            Number("Xp", null, s.ExperienceBonusPercent, 0, 10000, 10, "%", v => s.ExperienceBonusPercent = (int)v),
            Number("JobXp", null, s.JobExperienceBonusPercent, 0, 10000, 10, "%", v => s.JobExperienceBonusPercent = (int)v),
            Number("Drop", null, s.DropBonusPercent, 0, 10000, 10, "%", v => s.DropBonusPercent = (int)v),
            Number("Kamas", null, s.KamasBonusPercent, 0, 10000, 10, "%", v => s.KamasBonusPercent = (int)v), CardHint("RatesHint"));
        var characters = Card("Characters",
            Number("MaxAp", null, s.MaxActionPoints, 0, 30, 1, "", v => s.MaxActionPoints = (int)v),
            Number("MaxMp", "CapHint", s.MaxMovementPoints, 0, 20, 1, "", v => s.MaxMovementPoints = (int)v),
            Toggle("NoEnergy", "NoEnergyHint", s.NoEnergyLoss, v => s.NoEnergyLoss = v),
            Toggle("Hardcore", "HardcoreHint", s.Hardcore, v => s.Hardcore = v));
        var startingBonuses = Card("StartingBonuses",
            Number("StartingKamas", "StartingKamasHint", s.StartingKamas, 0, 1_000_000_000L, 1, "", v => s.StartingKamas = (long)v),
            Number("StartingScrollStats", "StartingScrollStatsHint", s.StartingScrollStats, 0, 10_000, 1, "", v => s.StartingScrollStats = (int)v),
            Toggle("StartingAdventurerSet", "StartingAdventurerSetHint", s.StartingAdventurerSet, v => s.StartingAdventurerSet = v));
        var conquests = Card("Conquests",
            Number("Weakened", "WeakenedHint", s.PrismWeakenedHours, 0, 168, 1, "h", v => s.PrismWeakenedHours = (int)v),
            Number("ConquestMax", "ConquestMaxHint", s.ConquestMaxMinutes, 20, 1440, 10, "min", v => s.ConquestMaxMinutes = (int)v),
            Number("Invulnerable", "InvulnerableHint", s.ConquestInvulnerabilityHours, 0, 168, 1, "h", v => s.ConquestInvulnerabilityHours = (int)v),
            Number("Protection", "ProtectionHint", s.PrismProtectionMinutes, 0, 1440, 10, "min", v => s.PrismProtectionMinutes = (int)v));
        var welcome = new TextBox { Name = "WelcomeMessage", Text = s.WelcomeMessage, Watermark = T("WelcomePlaceholder"), MaxLength = 300 };
        welcome.TextChanged += (_, _) => { s.WelcomeMessage = welcome.Text ?? ""; Changed(); };
        var welcomeCard = Card("Welcome", CardHint("WelcomeHint"), welcome);
        var botsMax = Number("BotsMax", "BotsMaxHint", s.SimulatedPlayersMax, 1, Bots.SimulatedPlayers.Most, 5, "", v => s.SimulatedPlayersMax = (int)v);
        var bots = Card("Bots", Toggle("BotsOn", "BotsOnHint", s.SimulatedPlayers, v => { s.SimulatedPlayers = v; botsMax.IsEnabled = v; }), botsMax);
        botsMax.IsEnabled = s.SimulatedPlayers;
        var chance = Number("Chance", "ChanceHint", (decimal)Math.Clamp(double.IsFinite(s.RandomLootChancePercent) ? s.RandomLootChancePercent : 10, 0, 100),
            0, 100, 1, "%", v => s.RandomLootChancePercent = (double)v, 2);
        var rarities = Rarities();
        var levels = CardHint("LootLevels");
        void LootState(bool on) { chance.IsEnabled = on; rarities.IsEnabled = on; levels.IsEnabled = on; }
        var loot = Toggle("RandomLoot", "RandomLootHint", s.RandomLoot, v => { s.RandomLoot = v; LootState(v); });
        LootState(s.RandomLoot);
        var lootCard = Card("Loot", loot, chance, rarities, levels);
        var raids = Card("Raids",
            Number("RaidMin", "RaidMinHint", s.RaidMinPlayers, 0, 16, 1, "", v => s.RaidMinPlayers = (int)v),
            Number("Matchmaking", "MatchmakingHint", s.MatchmakingGroupSize, 0, 4, 1, "", v => s.MatchmakingGroupSize = (int)v));
        var world = Card("World",
            Number("CharacterSlots", "CharacterSlotsHint", s.CharacterSlots, 1, 20, 1, "", v => s.CharacterSlots = (int)v),
            Toggle("RequireZaap", "RequireZaapHint", s.RequireZaapDiscovery, v => s.RequireZaapDiscovery = v));
        foreach (var card in new[] { rates, lootCard, characters, conquests, raids, bots, welcomeCard, startingBonuses, world })
            _cards.Children.Add(card);
        var scroll = new ScrollViewer { Name = "SettingsScroll", Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) =>
        {
            _cards.ViewportHeight = Math.Max(0, scroll.Bounds.Height - _cards.Margin.Top - _cards.Margin.Bottom);
            _cards.InvalidateMeasure();
        };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);

        _actions = new ServerSettingsActions(language, Editor, settings =>
        {
            var current = (latest ?? ServerSettings.Load)();
            var next = settings.Copy();
            next.CopyModsFrom(current);
            next.WindowLanguage = current.WindowLanguage;
            Write(next);
        }, leave,
            () => _numbers.All(ValidNumber), embedded, "Settings");
        Grid.SetRow(_actions, 2);
        layout.Children.Add(_actions);
        Content = layout;
        ServerSettingsActions.AttachShortcuts(this, () => _actions);
    }

    private TextBlock Note(string key)
    {
        var note = ServerTheme.Text(T(key), 12, LauncherPalette.MutedGold);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    /// <summary>
    /// A card's explanation, which goes with the rows' hints: hidden when the cards are compacted to
    /// fit the window, and then the card's tooltip.
    /// </summary>
    private TextBlock CardHint(string key)
    {
        var note = Note(key);
        _cards.Hints.Add(note);
        return note;
    }

    private Border Card(string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 14 };
        var heading = ServerTheme.Text(T(title), 14, LauncherPalette.LightGold);
        heading.FontWeight = FontWeight.Bold; heading.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(heading);
        foreach (var child in children) stack.Children.Add(child);
        var card = ServerTheme.Card(stack);
        var tips = children.OfType<TextBlock>().Where(_cards.Hints.Contains).Select(t => t.Text).ToList();
        if (tips.Count > 0) ToolTip.SetTip(card, string.Join(Environment.NewLine, tips));
        return card;
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
            string explanation = T(hint);
            var note = ServerTheme.Text(explanation, 12, LauncherPalette.MutedGold);
            note.TextWrapping = TextWrapping.Wrap;
            labels.Children.Add(note);
            _cards.Hints.Add(note);
            ToolTip.SetTip(row, explanation);
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
        number.Text = number.Value.Value.ToString(Culture.NumberFormat);
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

    private void Changed() => _actions?.Refresh();

    private static bool ValidNumber(NumericUpDown number) => number.Value != null &&
        decimal.TryParse(number.Text, System.Globalization.NumberStyles.Number, number.NumberFormat, out var parsed) &&
        parsed >= number.Minimum && parsed <= number.Maximum &&
        (number.FormatString != "0" || parsed == decimal.Truncate(parsed));
}
