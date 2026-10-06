using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// The Avalonia application: the styles and the window.
    /// </summary>
    /// <remarks>
    /// The colours are put in here from <see cref="LauncherPalette"/> instead of writing them in the
    /// XAML. It is more roundabout, but it is the only way for the launcher and the server to keep painting
    /// with the same numbers: as soon as a colour is written by hand in an .axaml, that copy and the
    /// Windows Forms one start drifting apart and nobody notices until the two are seen together.
    /// </remarks>
    public sealed class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            CargarLaPaleta();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime escritorio)
            {
                escritorio.MainWindow = new MainWindow();

                // Closing the window NO longer shuts down the emulator: it only ends the launcher's process.
                // The server is another program and carries on with its own, with whatever players it has
                // inside.
                escritorio.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void CargarLaPaleta()
        {
            // The ones the XAML uses as Color, inside a gradient.
            Poner("GreenTop", LauncherPalette.GreenTop);
            Poner("GreenBottom", LauncherPalette.GreenBottom);
            Poner("GreenTopHover", LauncherPalette.GreenTopHover);
            Poner("GreenBottomHover", LauncherPalette.GreenBottomHover);
            Poner("PurpleTop", LauncherPalette.PurpleTop);
            Poner("PurpleBottom", LauncherPalette.PurpleBottom);

            // And the ones it uses as Brush, loose.
            PonerBrocha("GoldBrush", LauncherPalette.Gold);
            PonerBrocha("LightGoldBrush", LauncherPalette.LightGold);
            PonerBrocha("SoftGoldBrush", LauncherPalette.SoftGold);
            PonerBrocha("MutedGoldBrush", LauncherPalette.MutedGold);
            PonerBrocha("GoldBorderBrush", LauncherPalette.GoldBorder);
            PonerBrocha("LightBrownBrush", LauncherPalette.LightBrown);
            PonerBrocha("BorderBrownBrush", LauncherPalette.BorderBrown);
            PonerBrocha("BaseTextBrush", LauncherPalette.BaseText);
            PonerBrocha("CardTextBrush", LauncherPalette.CardText);
            PonerBrocha("HighlightTextBrush", LauncherPalette.HighlightText);
            PonerBrocha("FieldTextBrush", LauncherPalette.FieldText);
            PonerBrocha("FieldBackgroundBrush", LauncherPalette.FieldBackground);
            PonerBrocha("DisabledFieldBackgroundBrush", LauncherPalette.DisabledFieldBackground);
            PonerBrocha("DisabledFieldBorderBrush", LauncherPalette.DisabledFieldBorder);
            PonerBrocha("DisabledFieldTextBrush", LauncherPalette.DisabledFieldText);
            PonerBrocha("GreenBorderBrush", LauncherPalette.GreenBorder);
            PonerBrocha("PurpleBorderBrush", LauncherPalette.PurpleBorder);
            PonerBrocha("CardFillBrush", LauncherPalette.CardFill);
            PonerBrocha("BarFillBrush", LauncherPalette.BarFill);
            PonerBrocha("BackgroundBrush", LauncherPalette.Background);
            PonerBrocha("RedBrush", LauncherPalette.Red);
            PonerBrocha("AlertBackgroundBrush", LauncherPalette.AlertBackground);
            PonerBrocha("AlertTextBrush", LauncherPalette.AlertText);
            PonerBrocha("LightBrownTextBrush", LauncherPalette.LightBrownText);
        }

        private void Poner(string name, uint argb) => Resources[name] = Color.FromUInt32(argb);

        private void PonerBrocha(string name, uint argb)
            => Resources[name] = new SolidColorBrush(Color.FromUInt32(argb));
    }
}
