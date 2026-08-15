using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Main window of the emulator launcher.
    ///
    /// Rebuilds in WinForms the interface that used to be a web page served by HaapiServer and
    /// opened in a browser: the access card on the left (login and register tabs, the
    /// "ready to play" panel and the service status) and the server event console docked on the
    /// right, all of it over the background artwork, with music.
    ///
    /// Unlike the web version, there are no HTTP requests here: every action calls
    /// <see cref="LauncherService"/> directly, which is the very same logic still serving the
    /// /api/* routes.
    /// </summary>
    internal sealed class LauncherWindow : Form
    {
        // ─── Measurements taken from the original stylesheet (in CSS pixels) ────
        private const int SideMargin = 50;
        private const int VerticalMargin = 35;
        private const int CardWidth = 350;
        private const int ConsoleWidth = 440;
        private const int BarHeight = 40;
        private const int InnerPadding = 20;
        private const int CardRadius = 12;

        // ─── State ──────────────────────────────────────────────────────────────
        private string _token = "";
        private string _account = "";
        private bool _serverOnline;
        private bool _registerMode;
        private bool _authenticated;
        private long _lastLogId;
        private float _scale = 1f;
        private Language _language;
        private LauncherTexts _texts;

        // ─── Resources ──────────────────────────────────────────────────────────
        private Image? _backgroundImage;
        private Bitmap? _composedBackground;
        private MusicPlayer? _music;

        // ─── Controls ───────────────────────────────────────────────────────────
        private readonly LauncherPanel _card = new();

        /// <summary>El rótulo de encima de la tarjeta, y cuánto sitio se le deja.</summary>
        private readonly LauncherLogo _logo = new();
        private const int LogoHeight = 120;
        private const int LogoGap = 10;
        private readonly LauncherPanel _topBar = new();
        private readonly LauncherButton _btnMusic = new();
        private readonly LauncherButton _btnEs = new();
        private readonly LauncherButton _btnEn = new();
        private readonly LauncherButton _btnFr = new();

        private readonly LauncherPanel _alertPanel = new();
        private readonly Label _lblAlert = new();

        private readonly LauncherButton _loginTab = new();
        private readonly LauncherButton _registerTab = new();
        private readonly Panel _tabsLine = new();

        private readonly Label _lblUsername = new();
        private readonly LauncherField _fieldUsername = new();
        private readonly Label _lblPassword = new();
        private readonly LauncherField _fieldPassword = new();
        private readonly LauncherButton _btnConnect = new();

        private readonly Label _lblRegUsername = new();
        private readonly LauncherField _fieldRegUsername = new();
        private readonly Label _lblRegPassword = new();
        private readonly LauncherField _fieldRegPassword = new();
        private readonly Label _lblRegNickname = new();
        private readonly LauncherField _fieldRegNickname = new();
        private readonly LauncherButton _btnCreate = new();

        private readonly Label _lblWelcome = new();
        private readonly Label _lblSubscription = new();
        private readonly LauncherButton _btnPlay = new();
        private readonly LinkLabel _lnkLogOut = new();

        private readonly LauncherButton _btnClientPath = new();

        private readonly StatusIndicator _statusIndicator = new();

        private readonly LauncherPanel _consolePanel = new();
        private readonly LauncherPanel _consoleHeader = new();
        private readonly Label _lblConsoleTitle = new();
        private readonly LauncherButton _chkAutoScroll = new();
        private readonly LauncherButton _btnClear = new();
        private readonly RichTextBox _console = new();

        private readonly System.Windows.Forms.Timer _statusTimer = new();
        private readonly System.Windows.Forms.Timer _logsTimer = new();

        public LauncherWindow()
        {
            _language = LauncherTexts.LoadLanguage();
            _texts = LauncherTexts.Get(_language);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            Text = "Dofus";
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 660);
            WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            Font = LauncherTheme.CreateFont(13f);

            LoadIcon();
            _backgroundImage = LauncherTheme.LoadImage("bg.jpg");

            BuildCard();
            BuildConsole();

            _statusTimer.Interval = 2000;
            _statusTimer.Tick += (s, e) => CheckStatus();
            _logsTimer.Interval = 500;
            _logsTimer.Tick += (s, e) => RefreshLogs();
        }

        /// <summary>Background image, already scaled and vignetted, that the panels cut out of.</summary>
        internal Bitmap? ComposedBackground => _composedBackground;

        // ═══════════════════════════════════════════════════════════════════════
        //  Building the interface
        // ═══════════════════════════════════════════════════════════════════════

        private void LoadIcon()
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(LauncherTheme.AssetsFolder, "favicon.ico"),
                         Path.Combine(LauncherTheme.AssetsFolder, "icon.ico")
                     })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    Icon = new Icon(candidate);
                    return;
                }
                catch { }
            }
        }

        private void BuildCard()
        {
            Controls.Add(_logo);

            _card.Layers.Add(LauncherTheme.CardFill);
            _card.CornerRadius = CardRadius;
            _card.BorderColor = LauncherTheme.GoldBorder;
            _card.BorderWidth = 2;
            Controls.Add(_card);

            // Top bar: music and languages.
            _topBar.Layers.Add(LauncherTheme.CardFill);
            _topBar.Layers.Add(LauncherTheme.BarFill);
            _topBar.CornerRadius = CardRadius - 2;
            _topBar.TopCornersOnly = true;
            _topBar.BottomLine = LauncherTheme.LightBrown;
            _card.Controls.Add(_topBar);

            _btnMusic.Icon = ButtonIcon.Speaker;
            _btnMusic.Font = LauncherTheme.CreateFont(11f, FontStyle.Bold);
            _btnMusic.BackgroundTop = _btnMusic.BackgroundBottom = Color.FromArgb(217, 45, 25, 12);
            _btnMusic.BackgroundTopHighlight = _btnMusic.BackgroundBottomHighlight = Color.FromArgb(110, 70, 24);
            _btnMusic.BorderColor = LauncherTheme.Gold;
            _btnMusic.BorderColorHighlight = Color.White;
            _btnMusic.TextColor = LauncherTheme.LightGold;
            _btnMusic.TextColorHighlight = Color.White;
            _btnMusic.CornerRadius = 5;
            _btnMusic.Click += (s, e) => ToggleMusic();
            _topBar.Controls.Add(_btnMusic);

            ConfigureLanguageButton(_btnEs, "es", Language.Es);
            ConfigureLanguageButton(_btnEn, "en", Language.En);
            ConfigureLanguageButton(_btnFr, "fr", Language.Fr);

            // Error notice (the equivalent of .alert-box).
            _alertPanel.Layers.Add(LauncherTheme.CardFill);
            _alertPanel.Layers.Add(LauncherTheme.AlertBackground);
            _alertPanel.CornerRadius = 5;
            _alertPanel.BorderColor = LauncherTheme.Red;
            _alertPanel.BorderWidth = 1;
            _alertPanel.Visible = false;
            _card.Controls.Add(_alertPanel);

            _lblAlert.BackColor = Color.Transparent;
            _lblAlert.ForeColor = LauncherTheme.AlertText;
            _lblAlert.Font = LauncherTheme.CreateFont(12f);
            _lblAlert.TextAlign = ContentAlignment.MiddleCenter;
            _lblAlert.AutoSize = false;
            _lblAlert.Dock = DockStyle.Fill;
            _alertPanel.Controls.Add(_lblAlert);

            // Tabs.
            ConfigureTab(_loginTab, false);
            ConfigureTab(_registerTab, true);
            _tabsLine.BackColor = LauncherTheme.LightBrown;
            _card.Controls.Add(_tabsLine);

            // Login form.
            ConfigureLabel(_lblUsername);
            ConfigureField(_fieldUsername, false);
            _fieldUsername.SubmitRequested += (s, e) => SignIn();
            ConfigureLabel(_lblPassword);
            ConfigureField(_fieldPassword, true);
            _fieldPassword.SubmitRequested += (s, e) => SignIn();

            ConfigureActionButton(_btnConnect, LauncherTheme.GreenTop, LauncherTheme.GreenBottom, LauncherTheme.GreenBorder,
                                  LauncherTheme.GreenTopHover, LauncherTheme.GreenBottomHover);
            _btnConnect.Click += (s, e) => SignIn();

            // Registration form.
            ConfigureLabel(_lblRegUsername);
            ConfigureField(_fieldRegUsername, false);
            _fieldRegUsername.SubmitRequested += (s, e) => RegisterAccount();
            ConfigureLabel(_lblRegPassword);
            ConfigureField(_fieldRegPassword, true);
            _fieldRegPassword.SubmitRequested += (s, e) => RegisterAccount();
            ConfigureLabel(_lblRegNickname);
            ConfigureField(_fieldRegNickname, false);
            _fieldRegNickname.SubmitRequested += (s, e) => RegisterAccount();

            ConfigureActionButton(_btnCreate, LauncherTheme.PurpleTop, LauncherTheme.PurpleBottom, LauncherTheme.PurpleBorder,
                                  LauncherTheme.PurpleTop, LauncherTheme.PurpleBottom);
            _btnCreate.Click += (s, e) => RegisterAccount();

            // Logged-in panel.
            _lblWelcome.BackColor = Color.Transparent;
            _lblWelcome.ForeColor = LauncherTheme.HighlightText;
            _lblWelcome.Font = LauncherTheme.CreateFont(16f);
            _lblWelcome.TextAlign = ContentAlignment.MiddleCenter;
            _lblWelcome.AutoSize = false;
            _card.Controls.Add(_lblWelcome);

            _lblSubscription.BackColor = Color.Transparent;
            _lblSubscription.ForeColor = LauncherTheme.Gold;
            _lblSubscription.Font = LauncherTheme.CreateFont(12f);
            _lblSubscription.TextAlign = ContentAlignment.MiddleCenter;
            _lblSubscription.AutoSize = false;
            _card.Controls.Add(_lblSubscription);

            ConfigureActionButton(_btnPlay, LauncherTheme.GreenTop, LauncherTheme.GreenBottom, LauncherTheme.GreenBorder,
                                  LauncherTheme.GreenTopHover, LauncherTheme.GreenBottomHover);
            // The play button is only visible once logged in, so it starts out enabled.
            _btnPlay.Enabled = true;
            _btnPlay.Click += (s, e) => LaunchGame();

            _lnkLogOut.BackColor = Color.Transparent;
            _lnkLogOut.LinkColor = Color.FromArgb(170, 170, 170);
            _lnkLogOut.ActiveLinkColor = Color.White;
            _lnkLogOut.VisitedLinkColor = Color.FromArgb(170, 170, 170);
            _lnkLogOut.LinkBehavior = LinkBehavior.AlwaysUnderline;
            _lnkLogOut.Font = LauncherTheme.CreateFont(11f);
            _lnkLogOut.TextAlign = ContentAlignment.MiddleCenter;
            _lnkLogOut.AutoSize = false;
            _lnkLogOut.LinkClicked += (s, e) => SignOut();
            _card.Controls.Add(_lnkLogOut);

            // Dónde está el cliente. Se enseña siempre, con sesión iniciada o sin ella, porque es
            // justo lo que hay que arreglar antes de poder jugar si el juego no está al lado.
            _btnClientPath.Icon = ButtonIcon.Folder;
            _btnClientPath.Font = LauncherTheme.CreateFont(9.5f);
            _btnClientPath.BackgroundTop = _btnClientPath.BackgroundBottom = Color.FromArgb(150, 30, 18, 10);
            _btnClientPath.BackgroundTopHighlight = _btnClientPath.BackgroundBottomHighlight = Color.FromArgb(190, 52, 32, 18);
            _btnClientPath.BackgroundTopActive = _btnClientPath.BackgroundBottomActive = Color.FromArgb(190, 52, 32, 18);
            _btnClientPath.BorderColor = LauncherTheme.BorderBrown;
            _btnClientPath.BorderColorHighlight = LauncherTheme.LightGold;
            _btnClientPath.BorderColorActive = LauncherTheme.LightGold;
            _btnClientPath.CornerRadius = 4;
            _btnClientPath.Click += (s, e) => ChooseClient();
            _card.Controls.Add(_btnClientPath);

            // Service status.
            _statusIndicator.Font = LauncherTheme.CreateFont(11f, FontStyle.Bold);
            _card.Controls.Add(_statusIndicator);

            RefreshClientPath();
        }

        /// <summary>
        /// Enseña qué Dofus.exe se va a lanzar y con qué idioma. En rojo cuando no hay ninguno, que
        /// es el único caso en el que el botón de jugar no puede funcionar.
        /// </summary>
        private void RefreshClientPath()
        {
            string ruta = LauncherService.ResolveClient();
            string guardada = LauncherPreferences.ClientExecutableRaw;
            string idioma = LauncherTexts.Code(_language).ToUpperInvariant();

            if (ruta.Length == 0)
            {
                _btnClientPath.Text = guardada.Length > 0
                    ? "Dofus.exe ya no está donde se dejó — elige dónde está"
                    : "No se encuentra Dofus.exe — elige dónde está";
                _btnClientPath.TextColor = _btnClientPath.TextColorHighlight =
                    _btnClientPath.TextColorActive = Color.FromArgb(236, 120, 96);
            }
            else
            {
                _btnClientPath.Text = Recortar(ruta) + "   ·   " + idioma;
                _btnClientPath.TextColor = LauncherTheme.Gold;
                _btnClientPath.TextColorHighlight = _btnClientPath.TextColorActive = Color.White;
            }
        }

        /// <summary>Rutas largas por el medio, que el final es lo que identifica al fichero.</summary>
        private static string Recortar(string ruta)
        {
            const int tope = 46;
            if (ruta.Length <= tope) return ruta;
            return ruta.Substring(0, 12) + "…" + ruta.Substring(ruta.Length - (tope - 13));
        }

        private void ChooseClient()
        {
            using var dialogo = new OpenFileDialog
            {
                Title = "¿Dónde está el cliente de Dofus?",
                Filter = "Dofus.exe|Dofus.exe|Ejecutables (*.exe)|*.exe",
                CheckFileExists = true,
            };

            string actual = LauncherService.ResolveClient();
            if (actual.Length > 0) dialogo.InitialDirectory = Path.GetDirectoryName(actual);

            if (dialogo.ShowDialog(this) != DialogResult.OK) return;

            LauncherPreferences.ClientExecutable = dialogo.FileName;
            RefreshClientPath();
            RebuildLayout();
        }

        private void ConfigureLanguageButton(LauncherButton button, string code, Language language)
        {
            button.Icon = ButtonIcon.Flag;
            button.FlagCode = code;
            button.Text = code.ToUpperInvariant();
            button.Font = LauncherTheme.CreateFont(12f);
            button.BackgroundTop = button.BackgroundBottom = Color.FromArgb(204, 40, 24, 14);
            button.BackgroundTopHighlight = button.BackgroundBottomHighlight = LauncherTheme.LightBrown;
            button.BackgroundTopActive = button.BackgroundBottomActive = LauncherTheme.LightBrown;
            button.BorderColor = LauncherTheme.BorderBrown;
            button.BorderColorHighlight = LauncherTheme.LightGold;
            button.BorderColorActive = LauncherTheme.LightGold;
            button.TextColor = LauncherTheme.Gold;
            button.TextColorHighlight = Color.White;
            button.TextColorActive = Color.White;
            button.CornerRadius = 4;
            button.Click += (s, e) => ChangeLanguage(language);
            _topBar.Controls.Add(button);
        }

        private void ConfigureTab(LauncherButton tab, bool isRegister)
        {
            tab.Font = LauncherTheme.CreateFont(12f, FontStyle.Bold);
            tab.LetterSpacing = 1f;
            tab.BackgroundTop = tab.BackgroundBottom = Color.FromArgb(204, 35, 20, 12);
            tab.BackgroundTopHighlight = tab.BackgroundBottomHighlight = Color.FromArgb(220, 55, 33, 18);
            tab.BackgroundTopActive = tab.BackgroundBottomActive = LauncherTheme.LightBrown;
            tab.TextColor = LauncherTheme.MutedGold;
            tab.TextColorHighlight = LauncherTheme.CardText;
            tab.TextColorActive = LauncherTheme.CardText;
            tab.BorderColor = Color.Transparent;
            tab.BorderWidth = 0;
            tab.CornerRadius = 5;
            tab.TopCornersOnly = true;
            tab.Underline = LauncherTheme.LightGold;
            tab.UnderlineWidth = 3;
            tab.TextShadow = true;
            tab.Click += (s, e) => ChangeTab(isRegister);
            _card.Controls.Add(tab);
        }

        private void ConfigureLabel(Label label)
        {
            label.BackColor = Color.Transparent;
            label.ForeColor = LauncherTheme.SoftGold;
            label.Font = LauncherTheme.CreateFont(11f, FontStyle.Bold);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoSize = false;
            _card.Controls.Add(label);
        }

        private void ConfigureField(LauncherField field, bool isPassword)
        {
            field.Font = LauncherTheme.CreateFont(13f);
            field.IsPassword = isPassword;
            field.Locked = true;
            _card.Controls.Add(field);
        }

        private void ConfigureActionButton(LauncherButton button, Color top, Color bottom, Color border, Color topHover, Color bottomHover)
        {
            button.Font = LauncherTheme.CreateFont(16f, FontStyle.Bold);
            button.LetterSpacing = 2f;
            button.TextShadow = true;
            button.BackgroundTop = top;
            button.BackgroundBottom = bottom;
            button.BackgroundTopHighlight = topHover;
            button.BackgroundBottomHighlight = bottomHover;
            button.BorderColor = border;
            button.BorderColorHighlight = border;
            button.TextColor = Color.White;
            button.TextColorHighlight = Color.White;
            button.BorderWidth = 2;
            button.CornerRadius = 6;
            button.Enabled = false;
            _card.Controls.Add(button);
        }

        private void BuildConsole()
        {
            _consolePanel.Layers.Add(LauncherTheme.ConsoleFill);
            _consolePanel.CornerRadius = CardRadius;
            _consolePanel.BorderColor = LauncherTheme.GoldBorder;
            _consolePanel.BorderWidth = 2;
            Controls.Add(_consolePanel);

            _consoleHeader.Layers.Add(LauncherTheme.ConsoleFill);
            _consoleHeader.Layers.Add(LauncherTheme.BarFill);
            _consoleHeader.CornerRadius = CardRadius - 2;
            _consoleHeader.TopCornersOnly = true;
            _consoleHeader.BottomLine = LauncherTheme.LightBrown;
            _consolePanel.Controls.Add(_consoleHeader);

            _lblConsoleTitle.BackColor = Color.Transparent;
            _lblConsoleTitle.ForeColor = LauncherTheme.HighlightText;
            _lblConsoleTitle.Font = LauncherTheme.CreateFont(11f, FontStyle.Bold);
            _lblConsoleTitle.TextAlign = ContentAlignment.MiddleLeft;
            _lblConsoleTitle.AutoSize = false;
            _consoleHeader.Controls.Add(_lblConsoleTitle);

            _chkAutoScroll.Icon = ButtonIcon.CheckBox;
            _chkAutoScroll.Font = LauncherTheme.CreateFont(11f);
            _chkAutoScroll.BackgroundTop = _chkAutoScroll.BackgroundBottom = Color.Transparent;
            _chkAutoScroll.BackgroundTopHighlight = _chkAutoScroll.BackgroundBottomHighlight = Color.Transparent;
            _chkAutoScroll.BorderColor = Color.Transparent;
            _chkAutoScroll.BorderWidth = 0;
            _chkAutoScroll.TextColor = LauncherTheme.Gold;
            _chkAutoScroll.TextColorHighlight = Color.White;
            _chkAutoScroll.Click += (s, e) => _chkAutoScroll.Active = !_chkAutoScroll.Active;
            _consoleHeader.Controls.Add(_chkAutoScroll);

            _btnClear.Font = LauncherTheme.CreateFont(11f);
            _btnClear.BackgroundTop = _btnClear.BackgroundBottom = Color.FromArgb(204, 45, 25, 15);
            _btnClear.BackgroundTopHighlight = _btnClear.BackgroundBottomHighlight = LauncherTheme.LightBrown;
            _btnClear.BorderColor = LauncherTheme.BorderBrown;
            _btnClear.BorderColorHighlight = LauncherTheme.LightGold;
            _btnClear.TextColor = LauncherTheme.SoftGold;
            _btnClear.TextColorHighlight = Color.White;
            _btnClear.CornerRadius = 3;
            _btnClear.Click += (s, e) => ClearConsole();
            _consoleHeader.Controls.Add(_btnClear);

            _console.BorderStyle = BorderStyle.None;
            _console.BackColor = LauncherTheme.ConsoleBackground;
            _console.ForeColor = LauncherTheme.LogNormal;
            _console.Font = LauncherTheme.CreateMonoFont(11f);
            _console.ReadOnly = true;
            _console.WordWrap = true;
            _console.ScrollBars = RichTextBoxScrollBars.Vertical;
            _console.DetectUrls = false;
            _console.TabStop = false;
            _consolePanel.Controls.Add(_console);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Lifecycle and layout
        // ═══════════════════════════════════════════════════════════════════════

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _scale = DeviceDpi / 96f;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _scale = DeviceDpi / 96f;
            RebuildBackground();
            ApplyLanguage();
            RebuildLayout();

            // Music starts by itself, just like the autoplay of the web interface.
            _music = new MusicPlayer(Path.Combine(LauncherTheme.AssetsFolder, "theme.mp3"));
            if (_music.Available) _music.Play();
            UpdateMusicButton();

            CheckStatus();
            RefreshLogs();
            _statusTimer.Start();
            _logsTimer.Start();

            BringToFrontOnOpen();
        }

        /// <summary>
        /// Puts the window in front when it opens. Windows will not hand the foreground to a
        /// window created by a process that was not the active one, so without this the launcher
        /// opens behind whatever was on screen and the only sign that it started is the music.
        /// Marking it topmost for an instant is what gets round that rule; it is dropped straight
        /// after so the window behaves normally from then on.
        /// </summary>
        private void BringToFrontOnOpen()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                TopMost = true;
                Activate();
                BringToFront();
                TopMost = false;
                Focus();
            }
            catch
            {
                // Not being able to come to the front is not a reason to fail: the window is
                // already there, just underneath.
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            RebuildBackground();
            RebuildLayout();
            Invalidate(true);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _statusTimer.Stop();
            _logsTimer.Stop();
            _music?.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_composedBackground != null)
            {
                e.Graphics.DrawImageUnscaled(_composedBackground, 0, 0);
            }
            else
            {
                using var brush = new SolidBrush(LauncherTheme.Background);
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        /// <summary>
        /// Composes the window background: the image cropped the way background-size: cover would
        /// do it, plus the radial vignette of the .overlay layer.
        /// </summary>
        private void RebuildBackground()
        {
            int width = Math.Max(1, ClientSize.Width);
            int height = Math.Max(1, ClientSize.Height);
            if (_composedBackground != null && _composedBackground.Width == width && _composedBackground.Height == height) return;

            var previous = _composedBackground;
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(LauncherTheme.Background);

                if (_backgroundImage != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float factor = Math.Max((float)width / _backgroundImage.Width, (float)height / _backgroundImage.Height);
                    int imageWidth = (int)Math.Ceiling(_backgroundImage.Width * factor);
                    int imageHeight = (int)Math.Ceiling(_backgroundImage.Height * factor);
                    g.DrawImage(_backgroundImage, (width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
                }

                using var ellipse = new GraphicsPath();
                ellipse.AddEllipse(-width * 0.25f, -height * 0.25f, width * 1.5f, height * 1.5f);
                using var vignette = new PathGradientBrush(ellipse)
                {
                    CenterPoint = new PointF(width / 2f, height / 2f),
                    CenterColor = Color.FromArgb(3, 0, 0, 0),
                    SurroundColors = new[] { Color.FromArgb(77, 0, 0, 0) }
                };
                g.FillRectangle(vignette, 0, 0, width, height);
            }

            _composedBackground = bitmap;
            previous?.Dispose();
        }

        /// <summary>Converts a measurement in CSS pixels into real screen pixels.</summary>
        private int Px(float pixels) => (int)Math.Round(pixels * _scale);

        /// <summary>Places the access card and the console keeping the original layout.</summary>
        private void RebuildLayout()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            SuspendLayout();

            int marginX = Px(SideMargin);
            int marginY = Px(VerticalMargin);

            int consoleWidth = Math.Min(Px(ConsoleWidth), Math.Max(Px(240), ClientSize.Width / 2));
            int consoleHeight = Math.Max(Px(200), ClientSize.Height - marginY * 2);
            _consolePanel.SetBounds(ClientSize.Width - marginX - consoleWidth, marginY, consoleWidth, consoleHeight);
            LayOutConsole();

            int cardWidth = Px(CardWidth);
            int cardHeight = LayOutCard(cardWidth);

            // El rótulo va encima de la tarjeta, y las dos cosas juntas se centran a lo alto. Si no
            // cabe, el que se encoge es el rótulo: la tarjeta lleva los campos y no puede recortarse.
            int logoHeight = Px(LogoHeight);
            int hueco = ClientSize.Height - marginY * 2 - cardHeight - Px(LogoGap);
            logoHeight = Math.Max(0, Math.Min(logoHeight, hueco));

            int total = logoHeight + (logoHeight > 0 ? Px(LogoGap) : 0) + cardHeight;
            int y = Math.Max(marginY, (ClientSize.Height - total) / 2);

            _logo.Visible = logoHeight > 0;
            _logo.SetBounds(marginX, y, cardWidth, logoHeight);
            _card.SetBounds(marginX, y + total - cardHeight, cardWidth, cardHeight);

            ResumeLayout();
            _logo.Invalidate();
            _card.Invalidate(true);
            _consolePanel.Invalidate(true);
        }

        private void LayOutConsole()
        {
            int width = _consolePanel.Width;
            int height = _consolePanel.Height;
            int border = 2;
            int headerHeight = Px(BarHeight);

            _consoleHeader.SetBounds(border, border, width - border * 2, headerHeight);

            int margin = Px(18);
            int clearWidth = MeasureButton(_btnClear, Px(16));
            int autoScrollWidth = MeasureButton(_chkAutoScroll, Px(24));
            int controlHeight = Px(22);
            int controlY = (headerHeight - controlHeight) / 2;

            _btnClear.SetBounds(_consoleHeader.Width - margin - clearWidth, controlY, clearWidth, controlHeight);
            _chkAutoScroll.SetBounds(_btnClear.Left - Px(10) - autoScrollWidth, controlY, autoScrollWidth, controlHeight);
            _lblConsoleTitle.SetBounds(margin, 0, Math.Max(Px(40), _chkAutoScroll.Left - margin - Px(8)), headerHeight);

            int sideMargin = Px(16);
            int topMargin = Px(14);
            _console.SetBounds(sideMargin, border + headerHeight + topMargin,
                               Math.Max(Px(60), width - sideMargin * 2),
                               Math.Max(Px(60), height - border - headerHeight - topMargin * 2));
        }

        /// <summary>Lays out the card contents from top to bottom and returns its total height.</summary>
        private int LayOutCard(int width)
        {
            int padding = Px(InnerPadding);
            int inner = width - padding * 2;
            int barHeight = Px(BarHeight);

            _topBar.SetBounds(2, 2, width - 4, barHeight - 2);
            LayOutTopBar(_topBar.Width, _topBar.Height);

            int y = barHeight + padding;

            if (_alertPanel.Visible)
            {
                int alertHeight = AlertHeight(inner);
                _alertPanel.SetBounds(padding, y, inner, alertHeight);
                y += alertHeight + Px(15);
            }

            bool showingAuth = !_authenticated;
            _loginTab.Visible = showingAuth;
            _registerTab.Visible = showingAuth;
            _tabsLine.Visible = showingAuth;

            bool loginForm = showingAuth && !_registerMode;
            bool registerForm = showingAuth && _registerMode;

            _lblUsername.Visible = _fieldUsername.Visible = _lblPassword.Visible = _fieldPassword.Visible = _btnConnect.Visible = loginForm;
            _lblRegUsername.Visible = _fieldRegUsername.Visible = _lblRegPassword.Visible = _fieldRegPassword.Visible =
                _lblRegNickname.Visible = _fieldRegNickname.Visible = _btnCreate.Visible = registerForm;
            _lblWelcome.Visible = _lblSubscription.Visible = _btnPlay.Visible = _lnkLogOut.Visible = _authenticated;

            if (showingAuth)
            {
                int tabHeight = Px(33);
                int half = inner / 2;
                _loginTab.SetBounds(padding, y, half, tabHeight);
                _registerTab.SetBounds(padding + half, y, inner - half, tabHeight);
                _tabsLine.SetBounds(padding, y + tabHeight, inner, Px(2));
                y += tabHeight + Px(2) + Px(16);

                if (loginForm)
                {
                    y = LayOutGroup(_lblUsername, _fieldUsername, padding, y, inner);
                    y = LayOutGroup(_lblPassword, _fieldPassword, padding, y, inner);
                    _btnConnect.SetBounds(padding, y + Px(8), inner, Px(46));
                    y += Px(8) + Px(46);
                }
                else
                {
                    y = LayOutGroup(_lblRegUsername, _fieldRegUsername, padding, y, inner);
                    y = LayOutGroup(_lblRegPassword, _fieldRegPassword, padding, y, inner);
                    y = LayOutGroup(_lblRegNickname, _fieldRegNickname, padding, y, inner);
                    _btnCreate.SetBounds(padding, y + Px(8), inner, Px(46));
                    y += Px(8) + Px(46);
                }
            }
            else
            {
                _lblWelcome.SetBounds(padding, y, inner, Px(24));
                y += Px(24) + Px(10);
                _lblSubscription.SetBounds(padding, y, inner, Px(16));
                y += Px(16) + Px(18);
                _btnPlay.SetBounds(padding, y, inner, Px(46));
                y += Px(46) + Px(14);
                _lnkLogOut.SetBounds(padding, y, inner, Px(16));
                y += Px(16);
            }

            y += Px(14);
            _btnClientPath.SetBounds(padding, y, inner, Px(26));
            y += Px(26) + Px(10);
            _statusIndicator.SetBounds(padding, y, inner, Px(16));
            y += Px(16) + padding;

            return y;
        }

        private void LayOutTopBar(int width, int height)
        {
            int margin = Px(16);
            int buttonHeight = Px(24);
            int buttonY = (height - buttonHeight) / 2;

            int musicWidth = MeasureButton(_btnMusic, Px(34));
            _btnMusic.SetBounds(margin, buttonY, musicWidth, buttonHeight);

            int gap = Px(6);
            int widthEs = MeasureButton(_btnEs, Px(30));
            int widthEn = MeasureButton(_btnEn, Px(30));
            int widthFr = MeasureButton(_btnFr, Px(30));

            int right = width - margin;
            _btnFr.SetBounds(right - widthFr, buttonY, widthFr, buttonHeight);
            _btnEn.SetBounds(_btnFr.Left - gap - widthEn, buttonY, widthEn, buttonHeight);
            _btnEs.SetBounds(_btnEn.Left - gap - widthEs, buttonY, widthEs, buttonHeight);
        }

        private int LayOutGroup(Label label, LauncherField field, int x, int y, int width)
        {
            label.SetBounds(x, y, width, Px(14));
            y += Px(14) + Px(5);
            field.SetBounds(x, y, width, Px(36));
            return y + Px(36) + Px(14);
        }

        private int MeasureButton(LauncherButton button, int extra)
        {
            int textWidth = TextRenderer.MeasureText(button.Text, button.Font).Width;
            textWidth += (int)Math.Round(button.LetterSpacing * Math.Max(0, button.Text.Length - 1));
            return textWidth + extra + Px(10);
        }

        private int AlertHeight(int width)
        {
            var measured = TextRenderer.MeasureText(_lblAlert.Text, _lblAlert.Font, new Size(width - Px(20), 0), TextFormatFlags.WordBreak);
            return Math.Max(Px(34), measured.Height + Px(20));
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Language and interface state
        // ═══════════════════════════════════════════════════════════════════════

        private void ChangeLanguage(Language language)
        {
            if (_language == language) return;
            _language = language;
            _texts = LauncherTexts.Get(language);
            LauncherTexts.SaveLanguage(language);
            ApplyLanguage();
            RebuildLayout();
        }

        private void ApplyLanguage()
        {
            _btnEs.Active = _language == Language.Es;
            _btnEn.Active = _language == Language.En;
            _btnFr.Active = _language == Language.Fr;

            // El idioma manda también sobre el juego: es el --langCode con el que arranca. Por eso
            // la fila de la ruta lo enseña, para que no haya que adivinar en qué idioma va a abrir.
            RefreshClientPath();

            _loginTab.Text = _texts.LoginTab;
            _registerTab.Text = _texts.RegisterTab;
            _loginTab.Active = !_registerMode;
            _registerTab.Active = _registerMode;

            _lblUsername.Text = _texts.UsernameLabel.ToUpperInvariant();
            _lblPassword.Text = _texts.PasswordLabel.ToUpperInvariant();
            _lblRegUsername.Text = _texts.NewUsernameLabel.ToUpperInvariant();
            _lblRegPassword.Text = _texts.NewPasswordLabel.ToUpperInvariant();
            _lblRegNickname.Text = _texts.NicknameLabel.ToUpperInvariant();

            _fieldUsername.Placeholder = _texts.UsernamePlaceholder;
            _fieldPassword.Placeholder = "••••••••";
            _fieldRegUsername.Placeholder = _texts.NewUsernamePlaceholder;
            _fieldRegPassword.Placeholder = "••••••••";
            _fieldRegNickname.Placeholder = _texts.NicknamePlaceholder;

            _btnConnect.Text = _texts.ConnectButton;
            _btnCreate.Text = _texts.CreateButton;
            _btnPlay.Text = _texts.PlayButton;
            _lnkLogOut.Text = _texts.LogOutButton;
            _lblSubscription.Text = _texts.Subscription;
            _lblWelcome.Text = $"{_texts.Welcome} {_account.ToUpperInvariant()}!";

            _lblConsoleTitle.Text = _texts.ConsoleTitle;
            _btnClear.Text = _texts.ClearButton;
            _chkAutoScroll.Text = _texts.AutoScroll;

            UpdateMusicButton();
            UpdateStatusIndicator();
        }

        private void UpdateMusicButton()
        {
            bool playing = _music?.Playing ?? false;
            _btnMusic.Text = playing ? _texts.MusicOn : _texts.MusicOff;
            _btnMusic.IconMuted = !playing;

            // Off state: the colors dim exactly like the .muted class did on the web.
            _btnMusic.BorderColor = playing ? LauncherTheme.Gold : Color.FromArgb(85, 68, 51);
            _btnMusic.TextColor = playing ? LauncherTheme.LightGold : Color.FromArgb(136, 119, 102);
            _btnMusic.BackgroundTop = _btnMusic.BackgroundBottom = playing
                ? Color.FromArgb(217, 45, 25, 12)
                : Color.FromArgb(153, 20, 10, 5);
            _btnMusic.Invalidate();
        }

        private void UpdateStatusIndicator()
        {
            _statusIndicator.Online = _serverOnline;
            _statusIndicator.Caption = _serverOnline ? _texts.StatusOnline : _texts.StatusChecking;
            _statusIndicator.Invalidate();
        }

        private void ChangeTab(bool register)
        {
            HideAlert();
            _registerMode = register;
            _loginTab.Active = !register;
            _registerTab.Active = register;
            RebuildLayout();
        }

        private void ShowAlert(string message)
        {
            _lblAlert.Text = string.IsNullOrWhiteSpace(message) ? _texts.GenericError : message;
            _alertPanel.Visible = true;
            RebuildLayout();
        }

        private void HideAlert()
        {
            if (!_alertPanel.Visible) return;
            _alertPanel.Visible = false;
            RebuildLayout();
        }

        private void EnableControls(bool enabled)
        {
            _fieldUsername.Locked = !enabled;
            _fieldPassword.Locked = !enabled;
            _fieldRegUsername.Locked = !enabled;
            _fieldRegPassword.Locked = !enabled;
            _fieldRegNickname.Locked = !enabled;
            _btnConnect.Enabled = enabled;
            _btnCreate.Enabled = enabled;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Actions
        // ═══════════════════════════════════════════════════════════════════════

        private void CheckStatus()
        {
            bool online;
            try
            {
                online = LauncherService.GetStatus().Online;
            }
            catch
            {
                online = false;
            }

            if (online != _serverOnline)
            {
                _serverOnline = online;
                EnableControls(online);
            }

            _statusIndicator.Online = online;
            _statusIndicator.Caption = online ? _texts.StatusOnline : _texts.StatusOffline;
            _statusIndicator.Invalidate();

            _music?.KeepLooping();
        }

        private void SignIn()
        {
            if (!_serverOnline || _authenticated) return;
            HideAlert();

            string username = _fieldUsername.Value.Trim();
            string password = _fieldPassword.Value.Trim();

            LauncherService.SignInResult result;
            try
            {
                Cursor = Cursors.WaitCursor;
                result = LauncherService.SignIn(username, password, LauncherService.LocalIp);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            if (!result.Success)
            {
                ShowAlert(result.Message);
                return;
            }

            _token = result.Token;
            _account = string.IsNullOrEmpty(result.Nickname) ? username : result.Nickname;
            _authenticated = true;
            _fieldPassword.Value = "";
            _lblWelcome.Text = $"{_texts.Welcome} {_account.ToUpperInvariant()}!";
            RebuildLayout();
        }

        private void RegisterAccount()
        {
            if (!_serverOnline || _authenticated) return;
            HideAlert();

            var result = LauncherService.RegisterAccount(
                _fieldRegUsername.Value.Trim(),
                _fieldRegPassword.Value.Trim(),
                _fieldRegNickname.Value.Trim(),
                LauncherService.LocalIp);

            if (!result.Success)
            {
                ShowAlert(result.Message);
                return;
            }

            LauncherDialog.Show(this, Text, _texts.AccountCreatedMessage, _texts.DialogAccept);
            _fieldRegUsername.Value = "";
            _fieldRegPassword.Value = "";
            _fieldRegNickname.Value = "";
            ChangeTab(false);
        }

        private void LaunchGame()
        {
            var result = LauncherService.LaunchClient(_token);
            if (!result.Success)
            {
                ShowAlert(result.Message);
                return;
            }

            // Starting the client silences the launcher music.
            if (_music != null && _music.Playing)
            {
                _music.Stop();
                UpdateMusicButton();
            }

            // No confirmation popup here: the client window opening is confirmation enough, and a
            // modal dialog would just sit on top of the game waiting to be dismissed.
        }

        private void SignOut()
        {
            _token = "";
            _account = "";
            _authenticated = false;
            HideAlert();
            RebuildLayout();
        }

        private void ToggleMusic()
        {
            if (_music == null || !_music.Available) return;

            if (_music.Playing) _music.Pause();
            else _music.Play();

            UpdateMusicButton();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Event console
        // ═══════════════════════════════════════════════════════════════════════

        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;
        private const int MaxLines = 200;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Point point);

        private void RefreshLogs()
        {
            IReadOnlyList<LauncherService.LogEntry> entries;
            try
            {
                entries = LauncherService.GetLogs(_lastLogId);
            }
            catch
            {
                return;
            }
            if (entries.Count == 0) return;

            bool auto = _chkAutoScroll.Active;
            IntPtr handle = _console.Handle;
            var scrollPos = new Point();
            SendMessage(handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPos);
            int selectionStart = _console.SelectionStart;
            int selectionLength = _console.SelectionLength;

            SendMessage(handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                using var timeFont = LauncherTheme.CreateMonoFont(10f);
                foreach (var entry in entries)
                {
                    _lastLogId = Math.Max(_lastLogId, entry.Id);
                    AppendText($"[{entry.Time}]  ", LauncherTheme.LogTime, timeFont);
                    AppendText(entry.Message + Environment.NewLine, LineColor(entry.Message), _console.Font);
                }
                TrimConsole();

                if (!auto)
                {
                    _console.SelectionStart = Math.Min(selectionStart, _console.TextLength);
                    _console.SelectionLength = Math.Max(0, Math.Min(selectionLength, _console.TextLength - _console.SelectionStart));
                    SendMessage(handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPos);
                }
            }
            finally
            {
                SendMessage(handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _console.Invalidate();
            }

            if (auto)
            {
                _console.SelectionStart = _console.TextLength;
                _console.SelectionLength = 0;
                _console.ScrollToCaret();
            }
        }

        private void AppendText(string text, Color color, Font font)
        {
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectionColor = color;
            _console.SelectionFont = font;
            _console.AppendText(text);
        }

        /// <summary>Same color classification as the .log-* classes of the web interface.</summary>
        private static Color LineColor(string message)
        {
            if (message.Contains("[HAAPI]")) return LauncherTheme.LogHaapi;
            if (message.Contains("[Zaap]") || message.Contains("[Thrift]")) return LauncherTheme.LogZaap;
            if (message.Contains("[+]") || message.Contains("ONLINE")) return LauncherTheme.LogSuccess;
            if (message.Contains("[-]")) return LauncherTheme.LogError;
            if (message.Contains("[DatabaseManager]") || message.Contains("[World]")) return LauncherTheme.LogServer;
            if (message.Contains("[Anti-DDoS]") || message.Contains("Error")) return LauncherTheme.LogError;
            return LauncherTheme.LogNormal;
        }

        /// <summary>Keeps the console down to the last 200 lines, as the web version did.</summary>
        private void TrimConsole()
        {
            int total = _console.Lines.Length;
            if (total <= MaxLines) return;

            int cut = _console.GetFirstCharIndexFromLine(total - MaxLines);
            if (cut <= 0) return;

            _console.ReadOnly = false;
            _console.Select(0, cut);
            _console.SelectedText = "";
            _console.ReadOnly = true;
        }

        private void ClearConsole()
        {
            _console.Clear();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Service status indicator
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Glowing dot and caption of the .server-status block.</summary>
        private sealed class StatusIndicator : Control
        {
            public bool Online { get; set; }
            public string Caption { get; set; } = "";

            public StatusIndicator()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Color textColor = Online ? LauncherTheme.OnlineGreen : LauncherTheme.Red;
                Color dotColor = Online ? LauncherTheme.DotGreen : LauncherTheme.Red;

                var format = StringFormat.GenericTypographic;
                float textWidth = g.MeasureString(Caption, Font, PointF.Empty, format).Width + Caption.Length;
                int diameter = Math.Max(7, Height / 2);
                float total = diameter + 7 + textWidth;
                float x = Math.Max(0, (Width - total) / 2f);
                float y = (Height - diameter) / 2f;

                using (var halo = new SolidBrush(Color.FromArgb(70, dotColor)))
                {
                    g.FillEllipse(halo, x - 3, y - 3, diameter + 6, diameter + 6);
                }
                using (var dot = new SolidBrush(dotColor))
                {
                    g.FillEllipse(dot, x, y, diameter, diameter);
                }

                var area = new Rectangle((int)(x + diameter + 7), 0, (int)Math.Ceiling(textWidth) + 4, Height);
                LauncherTheme.DrawSpacedText(g, Caption, Font, textColor, area, 1f, ContentAlignment.MiddleLeft);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Startup
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the window on its own STA thread so it does not interfere with the asynchronous
        /// startup of the emulator servers. Closing it terminates the process, just like closing
        /// the browser window used to.
        /// </summary>
        public static void OpenOnDedicatedThread()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
                    Application.Run(new LauncherWindow());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] Launcher window error: {ex.Message}");
                }

                // Closing the window shuts down the whole emulator. We ask for a graceful shutdown
                // instead of killing the process outright, so the servers release their ports and
                // nothing is left running in the background.
                Program.RequestShutdown("launcher window closed");
            })
            {
                Name = "LauncherWindow",
                IsBackground = true
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }
}
