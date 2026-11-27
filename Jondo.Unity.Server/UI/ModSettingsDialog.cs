using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Mods;

namespace Jondo.Unity.Server.UI;

internal sealed class ModSettingsDialog : Window
{
    internal static bool HasFields(ServerModConfiguration schema) => EditableFields(schema).Any();

    internal ModSettingsDialog(Language language, string name, ServerModConfiguration schema, ServerSettings edited)
    {
        Title = name;
        Width = 560;
        MinWidth = 380;
        MaxHeight = 650;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ServerTheme.Window(this);
        string T(string key) => ServerSettingsTexts.Get(language, key);
        string? Localized(LocalizedModText? text) => language switch
        {
            Language.En => text?.En ?? text?.Es,
            Language.Fr => text?.Fr ?? text?.En ?? text?.Es,
            _ => text?.Es ?? text?.En,
        };

        var values = ServerModConfiguration.Resolve(edited, schema.Id, schema);
        var fields = new StackPanel { Spacing = 16, Margin = new Thickness(24, 20) };
        var numbers = new List<NumericUpDown>();
        var applyFields = new List<Action>();
        var cancel = ServerTheme.Button(T("Cancel"), () => Close(false));
        cancel.Name = "CancelModSettings";
        var apply = ServerTheme.Button(T("Apply"), () =>
        {
            if (!numbers.All(n => TryRead(n, out _))) return;
            foreach (var update in applyFields) update();
            Close(true);
        });
        apply.Name = "ApplyModSettings";
        apply.IsDefault = true;
        var culture = CultureInfo.GetCultureInfo(language switch
        {
            Language.En => "en-GB",
            Language.Fr => "fr-FR",
            _ => "es-ES",
        });
        foreach (var field in EditableFields(schema))
        {
            if (!values.TryGetProperty(field.Key, out var value)) continue;
            string label = Localized(field.Label) ?? field.Key;
            Control input;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                bool original = value.GetBoolean();
                var toggle = new ToggleSwitch { IsChecked = original, OnContent = "", OffContent = "" };
                applyFields.Add(() =>
                {
                    bool current = toggle.IsChecked == true;
                    if (current != original) ServerModConfiguration.Set(edited, schema, field.Key, current);
                });
                input = toggle;
            }
            else
            {
                int original = value.GetInt32();
                int displayed = Math.Clamp(original, field.Minimum, field.Maximum);
                var number = new NumericUpDown
                {
                    Value = displayed, Minimum = field.Minimum, Maximum = field.Maximum, Increment = field.Step,
                    FormatString = "0", NumberFormat = culture.NumberFormat, Width = 114,
                    Text = displayed.ToString(culture),
                };
                numbers.Add(number);
                number.PropertyChanged += (_, e) =>
                {
                    if (e.Property == NumericUpDown.TextProperty) apply.IsEnabled = numbers.All(n => TryRead(n, out int _));
                };
                applyFields.Add(() =>
                {
                    if (TryRead(number, out int current) && current != displayed)
                        ServerModConfiguration.Set(edited, schema, field.Key, current);
                });
                input = number;
            }
            input.Name = $"{schema.Id}_{field.Key}";
            input.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(input, label);
            var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var title = ServerTheme.Text(label);
            title.TextWrapping = TextWrapping.Wrap;
            labels.Children.Add(title);
            if (Localized(field.Hint) is { } hint)
            {
                var description = ServerTheme.Text(hint, 12, LauncherPalette.MutedGold);
                description.TextWrapping = TextWrapping.Wrap;
                labels.Children.Add(description);
            }
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            controls.Children.Add(input);
            if (!string.IsNullOrEmpty(field.Unit)) controls.Children.Add(ServerTheme.Text(field.Unit, 12, LauncherPalette.MutedGold));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
            Grid.SetColumn(controls, 2);
            row.Children.Add(labels);
            row.Children.Add(controls);
            fields.Children.Add(row);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(24, 0, 24, 18),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        Content = layout;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
    }

    private static IEnumerable<ServerModSetting> EditableFields(ServerModConfiguration schema) => schema.Fields.Where(field =>
        !string.Equals(field.Key, schema.ActivationSetting, StringComparison.OrdinalIgnoreCase) &&
        (field.Default.ValueKind is JsonValueKind.True or JsonValueKind.False ||
         (field.Default.ValueKind == JsonValueKind.Number && field.Default.TryGetInt32(out _) &&
          field.Minimum <= field.Maximum && field.Step > 0)));

    private static bool TryRead(NumericUpDown number, out int value) =>
        int.TryParse(number.Text, NumberStyles.Integer | NumberStyles.AllowThousands, number.NumberFormat, out value) &&
        value >= number.Minimum && value <= number.Maximum;
}
