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
    /// Colours come from <see cref="LauncherPalette"/> so the desktop applications share the same values.
    /// Avalonia handles alpha composition, gradients, rounded corners, shadows and DPI scaling.
    /// </remarks>
    internal static class LauncherSkin
    {
        // ─── Colours ────────────────────────────────────────────────────────────

        public static Color Of(uint argb) => Color.FromUInt32(argb);

        public static readonly Color CardFill = Of(LauncherPalette.CardFill);
        public static readonly Color GoldBorder = Of(LauncherPalette.GoldBorder);
        public static readonly Color LightGold = Of(LauncherPalette.LightGold);
        public static readonly Color Gold = Of(LauncherPalette.Gold);
        public static readonly Color SoftGold = Of(LauncherPalette.SoftGold);
        public static readonly Color MutedGold = Of(LauncherPalette.MutedGold);
        public static readonly Color LightBrown = Of(LauncherPalette.LightBrown);
        public static readonly Color BorderBrown = Of(LauncherPalette.BorderBrown);
        public static readonly Color BaseText = Of(LauncherPalette.BaseText);
        public static readonly Color CardText = Of(LauncherPalette.CardText);
        public static readonly Color Red = Of(LauncherPalette.Red);
        public static readonly Color OnlineGreen = Of(LauncherPalette.OnlineGreen);
        public static readonly Color DotGreen = Of(LauncherPalette.DotGreen);
        public static readonly Color TextSecondary = Of(LauncherPalette.TextSecondary);
        public static readonly Color TextTertiary = Of(LauncherPalette.TextTertiary);
        public static readonly Color Danger = Of(LauncherPalette.Danger);
        public static readonly Color Warning = Of(LauncherPalette.Warning);
        public static readonly Color SurfaceRaised = Of(LauncherPalette.SurfaceRaised);
        public static readonly Color Hairline = Of(LauncherPalette.Hairline);

        // ─── Typography ─────────────────────────────────────────────────────────

        /// <summary>
        /// The same fallback chain as the website: Cinzel if it is there, then Trebuchet MS, then whatever
        /// the system uses for its interface.
        /// </summary>
        /// <remarks>
        /// Avalonia accepts the whole list in a single comma-separated FontFamily and picks the first installed.
        /// </remarks>
        public static readonly FontFamily Title = new FontFamily("Cinzel, Trebuchet MS, Segoe UI, sans-serif");

        /// <summary>The interface's text: Windows 11's own face, then the usual sans of each system.</summary>
        public static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI, Noto Sans, DejaVu Sans, sans-serif");

        /// <summary>Headings: the display cut of the same family, so sizes above 16 keep their shape.</summary>
        public static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI, Noto Sans, DejaVu Sans, sans-serif");

        // ─── Files ──────────────────────────────────────────────────────────────

        /// <summary>Where the launcher's images and music are.</summary>
        public static string AssetsFolder => Path.Combine(Paths.Root, "launcher_assets");

        private static readonly Dictionary<string, Bitmap?> _imagenes = new();

        /// <summary>
        /// A file of the launcher's look: the one in launcher_assets when it is there, else the copy
        /// the executable carries -- the background and the theme travel inside it, so the setup,
        /// downloaded alone, looks and sounds like the launcher it installs. Null when neither is.
        /// </summary>
        public static string? AssetPath(string name)
        {
            string path = Path.Combine(AssetsFolder, name);
            if (File.Exists(path)) return path;
            using var embedded = typeof(LauncherSkin).Assembly.GetManifestResourceStream("JondoAssets." + name);
            if (embedded == null) return null;
            try
            {
                string cache = Path.Combine(Path.GetTempPath(), "JondoEmu-launcher");
                Directory.CreateDirectory(cache);
                string copy = Path.Combine(cache, name);
                if (!File.Exists(copy) || new FileInfo(copy).Length != embedded.Length)
                {
                    using var sink = File.Create(copy + ".tmp");
                    embedded.CopyTo(sink);
                    sink.Close();
                    File.Move(copy + ".tmp", copy, overwrite: true);
                }
                return copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

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
                    string? path = AssetPath(name);
                    if (path != null) bitmap = new Bitmap(path);
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
