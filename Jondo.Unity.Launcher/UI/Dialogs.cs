using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher.UI.Widgets;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The launcher's modal notice, with the launcher's face.
    /// </summary>
    /// <remarks>
    /// Avalonia brings no message box, and that turns out to be an advantage: the system's one
    /// appeared over the art with its usual grey. This one is a card of the launcher: its icon on
    /// a tile, the title, the message, and the buttons at the bottom right -- the one that goes on
    /// last, as a dialog does.
    /// </remarks>
    internal static class Dialogs
    {
        public static async Task ShowAsync(Window owner, string title, string message, string accept)
        {
            var ok = Boton(Textos.Frase(accept), "launcher", "green");
            var ventana = Ventana(owner, title, message, "info", false, ok);
            ok.Click += (_, _) => ventana.Close();
            await ventana.ShowDialog(owner);
        }

        /// <summary>A yes/no question with the launcher's look. True only when "yes" is pressed.</summary>
        /// <param name="peligro">
        /// True when "yes" deletes or loses something: then it is the red button. An update or a
        /// change of server is asked too, but it is no deletion, and it goes in green.
        /// </param>
        public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string yes, string no,
                                                    bool peligro = true, bool scrollMessage = false)
        {
            bool answer = false;
            var confirm = peligro ? Boton(Textos.Frase(yes), "danger") : Boton(Textos.Frase(yes), "launcher", "green");
            var cancel = Boton(Textos.Frase(no), "secondary");
            var ventana = Ventana(owner, title, message, peligro ? "alert" : "info", scrollMessage, cancel, confirm);
            confirm.Click += (_, _) => { answer = true; ventana.Close(); };
            cancel.Click += (_, _) => ventana.Close();
            await ventana.ShowDialog(owner);
            return answer;
        }

        private static Button Boton(string text, params string[] classes)
        {
            var boton = new Button
            {
                Height = 38,
                MinWidth = 110,
                Padding = new Thickness(18, 0),
                Content = new TextBlock
                {
                    Text = text,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            foreach (string c in classes) boton.Classes.Add(c);
            return boton;
        }

        private static Window Ventana(Window owner, string title, string message, string icon,
                                      bool scrollMessage, params Button[] buttons)
        {
            // Most questions come with the window's own title, which says nothing: then the
            // message is the heading.
            bool conTitulo = title != owner.Title;
            bool alerta = icon == "alert";
            var tono = alerta ? LauncherSkin.Danger : LauncherSkin.Gold;

            var cabecera = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var tile = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(tono, 0.15),
                BorderBrush = new SolidColorBrush(tono, 0.3),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Icon
                {
                    Kind = icon,
                    Width = 20,
                    Height = 20,
                    Foreground = new SolidColorBrush(tono),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            TextBlock body = scrollMessage ? new SelectableTextBlock() : new TextBlock();
            body.Text = message;
            body.FontSize = conTitulo ? 13 : 14;
            body.LineHeight = conTitulo ? 19 : 20;
            body.Margin = new Thickness(0, conTitulo ? 0 : 9, 0, 0);
            body.TextWrapping = TextWrapping.Wrap;
            body.Foreground = new SolidColorBrush(conTitulo ? LauncherSkin.TextSecondary : LauncherSkin.CardText);
            var textos = new StackPanel
            {
                Spacing = 6,
                Margin = new Thickness(14, 0, 0, 0),
                Children =
                {
                    new TextBlock
                    {
                        IsVisible = conTitulo,
                        Text = title,
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        FontFamily = LauncherSkin.Display,
                        Foreground = new SolidColorBrush(LauncherSkin.CardText),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    scrollMessage ? new ScrollViewer { Content = body, MaxHeight = 320 } : body,
                },
            };
            Grid.SetColumn(textos, 1);
            cabecera.Children.Add(tile);
            cabecera.Children.Add(textos);

            var fila = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            foreach (var b in buttons) fila.Children.Add(b);

            return new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(LauncherSkin.SurfaceRaised),
                FontFamily = LauncherSkin.Ui,
                Content = new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    BorderBrush = new SolidColorBrush(LauncherSkin.Hairline),
                    Padding = new Thickness(24, 22, 24, 20),
                    Child = new StackPanel
                    {
                        Spacing = 22,
                        Width = 440,
                        Children = { cabecera, fila },
                    },
                },
            };
        }
    }
}
