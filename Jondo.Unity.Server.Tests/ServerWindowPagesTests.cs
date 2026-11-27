using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.UI;
using Jondo.Unity.Server.UI.Pages;
using Jondo.Unity.Server.UI.Skin;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// Every page of the server window, driven through its controls with a fake server behind it:
/// nothing here writes the operator's settings, events or language, or touches real players.
/// </summary>
public sealed class ServerWindowPagesTests
{
    private sealed class FakeServer
    {
        internal bool Ready;
        internal bool Stopped;
        internal readonly List<ServerControl.Player> Players = new();
        internal readonly List<Guid> Kicked = new();
        internal readonly List<string> Announced = new();
        internal int Listeners = 3;
        internal EventSettings Events = new();
        internal int EventApplies;
        internal DateTime Now = new(2026, 12, 25, 12, 0, 0);
        internal ServerSettings Saved = new() { ExperienceBonusPercent = 20 };
        internal readonly List<Language> Languages = new();

        internal ServerWindowServices Services => new(
            () => Ready,
            () => Players.ToList(),
            session =>
            {
                Kicked.Add(session);
                return Players.RemoveAll(p => p.Session == session) > 0;
            },
            text => { Announced.Add(text); return Task.FromResult(Listeners); },
            () => Events,
            settings => { Events = settings; EventApplies++; },
            () => Now,
            () => Saved,
            () => Saved,
            Languages.Add);

        internal ServerWindow Window(Language language = Language.En) =>
            new(() => Stopped, () => Stopped = true, new ServerLog(), language, Services) { Width = 1280, Height = 760 };
    }

    private static string T(Language language, string key) => ServerSettingsTexts.Get(language, key);

