using System.Drawing;
using System.Windows.Forms;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator.UI;

/// <summary>
/// Which model is asked, and with what key.
///
/// It is behind a button and not on the main screen because most of the work does not
/// need it: the structure resolves the bulk without connecting to anything. The model is only needed for
/// the doubts, and some will prefer not to spend on that.
///
/// ─── Three decisions that show when using it ────────────────────────────────────────────
///
/// The providers are shortcuts, not a closed list: choosing «Claude» fills in address and dialect,
/// but both fields are still editable, and «Otro» accepts any server that speaks like
/// OpenAI, which by now is almost any.
///
/// The model is NOT typed by hand if it can be avoided. <b>Probar</b> asks the provider which
/// models it has and fills the list, which checks in one go the address, the key and the
/// model's name —the three places where things fail— here and not in the middle of a sweep.
///
/// The key is stored per provider and encrypted against the Windows account, in
/// <c>%APPDATA%\Jondo\</c>. Never in the repository: there is no code path that writes it
/// inside the emulator's folder.
/// </summary>
internal sealed class ModelDialog : Form, IBackgroundWindow
{
    private readonly Settings _settings;
    private readonly float _scale;
    private readonly List<LauncherButton> _providers = new();
    private readonly LauncherField _url;
    private readonly ComboBox _model;
    private readonly LauncherField _key;
    private readonly NumericUpDown _atOnce;
    private readonly LauncherButton _test;
    private readonly Label _keyLabel;
    private readonly Label _hint;
    private readonly Label _verdict;

    // This fixed dialog uses a solid background; transparent child controls therefore have no
    // composed image to crop, unlike MapperWindow and the launcher.
    public Image? ComposedBackground => null;

    private int E(int pixels) => (int)Math.Round(pixels * _scale);
    private Font Letter(float pixels, FontStyle style = FontStyle.Regular)
        => LauncherTheme.CreateFont(pixels, style);
    private Texts Texts => Texts.Get(_settings.Language);

