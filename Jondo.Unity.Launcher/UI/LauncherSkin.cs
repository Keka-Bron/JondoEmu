using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// El aspecto del lanzador, en Avalonia.
    /// </summary>
    /// <remarks>
    /// Es el gemelo del LauncherSkin de Windows Forms que sigue usando el servidor, y los dos
    /// leen los MISMOS numeros de <see cref="LauncherPalette"/>. Aqui no hay ni un color escrito a
    /// mano a proposito: en cuanto hubiera uno, el lanzador y el servidor empezarian a separarse.
    ///
    /// Lo que si cambia respecto a la version de Windows Forms es todo lo que aquella tenia que
    /// hacer a mano y aqui no hace falta:
    ///
    ///   - La transparencia es de verdad. Windows Forms no compone con alfa, asi que cada panel
    ///     recortaba del fondo ya pintado el trozo que le tocaba -- eso era IBackgroundWindow y su
    ///     ComposedBackground -- para fingirla. Avalonia compone, asi que los paneles llevan un
    ///     color con alfa y ya esta.
    ///   - Los degradados, las esquinas redondas y las sombras son propiedades, no dibujo a mano.
    ///   - El escalado por DPI lo lleva el propio Avalonia, asi que desaparece el Px() que
    ///     multiplicaba cada medida.
    /// </remarks>
    internal static class LauncherSkin
    {
        // ─── Colores ────────────────────────────────────────────────────────────

        public static Color Of(uint argb) => Color.FromUInt32(argb);

        public static SolidColorBrush Brush(uint argb) => new SolidColorBrush(Of(argb));

        public static readonly Color Background = Of(LauncherPalette.Background);
        public static readonly Color CardFill = Of(LauncherPalette.CardFill);
        public static readonly Color BarFill = Of(LauncherPalette.BarFill);
        public static readonly Color GoldBorder = Of(LauncherPalette.GoldBorder);
        public static readonly Color LightGold = Of(LauncherPalette.LightGold);
        public static readonly Color Gold = Of(LauncherPalette.Gold);
        public static readonly Color SoftGold = Of(LauncherPalette.SoftGold);
        public static readonly Color MutedGold = Of(LauncherPalette.MutedGold);
        public static readonly Color LightBrown = Of(LauncherPalette.LightBrown);
        public static readonly Color BorderBrown = Of(LauncherPalette.BorderBrown);
        public static readonly Color BaseText = Of(LauncherPalette.BaseText);
        public static readonly Color CardText = Of(LauncherPalette.CardText);
        public static readonly Color HighlightText = Of(LauncherPalette.HighlightText);
        public static readonly Color Red = Of(LauncherPalette.Red);
        public static readonly Color OnlineGreen = Of(LauncherPalette.OnlineGreen);
        public static readonly Color DotGreen = Of(LauncherPalette.DotGreen);

        // ─── Tipografia ─────────────────────────────────────────────────────────

        /// <summary>
        /// La misma cadena de respaldo que la web: Cinzel si esta, luego Trebuchet MS, luego lo que
        /// el sistema use para su interfaz.
        /// </summary>
        /// <remarks>
        /// Avalonia acepta la lista entera en una sola FontFamily separada por comas y elige la
        /// primera instalada, asi que aqui no hace falta el rodeo de preguntarle a
        /// InstalledFontCollection que hacia la version de Windows Forms.
        /// </remarks>
        public static readonly FontFamily Title = new FontFamily("Cinzel, Trebuchet MS, Segoe UI, sans-serif");

        public static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

        /// <summary>
        /// De pixeles de la hoja de estilos a los de Avalonia.
        /// </summary>
        /// <remarks>
        /// La version de Windows Forms convertia a puntos -- por 0,75 -- porque GDI+ mide las
        /// fuentes en puntos. Avalonia mide en pixeles independientes del dispositivo, que es la
        /// misma unidad que usaba la hoja de estilos, asi que la medida pasa tal cual.
        /// </remarks>
        public static double Font(double cssPixels) => cssPixels;

        // ─── Ficheros ───────────────────────────────────────────────────────────

        /// <summary>Donde estan las imagenes y la musica del lanzador.</summary>
        public static string AssetsFolder => Path.Combine(Paths.Root, "launcher_assets");

        private static readonly Dictionary<string, Bitmap?> _imagenes = new();

        /// <summary>Una imagen de la carpeta de recursos, o null si no esta.</summary>
        /// <remarks>
        /// Se cachea porque el fondo se pide en cada cambio de tamano de ventana y volver a leer un
        /// JPEG de dos megas cada vez que alguien arrastra un borde se nota.
        /// </remarks>
        public static Bitmap? LoadImage(string name)
        {
            lock (_imagenes)
            {
                if (_imagenes.TryGetValue(name, out var cached)) return cached;

                Bitmap? bitmap = null;
                try
                {
                    string path = Path.Combine(AssetsFolder, name);
                    if (File.Exists(path)) bitmap = new Bitmap(path);
                }
                catch
                {
                    // Sin fondo se sigue viendo: la ventana se queda con el color de base, que es
                    // justo para lo que existe.
                }

                _imagenes[name] = bitmap;
                return bitmap;
            }
        }
    }
}
