using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Jondo.Unity.Server.Tests.ServerDesktopTests))]

namespace Jondo.Unity.Server.Tests;

public sealed class ServerDesktopTests
{
    // Use the production theme with a real Skia renderer, without starting world services.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DesktopTestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public sealed class DesktopTestApp : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }
    }

    [AvaloniaTheory]
    [InlineData(Language.Es)]
    [InlineData(Language.En)]
    [InlineData(Language.Fr)]
    public void Settings_render_and_edit_without_changing_running_rules(Language language)
    {
        var running = new ServerSettings { ExperienceBonusPercent = 20 };
        var window = new ServerSettingsWindow(language, running, running) { Width = 1000, Height = 750 };
        try
        {
            window.Show();
            var experience = Find<NumericUpDown>(window, "Xp");
            Assert.Equal(20, experience.Value);
            Assert.False(Find<Button>(window, "SaveSettings").IsEnabled);
            experience.Value = 80;
            Assert.Equal(80, window.Editor.Edited.ExperienceBonusPercent);
            Assert.Equal(20, running.ExperienceBonusPercent);
            Assert.True(Find<Button>(window, "SaveSettings").IsEnabled);
            Assert.True(Find<Button>(window, "RestartServer").IsEnabled);
            Assert.NotNull(window.Icon);

            // Real rendering catches template/font/native-Skia failures that a build cannot.
            using var image = new RenderTargetBitmap(new PixelSize(1000, 750));
            image.Render(window);
            AssertPainted(image);
            window.Width = 640;
            window.Height = 480;
            window.UpdateLayout();
            var restart = Find<Button>(window, "RestartServer");
            var position = restart.TranslatePoint(default, window)!.Value;
            Assert.True(position.Y >= 0 && position.Y + restart.Bounds.Height <= window.ClientSize.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Numeric_validation_and_loot_dependencies_follow_the_controls()
    {
        var defaults = new ServerSettings();
        var window = new ServerSettingsWindow(Language.En, defaults, defaults);
        try
        {
            window.Show();
            var experience = Find<NumericUpDown>(window, "Xp");
            experience.Value = 50;
            experience.Text = "not a number";
            Assert.False(Find<Button>(window, "SaveSettings").IsEnabled);
            Assert.False(Find<Button>(window, "RestartServer").IsEnabled);
            experience.Text = "60";
            Assert.True(Find<Button>(window, "SaveSettings").IsEnabled);
            var chance = Find<NumericUpDown>(window, "Chance");
            Assert.False(chance.IsEffectivelyEnabled);
            Find<ToggleSwitch>(window, "RandomLoot").IsChecked = true;
            Assert.True(chance.IsEffectivelyEnabled);
            chance.Value = 12.5m;
            Assert.Equal(12.5, window.Editor.Edited.RandomLootChancePercent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Restart_requires_confirmation_and_back_retains_edits()
    {
        var defaults = new ServerSettings();
        var window = new ServerSettingsWindow(Language.En, defaults, defaults);
        try
        {
            window.Show();
            Find<NumericUpDown>(window, "MaxAp").Value = 12;
            var restart = Find<Button>(window, "RestartServer");
            restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("YES, RESTART", restart.Content);
            Assert.False(Find<Button>(window, "SaveSettings").IsVisible);
            Assert.False(ServerRestart.Counting);
            Find<Button>(window, "CloseSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("SAVE AND RESTART", restart.Content);
            Assert.True(window.IsVisible);
            Assert.Equal(12, window.Editor.Edited.MaxActionPoints);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Dashboard_close_can_be_cancelled_and_stop_waits_for_confirmation()
    {
        bool stopped = false;
        var log = new ServerLog();
        log.Append(Enumerable.Range(1, 2000).Select(i => new ConsoleLogBuffer.LogEntry(i,
            $"{i} [server>client] abc (ExampleEvent) {{ 1: 453 }} 3 B", "12:34:56")));
        var window = new ServerWindow(() => stopped, () => stopped = true, log, Language.En);
        try
        {
            window.Show();
            using var image = new RenderTargetBitmap(new PixelSize(1280, 760));
            image.Render(window);
            AssertPainted(image);
            int realized = Find<ListBox>(window, "ServerLog").GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.InRange(realized, 1, 199);
            window.Close();
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.False(stopped);
            Find<Button>(dialog, "CancelStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(window.IsVisible);
            Assert.False(stopped);
            Find<Button>(window, "StopServer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = Assert.Single(window.OwnedWindows);
            Find<Button>(dialog, "ConfirmStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(stopped);
            Assert.False(Find<Button>(window, "StopServer").IsEnabled);
        }
        finally { stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Log_template_handles_null_data_and_recycling_during_scroll_trim_clear_and_refill()
    {
        bool stopped = false;
        var log = new ServerLog();
        var window = new ServerWindow(() => stopped, () => stopped = true, log, Language.En);
        try
        {
            window.Show();
            var list = Find<ListBox>(window, "ServerLog");
            // Avalonia clears container content during recycling and can build the template with null.
            Assert.Null(list.ItemTemplate!.Build(null));

            void Render()
            {
                window.UpdateLayout();
                using var image = new RenderTargetBitmap(new PixelSize(1280, 760));
                image.Render(window);
                AssertPainted(image);
            }
            static string RowText(TextBlock text) => string.Concat(text.Inlines!
                .OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text));

            log.Append(Enumerable.Range(1, ServerLog.Capacity).Select(i => new ConsoleLogBuffer.LogEntry(i,
                $"{i} [server>client] abc (ExampleEvent) {{ 1: 453 }} 3 B", "12:34:56")));
            Render();
            foreach (int index in new[] { 3999, 0, 2000, 100, 3900 })
            {
                string expected = string.Concat(log.Lines[index].Pieces.Select(piece => piece.Text));
                list.ScrollIntoView(log.Lines[index]);
                Render();
                Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(),
                    text => RowText(text) == expected);
            }

            // Evict rows that already have realized containers, then recycle them by scrolling.
            log.Append(Enumerable.Range(4001, 300).Select(i => new ConsoleLogBuffer.LogEntry(i, $"New entry {i}", "12:34:57")));
            Assert.Equal(ServerLog.Capacity, log.Lines.Count);
            window.ScrollLogToLatest();
            Render();
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(),
                text => RowText(text) == "12:34:57  New entry 4300");
            log.Clear();
            Render();
            Assert.Empty(list.GetRealizedContainers());
            log.Append(new[] { new ConsoleLogBuffer.LogEntry(4301, "[+] Refilled after clear", "12:34:58") });
            Render();
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(),
                text => RowText(text).Contains("Refilled after clear"));
        }
        finally { stopped = true; window.Close(); }
    }

    [AvaloniaFact]
    public void Embedded_assets_are_available_without_loose_artwork_files()
    {
        using var background = AssetLoader.Open(new Uri("avares://Jondo Server/Assets/servidor_fondo.jpg"));
        using var bitmap = new Bitmap(background);
        Assert.True(bitmap.PixelSize.Width > 100);
        using var icon = AssetLoader.Open(new Uri("avares://Jondo Server/Assets/icono_servidor.ico"));
        Assert.NotNull(new WindowIcon(icon));
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static void AssertPainted(RenderTargetBitmap image)
    {
        int stride = image.PixelSize.Width * 4;
        var pixels = new byte[stride * image.PixelSize.Height];
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
        try
        {
            image.CopyPixels(new PixelRect(image.PixelSize), memory, pixels.Length, stride);
            System.Runtime.InteropServices.Marshal.Copy(memory, pixels, 0, pixels.Length);
            int brightPixels = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] > 100 && pixels[i + 1] > 100 && pixels[i + 2] > 100) brightPixels++;
            Assert.True(brightPixels > 100, "The real Skia render must paint text, not an empty surface.");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }
}
