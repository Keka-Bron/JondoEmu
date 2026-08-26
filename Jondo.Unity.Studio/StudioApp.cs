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

            // Dark by default because the thing it edits is looked at next to a running game
            // client, which is dark, and because most of the surface here is a map painted on a
            // dark ground.
            RequestedThemeVariant = ThemeVariant.Dark;

            // The accent is pinned instead of inherited. Fluent takes SystemAccentColor from the
            // machine, and on a machine whose accent is red every selected row looks like an error
            // state. An editor that shows measured data next to authored data cannot afford to
            // spend red on "this row is selected".
            Resources["SystemAccentColor"] = Color.Parse("#3E6E9E");
            Resources["SystemAccentColorDark1"] = Color.Parse("#35608A");
            Resources["SystemAccentColorDark2"] = Color.Parse("#2C5175");
            Resources["SystemAccentColorDark3"] = Color.Parse("#234160");
            Resources["SystemAccentColorLight1"] = Color.Parse("#4E82B4");
            Resources["SystemAccentColorLight2"] = Color.Parse("#6396C6");
            Resources["SystemAccentColorLight3"] = Color.Parse("#7FAAD5");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new Shell();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
