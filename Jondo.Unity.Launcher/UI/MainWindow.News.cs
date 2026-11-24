using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Jondo.Unity.Launcher.Setup;
using Jondo.Unity.Launcher.UI.News;
using Jondo.Unity.Launcher.UI.Widgets;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The News page: the latest release's notes, as GitHub gives them, beside the roadmap.
    /// </summary>
    /// <remarks>
    /// The page shows the copy kept from the last time at once and asks GitHub once per start of
    /// the launcher, when the page is first opened (or on the refresh button). The notes are drawn
    /// with the launcher's own type: headings, bullets, bold and code, nothing else.
    /// </remarks>
    public sealed partial class MainWindow
    {
        private NewsFeed? _noticias;
        private ReleaseNews? _noticiaMostrada;
        private bool _noticiasPedidas;
        private bool _pidiendoNoticias;
        private EstadoNoticias _estadoNoticias = EstadoNoticias.Nada;

        private enum EstadoNoticias { Nada, Pidiendo, Guardadas, SinConexion, Frescas }

        private NewsFeed Noticias => _noticias ??= new NewsFeed(RepositorioDeNoticias(), NewsFeed.DefaultCacheFile);

        /// <summary>The repository the installed launcher updates from, or the one it was built for.</summary>
        private static string RepositorioDeNoticias()
        {
            try
            {
                string repositorio = Program.Installation?.Load().Repository ?? "";
                if (repositorio.Length > 0) return repositorio;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // A broken install.json: the default repository still has the news.
            }
            return ReleaseFeed.DefaultRepository;
        }

        /// <summary>Opening the page: the kept copy at once, GitHub once per start.</summary>
        private void AbrirNoticias()
        {
            if (_noticiaMostrada == null)
            {
                var guardada = Noticias.Cached();
                if (guardada != null)
                {
                    _estadoNoticias = EstadoNoticias.Guardadas;
                    MostrarNoticia(guardada);
                }
            }
            NoticiaNueva.IsVisible = false;
            if (!_noticiasPedidas) _ = PedirNoticiasAsync();
            else PintarEstadoNoticias();
        }

        private void AlPulsarRecargarNoticias(object? sender, RoutedEventArgs e) => _ = PedirNoticiasAsync();

        private void AlPulsarVerEnGitHub(object? sender, RoutedEventArgs e)
        {
            if (_noticiaMostrada == null) return;
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher != null) _ = launcher.LaunchUriAsync(new Uri(_noticiaMostrada.Url));
        }

        private async Task PedirNoticiasAsync()
        {
            if (_pidiendoNoticias) return;
            _pidiendoNoticias = true;
            _noticiasPedidas = true;
            BotonRecargarNoticias.IsEnabled = false;
            if (_noticiaMostrada == null) _estadoNoticias = EstadoNoticias.Pidiendo;
            PintarEstadoNoticias();
            try
            {
                using var tiempo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var noticia = await Noticias.FetchAsync(tiempo.Token);
                _estadoNoticias = EstadoNoticias.Frescas;
                MostrarNoticia(noticia);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No connection, GitHub's hourly limit, or a repository with no release yet.
                _estadoNoticias = _noticiaMostrada != null ? EstadoNoticias.Guardadas : EstadoNoticias.SinConexion;
            }
            finally
            {
                _pidiendoNoticias = false;
                BotonRecargarNoticias.IsEnabled = true;
                PintarEstadoNoticias();
            }
        }

        /// <summary>A release on the page: its title, tag, date and notes.</summary>
        private void MostrarNoticia(ReleaseNews noticia)
        {
            _noticiaMostrada = noticia;
            string nombre = noticia.Name.Trim();
            TituloVersion.Text = nombre.Length > 0 ? nombre : noticia.Tag;
            TextoTag.Text = noticia.Tag;
            PildoraTag.IsVisible = nombre.Length > 0 && !string.Equals(nombre, noticia.Tag, StringComparison.OrdinalIgnoreCase);
            BotonVerEnGitHub.IsEnabled = true;
            PintarFechaNoticia();

            NotasDeLaVersion.Children.Clear();
            var bloques = ReleaseNotes.Parse(noticia.Notes);
            if (bloques.Count == 0)
            {
                NotasDeLaVersion.Children.Add(new TextBlock
                {
                    Text = Textos.NotasVacias(_idioma),
                    Classes = { "desc" },
                });
            }
            else
            {
                bool primero = true;
                foreach (var bloque in bloques)
                {
                    NotasDeLaVersion.Children.Add(Bloque(bloque, primero));
                    primero = false;
                }
            }
            DesplazarNotas.Offset = default;
        }

        private void PintarFechaNoticia()
        {
            if (_noticiaMostrada == null)
            {
                FechaVersion.Text = "";
                return;
            }
            string fecha = _noticiaMostrada.PublishedAt > DateTime.MinValue.AddDays(1)
                ? Textos.Publicada(_idioma, _noticiaMostrada.PublishedAt) : "";
            string instalada = VersionDelLanzador();
            FechaVersion.Text = instalada.Length > 0
                ? (fecha.Length > 0 ? fecha + "  ·  " : "") + Textos.VersionInstalada(_idioma, instalada)
                : fecha;
        }

        /// <summary>The version this launcher was installed as; empty when it runs from a build.</summary>
        private static string VersionDelLanzador()
        {
            try
            {
                return Program.Installation?.Load().Version ?? "";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return "";
            }
        }

        private void PintarEstadoNoticias()
        {
            string? aviso = _estadoNoticias switch
            {
                EstadoNoticias.Pidiendo => Textos.PidiendoNotas(_idioma),
                EstadoNoticias.Guardadas when _noticiasPedidas && !_pidiendoNoticias => Textos.NotasGuardadas(_idioma),
                EstadoNoticias.SinConexion => Textos.SinNotas(_idioma),
                _ => null,
            };
            AvisoNoticias.IsVisible = aviso != null;
            TextoAvisoNoticias.Text = aviso ?? "";
            if (_noticiaMostrada == null)
            {
                TituloVersion.Text = "—";
                PildoraTag.IsVisible = false;
                BotonVerEnGitHub.IsEnabled = false;
                FechaVersion.Text = "";
            }
        }

        /// <summary>The page's words and the roadmap, in the launcher's language.</summary>
        private void AplicarIdiomaANoticias()
        {
            EyebrowVersion.Text = Textos.UltimaVersion(_idioma);
            TextoVerEnGitHub.Text = Textos.VerEnGitHub(_idioma);
            TituloHojaDeRuta.Text = Textos.HojaDeRuta(_idioma);
            DescHojaDeRuta.Text = Textos.DescHojaDeRuta(_idioma);
            ToolTip.SetTip(BotonRecargarNoticias, Textos.Frase(Textos.Noticias(_idioma)));
            PintarFechaNoticia();
            PintarEstadoNoticias();
            if (_noticiaMostrada != null && _noticiaMostrada.Notes.Trim().Length == 0) MostrarNoticia(_noticiaMostrada);
            PintarHojaDeRuta();
            if (!_noticiasPedidas) MarcarNoticiaNueva();
        }

        // ── The notes ───────────────────────────────────────────────────────────

        private static IBrush Br(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));

        private static readonly FontFamily Monoespaciada = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

        private Control Bloque(NewsBlock bloque, bool primero)
        {
            switch (bloque.Kind)
            {
                case NewsBlockKind.Heading:
                {
                    var titulo = Texto(bloque.Spans, bloque.Level switch { 1 => 22, 2 => 17, _ => 14.5 }, FontWeight.SemiBold,
                                       bloque.Level >= 3 ? Br(LauncherPalette.Gold) : Br(LauncherPalette.CardText));
                    if (bloque.Level <= 2) titulo.FontFamily = LauncherSkin.Display;
                    titulo.Margin = new Thickness(0, primero ? 0 : (bloque.Level == 1 ? 18 : 14), 0, bloque.Level == 1 ? 4 : 2);
                    if (bloque.Level != 2) return titulo;
                    // A section heading carries a short gold rule, the launcher's mark.
                    return new StackPanel
                    {
                        Spacing = 6,
                        Margin = titulo.Margin,
                        Children =
                        {
                            new Border { Width = 26, Height = 2, CornerRadius = new CornerRadius(1), Background = Br(LauncherPalette.Gold),
                                         HorizontalAlignment = HorizontalAlignment.Left },
                            Sin(titulo),
                        },
                    };
                }
                case NewsBlockKind.Bullet:
                {
                    var texto = Texto(bloque.Spans, 13.5, FontWeight.Normal, Br(LauncherPalette.TextSecondary));
                    var punto = new Border
                    {
                        Width = bloque.Level == 0 ? 6 : 5,
                        Height = bloque.Level == 0 ? 6 : 5,
                        CornerRadius = new CornerRadius(3),
                        Background = bloque.Level == 0 ? Br(LauncherPalette.Gold) : Brushes.Transparent,
                        BorderBrush = Br(LauncherPalette.Gold),
                        BorderThickness = new Thickness(bloque.Level == 0 ? 0 : 1),
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 7, 0, 0),
                    };
                    var fila = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("16,*"),
                        Margin = new Thickness(bloque.Level * 20, 1, 0, 1),
                    };
                    fila.Children.Add(punto);
                    Grid.SetColumn(texto, 1);
                    fila.Children.Add(texto);
                    return fila;
                }
                default:
                {
                    var texto = Texto(bloque.Spans, 13.5, FontWeight.Normal, Br(LauncherPalette.TextSecondary));
                    texto.Margin = new Thickness(0, 2, 0, 4);
                    return texto;
                }
            }
        }

        private static TextBlock Sin(TextBlock texto)
        {
            texto.Margin = default;
            return texto;
        }

        private static SelectableTextBlock Texto(IReadOnlyList<NewsSpan> trozos, double tamano, FontWeight peso, IBrush color)
        {
            var texto = new SelectableTextBlock
            {
                FontSize = tamano,
                FontWeight = peso,
                Foreground = color,
                FontFamily = LauncherSkin.Ui,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = tamano * 1.5,
            };
            var lineas = texto.Inlines ??= new InlineCollection();
            foreach (var trozo in trozos)
            {
                var run = new Run(trozo.Text);
                if (trozo.Bold)
                {
                    run.FontWeight = FontWeight.SemiBold;
                    run.Foreground = Br(LauncherPalette.CardText);
                }
                if (trozo.Code)
                {
                    run.FontFamily = Monoespaciada;
                    run.FontSize = tamano - 1;
                    run.Foreground = Br(LauncherPalette.Gold);
                    run.Background = Br(LauncherPalette.SurfaceSunken);
                }
                lineas.Add(run);
            }
            return texto;
        }

        // ── The roadmap ─────────────────────────────────────────────────────────

        private void PintarHojaDeRuta()
        {
            PasosDeLaHoja.Children.Clear();
            var pasos = Roadmap.For(_idioma);
            for (int i = 0; i < pasos.Count; i++)
                PasosDeLaHoja.Children.Add(Paso(pasos[i], ultimo: i == pasos.Count - 1));
        }

        /// <summary>A step: a dot on the line (filled when done, ringed and glowing now, hollow next), its date, title and text.</summary>
        private Control Paso(RoadmapStep paso, bool ultimo)
        {
            IBrush color = paso.State switch
            {
                RoadmapState.Done => Br(LauncherPalette.OnlineGreen),
                RoadmapState.Now => Br(LauncherPalette.Gold),
                _ => Br(LauncherPalette.TextTertiary),
            };

            var fila = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };

            // The line runs down from this dot to the next.
            var columna = new Grid();
            if (!ultimo)
                columna.Children.Add(new Border
                {
                    Width = 2,
                    Background = Br(LauncherPalette.Hairline),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 18, 0, 0),
                });
            var punto = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                BorderBrush = color,
                BorderThickness = new Thickness(2),
                Background = paso.State == RoadmapState.Done ? color : Br(LauncherPalette.SurfaceSunken),
            };
            if (paso.State == RoadmapState.Now)
            {
                punto.BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, Spread = 1, Color = Color.FromArgb(0x90, 0xD4, 0xAF, 0x37) });
                punto.Child = new Border
                {
                    Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = color,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            columna.Children.Add(punto);
            fila.Children.Add(columna);

            var texto = new StackPanel { Spacing = 3, Margin = new Thickness(10, 0, 0, ultimo ? 0 : 22) };
            texto.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = Textos.EstadoHojaDeRuta(_idioma, paso.State),
                        FontSize = 10.5, FontWeight = FontWeight.Bold, LetterSpacing = 1.2,
                        Foreground = color, VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = paso.When, FontSize = 11.5, Foreground = Br(LauncherPalette.TextTertiary),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            });
            texto.Children.Add(new TextBlock
            {
                Text = paso.Title,
                FontFamily = LauncherSkin.Display,
                FontSize = paso.State == RoadmapState.Next ? 18 : 15.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = paso.State == RoadmapState.Next ? Br(LauncherPalette.Gold) : Br(LauncherPalette.CardText),
                TextWrapping = TextWrapping.Wrap,
            });
            texto.Children.Add(new TextBlock
            {
                Text = paso.Text,
                FontSize = 12.5,
                Foreground = Br(LauncherPalette.TextSecondary),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
            });
            Grid.SetColumn(texto, 1);
            fila.Children.Add(texto);
            return fila;
        }

        // ── For the screenshot tests ────────────────────────────────────────────

        /// <summary>The page as it looks with a release in hand, without asking GitHub.</summary>
        internal void MostrarNoticiaDeMentira(ReleaseNews noticia)
        {
            _noticiasPedidas = true;
            _estadoNoticias = EstadoNoticias.Frescas;
            MostrarNoticia(noticia);
            PintarEstadoNoticias();
        }

        /// <summary>A start that marks the dot on the News button when a newer release than the one installed is out.</summary>
        private void MarcarNoticiaNueva()
        {
            var guardada = Noticias.Cached();
            string instalada = VersionDelLanzador();
            NoticiaNueva.IsVisible = guardada != null && instalada.Length > 0
                && !string.Equals(guardada.Tag, instalada, StringComparison.OrdinalIgnoreCase);
        }
    }
}
