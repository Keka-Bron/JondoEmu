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
    /// Que el lanzador de Avalonia carga y dibuja de verdad.
    /// </summary>
    /// <remarks>
    /// El XAML lo compila el propio Avalonia al construir, así que los nombres de tipo, de
    /// propiedad y los <c>x:Name</c> ya están comprobados cuando la solución compila. Lo que NO
    /// comprueba nadie hasta que se abre la ventana son dos cosas, y las dos revientan en tiempo de
    /// ejecución:
    ///
    ///   - los <c>{DynamicResource}</c>, que se resuelven por nombre: una errata deja el color sin
    ///     poner y no lo dice
    ///   - el dibujo a mano de los cuatro controles propios —el rótulo, la banderita, el punto de
    ///     estado y el texto espaciado—, que es código nuevo
    ///
    /// Con esto una errata en un nombre de recurso o un fallo al dibujar salen aquí y no en la cara
    /// de quien abra el lanzador.
    /// </remarks>
    public class LauncherRenderTests
    {
        /// <summary>La misma aplicación que arranca el lanzador, sin ventana de verdad.</summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UseSkia()
                // Con el dibujo de mentira -- lo que trae headless por omision -- Render() no llega
                // a llamarse nunca y la prueba pasaria sin dibujar nada. Con Skia se pinta de
                // verdad sobre un lienzo en memoria, que es lo unico que hace util esta prueba.
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

        [AvaloniaFact]
        public void Los_colores_del_xaml_existen_todos()
        {
            // Cada nombre que el XAML pide con DynamicResource tiene que estar puesto. Si falta,
            // Avalonia no protesta: deja la propiedad sin valor y el botón sale gris.
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
            // Que los recursos existan no basta: tienen que traer el color bueno.
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
            // No basta con que no reviente. El rótulo estuvo SIN DIBUJARSE desde la migración a
            // Avalonia y nadie se enteró: la prueba de antes sólo comprobaba que Render() no
            // lanzaba, y no lanzaba -- se limitaba a no pintar nada. El motivo era que
            // FormattedText.BuildGeometry devuelve la silueta vacía si el texto no lleva brocha.
            //
            // Así que aquí se cuentan píxeles: sobre negro, el rótulo tiene que dejar oro.
            Assert.True(PintaAlgo(new LogoBanner(), 350, 120),
                        "El rótulo no ha pintado un solo píxel.");

            // Y por debajo de cierto tamaño no dibuja nada A PROPÓSITO: la ventana lo encoge
            // cuando no cabe, y medio rótulo cortado es peor que ninguno.
            Assert.False(PintaAlgo(new LogoBanner(), 40, 10));
        }

        /// <summary>Si el control deja algún píxel distinto del fondo negro.</summary>
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
                    // BGRA: basta con que alguno de los tres canales se salga del negro.
                    if (fila[x * 4] > 24 || fila[x * 4 + 1] > 24 || fila[x * 4 + 2] > 24) distintos++;
                }
            }

            ventana.Close();
            return distintos > 200;
        }

        [AvaloniaFact]
        public void El_texto_espaciado_se_dibuja_con_y_sin_separacion()
        {
            // Con separación cero se pinta de una vez, y con separación letra a letra: son dos
            // caminos distintos dentro del mismo método.
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

            // Y el vacío no se cae.
            Dibujar(new SpacedText { Text = "", FontSize = 16, FontFamily = LauncherSkin.Title }, 300, 46);
        }

        [AvaloniaFact]
        public void La_ventana_del_lanzador_se_construye_y_se_pinta()
        {
            // Lo que el compilador de XAML NO comprueba: que la ventana entera se monta y se pinta
            // con los estilos puestos. Se puede construir aqui porque el constructor ya no habla
            // con el servidor -- eso se hace al abrirse, en CargarLasCuentasAsync -- asi que la
            // prueba no depende de que haya un servidor ni de las cuentas de esta maquina.
            var ventana = new MainWindow { Width = 1000, Height = 660 };
            ventana.Show();
            ventana.CaptureRenderedFrame();
            ventana.Close();
        }

        /// <summary>Mete el control en una ventana sin pantalla y le pide que se pinte.</summary>
        private static void Dibujar(Avalonia.Controls.Control control, double ancho = 120, double alto = 40)
        {
            var ventana = new Window { Width = ancho, Height = alto, Content = control };
            ventana.Show();
            ventana.CaptureRenderedFrame();
            ventana.Close();
        }
    }
}
