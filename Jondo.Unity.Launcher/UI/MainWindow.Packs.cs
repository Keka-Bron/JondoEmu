using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.Packs;
using Jondo.Unity.Launcher.UI.Widgets;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The "Graphics" block of the settings: the client's optional HD and 4K scenery packs.
    /// </summary>
    /// <remarks>
    /// One row per pack with its size, its state, one button that does what the state calls for
    /// (download, resume, verify, or cancel while working), "Remove", and whether to offer the
    /// pack to the game. The work itself is in <see cref="TexturePackService"/>; this only shows
    /// it. One pack operation at a time: two downloads side by side would only share the line.
    /// </remarks>
    public sealed partial class MainWindow
    {
        private sealed class PackRow
        {
            public PackRow(TexturePack pack) => Pack = pack;

            public TexturePack Pack { get; }
            public PackStatus? Last { get; set; }

            /// <summary>A line that replaces the state until the next action, such as "paused".</summary>
            public string? Note { get; set; }

            public Border Root { get; set; } = null!;
            public TextBlock Name { get; } = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold };
            public TextBlock Size { get; } = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            public TextBlock Hint { get; } = new() { Classes = { "pie" } };
            public TextBlock State { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            public ProgressStrip Bar { get; } = new() { Height = 6 };
            public TextBlock ActionText { get; } = new();
            public Button Action { get; } = new() { Classes = { "secondary" }, MinHeight = 32, Height = 32, MinWidth = 104 };
            public TextBlock RemoveText { get; } = new();
            public Button Remove { get; } = new() { Classes = { "danger" }, MinHeight = 32, Height = 32, MinWidth = 84 };
            public CheckBox Use { get; } = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        }

        private readonly List<PackRow> _filasDePacks = new();
        private TexturePack? _packEnMarcha;
        private PackProgress? _packProgreso;
        private CancellationTokenSource? _packCancelar;

        private static TexturePackService ServicioDePacks()
            => TexturePackService.ForClient(LauncherService.ResolveClient());

        private void RefrescarPacks()
        {
            RotuloGraficos.Text = Textos.Graficos(_idioma);
            PieGraficos.Text = Textos.PieGraficos(_idioma);

            if (_filasDePacks.Count == 0)
            {
                foreach (var pack in TexturePack.All)
                {
                    var fila = CrearFila(pack);
                    _filasDePacks.Add(fila);
                    FilasDePacks.Children.Add(fila.Root);
                }
            }

            // File system only: no network and no hashing, so it is cheap enough to do here.
            var servicio = ServicioDePacks();
            foreach (var fila in _filasDePacks)
            {
                fila.Last = servicio.Status(fila.Pack);
                PintarFila(fila);
            }
        }

        private PackRow CrearFila(TexturePack pack)
        {
            var fila = new PackRow(pack);

            fila.Name.Foreground = new SolidColorBrush(LauncherSkin.CardText);
            fila.Size.Foreground = new SolidColorBrush(LauncherSkin.TextTertiary);
            fila.State.Foreground = new SolidColorBrush(LauncherSkin.TextSecondary);
            fila.ActionText.HorizontalAlignment = HorizontalAlignment.Center;
            fila.RemoveText.HorizontalAlignment = HorizontalAlignment.Center;
            fila.Action.Content = fila.ActionText;
            fila.Remove.Content = fila.RemoveText;

            fila.Action.Click += (_, _) => _ = AlPulsarAccionDePackAsync(fila);
            fila.Remove.Click += (_, _) => _ = AlPulsarQuitarPackAsync(fila);
            fila.Use.Click += (_, _) =>
            {
                bool on = fila.Use.IsChecked == true;
                if (fila.Pack == TexturePack.Hd) LauncherPreferences.PackHd = on;
                else LauncherPreferences.Pack4k = on;
                PintarFila(fila);
            };

            var cabecera = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(fila.Size, 1);
            cabecera.Children.Add(fila.Name);
            cabecera.Children.Add(fila.Size);

            // What can be done with it, and under that whether the game is offered it.
            fila.Remove.Margin = new Avalonia.Thickness(8, 0, 0, 0);
            var acciones = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { fila.Action, fila.Remove },
            };

            fila.Root = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(10),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(LauncherSkin.Hairline),
                Background = new SolidColorBrush(Color.FromUInt32(LauncherPalette.SurfaceSunken)),
                Padding = new Avalonia.Thickness(14, 12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        cabecera,
                        fila.Hint,
                        fila.State,
                        fila.Bar,
                        new Border { Height = 2 },
                        acciones,
                        fila.Use,
                    },
                },
            };
            return fila;
        }

        private void PintarFila(PackRow fila)
        {
            var estado = fila.Last;
            if (estado == null) return;

            bool trabajando = _packEnMarcha == fila.Pack;
            bool otro = _packEnMarcha != null && !trabajando;

            fila.Name.Text = Textos.NombreDePack(_idioma, fila.Pack);
            fila.Size.Text = estado.Size is long bytes ? Textos.Tamano(bytes) : "";
            fila.Hint.Text = Textos.DescripcionDePack(_idioma, fila.Pack);

            fila.State.Text = trabajando && _packProgreso is { } progreso
                ? Textos.ProgresoDePack(_idioma, progreso)
                : fila.Note ?? Textos.EstadoDePack(_idioma, estado);

            fila.Bar.IsVisible = trabajando;
            fila.Bar.Fraction = trabajando && _packProgreso is { } p ? p.Fraction : 0;
            fila.Bar.Working = trabajando;

            // While working, the same button cancels. Removing cannot be cancelled half way.
            fila.ActionText.Text = trabajando ? Textos.Cancelar(_idioma) : Textos.AccionDePack(_idioma, estado.State);
            fila.Action.IsEnabled = trabajando ? _packCancelar != null : !otro;

            fila.RemoveText.Text = Textos.QuitarPack(_idioma);
            fila.Remove.IsVisible = estado.HasFiles && !trabajando;
            fila.Remove.IsEnabled = !otro;

            // Wanting a pack that is not installed does nothing at launch, so it can only be
            // turned on once it is; it can always be turned off.
            bool quiere = fila.Pack == TexturePack.Hd ? LauncherPreferences.PackHd : LauncherPreferences.Pack4k;
            fila.Use.Content = Textos.UsarPack(_idioma);
            fila.Use.IsChecked = quiere;
            fila.Use.IsEnabled = quiere || estado.State == PackState.Installed;
        }

        private async Task AlPulsarAccionDePackAsync(PackRow fila)
        {
            if (_packEnMarcha != null)
            {
                if (_packEnMarcha == fila.Pack) _packCancelar?.Cancel();
                return;
            }

            QuitarElAviso();
            fila.Note = null;
            _packEnMarcha = fila.Pack;
            _packProgreso = null;
            _packCancelar = new CancellationTokenSource();
            var token = _packCancelar.Token;
            RefrescarPacks();

            var servicio = ServicioDePacks();
            var progreso = new Progress<PackProgress>(p =>
            {
                // Reports can arrive after the operation ended; those are stale.
                if (_packEnMarcha != fila.Pack) return;
                _packProgreso = p;
                PintarFila(fila);
            });

            try
            {
                await Task.Run(() => servicio.InstallAsync(fila.Pack, progreso, token));
            }
            catch (OperationCanceledException)
            {
                fila.Note = Textos.PackEnPausa(_idioma);
            }
            catch (PackException ex)
            {
                Avisar(Textos.ErrorDePack(_idioma, ex));
            }
            catch (Exception ex)
            {
                Avisar(_textos.GenericError + "\n" + ex.Message);
            }
            finally
            {
                _packEnMarcha = null;
                _packProgreso = null;
                _packCancelar?.Dispose();
                _packCancelar = null;
                RefrescarPacks();
            }
        }

        private async Task AlPulsarQuitarPackAsync(PackRow fila)
        {
            if (_packEnMarcha != null) return;

            bool seguro = await Dialogs.ConfirmAsync(this, Title ?? "Jondo",
                Textos.ConfirmarQuitarPack(_idioma, fila.Pack, fila.Last?.Size),
                Textos.QuitarPack(_idioma), Textos.Cancelar(_idioma));
            if (!seguro || _packEnMarcha != null) return;

            QuitarElAviso();
            _packEnMarcha = fila.Pack;
            RefrescarPacks();

            var servicio = ServicioDePacks();
            try
            {
                await Task.Run(() => servicio.Remove(fila.Pack));
                fila.Note = null;

                // A removed pack cannot be offered to the game; leaving it "on" would only make
                // the box lie until the pack came back.
                if (fila.Pack == TexturePack.Hd) LauncherPreferences.PackHd = false;
                else LauncherPreferences.Pack4k = false;
            }
            catch (PackException ex)
            {
                Avisar(Textos.ErrorDePack(_idioma, ex));
            }
            finally
            {
                _packEnMarcha = null;
                RefrescarPacks();
            }
        }
    }
}
