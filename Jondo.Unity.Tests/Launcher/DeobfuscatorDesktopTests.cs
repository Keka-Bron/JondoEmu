using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Jondo.Unity.Deobfuscator;
using Jondo.Unity.Deobfuscator.UI;
using Jondo.Unity.Launcher.Security;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public sealed class DeobfuscatorDesktopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo-desktop-" + Guid.NewGuid().ToString("N"));
    public DeobfuscatorDesktopTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private Settings Preferences(Language language = Language.En) => new(Path.Combine(_root, "preferences.cfg"))
    { Language = language };

    private static T Named<T>(Window window, string name) where T : Control
        => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    [Fact]
    public void Browsing_starts_at_the_current_directory_or_dll_parent_then_falls_back_to_the_client()
    {
        string client = Directory.CreateDirectory(Path.Combine(_root, "client é")).FullName;
        string dump = Directory.CreateDirectory(Path.Combine(_root, "old dump")).FullName;
        string dll = Path.Combine(dump, "Ankama.Dofus.Protocol.Game.dll");
        File.WriteAllText(dll, "");
        Assert.Equal(dump, MapperWindow.PickerStartDirectory(" " + dump + " ", client));
        Assert.Equal(dump, MapperWindow.PickerStartDirectory(dll, client));
        Assert.Equal(client, MapperWindow.PickerStartDirectory("missing", client));
        Assert.Equal(client, MapperWindow.PickerStartDirectory(null, client));
        Assert.Null(MapperWindow.PickerStartDirectory("missing", "also missing"));
    }

    [AvaloniaTheory]
    [InlineData(Language.Es)]
    [InlineData(Language.En)]
    [InlineData(Language.Fr)]
    public async Task Full_catalogue_can_be_browsed_without_erasing_a_custom_model_and_provider_switches_clear_it(Language language)
    {
        var settings = Preferences(language);
        settings.Model = "custom-model";
        var window = new ModelDialog(settings);
        window.Show();
        try
        {
            var model = Named<TextBox>(window, "ProviderModel");
            var catalogue = Named<ComboBox>(window, "ProviderCatalogue");
            var key = Named<TextBox>(window, "ProviderKey");
            key.Text = "provider-one-key";
            await window.TestAsync(_ => Task.FromResult<IReadOnlyList<string>>(new[] { "z-model", "a-model" }));
            Assert.Equal("custom-model", model.Text);
            Assert.Equal(new[] { "a-model", "z-model" }, catalogue.Items.Cast<string>());
            Assert.True(catalogue.IsEnabled);
            catalogue.IsDropDownOpen = true;
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            catalogue.SelectedIndex = 1;
            Assert.Equal("z-model", model.Text);
            model.Text = "another-custom-model";
            Assert.Null(catalogue.SelectedItem);
            Assert.Equal(2, catalogue.ItemCount);
            catalogue.IsDropDownOpen = false;

            var provider = window.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Tag is Provider p && p.Name == "Ollama");
            provider.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(catalogue.Items);
            Assert.False(catalogue.IsEnabled);
            Assert.False(key.IsVisible);
            Assert.Equal("another-custom-model", model.Text);
            Assert.Equal("Ollama", settings.ProviderName);
        }
        finally { window.Close(); }
        var saved = Settings.Load(Path.Combine(_root, "preferences.cfg"));
        Assert.Equal(language, saved.Language);
        Assert.Equal("another-custom-model", saved.Model);
        saved.Use(Provider.All[0]);
        Assert.Equal("provider-one-key", saved.Key);
    }

    [AvaloniaFact]
    public async Task Closing_the_model_dialog_cancels_and_ignores_a_late_catalogue_response()
    {
        var settings = Preferences();
        settings.Model = "custom-model";
        var window = new ModelDialog(settings);
        var finish = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        window.Show();
        var testing = window.TestAsync(token => { requestToken = token; return finish.Task; });
        try
        {
            Assert.False(Named<TextBox>(window, "ProviderUrl").IsEnabled);
            window.Close();
            Assert.True(requestToken.IsCancellationRequested);
        }
        finally
        {
            finish.TrySetResult(new[] { "late-model" });
            await testing.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
        }
        Assert.Empty(Named<ComboBox>(window, "ProviderCatalogue").Items);
        Assert.Equal("custom-model", Settings.Load(Path.Combine(_root, "preferences.cfg")).Model);
    }

    [AvaloniaFact]
    public async Task Cancel_stops_work_restores_controls_and_close_waits_for_cleanup()
    {
        var window = new MapperWindow(Preferences());
        window.Show();
        try
        {
            bool cleanedUp = false;
            Task Run() => window.WorkAsync(async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cleanedUp = true; }
            });
            var work = Run();
            var button = Named<Button>(window, "BuildMapping");
            Assert.False(Named<TextBox>(window, "OldProtocolPath").IsEnabled);
            Assert.Equal(Texts.Get(Language.En).Cancel, button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(cleanedUp);
            Assert.True(window.IsVisible);
            Assert.True(Named<TextBox>(window, "OldProtocolPath").IsEnabled);
            Assert.Equal(Texts.Get(Language.En).MapRun, button.Content);

            cleanedUp = false;
            bool closedAfterCleanup = false;
            window.Closed += (_, _) => closedAfterCleanup = cleanedUp;
            work = Run();
            window.Close();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(closedAfterCleanup);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(Language.Es)]
    [InlineData(Language.En)]
    [InlineData(Language.Fr)]
    public void Results_render_localized_origins_evidence_and_emulator_priority(Language language)
    {
        var window = new MapperWindow(Preferences(language)) { WindowState = WindowState.Normal, Width = 1180, Height = 780 };
        var rows = new[]
        {
            new Mapper.Row { Old = "aaa", New = "bbb", How = Mapper.How.Structure, Name = "KnownMessage" },
            new Mapper.Row { Old = "ccc", New = "ddd", How = Mapper.How.Structure, Mine = true },
            new Mapper.Row { Old = "eee", How = Mapper.How.Doubt, Mine = true, Meaning = "Needs a decision",
                Candidates = { "fff", "ggg" } },
            new Mapper.Row { Old = "hhh", New = "iii", How = Mapper.How.Model, Because = "Model evidence" },
            new Mapper.Row { Old = "jjj", How = Mapper.How.Gone },
        };
        window.Fill(rows);
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var list = Named<ListBox>(window, "MappingResults");
            Assert.Equal(new[] { "eee", "ccc", "hhh", "aaa" }, list.Items.Cast<Mapper.Row>().Select(row => row.Old));
            var texts = Texts.Get(language);
            var labels = list.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Contains(labels, text => text.Text == string.Format(texts.MapDoubtFormat, 2));
            Assert.Contains(labels, text => text.Text == texts.MapByStructure && Equals(text.Foreground, DesktopTheme.Green));
            Assert.Contains(labels, text => text.Text == texts.MapByModel && Equals(text.Foreground, DesktopTheme.Gold));
            Assert.Contains(list.GetVisualDescendants().OfType<Border>(), border =>
                ToolTip.GetTip(border) is string tip && tip.Contains("Model evidence"));
            Assert.Contains(list.GetVisualDescendants().OfType<Border>(), border =>
                ToolTip.GetTip(border) is string tip && tip.Contains("fff, ggg") && border.BorderThickness.Left == 3);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Legacy_preferences_keep_paths_language_dialect_and_provider_keys()
    {
        string path = Path.Combine(_root, "preferences.cfg");
        string guarded = SecretStore.Protect("legacy-key");
        // The former Windows UI used unprefixed DPAPI, never unencrypted Base64.
        if (OperatingSystem.IsWindows()) guarded = guarded["dpapi:".Length..];
        File.WriteAllLines(path, new[] { "proveedor=Gemini", "url=https://example.test", "modelo=custom",
            "dialecto=gemini", "a.la.vez=7", "idioma=fr", "cliente=new-client", "protocolo.viejo=old.dll",
            "paso=8", "clave.Gemini=" + guarded });
        var settings = Settings.Load(path);
        Assert.Equal("legacy-key", settings.Key);
        Assert.Equal(Language.Fr, settings.Language);
        Assert.Equal(Llm.Dialect.Gemini, settings.Dialect);
        Assert.Equal(7, settings.AtOnce);
        Assert.Equal("old.dll", settings.OldProtocolDll);
        Assert.Equal("new-client", settings.ClientFolder);
        settings.Save();
        var loaded = Settings.Load(path);
        Assert.Equal("https://example.test", loaded.Url);
        Assert.Equal("custom", loaded.Model);
        Assert.Equal("legacy-key", loaded.Key);
        Assert.DoesNotContain("legacy-key", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public unsafe void The_restored_logo_paints_visible_pixels()
    {
        var window = new Window { Width = 500, Height = 100, Background = Brushes.Black, Content = new DesktopTheme.Logo() };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var pixels = frame!.Lock();
            int visible = 0;
            for (int y = 0; y < pixels.Size.Height; y++)
            {
                byte* row = (byte*)pixels.Address + y * pixels.RowBytes;
                for (int x = 0; x < pixels.Size.Width; x++)
                    if (row[x * 4] > 24 || row[x * 4 + 1] > 24 || row[x * 4 + 2] > 24) visible++;
            }
            Assert.True(visible > 200, "The logo must draw its gold lettering, not just render without throwing.");
        }
        finally { window.Close(); }
    }
}