    private static T Find<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Control root, string name)
        => Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Task Settle() => Dispatcher.UIThread.InvokeAsync(() => { }).GetTask();

    private static void Render(Window window)
    {
        window.UpdateLayout();
        using var image = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
        image.Render(window);
        int stride = image.PixelSize.Width * 4;
        var pixels = new byte[stride * image.PixelSize.Height];
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
        try
        {
            image.CopyPixels(new PixelRect(image.PixelSize), memory, pixels.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(memory, pixels, 0, pixels.Length);
            int bright = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] > 100 && pixels[i + 1] > 100 && pixels[i + 2] > 100) bright++;
            Assert.True(bright > 100, "The real Skia render must paint the page, not an empty surface.");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }

    [AvaloniaTheory]
    [InlineData(Language.Es)]
    [InlineData(Language.En)]
    [InlineData(Language.Fr)]
    public void Every_page_renders_in_every_language(Language language)
    {
        var server = new FakeServer { Ready = true };
        server.Players.Add(new ServerControl.Player(Guid.NewGuid(), "Alpha", 120, 154010883, false, false));
        var window = server.Window(language);
        try
        {
            window.Show();
            foreach (var page in Enum.GetValues<ServerWindow.Page>())
            {
                window.Show(page);
                Assert.Equal(page, window.Current);
                Render(window);
            }
            // A narrow window keeps the stop button inside it.
            window.Width = 980; window.Height = 600;
            window.UpdateLayout();
            var stop = Find<Button>(window, "StopServer");
            var position = stop.TranslatePoint(default, window)!.Value;
            Assert.True(position.Y >= 0 && position.Y + stop.Bounds.Height <= window.ClientSize.Height);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    /// <summary>
    /// A tool, not a check: a photo of every page in <c>capturas-servidor/</c> (git-ignored), to judge
    /// the design by eye. Asked for with --filter "FullyQualifiedName~A_photo_of_every_page".
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Language.En)]
    [InlineData(Language.Es)]
    public void A_photo_of_every_page(Language language)
    {
        var server = new FakeServer { Ready = true };
        server.Players.Add(new ServerControl.Player(Guid.NewGuid(), "Alpha", 200, 154010883, true, false));
        server.Players.Add(new ServerControl.Player(Guid.NewGuid(), "Bravo", 87, 191105026, false, false));
        server.Players.Add(new ServerControl.Player(Guid.NewGuid(), "Jondobot-Ab", 140, 68420097, false, true));
        var log = new ServerLog();
        string[] sample =
        {
            "[+] World loaded: 15 420 maps, 3 012 monsters",
            "[Zaap] Listening on 5555",
            "[Control] Seasonal events applied",
            "[Fight] Fight 12 started on map 191105026",
            "[!] A sample error line",
            "   41 [client>server] game.map.move (MapMovementRequest) cells=4 28 B",
            "   42 [server>client] game.map.moved (MapMovementEvent) 36 B",
        };
        log.Append(Enumerable.Range(1, 60).Select(i => new ConsoleLogBuffer.LogEntry(i, sample[i % sample.Length], "04:5" + (i % 10))));
        var window = new ServerWindow(() => server.Stopped, () => server.Stopped = true, log, language, server.Services)
            { Width = 1280, Height = 800 };
        string folder = Path.Combine(SolutionRoot(), "capturas-servidor");
        Directory.CreateDirectory(folder);
        try
        {
            window.Show();
            foreach (var page in new[] { "Console", "Players", "Events", "Settings", "Mods" })
            {
                Click(window, "Nav." + page);
                for (int i = 0; i < 30; i++) Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var image = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
                image.Render(window);
                image.Save(Path.Combine(folder, $"{page.ToLowerInvariant()}-{language.ToString().ToLowerInvariant()}.png"));
            }
        }
        finally { server.Stopped = true; window.Close(); }
    }

    private static string SolutionRoot()
    {
        string? root = Environment.GetEnvironmentVariable("JONDO_EMULATOR_ROOT");
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) return root;
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !folder.GetFiles("*.sln").Any() && !folder.GetFiles("*.slnx").Any()) folder = folder.Parent;
        return folder?.FullName ?? Path.GetTempPath();
    }

    [AvaloniaFact]
    public void Sidebar_switches_pages_and_waits_for_the_world_before_settings()
    {
        var server = new FakeServer();
        var window = server.Window();
        try
        {
            window.Show();
            Assert.Equal(ServerWindow.Page.Console, window.Current);
            Assert.True(window.Console.IsVisible);
            Assert.False(Find<Button>(window, "Nav.Settings").IsEnabled);
            Assert.Equal(T(Language.En, "Starting"), Find<TextBlock>(window, "ServerState").Text);

            Click(window, "Nav.Players");
            Assert.Equal(ServerWindow.Page.Players, window.Current);
            Assert.True(window.Players.IsVisible);
            Assert.False(window.Console.IsVisible);
            Click(window, "Nav.Events");
            Assert.True(window.Events.IsVisible);
            Assert.False(window.Players.IsVisible);

            // Settings read the database: before the world is ready, the page does not open.
            window.Show(ServerWindow.Page.Settings);
            Assert.Equal(ServerWindow.Page.Events, window.Current);
            Assert.Null(window.Settings.View);

            server.Ready = true;
            Click(window, "Lang.en"); // repaints the status at once, as the timer would
            Assert.True(Find<Button>(window, "Nav.Settings").IsEnabled);
            Assert.Equal(T(Language.En, "Online"), Find<TextBlock>(window, "ServerState").Text);
            Click(window, "Nav.Settings");
            Assert.Equal(ServerWindow.Page.Settings, window.Current);
            Assert.NotNull(window.Settings.View);
            Assert.True(window.Settings.IsVisible);

            // Ctrl+1 goes back to the console.
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D1, KeyModifiers = KeyModifiers.Control });
            Assert.Equal(ServerWindow.Page.Console, window.Current);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Language_buttons_translate_every_page_and_remember_the_choice()
    {
        var server = new FakeServer { Ready = true };
        var window = server.Window(Language.En);
        try
        {
            window.Show();
            Click(window, "Lang.fr");
            Assert.Equal(Language.Fr, window.Language);
            Assert.Equal(new[] { Language.Fr }, server.Languages);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == T(Language.Fr, "NavPlayers"));
            Assert.Equal(T(Language.Fr, "ModeOn"), Find<Button>(window, $"Mode.{SeasonalEvents.KeyOf(SeasonalEvents.Halouine)}.On").Content);
            Assert.Equal(T(Language.Fr, "Send"), window.Players.AnnounceSend.Content);
            Assert.Equal(T(Language.Fr, "LogFilter"), window.Console.Filter.Watermark);
            Click(window, "Lang.es");
            Assert.Equal(T(Language.Es, "ModeOff"), Find<Button>(window, $"Mode.{SeasonalEvents.KeyOf(SeasonalEvents.Halouine)}.Off").Content);
            Assert.Equal(new[] { Language.Fr, Language.Es }, server.Languages);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Event_modes_monsters_and_chance_apply_through_the_page()
    {
        var server = new FakeServer();
        var window = server.Window();
        try
        {
            window.Show();
            window.Show(ServerWindow.Page.Events);
            string halouine = SeasonalEvents.KeyOf(SeasonalEvents.Halouine);
            string kwismas = SeasonalEvents.KeyOf(SeasonalEvents.Kwismas);

            // On the 25th of December, Kwismas runs on its calendar and Halouine does not.
            Assert.True(Find<StatusDot>(window, "Dot." + kwismas).Online);
            Assert.False(Find<StatusDot>(window, "Dot." + halouine).Online);
            Assert.False(string.IsNullOrEmpty(Find<TextBlock>(window, "Calendar." + halouine).Text));

            Click(window, $"Mode.{halouine}.On");
            Assert.Equal(EventMode.On, server.Events.Halouine);
            Assert.True(Find<StatusDot>(window, "Dot." + halouine).Online);
            Assert.Equal(T(Language.En, "StateOn"), Find<TextBlock>(window, "State." + halouine).Text);
            Assert.Contains(T(Language.En, "Event." + halouine), window.Events.Status.Text);

            Click(window, $"Mode.{kwismas}.Off");
            Assert.Equal(EventMode.Off, server.Events.Kwismas);
            Assert.False(Find<StatusDot>(window, "Dot." + kwismas).Online);
            Click(window, $"Mode.{kwismas}.Auto");
            Assert.Equal(EventMode.Auto, server.Events.Kwismas);
            Assert.True(Find<StatusDot>(window, "Dot." + kwismas).Online);

            // The same mode again changes nothing.
            int applies = server.EventApplies;
            Click(window, $"Mode.{kwismas}.Auto");
            Assert.Equal(applies, server.EventApplies);

            var monsters = Find<ToggleSwitch>(window, "EventMonsters");
            var chance = Find<Slider>(window, "EventChance");
            monsters.IsChecked = false;
            Assert.False(server.Events.EventMonsters);
            Assert.False(chance.IsEnabled);
            monsters.IsChecked = true;
            Assert.True(server.Events.EventMonsters);
            Assert.True(chance.IsEnabled);

            // The slider waits until it rests before applying.
            chance.Value = 60;
            Assert.Equal(35, server.Events.EventMonsterChance);
            window.Events.Tick();
            Assert.Equal(60, chance.Value);
            window.Events.Flush();
            Assert.Equal(60, server.Events.EventMonsterChance);

            // Another window changing the events shows on the next tick.
            var outside = server.Events.Copy();
            outside.Vulkania = EventMode.On;
            server.Events = outside;
            window.Events.Tick();
            Assert.True(Find<StatusDot>(window, "Dot." + SeasonalEvents.KeyOf(SeasonalEvents.Vulkania)).Online);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Event_page_reports_a_failed_apply_and_keeps_the_settings()
    {
        var server = new FakeServer();
        var services = server.Services with { ApplyEvents = _ => throw new IOException("disk full") };
        var window = new ServerWindow(() => server.Stopped, () => server.Stopped = true, new ServerLog(), Language.En, services);
        try
        {
            window.Show();
            Click(window, $"Mode.{SeasonalEvents.KeyOf(SeasonalEvents.Pwak)}.On");
            Assert.Equal("disk full", window.Events.Status.Text);
            Assert.Equal(EventMode.Auto, server.Events.Pwak);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Players_page_lists_filters_kicks_and_announces()
    {
        var server = new FakeServer { Ready = true };
        var alpha = new ServerControl.Player(Guid.NewGuid(), "Alpha", 120, 154010883, false, false);
        var beta = new ServerControl.Player(Guid.NewGuid(), "Beta", 45, 88212746, true, false);
        var bot = new ServerControl.Player(Guid.NewGuid(), "Helper-One", 60, 154010883, false, true);
        server.Players.AddRange(new[] { alpha, beta, bot });
        var window = server.Window();
        try
        {
            window.Show();
            Click(window, "Nav.Players");
            var page = window.Players;
            Assert.Equal(2, page.Rows.Children.Count);
            Assert.Equal(string.Format(T(Language.En, "PlayersCount"), 2), Find<TextBlock>(window, "PlayerCount").Text);

            page.ShowBots.IsChecked = true;
            Assert.Equal(3, page.Rows.Children.Count);
            Assert.False(Find<Button>(window, "Kick." + bot.Session).IsEnabled);
            Assert.True(Find<Button>(window, "Kick." + alpha.Session).IsEnabled);

            page.Search.Text = "bet";
            Assert.Single(page.Rows.Children);
            page.Search.Text = "nobody";
            Assert.Empty(page.Rows.Children);
            Assert.True(Find<TextBlock>(window, "NoPlayers").IsVisible);
            page.Search.Text = "";
            Assert.False(Find<TextBlock>(window, "NoPlayers").IsVisible);

            // Kick asks first, with the window's own dialog; Cancel keeps the player.
            Click(window, "Kick." + alpha.Session);
            await Settle();
            var dialog = Assert.Single(window.OwnedWindows);
            Find<Button>(dialog, "CancelStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.Empty(server.Kicked);
            Click(window, "Kick." + alpha.Session);
            await Settle();
            dialog = Assert.Single(window.OwnedWindows);
            Find<Button>(dialog, "ConfirmStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.Equal(new[] { alpha.Session }, server.Kicked);
            Assert.DoesNotContain(page.Rows.GetLogicalDescendants().OfType<Button>(), b => b.Name == "Kick." + alpha.Session);

            // A player who left meanwhile is reported, not an error.
            page.Confirm = _ => Task.FromResult(true);
            await page.KickClicked(alpha);
            Assert.Contains(page.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == string.Format(T(Language.En, "KickGone"), "Alpha"));

            Assert.False(page.AnnounceSend.IsEnabled);
            page.AnnounceText.Text = "  ";
            Assert.False(page.AnnounceSend.IsEnabled);
            page.AnnounceText.Text = "Maintenance in ten minutes";
            Assert.True(page.AnnounceSend.IsEnabled);
            Click(window, "AnnounceSend");
            await Settle();
            Assert.Equal(new[] { "Maintenance in ten minutes" }, server.Announced);
            Assert.Equal(string.Format(T(Language.En, "Sent"), 3), page.AnnounceStatus.Text);
            Assert.Equal("", page.AnnounceText.Text);

            // With nobody in the world the text stays, to send later.
            server.Listeners = 0;
            page.AnnounceText.Text = "Anyone?";
            page.AnnounceText.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Settle();
            Assert.Equal(T(Language.En, "NobodyToTell"), page.AnnounceStatus.Text);
            Assert.Equal("Anyone?", page.AnnounceText.Text);
            Render(window);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Console_filter_keeps_matching_lines_and_follows_new_ones()
    {
        var server = new FakeServer();
        var log = new ServerLog();
        log.Append(Enumerable.Range(1, 500).Select(i => new ConsoleLogBuffer.LogEntry(i,
            i % 50 == 0 ? $"[Fight] needle {i}" : $"[Map] line {i}", "12:00:00")));
        var window = new ServerWindow(() => server.Stopped, () => server.Stopped = true, log, Language.En, server.Services);
        try
        {
            window.Show();
            var console = window.Console;
            console.Filter.Text = "NEEDLE";
            Assert.Equal(10, console.Shown.Count);
            Assert.Same(console.Shown, console.List.ItemsSource);
            log.Append(new[] { new ConsoleLogBuffer.LogEntry(501, "[Fight] needle late", "12:00:01"), new ConsoleLogBuffer.LogEntry(502, "[Map] other", "12:00:01") });
            console.Refilter(force: true);
            Assert.Equal(11, console.Shown.Count);
            Assert.EndsWith("needle late", console.Shown[^1].Text);
            console.ScrollToLatest();
            Render(window);

            console.Filter.Text = "";
            Assert.Equal(502, console.Shown.Count);
            Assert.Same(log.Lines, console.List.ItemsSource);

            Click(window, "ClearLog");
            Assert.Empty(log.Lines);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Settings_page_edits_saves_through_the_hook_and_cancels()
    {
        var server = new FakeServer { Ready = true };
        ServerSettings? written = null;
        var window = server.Window();
        window.Settings.Write = settings => written = settings;
        try
        {
            window.Show();
            Click(window, "Nav.Settings");
            var view = window.Settings.View!;
            var xp = Find<NumericUpDown>(window, "Xp");
            Assert.Equal(20, xp.Value);
            Assert.False(Find<Button>(window, "CloseSettings").IsVisible);
            xp.Value = 75;
            Assert.True(view.Editor.Dirty);
            Assert.True(Find<Button>(window, "CloseSettings").IsVisible);

            // Cancel throws the edit away: a fresh editor from the saved settings.
            Click(window, "CloseSettings");
            Assert.NotSame(view, window.Settings.View);
            Assert.Equal(20, Find<NumericUpDown>(window, "Xp").Value);
            Assert.Null(written);

            window.UpdateLayout();
            Find<NumericUpDown>(window, "Xp").Value = 90;
            Assert.True(Find<Button>(window, "SaveSettings").IsEnabled);
            Click(window, "SaveSettings");
            Assert.NotNull(written);
            Assert.Equal(90, written!.ExperienceBonusPercent);
            Assert.False(window.Settings.View!.Editor.Dirty);
            Assert.Equal(20, server.Saved.ExperienceBonusPercent);
            Render(window);

            // A clean editor follows the language; one with changes keeps them.
            Click(window, "Lang.fr");
            Assert.False(window.Settings.View!.Editor.Dirty);
            Find<NumericUpDown>(window, "Xp").Value = 10;
            var dirty = window.Settings.View;
            Click(window, "Lang.es");
            Assert.Same(dirty, window.Settings.View);
        }
        finally { server.Stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Settings_rows_never_cover_each_other_in_the_window()
    {
        var server = new FakeServer { Ready = true };
        var window = server.Window();
        try
        {
            window.Show();
            foreach (var (width, height) in new[] { (1280, 760), (980, 600), (1600, 900) })
            {
                window.Width = width; window.Height = height;
                Click(window, "Nav.Settings");
                window.UpdateLayout();
                var panel = window.Settings.GetLogicalDescendants().OfType<SettingsCardsPanel>().Single();
                // Each card's rows, top to bottom: a hint shown or hidden must move the rows below it.
                foreach (var stack in panel.Children.Select(c => ((Border)c).Child).OfType<StackPanel>())
                {
                    var rows = stack.Children.Where(c => c.IsVisible).ToList();
                    for (int i = 1; i < rows.Count; i++)
                        Assert.True(rows[i - 1].Bounds.Bottom <= rows[i].Bounds.Top + 0.5,
                            $"{width}x{height}: row {i} of a card starts at {rows[i].Bounds.Top}, above the end of the one before ({rows[i - 1].Bounds.Bottom})");
                }
                // A shown hint lies inside the labels it belongs to, not over the next row.
                foreach (var hint in panel.Hints.Where(h => h.IsVisible))
                    Assert.True(hint.Bounds.Bottom <= ((Control)hint.Parent!).Bounds.Height + 0.5,
                        $"{width}x{height}: the hint \"{hint.Text}\" runs past its row");
            }
        }
        finally { server.Stopped = true; window.Close(); }
    }
}
