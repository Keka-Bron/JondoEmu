using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Jondo.Unity.Server.UI
{
    /// <summary>
    /// The server's face.
    ///
    /// The log was seen in the launcher, which was the same process. Since they are two, the log
    /// belongs to the server and is seen here: whoever runs it has it in front of him without depending on a
    /// launcher being open, and the launcher handed out to the players is left without any way of
    /// reading anybody's console.
    ///
    /// It draws with <see cref="LauncherTheme"/> and <see cref="LauncherLogo"/>, which live in the
    /// contract and are shared by both executables: they look alike because they draw with the SAME thing, not because
    /// someone copied the colours from one place to the other.
    ///
    /// Everything shown comes from what the server already knows. Nothing invented to fill in.
    /// </summary>
    internal sealed class ServerWindow : Form, IBackgroundWindow
    {
        private Image? _foto;
        private Bitmap? _fondoCompuesto;

        /// <summary>The background already composed, so the panels cut out their piece.</summary>
        public Image? ComposedBackground => _fondoCompuesto;

        private readonly Panel _caja;
        private readonly FlickerFreePanel _cifras;
        private readonly LauncherLogView _registro;
        private readonly LauncherLogo _logo;
        private readonly System.Windows.Forms.Timer _reloj;
        private readonly CheckBox _seguir;
        private readonly LauncherButton _parar;
        private readonly LauncherButton _limpiar;
        private readonly List<LauncherButton> _idiomas = new();

        private long _ultimaLinea;
        private readonly DateTime _arranque = DateTime.UtcNow;
        private readonly System.Diagnostics.Process _yo = System.Diagnostics.Process.GetCurrentProcess();

        private Language _idioma = ServerPreferences.Language;
        private LauncherTexts _textos = LauncherTexts.Get(ServerPreferences.Language);

        private readonly float _escala;
        private int E(int px) => (int)Math.Round(px * _escala);
        private Font Letra(float cuerpo, FontStyle estilo = FontStyle.Regular)
            => new Font(LauncherTheme.TitleFamily, cuerpo * _escala, estilo);
        private Font Mono(float cuerpo) => new Font(LauncherTheme.MonoFamily, cuerpo * _escala);

        /// <summary>
        /// A panel that does not flicker on repainting.
        ///
        /// The figures refresh every second and gave a blink on each one: a normal Panel
        /// erases the background and then draws, and between the two the gap is seen. With double buffering it is
        /// composed off-screen and dumped in one go.
        /// </summary>
        private sealed class FlickerFreePanel : Panel
        {
            public FlickerFreePanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }
        }

        /// <summary>Each figure: its label, where it comes from and what colour it goes in.</summary>
        private sealed class Metric
        {
            public Func<LauncherTexts, string> Etiqueta = _ => "";
            public Func<string> Valor = () => "";
            public Color Tono = LauncherTheme.LightGold;
            public string Ultimo = "";

            /// <summary>Whether it is a block's header instead of a datum.</summary>
            public bool EsGrupo;
        }

        private readonly List<Metric> _lista = new();

        public ServerWindow()
        {
            _escala = DeviceDpi / 96f;

            Text = "Jondo Server";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(E(900), E(560));
            Size = new Size(E(1280), E(760));
            // Maximised, which is how a server wants to be seen: at a glance and without arranging it.
            WindowState = FormWindowState.Maximized;
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;
            DoubleBuffered = true;
            TryCargarIcono();
            _foto = Espejar(LauncherTheme.LoadImage("servidor_fondo.jpg") ?? LauncherTheme.LoadImage("bg.jpg"));

            _logo = new LauncherLogo
            {
                Primera = "JONDO",
                Segunda = "SERVER",
                BackColor = Color.Transparent,
                Height = E(92),
                Dock = DockStyle.Top,
            };

            // The four indicators in ONE column on the left.
            //
            // Before, they were spread on both sides of the drawing, and it was pretty but it stole
            // half the width from the log: the console was left as a strip a quarter of the
            // window wide where each line broke into three. A log that has to be rebuilt
            // mentally is not read. The four together on one side and the rest for the console.
            _cifras = new FlickerFreePanel
            {
                Dock = DockStyle.Left,
                Width = 0,   // AjustarConsola sets it
                BackColor = Color.Transparent,
            };
            _cifras.Paint += (s, e) => ConRed(e.Graphics, _cifras, 0, 4);

            DefinirCifras();

            // The console takes everything the figures do not take up.
            //
            // It was at the bottom full width —it cut the drawing in half— and then in a column on
            // the right, which let the whole drawing be seen but gave it a quarter of the window. A
            // traffic line is some hundred characters and they do not fit there: it broke into three and had
            // to be put back together by eye. Now the log rules and the drawing is seen behind it.
            _caja = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(E(10), E(14), E(22), E(10)),
            };

            var barra = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = E(40),
                BackColor = Color.Transparent,
                Padding = new Padding(E(22), 0, E(22), 0),
            };

            // The log is drawn by hand so that the drawing is seen behind it.
            //
            // With a RichTextBox there was no way: a WinForms text box is opaque and there is no
            // transparent colour that changes it, so the log was a black rectangle stuck
            // on top of Nox. LauncherLogView inherits from LauncherPanel, which cuts out the piece of background
            // that belongs to it and puts on top the colour layers it is told; the dark veil that
            // makes the text readable is a layer with alpha and the drawing is still seen beneath.
            //
            // It is paid for with copy and paste: they are drawn lines, not selectable text. The
            // whole log is still written to logs\, which is where it has to be taken from.
            _registro = new LauncherLogView
            {
                Dock = DockStyle.Fill,
                ForeColor = LauncherTheme.LogNormal,
                CornerRadius = E(6),
                BorderColor = LauncherTheme.GoldBorder,
                BorderWidth = Math.Max(2, E(2)),
                Padding = new Padding(E(10), E(8), E(10), E(8)),

                // The font goes in PIXELS and without multiplying by the monitor's scale.
                //
                // Before it was «Mono(6f)», which inside did 6 × DeviceDpi/96. A size in points
                // is already DPI-independent, so multiplying it doubles it: on a
                // 192 dpi monitor it came out at twelve points and that is why the log looked huge and broke. It is
                // the same bug already fixed in the deobfuscator.
                Font = LauncherTheme.CreateMonoFont(12f),
            };
            _registro.Layers.Add(LauncherTheme.ConsoleFill);

            // Right-click on a packet to say what it is. The real names are in the
            // client but orphaned —nothing says which goes with which—, so the binding is done by a
            // person with the packet in front, choosing from the real list. What is chosen is stored
            // and from then on it comes out in the log.
            _registro.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) Bautizar(e.Location); };

            _caja.Controls.Add(_registro);

            // The order matters: WinForms docks from FRONT to back, that is the reverse of the order in
            // which they are added. It reads from bottom to top: the title takes its strip at the top, the bar
            // the one at the bottom, the figures the left column, and the console —which goes in Fill— keeps
            // EVERYTHING left over.
            //
            // That is why the console is added first even though it comes out last: in Fill one has to be
            // at the bottom of the stack or one eats the others' space.
            Controls.Add(_caja);
            Controls.Add(_cifras);
            Controls.Add(barra);
            Controls.Add(_logo);

            _seguir = new CheckBox
            {
                Checked = true,
                ForeColor = LauncherTheme.MutedGold,
                BackColor = Color.Transparent,
                AutoSize = true,
                Font = LauncherTheme.CreateFont(11f * _escala),
                Location = new Point(E(2), E(13)),
            };
            barra.Controls.Add(_seguir);

            foreach (var cual in new[] { Language.Es, Language.En, Language.Fr })
            {
                var boton = Boton(LauncherTexts.Code(cual).ToUpperInvariant(), LauncherTheme.MutedGold, E(46));
                var elegido = cual;
                boton.Click += (s, e) => CambiarIdioma(elegido);
                _idiomas.Add(boton);
                barra.Controls.Add(boton);
            }

            _limpiar = Boton("", LauncherTheme.SoftGold);
            _limpiar.Click += (s, e) => _registro.Wipe();
            barra.Controls.Add(_limpiar);

            _parar = Boton("", LauncherTheme.Red);
            _parar.Click += PararloTodo;
            barra.Controls.Add(_parar);

            void Colocar()
            {
                _parar.Location = new Point(barra.Width - _parar.Width - E(2), E(7));
                _limpiar.Location = new Point(_parar.Left - _limpiar.Width - E(10), E(7));
                int x = _seguir.Right + E(18);
                foreach (var boton in _idiomas)
                {
                    boton.Location = new Point(x, E(7));
                    x += boton.Width + E(6);
                }
            }
            barra.Resize += (s, e) => Colocar();

            AplicarIdioma();
            AjustarConsola();
            Colocar();

            _reloj = new System.Windows.Forms.Timer { Interval = 1000 };
            _reloj.Tick += (s, e) => Refrescar();
            _reloj.Start();

            Refrescar();
        }

        // ─── Idioma ─────────────────────────────────────────────────────────────────────────

        private void CambiarIdioma(Language cual)
        {
            if (cual == _idioma) return;
            _idioma = cual;
            ServerPreferences.Language = cual;
            _textos = LauncherTexts.Get(cual);
            AplicarIdioma();
            _cifras.Invalidate();

        }

        private void AplicarIdioma()
        {
            _seguir.Text = _textos.AutoScroll;
            _limpiar.Text = _textos.ClearButton;
            _parar.Text = _textos.StopServer;
            Redimensionar(_limpiar);
            Redimensionar(_parar);

            for (int i = 0; i < _idiomas.Count; i++)
            {
                var cual = (Language)i;
                _idiomas[i].TextColor = cual == _idioma ? LauncherTheme.LightGold : LauncherTheme.MutedGold;
                _idiomas[i].Active = cual == _idioma;
                _idiomas[i].BorderColor = cual == _idioma
                    ? LauncherTheme.GoldBorder : LauncherTheme.BorderBrown;
            }
        }

        private void Redimensionar(LauncherButton boton)
            => boton.Width = TextRenderer.MeasureText(boton.Text, boton.Font).Width + E(34);

        // ─── The figures ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What is shown, in its order and grouped.
        ///
        /// Grouped and in a column, not in a cramped row at the top: that way the ones needed
        /// to run a server fit —traffic, CPU, threads— and are read at a glance by blocks.
        ///
        /// All of them come from something the server really knows. None is there to fill in:
        /// if it cannot be measured, it is not shown.
        /// </summary>
        private void DefinirCifras()
        {
            Grupo(t => t.GroupWorld);

            // Connected is the number of live game sockets. "En el mundo" are the ones that have also
            // managed to enter a map: between one and the other there are a few seconds of loading, and
            // with a stuck client the difference stays there and is seen.
            Cifra_(t => t.StatPlayers, LauncherTheme.OnlineGreen,
                   () => $"{Network.GameNodeProxy.SesionesVivas.Count}/{Contract.ClientesEnTotal}");
            Cifra_(t => t.StatInWorld, LauncherTheme.DotGreen, () =>
            {
                int dentro = 0;
                foreach (var s in Network.GameNodeProxy.SesionesVivas.Values) if (s.IsInWorld) dentro++;
                return dentro.ToString();
            });
            Cifra_(t => t.StatFights, LauncherTheme.Red,
                   () => Handlers.FightHandler.CombatesEnCurso.ToString());
            Cifra_(t => t.StatMaps, LauncherTheme.LogHaapi, () =>
            {
                var mapas = new HashSet<long>();
                foreach (var s in Network.GameNodeProxy.SesionesVivas.Values)
                {
                    if (s.IsInWorld) mapas.Add(s.MapId);
                }
                return mapas.Count.ToString();
            });
            Cifra_(t => t.StatClients, LauncherTheme.LightGold,
                   () => Network.ClientLaunchRegistry.ActiveCount.ToString());

            Grupo(t => t.GroupNetwork);

            // What has gone through the sockets since it started, and at what rate it goes now. The rate
            // is what says whether the server is doing something: the totals only say it did.
            Cifra_(t => t.StatSent, LauncherTheme.LogSuccess,
                   () => $"{Bonito(Jondo.Protocol.NetworkMessage.BytesFuera)}  " +
                         $"({Miles(Jondo.Protocol.NetworkMessage.PaquetesFuera)})");
            Cifra_(t => t.StatReceived, LauncherTheme.LogZaap,
                   () => $"{Bonito(Jondo.Protocol.NetworkMessage.BytesDentro)}  " +
                         $"({Miles(Jondo.Protocol.NetworkMessage.PaquetesDentro)})");
            Cifra_(t => t.StatRate, LauncherTheme.LightGold, () => $"{_porSegundo:0.0} KB/s");

            Grupo(t => t.GroupMachine);

            Cifra_(t => t.StatCpu, LauncherTheme.LogHaapi, () => $"{_cpu:0.0} %");
            Cifra_(t => t.StatMemory, LauncherTheme.SoftGold, () =>
            {
                _yo.Refresh();
                return Bonito(_yo.WorkingSet64);
            });
            Cifra_(t => t.StatThreads, LauncherTheme.LogZaap, () =>
            {
                _yo.Refresh();
                return _yo.Threads.Count.ToString();
            });
            Cifra_(t => t.StatUptime, LauncherTheme.OnlineGreen, () =>
            {
                var va = DateTime.UtcNow - _arranque;
                return va.TotalDays >= 1 ? $"{(int)va.TotalDays}d {va.Hours}h"
                     : va.TotalHours >= 1 ? $"{(int)va.TotalHours}h {va.Minutes:00}m"
                     : $"{va.Minutes}m {va.Seconds:00}s";
            });

            Grupo(t => t.GroupLoaded);

            // This does not change during the whole run, but it says at a glance whether the world loaded
            // whole or whether something was left half done, which is the first thing one wants to know.
            Cifra_(t => t.StatWorldMaps, LauncherTheme.SoftGold,
                   () => Miles(Managers.MobSpawnManager.MapasConGrupos));
            Cifra_(t => t.StatWorldGroups, LauncherTheme.MutedGold,
                   () => Miles(Managers.MobSpawnManager.TotalGrupos));
            Cifra_(t => t.StatWorldNpcs, LauncherTheme.HighlightText,
                   () => Miles(Managers.Npcs.Count));
        }

        private void Grupo(Func<LauncherTexts, string> titulo)
            => _lista.Add(new Metric { Etiqueta = titulo, EsGrupo = true });

        private void Cifra_(Func<LauncherTexts, string> etiqueta, Color tono, Func<string> valor)
            => _lista.Add(new Metric { Etiqueta = etiqueta, Tono = tono, Valor = valor });

        private static string Bonito(long bytes)
            => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
             : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0} MB"
             : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
             : $"{bytes} B";

        private static string Miles(long cuantos) => cuantos.ToString("N0");

        // ─── CPU and rate, which have to be measured between two moments ────────────────────

        private TimeSpan _cpuAntes;
        private DateTime _cuandoAntes = DateTime.UtcNow;
        private long _bytesAntes;
        private double _cpu;
        private double _porSegundo;

        private void MedirRitmos()
        {
            try
            {
                _yo.Refresh();
                var ahora = DateTime.UtcNow;
                double segundos = (ahora - _cuandoAntes).TotalSeconds;
                if (segundos <= 0) return;

                var cpuAhora = _yo.TotalProcessorTime;
                // Across all the cores: otherwise, a server using one whole core out of eight
                // would show 100% and would seem to be choking when it has seven to spare.
                _cpu = (cpuAhora - _cpuAntes).TotalSeconds / segundos / Environment.ProcessorCount * 100.0;
                _cpuAntes = cpuAhora;

                long bytesAhora = Jondo.Protocol.NetworkMessage.BytesFuera +
                                  Jondo.Protocol.NetworkMessage.BytesDentro;
                _porSegundo = (bytesAhora - _bytesAntes) / 1024.0 / segundos;
                _bytesAntes = bytesAhora;

                _cuandoAntes = ahora;
            }
            catch { }
        }

        private bool _yaAvise;

        /// <summary>
        /// Wraps a column's painting. A Paint that blows up is not seen: the column stays
        /// blank and there is not even a warning. It already happened once and cost more than it should.
        /// </summary>
        private void ConRed(Graphics g, Control panel, int desde, int cuantos)
        {
            try { PintarColumna(g, panel, desde, cuantos); }
            catch (Exception ex)
            {
                if (_yaAvise) return;
                _yaAvise = true;
                Console.WriteLine($"[Servidor] Una columna de cifras no se ha podido pintar: {ex}");
            }
        }

        /// <summary>
        /// The figures column: one card per block and inside one line per datum.
        ///
        /// The first version was a row of cards cramped at the top. Six fitted and were already
        /// tight; in a column the fifteen needed to run a server fit, and grouped
        /// they are read by blocks instead of as a string.
        /// </summary>
        private void PintarColumna(Graphics g, Control panel, int desdeGrupo, int cuantosGrupos)
        {
            RecortarFondo(g, panel);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int margen = E(12);
            int ancho = panel.Width - margen * 2;
            if (ancho <= 0) return;

            using var fGrupo = LauncherTheme.CreateFont(10f * _escala, FontStyle.Bold);
            using var fEtiqueta = LauncherTheme.CreateFont(9.5f * _escala);
            // The value goes at the SAME size as its label, only bold and coloured. It went
            // three points bigger and in blocks like RED —where the value is "0,0 KB/s" and
            // not a short number— the line was out of line: the small word and the datum
            // huge beside it, for no reason at all.
            using var fValor = LauncherTheme.CreateFont(9.5f * _escala, FontStyle.Bold);
            using var pincelGrupo = new SolidBrush(LauncherTheme.SoftGold);
            using var pincelEtiqueta = new SolidBrush(LauncherTheme.MutedGold);
            using var relleno = new SolidBrush(LauncherTheme.CardFill);
            using var borde = new Pen(LauncherTheme.BorderBrown, Math.Max(1f, _escala));
            using var izquierda = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.EllipsisCharacter,
            };
            using var derecha = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.EllipsisCharacter,
                Alignment = StringAlignment.Far,
            };

            int altoLinea = E(21);
            int y = E(6);

            // Only the blocks belonging to THIS column are painted: the left one carries the
            // first two and the right one the next two.
            int vistos = -1;
            for (int i = 0; i < _lista.Count; i++)
            {
                if (!_lista[i].EsGrupo) continue;
                vistos++;
                if (vistos < desdeGrupo) continue;
                if (vistos >= desdeGrupo + cuantosGrupos) break;

                int cuantas = 0;
                for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++) cuantas++;

                int altoCaja = E(20) + cuantas * altoLinea + E(8);
                var caja = new Rectangle(margen, y, ancho, altoCaja);
                if (caja.Bottom > panel.Height) break;

                using (var camino = Redondeado(caja, E(7)))
                {
                    g.FillPath(relleno, camino);
                    g.DrawPath(borde, camino);
                }

                g.DrawString(_lista[i].Etiqueta(_textos), fGrupo, pincelGrupo,
                             new RectangleF(caja.X + E(10), caja.Y + E(5), caja.Width - E(20),
                                            fGrupo.GetHeight(g) + 2), izquierda);

                int linea = caja.Y + E(22);
                for (int j = i + 1; j < _lista.Count && !_lista[j].EsGrupo; j++)
                {
                    var cifra = _lista[j];
                    var hueco = new RectangleF(caja.X + E(10), linea, caja.Width - E(20), altoLinea);

                    // Both take the WHOLE WIDTH, one stuck to the left and the other to the
                    // right, instead of splitting the line into two halves. With halves, a
                    // label like "JUGADORES" did not fit in its own and came out cut even though
                    // there was plenty of room to spare beside it, because the value's slot was empty.
                    g.DrawString(cifra.Etiqueta(_textos), fEtiqueta, pincelEtiqueta,
                                 new RectangleF(hueco.X, hueco.Y + E(3), hueco.Width,
                                                fEtiqueta.GetHeight(g) + 2), izquierda);

                    using var pincelValor = new SolidBrush(cifra.Tono);
                    // At the same height as the label: with the same body, if one starts three
                    // pixels higher than the other it shows that they are misplaced.
                    g.DrawString(cifra.Ultimo, fValor, pincelValor,
                                 new RectangleF(hueco.X, hueco.Y + E(3), hueco.Width,
                                                fValor.GetHeight(g) + 2), derecha);
                    linea += altoLinea;
                }

                y = caja.Bottom + E(8);
            }
        }

        private static GraphicsPath Redondeado(Rectangle r, int radio)
        {
            var camino = new GraphicsPath();
            int d = Math.Max(2, radio * 2);
            camino.AddArc(r.X, r.Y, d, d, 180, 90);
            camino.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            camino.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            camino.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            camino.CloseFigure();
            return camino;
        }

        // ─── The background ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Composes the background: the photo cropped as a background-size: cover would.
        ///
        /// It is composed ONCE per size and kept, instead of scaling the image on each
        /// repaint. It is what the launcher does and it is what keeps the window from stuttering.
        /// </summary>
        /// <summary>
        /// The background without the watermark and flipped, so that Nox ends up behind the log.
        ///
        /// The drawing brings Nox on the left, which is exactly where the figures column goes: he was
        /// covered. Flipped he falls behind the log, which is now translucent, and he is seen whole.
        ///
        /// The Wakfu logo and the copyright line live in the drawing's right strip, and
        /// mirrored they would come out written backwards. The first attempt was to paste that corner back without
        /// flipping, and the box was noticeable a mile off: an unflipped piece inside a flipped
        /// image always leaves a seam. So the strip is CROPPED before flipping. Cropping and
        /// not painting over is what leaves no artefacts: there is nothing to disguise, that
        /// part of the drawing simply is not there.
        /// </summary>
        private static Image? Espejar(Image? foto)
        {
            if (foto == null) return null;

            try
            {
                // The right 18% amply covers the logo and the copyright, and what is lost
                // is the edge of the pine wood, which nobody misses.
                int ancho = Math.Max(1, (int)(foto.Width * 0.82f));
                var recorte = new Rectangle(0, 0, ancho, foto.Height);

                var espejo = new Bitmap(ancho, foto.Height);
                using (var g = Graphics.FromImage(espejo))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(foto, new Rectangle(0, 0, ancho, foto.Height), recorte, GraphicsUnit.Pixel);
                }

                espejo.RotateFlip(RotateFlipType.RotateNoneFlipX);
                foto.Dispose();
                return espejo;
            }
            catch
            {
                // If something fails, the original background works all the same: Nox will be seen covered by the
                // cards, which is how it was before, but the window opens.
                return foto;
            }
        }

        private void ComponerFondo()
        {
            int ancho = Math.Max(1, ClientSize.Width);
            int alto = Math.Max(1, ClientSize.Height);
            if (_fondoCompuesto != null && _fondoCompuesto.Width == ancho && _fondoCompuesto.Height == alto) return;

            _fondoCompuesto?.Dispose();
            _fondoCompuesto = new Bitmap(ancho, alto);

            using var g = Graphics.FromImage(_fondoCompuesto);
            g.Clear(LauncherTheme.Background);

            if (_foto != null)
            {
                float factor = Math.Max((float)ancho / _foto.Width, (float)alto / _foto.Height);
                int anchoFoto = (int)Math.Ceiling(_foto.Width * factor);
                int altoFoto = (int)Math.Ceiling(_foto.Height * factor);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_foto, (ancho - anchoFoto) / 2, (alto - altoFoto) / 2, anchoFoto, altoFoto);
            }

            // A VERY soft veil, only what is needed so that what is on top is readable. The first attempt
            // carried one so dark that the photo could not be seen: it looked like a black background, full stop.
            using var velo = new SolidBrush(Color.FromArgb(84, 8, 4, 2));
            g.FillRectangle(velo, 0, 0, ancho, alto);
        }

        /// <summary>Gives a transparent panel the piece of background that belongs to it.</summary>
        private void RecortarFondo(Graphics g, Control panel)
        {
            ComponerFondo();
            if (_fondoCompuesto == null) { g.Clear(LauncherTheme.Background); return; }

            var recorte = Rectangle.Intersect(
                new Rectangle(panel.Location.X, panel.Location.Y, panel.Width, panel.Height),
                new Rectangle(0, 0, _fondoCompuesto.Width, _fondoCompuesto.Height));

            g.Clear(LauncherTheme.Background);
            if (recorte.Width <= 0 || recorte.Height <= 0) return;
            g.DrawImage(_fondoCompuesto, new Rectangle(0, 0, recorte.Width, recorte.Height),
                        recorte, GraphicsUnit.Pixel);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ComponerFondo();
            if (_fondoCompuesto != null) e.Graphics.DrawImageUnscaled(_fondoCompuesto, 0, 0);
            else base.OnPaintBackground(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AjustarConsola();
            ComponerFondo();
            Invalidate(true);
        }

        /// <summary>
        /// How much the console takes up: a little over half the window, not all of it.
        ///
        /// It is a proportion and not a fixed height so that it looks equally good maximised on a
        /// laptop as on a big monitor. And with a minimum, so that narrowing the window does not
        /// leave it at two lines.
        /// </summary>
        private void AjustarConsola()
        {
            // It can arrive early: setting WindowState = Maximized in the constructor already
            // fires an OnResize, and at that moment there is still no panel to adjust. Without this
            // line the window did not even open -and the failure came out as a terse "Object reference
            // not set" in the log-.
            if (_caja == null) return;

            // The console is no longer sized: it goes in Fill and keeps whatever is left. The only thing
            // to decide is how much the figures column takes, and it is given just enough for
            // its two numbers per line to fit without eating space from the log.
            int columna = Math.Max(E(260), Math.Min((int)(ClientSize.Width * 0.20f), E(420)));
            if (_cifras != null) _cifras.Width = columna;
        }

        /// <summary>
        /// The background behind the console, and a frame around it.

        // ─── The heartbeat ──────────────────────────────────────────────────────────────────

        private void Refrescar()
        {
            MedirRitmos();

            bool cambio = false;
            foreach (var cifra in _lista)
            {
                if (cifra.EsGrupo) continue;
                string ahora;
                try { ahora = cifra.Valor(); }
                catch { ahora = "—"; }
                if (ahora != cifra.Ultimo) { cifra.Ultimo = ahora; cambio = true; }
            }
            // It is only repainted if something has changed, and the panel is double buffered: that way it does not flicker.
            if (cambio) _cifras.Invalidate();

            TraerRegistro();
        }

        private void TraerRegistro()
        {
            string json;
            try { json = ConsoleLogBuffer.GetLogsJson(_ultimaLinea); }
            catch { return; }

            var nuevas = new List<(long Id, string Hora, string Texto)>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("logs", out var lista)) return;
                foreach (var linea in lista.EnumerateArray())
                {
                    nuevas.Add((
                        linea.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                        linea.TryGetProperty("time", out var h) ? (h.GetString() ?? "") : "",
                        linea.TryGetProperty("msg", out var m) ? (m.GetString() ?? "") : ""));
                }
            }
            catch { return; }

            if (nuevas.Count == 0) return;

            foreach (var (id, hora, texto) in nuevas)
            {
                if (id > _ultimaLinea) _ultimaLinea = id;
                Escribir(hora, texto);
            }

            // Neither trimming nor moving the cursor down: the view takes care of both, since it knows how many
            // lines it keeps and whether it is stuck to the bottom.
            _registro.Follow = _seguir.Checked;
        }

        /// <summary>
        /// A traffic line, just as NetworkMessage writes it:
        ///
        ///   1579 [server&gt;client] kuf (CharacterExperienceGainEvent) { 1: 453 }        3 B
        ///
        /// It is recognised by its whole shape and not by a loose piece, so that a normal server
        /// message that happens to carry brackets does not end up painted as if it were a packet.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex Paquete = new(
            @"^(\s*\d+) (\[(?:client>server|server>client)\]) ([a-z]{3})( \([A-Za-z0-9_]+\))?(.*?)(\s+\d+ B)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Naming the packet under the mouse.
        ///
        /// It only opens if the line is a packet: on a normal server line there is nothing
        /// to christen and a menu that always comes up ends up coming up when it should not.
        /// </summary>
        private void Bautizar(Point donde)
        {
            var paquete = Paquete.Match(_registro.TextAt(donde));
            if (!paquete.Success) return;

            string opcode = paquete.Groups[3].Value;
            using var elegir = new NamePicker(opcode, Managers.NameBinding.Meaning(opcode),
                                              Managers.NameBinding.Of(opcode), _escala);

            if (elegir.ShowDialog(this) != DialogResult.OK) return;

            Managers.NameBinding.Bind(opcode, elegir.Chosen);
            Console.WriteLine(elegir.Chosen.Length > 0
                ? $"[Nombres] {opcode} es {elegir.Chosen}."
                : $"[Nombres] {opcode} vuelve a estar sin ligar.");
        }

        private void Escribir(string hora, string texto)
        {
            // An entry can bring SEVERAL lines inside.
            //
            // ConsoleLogBuffer keeps whatever reaches Console.WriteLine as is, and there are places
            // that write several lines at once —the dump of an unhandled packet, with its
            // dashes and its field tree—. The old RichTextBox split them on its own; the new
            // view draws each entry at ONE position, so a text with line breaks inside came out
            // perched on top of the next one and nothing could be read.
            //
            // The time goes only on the first. The rest carry their blank slot so that the text
            // stays aligned in the same column.
            string[] renglones = texto.Replace("\r", "").Split('\n');
            string sangria = hora.Length > 0 ? new string(' ', hora.Length + 2) : "";

            for (int i = 0; i < renglones.Length; i++)
            {
                var trozos = new List<LauncherLogView.Piece>(7);
                if (hora.Length > 0)
                    trozos.Add(new(i == 0 ? hora + "  " : sangria, LauncherTheme.LogTime));

                var paquete = Paquete.Match(renglones[i]);
                if (paquete.Success) Paquete_(paquete, trozos);
                else trozos.Add(new(renglones[i], ColorDe(renglones[i])));

                _registro.Add(trozos.ToArray());
            }
        }

        /// <summary>
        /// A packet's line, each piece in its colour.
        ///
        /// The colours are not decorative: the eye looks for the opcode, which goes in light, and the rest
        /// steps aside. The direction carries the same code as the rest of the emulator —blue what comes down
        /// from the server, gold what goes up from the client— so it does not have to be read.
        /// </summary>
        private static void Paquete_(System.Text.RegularExpressions.Match m,
                                     List<LauncherLogView.Piece> trozos)
        {
            void Trozo(string texto, Color color)
            {
                if (texto.Length > 0) trozos.Add(new LauncherLogView.Piece(texto, color));
            }

            string direccion = m.Groups[2].Value;

            Trozo(m.Groups[1].Value + " ", LauncherTheme.LogTime);
            Trozo(direccion + " ", direccion.Contains("server>") ? LauncherTheme.LogZaap : LauncherTheme.LogServer);
            Trozo(m.Groups[3].Value, LauncherTheme.HighlightText);
            Trozo(m.Groups[4].Value, LauncherTheme.LogNormal);
            Trozo(m.Groups[5].Value, LauncherTheme.LightBrownText);
            Trozo(m.Groups[6].Value, LauncherTheme.LogTime);
        }

        /// <summary>
        /// Each line's colour, with the same console palette as the launcher and looking at the
        /// same bracketed prefixes the server has always been writing.
        /// </summary>
        private static Color ColorDe(string linea)
        {
            if (linea.Contains("[!]") || linea.Contains("Error") || linea.Contains("error"))
                return LauncherTheme.LogError;
            if (linea.Contains("Rechazad") || linea.Contains("rechazad")) return LauncherTheme.Red;
            if (linea.Contains("[HAAPI]")) return LauncherTheme.LogHaapi;
            if (linea.Contains("[Zaap")) return LauncherTheme.LogZaap;
            if (linea.Contains("[+]")) return LauncherTheme.LogSuccess;
            if (linea.Contains("[Combate]") || linea.Contains("[FightHandler]")) return LauncherTheme.LogServer;
            if (linea.Contains("[Control]") || linea.Contains("[Comandos]")) return LauncherTheme.HighlightText;
            return LauncherTheme.LogNormal;
        }

        // ─── Botones ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A button in the launcher's style.
        ///
        /// Before, they were flat WinForms <see cref="Button"/>s, with their rectangle and their system
        /// font: next to the rest of the window they stuck out a mile. LauncherButton is the same
        /// control the launcher uses —gradient, rounded corners, spaced lettering— and it now
        /// lives in the contract so that both can use it.
        /// </summary>
        private LauncherButton Boton(string texto, Color tono, int ancho = 0)
        {
            var boton = new LauncherButton
            {
                Text = texto,
                Font = LauncherTheme.CreateFont(11f * _escala, FontStyle.Bold),
                Height = E(30),
                Width = ancho > 0 ? ancho : E(120),
                LetterSpacing = 1f,
                CornerRadius = E(5),
                BackgroundTop = Color.FromArgb(200, 34, 21, 12),
                BackgroundBottom = Color.FromArgb(200, 22, 13, 8),
                BackgroundTopHighlight = LauncherTheme.LightBrown,
                BackgroundBottomHighlight = Color.FromArgb(130, 75, 35),
                BorderColor = LauncherTheme.BorderBrown,
                BorderColorHighlight = LauncherTheme.GoldBorder,
                TextColor = tono,
                TextColorHighlight = Color.White,
                TextShadow = true,
                Cursor = Cursors.Hand,
            };
            return boton;
        }

        private void PararloTodo(object? sender, EventArgs e)
        {
            if (!Confirmar()) return;
            _parar.Enabled = false;
            Program.RequestShutdown("botón de la ventana del servidor");
        }

        private bool Confirmar()
        {
            int dentro = Network.GameNodeProxy.SesionesVivas.Count;
            string aviso = dentro > 0
                ? string.Format(_textos.StopServerWithPlayers, dentro)
                : _textos.StopServerConfirm;
            return MessageBox.Show(aviso, "Jondo Server", MessageBoxButtons.YesNo,
                                   MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing this window DOES stop the server: it is its window. But it asks, because
            // there may be people playing and an X is easy to hit by accident.
            if (e.CloseReason == CloseReason.UserClosing && !Program.ApagandoYa)
            {
                if (!Confirmar()) { e.Cancel = true; return; }
                Program.RequestShutdown("ventana del servidor cerrada");
            }

            _reloj.Stop();
            base.OnFormClosing(e);
        }

        private void TryCargarIcono()
        {
            try
            {
                // The server's is the same egg as the launcher's but in light blue, to
                // tell the two windows apart at a glance on the taskbar.
                foreach (string nombre in new[] { "icono_servidor.ico", "favicon.ico" })
                {
                    string ruta = Path.Combine(LauncherTheme.AssetsFolder, nombre);
                    if (File.Exists(ruta)) { Icon = new Icon(ruta); return; }
                }
            }
            catch { }
        }

        /// <summary>Opens the window on its own thread, so it does not get in the services' way.</summary>
        public static void Abrir()
        {
            var lista = new System.Threading.ManualResetEventSlim(false);
            var hilo = new System.Threading.Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
                    var ventana = new ServerWindow();
                    lista.Set();
                    Application.Run(ventana);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Servidor] No se ha podido abrir la ventana: {ex.Message}");
                    lista.Set();
                }
            })
            {
                Name = "ServerWindow",
                IsBackground = true,
            };
            hilo.SetApartmentState(System.Threading.ApartmentState.STA);
            hilo.Start();
            lista.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
