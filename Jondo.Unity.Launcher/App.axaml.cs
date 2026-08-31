using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// La aplicación de Avalonia: los estilos y la ventana.
    /// </summary>
    /// <remarks>
    /// Los colores se meten aquí desde <see cref="LauncherPalette"/> en vez de escribirlos en el
    /// XAML. Es más rodeo, pero es la única forma de que el lanzador y el servidor sigan pintando
    /// con los mismos números: en cuanto un color se escriba a mano en un .axaml, esa copia y la de
    /// Windows Forms empiezan a separarse y nadie se entera hasta que se ven las dos juntas.
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

                // Cerrar la ventana ya NO apaga el emulador: sólo termina el proceso del lanzador.
                // El servidor es otro programa y sigue con lo suyo, con los jugadores que tenga
                // dentro.
                escritorio.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void CargarLaPaleta()
        {
            // Los que el XAML usa como Color, dentro de un degradado.
            Poner("GreenTop", LauncherPalette.GreenTop);
            Poner("GreenBottom", LauncherPalette.GreenBottom);
            Poner("GreenTopHover", LauncherPalette.GreenTopHover);
            Poner("GreenBottomHover", LauncherPalette.GreenBottomHover);
            Poner("PurpleTop", LauncherPalette.PurpleTop);
            Poner("PurpleBottom", LauncherPalette.PurpleBottom);

            // Y los que usa como Brush, sueltos.
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
