using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ServerSettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(1264, 713)]
    [InlineData(1904, 1001)]
    public void Maximized_every_card_fits_without_overlap(double width, double height)
    {
        WithWindow(width, height, window =>
        {
            var cards = Find<SettingsCardsPanel>(window, "SettingsCards");
            Assert.Equal(9, cards.Children.Count);
            Assert.All(cards.Children, card =>
            {
                Assert.True(card.Bounds.Bottom <= cards.ViewportHeight + 1,
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
    public void Bot_and_hunt_controls_edit_settings_and_follow_toggles(Language language)
    {
        var settings = new ServerSettings { SimulatedPlayers = false, HeadHunters = false };
        var window = new ServerSettingsWindow(language, settings, settings);
        try
        {
            window.Show();
            var botsMax = Find<NumericUpDown>(window, "BotsMax");
            var hunt = Find<NumericUpDown>(window, "HuntMinutes");
            var rest = Find<NumericUpDown>(window, "RestMinutes");
            Assert.False(botsMax.IsEffectivelyEnabled);
            Assert.False(hunt.IsEffectivelyEnabled);
            Assert.False(rest.IsEffectivelyEnabled);
            Find<ToggleSwitch>(window, "BotsOn").IsChecked = true;
            Find<ToggleSwitch>(window, "HuntersOn").IsChecked = true;
            Assert.True(botsMax.IsEffectivelyEnabled);
            Assert.True(hunt.IsEffectivelyEnabled);
            Assert.True(rest.IsEffectivelyEnabled);
            botsMax.Value = 25; hunt.Value = 30; rest.Value = 45;
            ServerSettings? saved = null;
            window.Editor.Save(value => saved = value.Copy());
            Assert.True(saved!.SimulatedPlayers);
            Assert.True(saved.HeadHunters);
            Assert.Equal(25, saved.SimulatedPlayersMax);
            Assert.Equal(30, saved.HeadHuntWindowMinutes);
            Assert.Equal(45, saved.HeadHuntRestMinutes);
            Assert.False(settings.SimulatedPlayers);
            Assert.False(settings.HeadHunters);
        }
        finally { window.Close(); }
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
