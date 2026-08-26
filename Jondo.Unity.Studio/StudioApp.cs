using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Jondo.Unity.Studio
{
    /// <summary>
    /// The application itself.
    /// </summary>
    /// <remarks>
    /// Built entirely from code, with no XAML, the same way the launcher is. It is not dogma: it
    /// keeps the moving parts down — no markup compilation, no binding engine to debug — and this
    /// editor is mostly lists and one custom-painted grid, neither of which markup makes shorter.
    /// </remarks>
    public sealed class StudioApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());

            // And on top of Fluent, the editor's own. Fluent on its own gave flat corners, no
            // hover and a selected row painted in the machine's system accent, which on a machine
            // whose accent is red made every selection look like an error state.
            Styles.Add(Ui.Skin.Build());

            // Dark by default because the thing it edits is looked at next to a running game
            // client, which is dark, and because most of the surface here is a map painted on a
            // dark ground.
            RequestedThemeVariant = ThemeVariant.Dark;

            // The accent is pinned instead of inherited. Fluent takes SystemAccentColor from the
            // machine, and on a machine whose accent is red every selected row looks like an error
            // state. An editor that shows measured data next to authored data cannot afford to
            // spend red on "this row is selected".
            // Bronze, the same as the icon, and the same as "somebody decided this" everywhere
            // else in the editor.
            Resources["SystemAccentColor"] = Color.Parse("#E8933A");
            Resources["SystemAccentColorDark1"] = Color.Parse("#CE7C29");
            Resources["SystemAccentColorDark2"] = Color.Parse("#AC661F");
            Resources["SystemAccentColorDark3"] = Color.Parse("#8A5018");
            Resources["SystemAccentColorLight1"] = Color.Parse("#F0A455");
            Resources["SystemAccentColorLight2"] = Color.Parse("#F5B76F");
            Resources["SystemAccentColorLight3"] = Color.Parse("#F9CB94");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Null under --selftest, which sets Avalonia up without starting a lifetime. No window
            // then, on purpose: the sections are built off screen and the process exits.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new Shell();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
