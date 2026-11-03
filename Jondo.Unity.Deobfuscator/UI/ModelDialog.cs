using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator.UI;

internal sealed class ModelDialog : Window
{
    private readonly Settings _settings;
    private readonly TextBox _url;
    private readonly TextBox _model;
    private readonly ComboBox _catalogue;
    private readonly TextBox _key;
    private readonly NumericUpDown _atOnce;
    private readonly Button _test;
    private readonly TextBlock _keyLabel;
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _verdict = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> _providers = new();
    private CancellationTokenSource? _testing;
    private Texts Texts => Texts.Get(_settings.Language);

    public ModelDialog(Settings settings)
    {
        _settings = settings;
        DesktopTheme.Apply(this);
        Title = Texts.ModelTitle; Width = 900; Height = 610; MinWidth = 560; MinHeight = 400;
        _url = new TextBox { Name = "ProviderUrl", Text = settings.Url };
        _model = new TextBox { Name = "ProviderModel", Text = settings.Model };
        _catalogue = new ComboBox { Name = "ProviderCatalogue", PlaceholderText = Texts.ModelCatalogue,
            HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        _catalogue.SelectionChanged += (_, _) =>
        {
            if (_catalogue.SelectedItem is string model) _model.Text = model;
        };
        _model.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
                _catalogue.SelectedItem = _catalogue.Items.Cast<string>().FirstOrDefault(name => name == _model.Text);
        };
        _key = new TextBox { Name = "ProviderKey", Text = settings.Key, PasswordChar = '•' };
        _atOnce = new NumericUpDown { Minimum = 1, Maximum = 32, Value = Math.Clamp(settings.AtOnce, 1, 32) };
        _keyLabel = DesktopTheme.Caption(Texts.ModelKey);
        _test = DesktopTheme.Button(Texts.ModelTest, primary: true);
        _test.Click += async (_, _) => await TestAsync();
        var close = DesktopTheme.Button(Texts.Close);
        close.Click += (_, _) => Close();

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        panel.Children.Add(DesktopTheme.Caption(Texts.ModelProvider));
        var providers = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in Provider.All)
        {
            var button = DesktopTheme.Button(provider.Name);
            button.Tag = provider; button.Margin = new Thickness(0, 0, 6, 6);
            button.Click += (_, _) => Pick(provider);
            _providers.Add(button); providers.Children.Add(button);
        }
        panel.Children.Add(providers);
        panel.Children.Add(DesktopTheme.Caption(Texts.ModelUrl)); panel.Children.Add(_url);
        panel.Children.Add(DesktopTheme.Caption(Texts.ModelName));
        var models = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        _catalogue.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(_catalogue, 1);
        models.Children.Add(_model); models.Children.Add(_catalogue);
        panel.Children.Add(models);
        ToolTip.SetTip(models, Texts.ModelPickFromList);
        panel.Children.Add(_keyLabel); panel.Children.Add(_key);
        panel.Children.Add(DesktopTheme.Caption(Texts.ModelAtOnce)); panel.Children.Add(_atOnce);
        panel.Children.Add(_hint); panel.Children.Add(_test); panel.Children.Add(_verdict); panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel };
        ShowProvider(settings.Provider);
        Closing += (_, _) => { _testing?.Cancel(); Keep(); };
    }

    private void Pick(Provider provider)
    {
        Keep();
        _settings.Use(provider);
        _url.Text = _settings.Url; _model.Text = _settings.Model; _key.Text = _settings.Key;
        _catalogue.ItemsSource = Array.Empty<string>();
        _catalogue.IsEnabled = false;
        _verdict.Text = "";
        ShowProvider(provider);
    }

    private void ShowProvider(Provider provider)
    {
        foreach (var button in _providers)
        {
            button.Classes.Set("selected", ((Provider)button.Tag!).Name == provider.Name);
            button.Foreground = ((Provider)button.Tag!).Name == provider.Name ? DesktopTheme.Green : DesktopTheme.Gold;
        }
        _key.IsVisible = _keyLabel.IsVisible = provider.NeedsKey;
        _hint.Text = provider.NeedsKey ? Texts.ModelKeyHint : Texts.ModelLocalNote;
        if (provider.Hint.Length > 0) _verdict.Text = provider.Hint;
    }

    internal async Task TestAsync(Func<CancellationToken, Task<IReadOnlyList<string>>>? catalogue = null)
    {
        if (_testing != null) return;
        Keep();
        _testing = new CancellationTokenSource();
        _test.IsEnabled = false;
        _url.IsEnabled = _model.IsEnabled = _key.IsEnabled = _catalogue.IsEnabled = false;
        foreach (var button in _providers) button.IsEnabled = false;
        _verdict.Foreground = DesktopTheme.Muted; _verdict.Text = Texts.ModelTesting;
        try
        {
            using var llm = catalogue == null
                ? new Llm(Path.Combine(Paths.Root, "datos", "respuestas"), _settings.Endpoint()) : null;
            var models = await (catalogue?.Invoke(_testing.Token) ?? llm!.CatalogueAsync(_testing.Token));
            _testing.Token.ThrowIfCancellationRequested();
            _catalogue.ItemsSource = models.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (string.IsNullOrWhiteSpace(_model.Text)) _model.Text = models.FirstOrDefault() ?? "";
            _catalogue.SelectedItem = models.FirstOrDefault(name => name == _model.Text);
            _verdict.Foreground = DesktopTheme.Green;
            _verdict.Text = string.Format(Texts.ModelTestOkFormat, models.Count) +
                (models.Count > 0 ? "  " + Texts.ModelPickFromList : "");
        }
        catch (OperationCanceledException) when (_testing.IsCancellationRequested) { }
        catch (Exception error)
        {
            _verdict.Foreground = DesktopTheme.Red;
            _verdict.Text = string.Format(Texts.ModelTestFailFormat, error.Message);
        }
        finally
        {
            _testing.Dispose(); _testing = null;
            _test.IsEnabled = true;
            _url.IsEnabled = _model.IsEnabled = _key.IsEnabled = true;
            _catalogue.IsEnabled = _catalogue.ItemCount > 0;
            foreach (var button in _providers) button.IsEnabled = true;
        }
    }

    private void Keep()
    {
        _settings.Url = _url.Text?.Trim() ?? "";
        _settings.Model = _model.Text?.Trim() ?? "";
        _settings.Key = _key.Text ?? "";
        _settings.AtOnce = (int)(_atOnce.Value ?? 4);
        _settings.Save();
    }
}
