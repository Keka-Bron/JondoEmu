using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using System.Text.Json;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ServerSettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(1264, 713)]
    [InlineData(1904, 1001)]
    public void Maximized_cards_fit_or_scroll_without_overlap(double width, double height)
    {
        WithWindow(width, height, window =>
        {
            var cards = Find<SettingsCardsPanel>(window, "SettingsCards");
            Assert.Equal(9, cards.Children.Count);
            // At full HD the cards fit; at 1280x720 the panel can scroll without overlap.
            bool fits = width > 1800;
            Assert.All(cards.Children, card =>
            {
                if (fits) Assert.True(card.Bounds.Bottom <= cards.ViewportHeight + 1,
                    $"Card ends at {card.Bounds.Bottom}, viewport {cards.ViewportHeight}, compact {cards.Compact}");
                Assert.True(card.Bounds.Right <= cards.Bounds.Width + 1);
                foreach (var other in cards.Children.Where(c => c != card))
                    Assert.False(card.Bounds.Intersects(other.Bounds));
            });
            if (width > 1800) Assert.False(cards.Compact);
        });
    }

    [AvaloniaFact]
    public void Narrow_window_scrolls_one_column_with_inline_hints()
    {
        WithWindow(520, 500, window =>
        {
            var cards = Find<SettingsCardsPanel>(window, "SettingsCards");
            Assert.Single(cards.Children.Select(c => c.Bounds.X).Distinct());
            Assert.True(cards.Children.Max(c => c.Bounds.Bottom) > cards.ViewportHeight);
            Assert.False(cards.Compact);
            Assert.All(cards.Hints, hint => Assert.True(hint.IsVisible));
            var scroll = Find<ScrollViewer>(window, "SettingsScroll");
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
        });
    }

    [AvaloniaTheory]
    [InlineData(Language.Es)]
    [InlineData(Language.En)]
    [InlineData(Language.Fr)]
    public void Bot_controls_edit_settings_and_follow_toggle(Language language)
    {
        var settings = new ServerSettings { SimulatedPlayers = false };
        var view = new ServerSettingsView(language, settings, settings, () => { });
        var window = new Window { Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var botsMax = Find<NumericUpDown>(window, "BotsMax");
            Assert.False(botsMax.IsEffectivelyEnabled);
            Find<ToggleSwitch>(window, "BotsOn").IsChecked = true;
            Assert.True(botsMax.IsEffectivelyEnabled);
            botsMax.Value = 25;

            ServerSettings? saved = null;
            view.Editor.Save(value => saved = value.Copy());
            Assert.True(saved!.SimulatedPlayers);
            Assert.Equal(25, saved.SimulatedPlayersMax);
            Assert.False(settings.SimulatedPlayers);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Unknown_settings_and_empty_client_mod_order_round_trip()
    {
        var settings = ServerSettings.Read("""
            {"LegacyToggle":false,"Fixture.Package":{"Delay":3,"Custom":["future value"]}}
            """);
        Assert.Equal(["JondoFix.dll"], settings.ClientMods);

        var next = settings.Copy();
        next.ClientMods = [];
        next.ModSettings["fixture.package"] = JsonSerializer.SerializeToElement(new { Delay = 4 });
        Assert.Equal(3, settings.ModSettings["fixture.package"].GetProperty("Delay").GetInt32());
        Assert.Equal("future value", settings.ModSettings["fixture.package"].GetProperty("Custom")[0].GetString());

        var read = ServerSettings.Read(JsonSerializer.Serialize(next));
        Assert.Empty(read.ClientMods);
        Assert.False(read.ModSettings["legacytoggle"].GetBoolean());
        Assert.Equal(4, read.ModSettings["fixture.package"].GetProperty("Delay").GetInt32());
    }

    private static void WithWindow(double width, double height, Action<Window> check)
    {
        var settings = new ServerSettings();
        var settingsWindow = new ServerSettingsWindow(Language.Es, settings, settings);
        var content = settingsWindow.Content;
        settingsWindow.Content = null;
        // Exercise the real content at an exact client size, independently of monitor clamping.
        var window = new Window { Width = width, Height = height, Content = content };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            check(window);
        }
        finally { window.Close(); settingsWindow.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
}
