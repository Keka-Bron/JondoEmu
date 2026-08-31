using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// El aviso modal del lanzador, con la cara del lanzador.
    /// </summary>
    /// <remarks>
    /// Avalonia no trae caja de mensaje, y eso resulta ser una ventaja: la de Windows Forms era la
    /// del sistema y aparecía en medio de la tarjeta de madera y oro con su gris de siempre. Ésta
    /// se pinta con la misma paleta que el resto.
    /// </remarks>
    internal static class Dialogs
    {
        public static async Task ShowAsync(Window owner, string title, string message, string accept)
        {
            var boton = new Button
            {
                Classes = { "launcher", "green" },
                Height = 36,
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Center,
                Content = new TextBlock
                {
                    Text = accept,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };

            var ventana = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(LauncherSkin.Of(LauncherPalette.Background)),
                FontFamily = LauncherSkin.Title,
                Content = new Border
                {
                    BorderThickness = new Avalonia.Thickness(2),
                    BorderBrush = new SolidColorBrush(LauncherSkin.GoldBorder),
                    CornerRadius = new Avalonia.CornerRadius(10),
                    Background = new SolidColorBrush(LauncherSkin.CardFill),
                    Padding = new Avalonia.Thickness(24),
                    Child = new StackPanel
                    {
                        Spacing = 18,
                        MaxWidth = 380,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = message,
                                TextWrapping = TextWrapping.Wrap,
                                TextAlignment = TextAlignment.Center,
                                FontSize = 13,
                                Foreground = new SolidColorBrush(LauncherSkin.BaseText),
                            },
                            boton,
                        },
                    },
                },
            };

            boton.Click += (_, _) => ventana.Close();
            await ventana.ShowDialog(owner);
        }
    }
}
