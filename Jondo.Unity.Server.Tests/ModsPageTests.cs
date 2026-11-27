using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.UI;
using Jondo.Unity.Server.UI.Pages;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ModsPageTests
{
    [AvaloniaFact]
    public void Package_activation_order_and_dialog_changes_are_staged_until_save()
    {
        var schema = new ServerModConfiguration("sample", "Sample", "Enabled",
        [
            new ServerModSetting("Enabled", JsonSerializer.SerializeToElement(true), null, null, "LegacyToggle", 0, 0, 0, null),
            new ServerModSetting("Minutes", JsonSerializer.SerializeToElement(15), null, null, null, 1, 120, 5, "min"),
            new ServerModSetting("CustomOption", JsonSerializer.SerializeToElement(false), null, null, null, 0, 0, 0, null),
        ]);
        var running = ServerSettings.Read("""
            {"ServerMods":["sample","other"],"ClientMods":["Sample.dll","JondoFix.dll","Missing.dll"],
              "LegacyToggle":false,"uninstalled":{"Enabled":false,"CustomValue":3}}
            """);
        var latest = running.Copy();
        var page = new ModsPage(Language.En, () => latest, () => running,
            () => [new("sample", schema.Name, "1.2.3", "Sample.dll", schema), new("other", "Other", "2.0.0", null, null),
                new("LooseServer.dll", null, null, null, null, IsStandalone: true)],
            () => ["Sample.dll", "JondoFix.dll", "Extra.dll", "LooseServer.dll"])
        { Write = settings => latest = settings };
        var window = new Window { Content = page };
        try
        {
            page.EnsureBuilt();
            window.Show();
            Assert.Contains(page.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "1.2.3");
            Assert.DoesNotContain(page.GetLogicalDescendants(), control => control is ToggleSwitch { Name: "Active.Client.Sample.dll" });
            Assert.DoesNotContain(page.GetLogicalDescendants(), control => control is ToggleSwitch { Name: "Active.Client.LooseServer.dll" });
            Assert.True(Find<ToggleSwitch>(page, "Active.Server.LooseServer.dll").IsEnabled);
            Assert.False(Find<ToggleSwitch>(page, "Active.Server.sample").IsChecked);
            Assert.False(Find<Button>(page, "Settings.Server.other").IsEnabled);
            Find<ToggleSwitch>(page, "Active.Server.sample").IsChecked = true;
            Assert.Equal(["other", "sample"], page.Editor!.Edited.ServerMods);
            Click(page, "Up.Server.sample");
            Assert.Equal(["sample", "other"], page.Editor.Edited.ServerMods);
            Find<ToggleSwitch>(page, "Active.Client.JondoFix.dll").IsChecked = false;
            Find<ToggleSwitch>(page, "Active.Client.Extra.dll").IsChecked = true;
            Click(page, "Up.Client.Extra.dll");

            Click(page, "Settings.Server.sample");
            var dialog = Assert.IsType<ModSettingsDialog>(Assert.Single(window.OwnedWindows));
            Assert.DoesNotContain(dialog.GetLogicalDescendants(), control => control is ToggleSwitch { Name: "sample_Enabled" });
            Find<NumericUpDown>(dialog, "sample_Minutes").Value = 25;
            Click(dialog, "CancelModSettings");
            Assert.Equal(15, page.Editor.Edited.ModSettings["sample"].GetProperty("Minutes").GetInt32());

            Click(page, "Settings.Server.sample");
            dialog = Assert.IsType<ModSettingsDialog>(Assert.Single(window.OwnedWindows));
            var minutes = Find<NumericUpDown>(dialog, "sample_Minutes");
            minutes.Text = "121";
            Assert.False(Find<Button>(dialog, "ApplyModSettings").IsEnabled);
            minutes.Text = "30";
            Find<ToggleSwitch>(dialog, "sample_CustomOption").IsChecked = true;
            Click(dialog, "ApplyModSettings");
            Assert.False(latest.ModSettings.ContainsKey("sample"));
            Assert.False(page.Editor.Pending);

            latest.ExperienceBonusPercent = 55;
            Click(page, "SaveMods");
            Assert.False(page.Editor.Dirty);
            Assert.True(page.Editor.Pending);
            Assert.True(Find<Button>(page, "RestartMods").IsEnabled);
            Assert.Equal(55, latest.ExperienceBonusPercent);
            Assert.Equal(["Extra.dll", "Missing.dll"], latest.ClientMods);
            Assert.Equal(["sample", "other"], latest.ServerMods);
            Assert.True(latest.ModSettings["sample"].GetProperty("Enabled").GetBoolean());
            Assert.True(latest.ModSettings["sample"].GetProperty("CustomOption").GetBoolean());
            Assert.Equal(30, latest.ModSettings["sample"].GetProperty("Minutes").GetInt32());
            Assert.True(JsonElement.DeepEquals(running.ModSettings["uninstalled"], latest.ModSettings["uninstalled"]));

            Click(page, "RestartMods");
            Assert.Equal("YES, RESTART", Find<Button>(page, "RestartMods").Content);
            Click(page, "CloseMods");
            Assert.Equal("SAVE AND RESTART", Find<Button>(page, "RestartMods").Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Saving_general_settings_preserves_mods_saved_while_the_form_was_open()
    {
        var running = new ServerSettings();
        var latest = running.Copy();
        var page = new SettingsPage(Language.En, () => latest, () => running) { Write = settings => latest = settings };
        var window = new Window { Content = page };
        try
        {
            page.EnsureBuilt();
            window.Show();
            Find<NumericUpDown>(page, "Xp").Value = 25;
            latest.ServerMods = ["sample"];
            latest.ClientMods = [];
            latest.ModSettings["sample"] = JsonSerializer.SerializeToElement(new { Minutes = 30 });
            Click(page, "SaveSettings");
            Assert.False(page.View!.Editor.Dirty);
            Assert.Equal(25, latest.ExperienceBonusPercent);
            Assert.Equal(["sample"], latest.ServerMods);
            Assert.Empty(latest.ClientMods);
            Assert.Equal(30, latest.ModSettings["sample"].GetProperty("Minutes").GetInt32());
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(Control root, string name) =>
        Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
