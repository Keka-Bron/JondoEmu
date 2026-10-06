using Avalonia;
using Avalonia.Controls;
using Application = Avalonia.Application;
using Color = Avalonia.Media.Color;
using Brushes = Avalonia.Media.Brushes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Launcher.UI.Widgets;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Jondo.Unity.Tests.Launcher.LauncherRenderTests))]

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// That the Avalonia launcher really loads and draws.
    /// </summary>
    /// <remarks>
    /// The XAML is compiled by Avalonia itself on building, so the type names, the
    /// property names and the <c>x:Name</c>s are already checked when the solution compiles. What NOBODY
    /// checks until the window is opened are two things, and both blow up at
    /// run time:
    ///
    ///   - the <c>{DynamicResource}</c>s, which are resolved by name: a typo leaves the colour
    ///     unset and does not say so
    ///   - the hand drawing of the four custom controls —the title, the little flag, the status
    ///     dot and the spaced text—, which is new code
    ///
    /// With this a typo in a resource name or a drawing failure comes out here and not in the face
    /// of whoever opens the launcher.
    /// </remarks>
    public class LauncherRenderTests
    {
        /// <summary>The same application the launcher starts, without a real window.</summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UseSkia()
                // With the fake drawing -- what headless brings by default -- Render() never gets
                // called and the test would pass without drawing anything. With Skia it really
                // paints onto an in-memory canvas, which is the only thing that makes this test useful.
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

        [AvaloniaFact]
        public void Los_colores_del_xaml_existen_todos()
        {
            // Every name the XAML asks for with DynamicResource has to be set. If one is missing,
            // Avalonia does not complain: it leaves the property without a value and the button comes out grey.
            string[] pedidos =
            {
                "GreenTop", "GreenBottom", "GreenTopHover", "GreenBottomHover",
                "PurpleTop", "PurpleBottom",
                "GoldBrush", "LightGoldBrush", "SoftGoldBrush", "MutedGoldBrush",
                "GoldBorderBrush", "LightBrownBrush", "BorderBrownBrush",
                "BaseTextBrush", "CardTextBrush", "HighlightTextBrush",
                "FieldTextBrush", "FieldBackgroundBrush",
                "DisabledFieldBackgroundBrush", "DisabledFieldBorderBrush", "DisabledFieldTextBrush",
                "GreenBorderBrush", "PurpleBorderBrush",
                "CardFillBrush", "BarFillBrush", "BackgroundBrush",
                "RedBrush", "AlertBackgroundBrush", "AlertTextBrush", "LightBrownTextBrush",
            };

            foreach (string nombre in pedidos)
            {
                Assert.True(Application.Current!.Resources.ContainsKey(nombre),
                    $"El XAML pide «{nombre}» y no está puesto en App.axaml.cs.");
            }
        }

        [AvaloniaFact]
        public void El_verde_del_boton_de_jugar_es_el_de_la_paleta()
        {
            // That the resources exist is not enough: they have to bring the right colour.
            Application.Current!.Resources.TryGetValue("GreenTop", out object? arriba);
            Application.Current!.Resources.TryGetValue("GoldBrush", out object? oro);

            Assert.Equal(Color.FromUInt32(LauncherPalette.GreenTop), Assert.IsType<Color>(arriba));
            Assert.Equal(Color.FromUInt32(LauncherPalette.Gold),
                         Assert.IsType<SolidColorBrush>(oro).Color);
        }

        [AvaloniaTheory]
        [InlineData("es")]
        [InlineData("en")]
        [InlineData("fr")]
        public void Las_banderas_se_dibujan(string codigo)
        {
            Dibujar(new FlagIcon { Code = codigo });
        }

        [AvaloniaFact]
        public void El_punto_de_estado_se_dibuja_en_los_dos_estados()
        {
            Dibujar(new StatusDot { Online = true });
            Dibujar(new StatusDot { Online = false });
        }

        [AvaloniaFact]
        public void El_rotulo_pinta_algo_de_verdad()
        {
            // Not blowing up is not enough. The title was NOT BEING DRAWN since the move to
            // Avalonia and nobody noticed: the earlier test only checked that Render() did not
            // throw, and it did not throw -- it simply painted nothing. The reason was that
            // FormattedText.BuildGeometry returns an empty outline if the text carries no brush.
            //
            // So here pixels are counted: on black, the title has to leave gold.
            Assert.True(PintaAlgo(new LogoBanner(), 350, 120),
                        "El rótulo no ha pintado un solo píxel.");

            // And below a certain size it draws nothing ON PURPOSE: the window shrinks it
            // when it does not fit, and half a cut-off title is worse than none.
            Assert.False(PintaAlgo(new LogoBanner(), 40, 10));
        }

        /// <summary>Whether the control leaves any pixel different from the black background.</summary>
        private static unsafe bool PintaAlgo(Avalonia.Controls.Control control, double ancho, double alto)
        {
            var ventana = new Window
            {
                Width = ancho, Height = alto,
                Background = Brushes.Black,
                Content = control,
            };
            ventana.Show();

            using var lienzo = ventana.CaptureRenderedFrame();
            Assert.NotNull(lienzo);

            using var cerrojo = lienzo!.Lock();
            int distintos = 0;
            for (int y = 0; y < cerrojo.Size.Height; y++)
            {
                var fila = (byte*)cerrojo.Address + y * cerrojo.RowBytes;
                for (int x = 0; x < cerrojo.Size.Width; x++)
                {
                    // BGRA: it is enough for any of the three channels to step out of black.
                    if (fila[x * 4] > 24 || fila[x * 4 + 1] > 24 || fila[x * 4 + 2] > 24) distintos++;
                }
            }

            ventana.Close();
            return distintos > 200;
        }

        [AvaloniaFact]
        public void El_texto_espaciado_se_dibuja_con_y_sin_separacion()
        {
            // With zero spacing it is painted in one go, and with letter-by-letter spacing: they are two
            // different paths inside the same method.
            Dibujar(new SpacedText
            {
                Text = "CONECTAR", Spacing = 2, Shadow = true, FontSize = 16,
                FontFamily = LauncherSkin.Title, Foreground = Brushes.White,
            }, 300, 46);

            Dibujar(new SpacedText
            {
                Text = "CONECTAR", Spacing = 0, FontSize = 16,
                FontFamily = LauncherSkin.Title, Foreground = Brushes.White,
            }, 300, 46);

            // And the empty one does not fall over.
            Dibujar(new SpacedText { Text = "", FontSize = 16, FontFamily = LauncherSkin.Title }, 300, 46);
        }

        [AvaloniaFact]
        public void La_ventana_del_lanzador_se_construye_y_se_pinta()
        {
            // What the XAML compiler does NOT check: that the whole window is assembled and painted
            // with the styles on. It can be built here because the constructor no longer talks
            // to the server -- that is done on opening, in CargarLasCuentasAsync -- so the
            // test does not depend on there being a server nor on this machine's accounts.
            var ventana = new MainWindow { Width = 1000, Height = 660 };
            ventana.Show();
            ventana.CaptureRenderedFrame();
            ventana.Close();
        }

        /// <summary>Puts the control in a window with no screen and asks it to paint itself.</summary>
        private static void Dibujar(Avalonia.Controls.Control control, double ancho = 120, double alto = 40)
        {
            var ventana = new Window { Width = ancho, Height = alto, Content = control };
            ventana.Show();
            ventana.CaptureRenderedFrame();
            ventana.Close();
        }
    }
}
