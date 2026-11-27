using Avalonia.Controls;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI;

/// <summary>The settings editor in a window of its own (<see cref="ServerSettingsView"/>).</summary>
internal sealed class ServerSettingsWindow : Window
{
    internal const int CountdownWithPlayers = ServerSettingsActions.CountdownWithPlayers, CountdownEmpty = ServerSettingsActions.CountdownEmpty;
    internal readonly ServerSettingsView View;
    internal ServerSettingsEditor Editor => View.Editor;

    internal ServerSettingsWindow(Language language) : this(language, ServerSettings.Load(), ServerSettings.Current, ServerSettings.Load) { }

    // The second constructor permits desktop tests without touching the real settings/database.
    internal ServerSettingsWindow(Language language, ServerSettings saved, ServerSettings running, Func<ServerSettings>? latest = null)
    {
        Title = ServerSettingsTexts.Get(language, "Caption"); Width = 1000; Height = 800; MinWidth = 360; MinHeight = 420;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ServerTheme.Window(this);
        View = new ServerSettingsView(language, saved, running, Close, latest: latest ?? (() => saved));
        Content = View;
        Opened += (_, _) =>
        {
            // Opened maximized from the server window, it fills the screen as that one does: the
            // limits below would hold a maximized window to the screen less 40.
            if (WindowState == WindowState.Maximized) return;
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null) return;
            MaxHeight = Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - 40);
            MaxWidth = Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling - 40);
            Height = Math.Min(Height, MaxHeight);
            Width = Math.Min(Width, MaxWidth);
        };
    }
}
