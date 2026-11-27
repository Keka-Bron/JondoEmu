using Avalonia.Controls;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI.Pages;

/// <summary>
/// The settings that apply at the next start, in the dashboard: <see cref="ServerSettingsView"/>
/// without its own title. It is built the first time the page is shown, once the world is ready.
/// </summary>
internal sealed class SettingsPage : UserControl, IServerPage
{
    private readonly Func<ServerSettings> _saved, _running;
    private Language _language;

    /// <summary>The editor shown now; null until the page is first shown.</summary>
    internal ServerSettingsView? View { get; private set; }

    /// <summary>For the tests: how the editors write the settings instead of the real file.</summary>
    internal Action<ServerSettings>? Write { get; set; }

    internal SettingsPage(Language language, Func<ServerSettings> saved, Func<ServerSettings> running)
    {
        _language = language;
        _saved = saved;
        _running = running;
    }

    /// <summary>A fresh editor from the file: after Cancel or a restart started, and the first time.</summary>
    internal void Rebuild() => Rebuild(_saved());

    private void Rebuild(ServerSettings saved)
    {
        View = new ServerSettingsView(_language, saved, _running(), Rebuild, embedded: true, latest: _saved);
        if (Write != null) View.Write = Write;
        Content = View;
    }

    internal void EnsureBuilt()
    {
        if (View?.Editor.Dirty == true) return;
        var saved = _saved();
        if (View == null || !View.Editor.MatchesSaved(saved)) Rebuild(saved);
    }

    public void ApplyLanguage(Language language)
    {
        if (language == _language) return;
        _language = language;
        // An editor with changes keeps them in its first language; a clean one follows the window.
        if (View != null && !View.Editor.Dirty) Rebuild();
    }

    public void Tick() { }
}
