using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The launcher's look, in Avalonia.
    /// </summary>
    /// <remarks>
    /// It is the twin of the Windows Forms LauncherSkin the server still uses, and both
    /// read the SAME numbers from <see cref="LauncherPalette"/>. Here there is not a single colour written by
    /// hand on purpose: as soon as there were one, the launcher and the server would start drifting apart.
    ///
    /// What does change from the Windows Forms version is everything that one had to
    /// do by hand and here is not needed:
    ///
    ///   - The transparency is real. Windows Forms does not compose with alpha, so each panel
    ///     cut from the already painted background the piece that belonged to it -- that was IBackgroundWindow and its
    ///     ComposedBackground -- to fake it. Avalonia composes, so the panels carry a
    ///     colour with alpha and that is it.
    ///   - The gradients, the rounded corners and the shadows are properties, not hand drawing.
    ///   - DPI scaling is handled by Avalonia itself, so the Px() that
    ///     multiplied each measure disappears.
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
        /// The same fallback chain as the website: Cinzel if it is there, then Trebuchet MS, then whatever
        /// the system uses for its interface.
        /// </summary>
        /// <remarks>
        /// Avalonia accepts the whole list in a single comma-separated FontFamily and picks the
        /// first installed, so here the detour of asking
        /// InstalledFontCollection that the Windows Forms version made is not needed.
        /// </remarks>
        public static readonly FontFamily Title = new FontFamily("Cinzel, Trebuchet MS, Segoe UI, sans-serif");

        public static readonly FontFamily Mono = new FontFamily("Consolas, Courier New, monospace");

        /// <summary>
        /// From the style sheet's pixels to Avalonia's.
        /// </summary>
        /// <remarks>
        /// The Windows Forms version converted to points -- times 0.75 -- because GDI+ measures
        /// fonts in points. Avalonia measures in device-independent pixels, which is the
        /// same unit the style sheet used, so the measure goes through as is.
        /// </remarks>
        public static double Font(double cssPixels) => cssPixels;

        // ─── Ficheros ───────────────────────────────────────────────────────────

        /// <summary>Where the launcher's images and music are.</summary>
        public static string AssetsFolder => Path.Combine(Paths.Root, "launcher_assets");

        private static readonly Dictionary<string, Bitmap?> _imagenes = new();

        /// <summary>An image from the resources folder, or null if it is not there.</summary>
        /// <remarks>
        /// It is cached because the background is asked for on every window resize and re-reading a
        /// two-meg JPEG every time someone drags an edge is noticeable.
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
                    // Without a background it is still visible: the window keeps the base colour, which is
                    // exactly what it exists for.
                }

                _imagenes[name] = bitmap;
                return bitmap;
            }
        }
    }
}