    public ModelDialog(Settings settings, float scale)
    {
        _settings = settings;
        _scale = scale;

        Text = Texts.ModelTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(E(980), E(400));
        BackColor = LauncherTheme.Background;
        DoubleBuffered = true;

        int y = E(16);
        Controls.Add(Caption(Texts.ModelProvider, E(24), y));
        y += E(24);

        int x = E(24);
        foreach (var provider in Provider.All)
        {
            var button = new LauncherButton
            {
                Text = provider.Name.ToUpperInvariant(),
                Bounds = new Rectangle(x, y, E(126), E(34)),
                Font = Letter(10f, FontStyle.Bold),
                CornerRadius = E(5),
                BackgroundTop = Color.FromArgb(200, 34, 21, 12),
                BackgroundBottom = Color.FromArgb(200, 22, 13, 8),
                BackgroundTopHighlight = LauncherTheme.LightBrown,
                BackgroundBottomHighlight = Color.FromArgb(130, 75, 35),
                BackgroundTopActive = LauncherTheme.LightBrown,
                BackgroundBottomActive = Color.FromArgb(130, 75, 35),
                BorderColor = LauncherTheme.BorderBrown,
                BorderColorHighlight = LauncherTheme.GoldBorder,
                BorderColorActive = LauncherTheme.LightGold,
                TextColor = provider.Local ? LauncherTheme.OnlineGreen : LauncherTheme.SoftGold,
                TextColorHighlight = Color.White,
                Cursor = Cursors.Hand,
                Tag = provider,
            };
            button.Click += (s, _) => Pick((Provider)((LauncherButton)s!).Tag!);
            _providers.Add(button);
            Controls.Add(button);
            x += E(132);
        }
        y += E(48);

        Controls.Add(Caption(Texts.ModelUrl, E(24), y));
        Controls.Add(Caption(Texts.ModelName, E(500), y));
        y += E(22);

        _url = new LauncherField
        {
            Bounds = new Rectangle(E(24), y, E(450), E(34)),
            Font = Letter(10.5f),
            Value = settings.Url,
        };
        _model = new ComboBox
        {
            Bounds = new Rectangle(E(500), y, E(450), E(34)),
            Font = Letter(10.5f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(13, 7, 4),
            ForeColor = LauncherTheme.FieldText,
            DropDownStyle = ComboBoxStyle.DropDown,
            Text = settings.Model,
        };
        y += E(46);

        _keyLabel = Caption(Texts.ModelKey, E(24), y);
        Controls.Add(Caption(Texts.ModelAtOnce, E(700), y));
        y += E(22);

        _key = new LauncherField
        {
            Bounds = new Rectangle(E(24), y, E(450), E(34)),
            Font = Letter(10.5f),
            IsPassword = true,
            Value = settings.Key,
        };
        _test = new LauncherButton
        {
            Text = Texts.ModelTest,
            Bounds = new Rectangle(E(500), y, E(170), E(34)),
            Font = Letter(10.5f, FontStyle.Bold),
            CornerRadius = E(5),
            BackgroundTop = LauncherTheme.GreenTop,
            BackgroundBottom = LauncherTheme.GreenBottom,
            BackgroundTopHighlight = LauncherTheme.GreenTopHover,
            BackgroundBottomHighlight = LauncherTheme.GreenBottomHover,
            BorderColor = LauncherTheme.GreenBorder,
            BorderColorHighlight = LauncherTheme.GreenBorder,
            TextColor = Color.White,
            TextColorHighlight = Color.White,
            Cursor = Cursors.Hand,
        };
        _test.Click += async (_, _) => await TestAsync();

        _atOnce = new NumericUpDown
        {
            Bounds = new Rectangle(E(700), y, E(90), E(34)),
            Minimum = 1,
            Maximum = 32,
            Value = Math.Clamp(settings.AtOnce, 1, 32),
            Font = Letter(10.5f),
            BackColor = Color.FromArgb(13, 7, 4),
            ForeColor = LauncherTheme.FieldText,
            BorderStyle = BorderStyle.None,
        };
        y += E(42);

        _hint = new Label
        {
            Bounds = new Rectangle(E(24), y, E(930), E(20)),
            BackColor = Color.Transparent,
            ForeColor = LauncherTheme.LightBrownText,
            Font = Letter(9.5f),
        };
        y += E(24);

        _verdict = new Label
        {
            Bounds = new Rectangle(E(24), y, E(930), E(44)),
            BackColor = Color.Transparent,
            ForeColor = LauncherTheme.LightBrownText,
            Font = Letter(10.5f),
        };

        var close = new LauncherButton
        {
            Text = Texts.Close,
            Bounds = new Rectangle(ClientSize.Width - E(200), ClientSize.Height - E(52), E(176), E(38)),
            Font = Letter(11f, FontStyle.Bold),
            CornerRadius = E(5),
            BackgroundTop = Color.FromArgb(200, 34, 21, 12),
            BackgroundBottom = Color.FromArgb(200, 22, 13, 8),
            BackgroundTopHighlight = LauncherTheme.LightBrown,
            BackgroundBottomHighlight = Color.FromArgb(130, 75, 35),
            BorderColor = LauncherTheme.BorderBrown,
            BorderColorHighlight = LauncherTheme.GoldBorder,
            TextColor = LauncherTheme.LightGold,
            TextColorHighlight = Color.White,
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { _keyLabel, _url, _model, _key, _test, _atOnce, _hint, _verdict, close });
        Show(settings.Provider);
    }

    private void Pick(Provider provider)
    {
        // What is typed in the key is stored BEFORE changing: the key is per provider, and otherwise
        // Claude's would be lost as soon as one tried Gemini for a while.
        _settings.Key = _key.Value;
        _settings.Use(provider);

        _url.Value = _settings.Url;
        _model.Text = _settings.Model;
        _key.Value = _settings.Key;
        _model.Items.Clear();
        _verdict.Text = "";
        Show(provider);
    }

    private void Show(Provider provider)
    {
        foreach (var button in _providers) button.Active = ((Provider)button.Tag!).Name == provider.Name;

        _key.Visible = provider.NeedsKey;
        _keyLabel.Visible = provider.NeedsKey;
        _hint.Text = provider.NeedsKey ? Texts.ModelKeyHint : Texts.ModelLocalNote;
        _test.Left = provider.NeedsKey ? E(500) : E(24);
        if (provider.Hint.Length > 0) _verdict.Text = provider.Hint;
    }

    private async Task TestAsync()
    {
        Keep();
        _test.Enabled = false;
        _verdict.ForeColor = LauncherTheme.LightBrownText;
        _verdict.Text = Texts.ModelTesting;

        try
        {
            using var llm = new Llm(Path.Combine(Paths.Root, "datos", "respuestas"), _settings.Endpoint());
            var models = await llm.CatalogueAsync();

            string chosen = _model.Text;
            _model.Items.Clear();
            foreach (string name in models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)) _model.Items.Add(name);
            _model.Text = chosen.Length > 0 ? chosen : models.FirstOrDefault() ?? "";

            _verdict.ForeColor = LauncherTheme.OnlineGreen;
            _verdict.Text = string.Format(Texts.ModelTestOkFormat, models.Count) +
                            (models.Count > 0 ? "  " + Texts.ModelPickFromList : "");
        }
        catch (Exception e)
        {
            _verdict.ForeColor = LauncherTheme.Red;
            _verdict.Text = string.Format(Texts.ModelTestFailFormat, e.Message);
        }
        finally { _test.Enabled = true; }
    }

    private void Keep()
    {
        _settings.Url = _url.Value.Trim();
        _settings.Model = _model.Text.Trim();
        _settings.Key = _key.Value;
        _settings.AtOnce = (int)_atOnce.Value;
        _settings.Save();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Keep();
        base.OnFormClosing(e);
    }

    private Label Caption(string text, int x, int y) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, E(400), E(18)),
        BackColor = Color.Transparent,
        ForeColor = LauncherTheme.MutedGold,
        Font = Letter(10f, FontStyle.Bold),
    };
}
