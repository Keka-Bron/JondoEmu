using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.Managers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jondo.Unity.Server.UI
{
    /// <summary>
    /// The server's Settings window: rates, caps and modes the operator chooses. They are saved to
    /// config\server_settings.json and apply when the server starts again, which the window offers
    /// to do at once -- telling the players in the world first, with a countdown.
    /// </summary>
    /// <remarks>
    /// Drawn like the main window rather than with Windows' grey controls: gold-titled cards in two
    /// columns that scroll when the screen is short, switches, steppers, and a footer that never
    /// scrolls away, with the buttons and what state the settings are in.
    /// </remarks>
    internal sealed class ServerSettingsWindow : Form
    {
        private ServerSettings _saved;
        private readonly Texts _t;
        private readonly float _escala;
        private int E(int px) => (int)Math.Round(px * _escala);

        private readonly NumberStepper _raidMin, _maxAp, _maxMp, _xp, _drop, _kamas, _randomLootChance;
        private readonly ToggleSwitch _hardcore, _noEnergy, _randomLoot;
        private readonly LauncherField _welcome;
        private readonly RarityTable _rarities;
        private readonly SettingNote _lootLevels;
        private readonly Body _body;
        private readonly SettingsCard[] _left, _right;
        private readonly LauncherButton _restart, _save, _close;
        private readonly Font _titleFont, _subtitleFont, _statusFont;

        private string _status = "";
        private Color _statusTone = LauncherTheme.MutedGold;
        private bool _confirming;
        private int _restartSeconds;

        /// <summary>The countdown when there are players in the world, and when there are none.</summary>
        public const int CountdownWithPlayers = 60;
        public const int CountdownEmpty = 3;

        private int HeaderHeight => E(78);
        private int FooterHeight => E(64);

        // ─── The words ─────────────────────────────────────────────────────────────────────

        /// <summary>The window's words, in the server window's three languages.</summary>
        private sealed class Texts
        {
            public string Culture, Caption, Title, Subtitle;
            public string Rates, Xp, Drop, Kamas, RatesHint;
            public string Characters, MaxAp, MaxMp, CapHint, NoEnergy, NoEnergyHint;
            public string Raids, RaidMin, RaidMinHint;
            public string Loot, RandomLoot, RandomLootHint, Chance, ChanceHint, LootLevels;
            public string Consumables, Equipment, Cosmetics, Creatures, Dofus, Legendary, Perfect, Percent;
            public string Modes, Hardcore, HardcoreHint;
            public string Welcome, WelcomeHint, WelcomePlaceholder;
            public string Close, Cancel, Back, Save, SaveRestart, ConfirmRestart;
            public string Unsaved, Pending, RestartAsk, RestartAskEmpty, Counting;
        }

        private static readonly Dictionary<Language, Texts> All = new()
        {
            [Language.Es] = new Texts
            {
                Culture = "es-ES",
                Caption = "Ajustes del servidor",
                Title = "AJUSTES DEL SERVIDOR",
                Subtitle = "Se guardan en config\\server_settings.json y se aplican al reiniciar el servidor. " +
                           "A quien esté en el mundo se le avisa antes por el chat.",
                Rates = "TASAS",
                Xp = "Bonus de experiencia", Drop = "Bonus de botín", Kamas = "Bonus de kamas",
                RatesHint = "Un porcentaje extra para todos. 0 = sin bonus.",
                Characters = "PERSONAJES",
                MaxAp = "PA máximos", MaxMp = "PM máximos",
                CapHint = "0 = sin tope (los oficiales: 12 PA y 6 PM).",
                NoEnergy = "Morir no cuesta energía", NoEnergyHint = "Perder un combate no gasta energía.",
                Raids = "RAIDS DE GREMIO",
                RaidMin = "Jugadores mínimos para iniciar una raid", RaidMinHint = "0 = los del juego (8).",
                Loot = "BOTÍN ALEATORIO",
                RandomLoot = "Botín aleatorio",
                RandomLootHint = "Además de su tabla, cada monstruo puede soltar de 2 a 5 objetos al azar.",
                Chance = "Probabilidad por monstruo", ChanceHint = "Los retos y el bonus de botín la suben.",
                LootLevels = "Equipo y consumibles a ±10 niveles del monstruo y dofus a ±20; monturas, mascotas " +
                             "y apariencia, hasta 10 por encima.",
                Consumables = "Consumibles", Equipment = "Equipo", Cosmetics = "Apariencia",
                Creatures = "Monturas y mascotas", Dofus = "Dofus", Legendary = "Legendario",
                Perfect = "Perfecto + exo PA/PM", Percent = "{0} %",
                Modes = "MODOS DE JUEGO",
                Hardcore = "Modo hardcore",
                HardcoreHint = "Monstruos al doble de tamaño, con el triple de vida y características.",
                Welcome = "BIENVENIDA",
                WelcomeHint = "Una línea en el chat al entrar al mundo. Vacío = ninguna.",
                WelcomePlaceholder = "Sin mensaje",
                Close = "CERRAR", Cancel = "CANCELAR", Back = "VOLVER", Save = "GUARDAR",
                SaveRestart = "GUARDAR Y REINICIAR", ConfirmRestart = "SÍ, REINICIAR",
                Unsaved = "Hay cambios sin guardar.",
                Pending = "Guardado. Se aplicará cuando el servidor se reinicie.",
                RestartAsk = "Hay {0} jugador(es) en el mundo: se les avisará por el chat y el servidor se reiniciará en {1} segundos.",
                RestartAskEmpty = "No hay nadie en el mundo: el servidor se reiniciará ahora.",
                Counting = "Ya hay un reinicio en cuenta atrás.",
            },
            [Language.En] = new Texts
            {
                Culture = "en-GB",
                Caption = "Server settings",
                Title = "SERVER SETTINGS",
                Subtitle = "Saved to config\\server_settings.json and applied when the server restarts. " +
                           "Anyone in the world is told in the chat first.",
                Rates = "RATES",
                Xp = "Experience bonus", Drop = "Drop bonus", Kamas = "Kamas bonus",
                RatesHint = "An extra percent for everybody. 0 = no bonus.",
                Characters = "CHARACTERS",
                MaxAp = "Most action points", MaxMp = "Most movement points",
                CapHint = "0 = no cap (the official ones: 12 AP and 6 MP).",
                NoEnergy = "Dying costs no energy", NoEnergyHint = "A lost fight spends no energy.",
                Raids = "GUILD RAIDS",
                RaidMin = "Fewest players to start a raid", RaidMinHint = "0 = the game's (8).",
                Loot = "RANDOM LOOT",
                RandomLoot = "Random loot",
                RandomLootHint = "Besides its table, each monster can drop 2 to 5 random items.",
                Chance = "Chance per monster", ChanceHint = "Challenges and the drop bonus raise it.",
                LootLevels = "Equipment and consumables within 10 levels of the monster, dofus within 20; mounts, " +
                             "pets and cosmetics up to 10 above.",
                Consumables = "Consumables", Equipment = "Equipment", Cosmetics = "Cosmetics",
                Creatures = "Mounts and pets", Dofus = "Dofus", Legendary = "Legendary",
                Perfect = "Perfect + AP/MP exo", Percent = "{0}%",
                Modes = "GAME MODES",
                Hardcore = "Hardcore mode",
                HardcoreHint = "Monsters at twice their size, with three times their life and characteristics.",
                Welcome = "WELCOME",
                WelcomeHint = "A line in the chat on coming into the world. Empty = none.",
                WelcomePlaceholder = "No message",
                Close = "CLOSE", Cancel = "CANCEL", Back = "BACK", Save = "SAVE",
                SaveRestart = "SAVE AND RESTART", ConfirmRestart = "YES, RESTART",
                Unsaved = "There are unsaved changes.",
                Pending = "Saved. It applies when the server restarts.",
                RestartAsk = "There are {0} player(s) in the world: they will be told in the chat and the server restarts in {1} seconds.",
                RestartAskEmpty = "Nobody is in the world: the server restarts now.",
                Counting = "A restart is already counting down.",
            },
            [Language.Fr] = new Texts
            {
                Culture = "fr-FR",
                Caption = "Réglages du serveur",
                Title = "RÉGLAGES DU SERVEUR",
                Subtitle = "Enregistrés dans config\\server_settings.json et appliqués au redémarrage du serveur. " +
                           "Les joueurs dans le monde sont prévenus dans le chat avant.",
                Rates = "TAUX",
                Xp = "Bonus d'expérience", Drop = "Bonus de butin", Kamas = "Bonus de kamas",
                RatesHint = "Un pourcentage en plus pour tous. 0 = sans bonus.",
                Characters = "PERSONNAGES",
                MaxAp = "PA maximum", MaxMp = "PM maximum",
                CapHint = "0 = sans plafond (les officiels : 12 PA et 6 PM).",
                NoEnergy = "Mourir ne coûte pas d'énergie", NoEnergyHint = "Perdre un combat ne dépense pas d'énergie.",
                Raids = "RAIDS DE GUILDE",
                RaidMin = "Joueurs minimum pour lancer un raid", RaidMinHint = "0 = ceux du jeu (8).",
                Loot = "BUTIN ALÉATOIRE",
                RandomLoot = "Butin aléatoire",
                RandomLootHint = "En plus de sa table, chaque monstre peut lâcher 2 à 5 objets au hasard.",
                Chance = "Chance par monstre", ChanceHint = "Les défis et le bonus de butin l'augmentent.",
                LootLevels = "Équipement et consommables à 10 niveaux du monstre, dofus à 20 ; montures, familiers " +
                             "et apparat jusqu'à 10 au-dessus.",
                Consumables = "Consommables", Equipment = "Équipement", Cosmetics = "Apparat",
                Creatures = "Montures et familiers", Dofus = "Dofus", Legendary = "Légendaire",
                Perfect = "Parfait + exo PA/PM", Percent = "{0} %",
                Modes = "MODES DE JEU",
                Hardcore = "Mode hardcore",
                HardcoreHint = "Monstres deux fois plus grands, avec trois fois leur vie et leurs caractéristiques.",
                Welcome = "BIENVENUE",
                WelcomeHint = "Une ligne dans le chat en entrant dans le monde. Vide = aucune.",
                WelcomePlaceholder = "Aucun message",
                Close = "FERMER", Cancel = "ANNULER", Back = "RETOUR", Save = "ENREGISTRER",
                SaveRestart = "ENREGISTRER ET REDÉMARRER", ConfirmRestart = "OUI, REDÉMARRER",
                Unsaved = "Des changements ne sont pas enregistrés.",
                Pending = "Enregistré. Cela s'appliquera au redémarrage du serveur.",
                RestartAsk = "Il y a {0} joueur(s) dans le monde : ils seront prévenus dans le chat et le serveur redémarre dans {1} secondes.",
                RestartAskEmpty = "Personne n'est dans le monde : le serveur redémarre maintenant.",
                Counting = "Un redémarrage est déjà en cours de décompte.",
            },
        };

        // ─── Building it ────────────────────────────────────────────────────────────────────

        public ServerSettingsWindow(Language language)
        {
            _escala = DeviceDpi / 96f;
            _t = All.TryGetValue(language, out var texts) ? texts : All[Language.Es];
            _saved = ServerSettings.Load();

            Text = _t.Caption;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;
            AutoScaleMode = AutoScaleMode.None;

            _titleFont = LauncherTheme.CreateFont(19f, FontStyle.Bold);
            _subtitleFont = LauncherTheme.CreateFont(12f);
            _statusFont = LauncherTheme.CreateFont(12.5f);

            // The inputs.
            _xp = Stepper(_saved.ExperienceBonusPercent, 0, 1000, 10, 0, "%");
            _drop = Stepper(_saved.DropBonusPercent, 0, 1000, 10, 0, "%");
            _kamas = Stepper(_saved.KamasBonusPercent, 0, 1000, 10, 0, "%");
            _maxAp = Stepper(_saved.MaxActionPoints, 0, 30, 1, 0, "");
            _maxMp = Stepper(_saved.MaxMovementPoints, 0, 20, 1, 0, "");
            _noEnergy = Toggle(_saved.NoEnergyLoss);
            _raidMin = Stepper(_saved.RaidMinPlayers, 0, 16, 1, 0, "");
            _randomLoot = Toggle(_saved.RandomLoot);
            _randomLootChance = Stepper((decimal)_saved.RandomLootChancePercent, 0, 100, 1, 2, "%");
            _hardcore = Toggle(_saved.Hardcore);
            _welcome = new LauncherField
            {
                Value = _saved.WelcomeMessage ?? "",
                Placeholder = _t.WelcomePlaceholder,
                MaxLength = 300,
                Height = E(38),
            };
            _welcome.ValueChanged += (s, e) => Changed();

            _rarities = new RarityTable(RarityLines(), _escala);
            _lootLevels = new SettingNote(_t.LootLevels);

            // The cards, in two columns.
            var rates = Card(_t.Rates,
                             Row(_t.Xp, null, _xp), Row(_t.Drop, null, _drop), Row(_t.Kamas, null, _kamas),
                             new SettingNote(_t.RatesHint));
            var characters = Card(_t.Characters,
                                  Row(_t.MaxAp, null, _maxAp), Row(_t.MaxMp, _t.CapHint, _maxMp),
                                  Row(_t.NoEnergy, _t.NoEnergyHint, _noEnergy));
            var raids = Card(_t.Raids, Row(_t.RaidMin, _t.RaidMinHint, _raidMin));
            var loot = Card(_t.Loot,
                            Row(_t.RandomLoot, _t.RandomLootHint, _randomLoot),
                            Row(_t.Chance, _t.ChanceHint, _randomLootChance),
                            _rarities, _lootLevels);
            var modes = Card(_t.Modes, Row(_t.Hardcore, _t.HardcoreHint, _hardcore));
            var welcome = Card(_t.Welcome, new SettingNote(_t.WelcomeHint), _welcome);
            _left = new[] { rates, characters, welcome };
            _right = new[] { loot, modes, raids };

            _body = new Body { BackColor = LauncherTheme.Background, AutoScroll = true };
            _body.HandleCreated += (s, e) => DarkScrollBars(_body);
            foreach (var card in _left.Concat(_right)) _body.Controls.Add(card);
            Controls.Add(_body);

            _randomLoot.CheckedChanged += (s, e) => ShowLootState();
            ShowLootState();

            // The footer's buttons.
            _restart = Button(_t.SaveRestart, primary: true, E(250));
            _restart.Click += (s, e) => RestartClicked();
            _save = Button(_t.Save, primary: false, E(150));
            _save.Click += (s, e) => SaveClicked();
            _close = Button(_t.Close, primary: false, E(140));
            _close.TextColor = LauncherTheme.MutedGold;
            _close.Click += (s, e) => CloseClicked();
            Controls.Add(_restart);
            Controls.Add(_save);
            Controls.Add(_close);

            LayOut();
            Changed();
        }

        private NumberStepper Stepper(decimal value, decimal min, decimal max, decimal step, int decimals, string suffix)
        {
            var stepper = new NumberStepper(value, min, max, step, decimals, suffix, _escala);
            stepper.ValueChanged += (s, e) => Changed();
            return stepper;
        }

        private ToggleSwitch Toggle(bool value)
        {
            var toggle = new ToggleSwitch(value, _escala);
            toggle.CheckedChanged += (s, e) => Changed();
            return toggle;
        }

        private SettingRow Row(string label, string hint, Control input) => new SettingRow(label, hint, input, _escala);

        private SettingsCard Card(string title, params Control[] rows)
        {
            var card = new SettingsCard(title, _escala);
            foreach (var row in rows) card.Add(row);
            return card;
        }

        /// <summary>The rarity table, from the random loot's own odds, commonest first.</summary>
        private IReadOnlyList<(string, string, Color)> RarityLines()
        {
            string Percent(double value)
                => string.Format(_t.Percent, value.ToString("0.##", CultureInfo.GetCultureInfo(_t.Culture)));
            double OddsOf(RandomLoot.Rarity rarity) => RandomLoot.Odds.First(o => o.Rarity == rarity).Percent;
            double consumables = 100.0 - RandomLoot.Odds.Sum(o => o.Percent);
            return new List<(string, string, Color)>
            {
                (_t.Consumables, Percent(consumables), LauncherTheme.MutedGold),
                (_t.Equipment, Percent(OddsOf(RandomLoot.Rarity.Equipment)), LauncherTheme.CardText),
                (_t.Cosmetics, Percent(OddsOf(RandomLoot.Rarity.Cosmetic)), Color.FromArgb(120, 190, 255)),
                (_t.Creatures, Percent(OddsOf(RandomLoot.Rarity.Creature)), LauncherTheme.OnlineGreen),
                (_t.Dofus, Percent(OddsOf(RandomLoot.Rarity.Dofus)), LauncherTheme.LightGold),
                (_t.Legendary, Percent(OddsOf(RandomLoot.Rarity.Legendary)), Color.FromArgb(255, 140, 58)),
                (_t.Perfect, Percent(OddsOf(RandomLoot.Rarity.Perfect)), LauncherTheme.PurpleBorder),
            };
        }

        private LauncherButton Button(string text, bool primary, int width) => new LauncherButton
        {
            Text = text,
            Font = LauncherTheme.CreateFont(13f, FontStyle.Bold),
            Height = E(38),
            Width = width,
            LetterSpacing = 1f,
            CornerRadius = E(6),
            BorderWidth = Math.Max(1, E(1)),
            BackgroundTop = primary ? LauncherTheme.GreenTop : Color.FromArgb(48, 30, 16),
            BackgroundBottom = primary ? LauncherTheme.GreenBottom : Color.FromArgb(30, 18, 10),
            BackgroundTopHighlight = primary ? LauncherTheme.GreenTopHover : LauncherTheme.LightBrown,
            BackgroundBottomHighlight = primary ? LauncherTheme.GreenBottomHover : Color.FromArgb(110, 65, 30),
            BorderColor = primary ? LauncherTheme.GreenBorder : LauncherTheme.BorderBrown,
            BorderColorHighlight = primary ? Color.FromArgb(200, 255, 120) : LauncherTheme.GoldBorder,
            TextColor = primary ? Color.White : LauncherTheme.SoftGold,
            TextColorHighlight = Color.White,
            BackgroundTopDisabled = Color.FromArgb(34, 22, 13),
            BackgroundBottomDisabled = Color.FromArgb(24, 15, 9),
            BorderColorDisabled = LauncherTheme.DisabledFieldBorder,
            TextColorDisabled = LauncherTheme.DisabledFieldText,
            TextShadow = true,
            Cursor = Cursors.Hand,
        };

        // ─── The layout ─────────────────────────────────────────────────────────────────────

        /// <summary>The cards in their columns, and the window as tall as they need, up to the screen.</summary>
        private void LayOut()
        {
            int width = E(960);
            int pad = E(24), gap = E(14);

            int Columns(int usable)
            {
                int column = (usable - pad * 2 - gap) / 2;
                int Stack(SettingsCard[] cards, int x)
                {
                    int y = E(16);
                    foreach (var card in cards)
                    {
                        int h = card.Arrange(column);
                        card.SetBounds(x, y + _body.AutoScrollPosition.Y, column, h);
                        y += h + gap;
                    }
                    return y - gap + E(16);
                }
                return Math.Max(Stack(_left, pad), Stack(_right, pad + column + gap));
            }

            int content = Columns(width);
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int chrome = Height - ClientSize.Height;
            int tallest = screen.Height - chrome - E(40);
            int client = Math.Min(HeaderHeight + content + FooterHeight, tallest);
            ClientSize = new Size(width, client);

            int bodyHeight = client - HeaderHeight - FooterHeight;
            _body.SetBounds(0, HeaderHeight, width, bodyHeight);
            // Short of room it scrolls, and the scroll bar takes its width from the columns.
            if (content > bodyHeight) Columns(width - SystemInformation.VerticalScrollBarWidth);

            int y = client - FooterHeight + (FooterHeight - _restart.Height) / 2 + Math.Max(1, E(1));
            _restart.Location = new Point(width - pad - _restart.Width, y);
            _save.Location = new Point(_restart.Left - E(10) - _save.Width, y);
            _close.Location = new Point(_save.Left - E(10) - _close.Width, y);
        }

        // ─── Painting the header and the footer ─────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = E(24);

            int titleHeight = TextRenderer.MeasureText("W", _titleFont).Height;
            SettingsPaint.Text(g, _t.Title, _titleFont, LauncherTheme.LightGold,
                               new Rectangle(pad, E(14), ClientSize.Width - pad * 2, titleHeight), wrap: false);
            SettingsPaint.Text(g, _t.Subtitle, _subtitleFont, LauncherTheme.MutedGold,
                               new Rectangle(pad, E(14) + titleHeight + E(4), ClientSize.Width - pad * 2,
                                             HeaderHeight - titleHeight - E(20)));

            // A gold line under the header, fading at both ends, and a plain one over the footer.
            var under = new Rectangle(0, HeaderHeight - Math.Max(1, E(1)), ClientSize.Width, Math.Max(1, E(1)));
            using (var fade = new LinearGradientBrush(under, Color.Transparent, Color.Transparent, 0f))
            {
                fade.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, LauncherTheme.GoldBorder), Color.FromArgb(170, LauncherTheme.GoldBorder),
                                     Color.FromArgb(0, LauncherTheme.GoldBorder) },
                    Positions = new[] { 0f, 0.5f, 1f },
                };
                g.FillRectangle(fade, under);
            }
            int footerTop = ClientSize.Height - FooterHeight;
            using (var footer = new SolidBrush(Color.FromArgb(20, 12, 6)))
                g.FillRectangle(footer, 0, footerTop, ClientSize.Width, FooterHeight);
            using (var line = new Pen(LauncherTheme.BorderBrown, Math.Max(1f, _escala)))
                g.DrawLine(line, 0, footerTop, ClientSize.Width, footerTop);

            // What state the settings are in, or the question before a restart.
            if (_status.Length > 0)
            {
                int left = pad, right = (_close.Visible ? _close.Left : _save.Left) - E(16);
                int h = SettingsPaint.HeightOf(_status, _statusFont, right - left);
                SettingsPaint.Text(g, _status, _statusFont, _statusTone,
                                   new Rectangle(left, footerTop + (FooterHeight - h) / 2, right - left, h));
            }
        }

        // ─── What it does ───────────────────────────────────────────────────────────────────

        /// <summary>The settings as the window has them now.</summary>
        private ServerSettings Edited()
        {
            var edited = _saved.Copy();
            edited.ExperienceBonusPercent = (int)_xp.Value;
            edited.DropBonusPercent = (int)_drop.Value;
            edited.KamasBonusPercent = (int)_kamas.Value;
            edited.MaxActionPoints = (int)_maxAp.Value;
            edited.MaxMovementPoints = (int)_maxMp.Value;
            edited.NoEnergyLoss = _noEnergy.Checked;
            edited.RaidMinPlayers = (int)_raidMin.Value;
            edited.RandomLoot = _randomLoot.Checked;
            edited.RandomLootChancePercent = (double)_randomLootChance.Value;
            edited.Hardcore = _hardcore.Checked;
            edited.WelcomeMessage = _welcome.Value.Trim();
            return edited;
        }

        private bool Dirty => Edited().DiffersFrom(_saved);

        /// <summary>Saved but not running yet: the server started with other settings.</summary>
        private bool Pending => _saved.DiffersFrom(ServerSettings.Current);

        /// <summary>Anything touched: the footer says where things stand, and a pending question is dropped.</summary>
        private void Changed()
        {
            if (_close == null) return;
            if (_confirming) LeaveConfirmation();
            bool dirty = Dirty;
            _save.Enabled = dirty;
            _restart.Enabled = dirty || Pending;
            _close.Text = dirty ? _t.Cancel : _t.Close;
            if (dirty) Status(_t.Unsaved, LauncherTheme.HighlightText);
            else if (Pending) Status(_t.Pending, LauncherTheme.OnlineGreen);
            else Status("", LauncherTheme.MutedGold);
        }

        private void Status(string text, Color tone)
        {
            _status = text;
            _statusTone = tone;
            Invalidate(new Rectangle(0, ClientSize.Height - FooterHeight, ClientSize.Width, FooterHeight));
        }

        /// <summary>The loot's chance and table only mean something with the loot on.</summary>
        private void ShowLootState()
        {
            bool on = _randomLoot.Checked;
            _randomLootChance.Enabled = on;
            _rarities.Enabled = on;
            _lootLevels.Enabled = on;
        }

        private void Save()
        {
            var edited = Edited();
            edited.Save();
            _saved = edited;
            Console.WriteLine($"[Settings] Saved to {Jondo.Unity.Launcher.Paths.ServerSettingsFile}; they apply at the next start.");
        }

        private void SaveClicked()
        {
            if (!Dirty) return;
            Save();
            Changed();
        }

        /// <summary>The first click asks, in the footer; the second restarts.</summary>
        private void RestartClicked()
        {
            if (ServerRestart.Counting)
            {
                Status(_t.Counting, LauncherTheme.Red);
                return;
            }
            if (!_confirming)
            {
                int players = ServerRestart.PlayersInside;
                _restartSeconds = players > 0 ? CountdownWithPlayers : CountdownEmpty;
                _confirming = true;
                _restart.Text = _t.ConfirmRestart;
                _close.Text = _t.Back;
                _save.Visible = false;
                _close.Location = new Point(_restart.Left - E(10) - _close.Width, _close.Top);
                Status(players > 0 ? string.Format(_t.RestartAsk, players, _restartSeconds) : _t.RestartAskEmpty,
                       LauncherTheme.HighlightText);
                return;
            }
            if (Dirty) Save();
            ServerRestart.Begin(_restartSeconds);
            Close();
        }

        private void LeaveConfirmation()
        {
            _confirming = false;
            _restart.Text = _t.SaveRestart;
            _save.Visible = true;
            _close.Location = new Point(_save.Left - E(10) - _close.Width, _close.Top);
        }

        private void CloseClicked()
        {
            if (!_confirming)
            {
                Close();
                return;
            }
            LeaveConfirmation();
            Changed();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                CloseClicked();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                SaveClicked();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        // ─── A dark title bar ───────────────────────────────────────────────────────────────

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private const int DarkMode = 20, BorderColour = 34, CaptionColour = 35, CaptionTextColour = 36;

        /// <summary>Windows' title bar in the window's own browns instead of white (Windows 11; older ones ignore it).</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, DarkMode, ref on, sizeof(int));
                int caption = ColourRef(Color.FromArgb(20, 12, 6));
                DwmSetWindowAttribute(Handle, CaptionColour, ref caption, sizeof(int));
                int text = ColourRef(LauncherTheme.SoftGold);
                DwmSetWindowAttribute(Handle, CaptionTextColour, ref text, sizeof(int));
                int border = ColourRef(LauncherTheme.BorderBrown);
                DwmSetWindowAttribute(Handle, BorderColour, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static int ColourRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subApp, string idList);

        /// <summary>Windows' own dark scroll bars, the ones Explorer uses, instead of the white ones.</summary>
        private static void DarkScrollBars(Control control)
        {
            try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _subtitleFont.Dispose();
                _statusFont.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>The scrolling body, double-buffered so the cards do not flicker as it moves.</summary>
        private sealed class Body : Panel
        {
            public Body()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }

            protected override void OnScroll(ScrollEventArgs se)
            {
                base.OnScroll(se);
                Invalidate(true);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                Invalidate(true);
            }
        }
    }
}
